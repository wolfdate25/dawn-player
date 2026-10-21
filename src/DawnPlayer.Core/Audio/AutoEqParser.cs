using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Parser and serializer for parametric equalizer profiles supporting:
/// 1. Standard AutoEq text format (ParametricEQ.txt from Jaakko Pasanen's AutoEq database).
/// 2. Dawn Player native JSON preset format (.dpeq / .json).
/// </summary>
public static class AutoEqParser
{
    private static readonly Regex PreampRegex = new(
        @"^Preamp:\s*([+-]?\d+(?:\.\d+)?)\s*dB",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FilterRegex = new(
        @"^Filter\s+\d+:\s*(ON|OFF)\s+([A-Za-z_]+)\s+Fc\s+([+-]?\d+(?:\.\d+)?)\s*Hz\s+Gain\s+([+-]?\d+(?:\.\d+)?)\s*dB\s+Q\s+([+-]?\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parses an AutoEq parametric equalizer text content into an <see cref="EqProfile"/>.
    /// Guaranteed not to throw on malformed or empty input.
    /// Clamps all values to safe audio invariants (Freq 20..20000 Hz, Gain -15..+15 dB, Q 0.1..8.0, Preamp -12..+12 dB).
    /// Enforces a maximum limit of 20 bands.
    /// </summary>
    public static EqProfile Parse(string? text, string? profileName = null)
    {
        var profile = new EqProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(profileName) ? "AutoEq Profile" : profileName.Trim(),
            Enabled = true,
            PreampDb = 0.0,
            Bands = new List<EqBandSettings>()
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return profile;
        }

        using var reader = new StringReader(text);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var preampMatch = PreampRegex.Match(trimmed);
            if (preampMatch.Success && double.TryParse(preampMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double preampVal))
            {
                profile.PreampDb = Math.Clamp(preampVal, -12.0, 12.0);
                continue;
            }

            var filterMatch = FilterRegex.Match(trimmed);
            if (filterMatch.Success)
            {
                string state = filterMatch.Groups[1].Value.ToUpperInvariant();
                if (state != "ON")
                {
                    // Ignore disabled/OFF filters
                    continue;
                }

                if (profile.Bands.Count >= 20)
                {
                    // Enforce maximum 20 bands
                    continue;
                }

                string typeStr = filterMatch.Groups[2].Value.ToUpperInvariant();
                if (!TryParseFilterType(typeStr, out var filterType))
                {
                    continue;
                }

                if (!double.TryParse(filterMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double freq))
                {
                    continue;
                }

                if (!double.TryParse(filterMatch.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gain))
                {
                    continue;
                }

                if (!double.TryParse(filterMatch.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double q))
                {
                    continue;
                }

                var band = new EqBandSettings
                {
                    Type = filterType,
                    FrequencyHz = Math.Clamp(freq, 20.0, 20000.0),
                    GainDb = Math.Clamp(gain, -15.0, 15.0),
                    Q = Math.Clamp(q, 0.1, 8.0)
                };

                profile.Bands.Add(band);
            }
        }

        return profile;
    }

    /// <summary>
    /// Exports an <see cref="EqProfile"/> into standard AutoEq parametric text format.
    /// </summary>
    public static string Export(EqProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Preamp: {profile.PreampDb:0.#} dB");

        if (profile.Bands != null)
        {
            int index = 1;
            foreach (var band in profile.Bands.Take(20))
            {
                string typeStr = FormatFilterType(band.Type);
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Filter {index}: ON {typeStr} Fc {band.FrequencyHz:0.#} Hz Gain {band.GainDb:0.#} dB Q {band.Q:0.##}");
                index++;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Serializes an <see cref="EqProfile"/> to a formatted JSON string (.dpeq preset).
    /// </summary>
    public static string SerializeJson(EqProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Serialize(profile, JsonOptions);
    }

    /// <summary>
    /// Deserializes an <see cref="EqProfile"/> from a JSON string. Returns null on failure.
    /// </summary>
    public static EqProfile? DeserializeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var profile = JsonSerializer.Deserialize<EqProfile>(json, JsonOptions);
            if (profile != null)
            {
                profile.PreampDb = Math.Clamp(profile.PreampDb, -12.0, 12.0);
                if (profile.Bands != null)
                {
                    if (profile.Bands.Count > 20)
                    {
                        profile.Bands = profile.Bands.Take(20).ToList();
                    }
                    foreach (var b in profile.Bands)
                    {
                        b.FrequencyHz = Math.Clamp(b.FrequencyHz, 20.0, 20000.0);
                        b.GainDb = Math.Clamp(b.GainDb, -15.0, 15.0);
                        b.Q = Math.Clamp(b.Q, 0.1, 8.0);
                    }
                }
            }
            return profile;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseFilterType(string token, out EqFilterType filterType)
    {
        switch (token)
        {
            case "PK":
            case "PEAK":
            case "PEAKEQ":
                filterType = EqFilterType.PeakEq;
                return true;

            case "LSC":
            case "LS":
            case "LOW_SHELF":
            case "LOWSHELF":
                filterType = EqFilterType.LowShelf;
                return true;

            case "HSC":
            case "HS":
            case "HIGH_SHELF":
            case "HIGHSHELF":
                filterType = EqFilterType.HighShelf;
                return true;

            case "LP":
            case "LOW_PASS":
            case "LOWPASS":
                filterType = EqFilterType.LowPass;
                return true;

            case "HP":
            case "HIGH_PASS":
            case "HIGHPASS":
                filterType = EqFilterType.HighPass;
                return true;

            default:
                filterType = EqFilterType.PeakEq;
                return false;
        }
    }

    private static string FormatFilterType(EqFilterType type) => type switch
    {
        EqFilterType.PeakEq => "PK",
        EqFilterType.LowShelf => "LSC",
        EqFilterType.HighShelf => "HSC",
        EqFilterType.LowPass => "LP",
        EqFilterType.HighPass => "HP",
        _ => "PK"
    };
}
