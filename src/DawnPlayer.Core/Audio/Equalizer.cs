using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Pure helper methods for canonicalizing audio output device keys and resolving
/// device-specific equalizer profiles with fallback to the global default profile.
/// </summary>
public static class EqualizerProfileResolver
{
    public static string CanonicalKey(AudioDriverType driver, string? resolvedDeviceId)
    {
        string prefix = driver switch
        {
            AudioDriverType.Wasapi => "wasapi",
            AudioDriverType.DirectSound => "dsound",
            AudioDriverType.WaveOut => "waveout",
            _ => "wasapi"
        };
        return $"{prefix}:{resolvedDeviceId ?? ""}";
    }

    public static EqProfile Resolve(EqualizerSettings? settings, AudioDriverType driver, string? resolvedDeviceId)
    {
        if (settings == null)
        {
            return new EqProfile { Id = "default", Name = "기본 프로필", Enabled = false };
        }

        // Deliberately does NOT call EnsureDefaultProfile: this runs on the audio session start
        // path, and mutating the shared Profiles dictionary from there raced the UI thread's
        // enumeration during settings serialization. Branch 3 below already covers an empty
        // Profiles map, and every UI path seeds the default itself.
        var key = CanonicalKey(driver, resolvedDeviceId);

        EqProfile resolved;
        // 1. Check if an explicit device binding exists
        if (settings.DeviceBindings.TryGetValue(key, out var profileId) && !string.IsNullOrEmpty(profileId) &&
            settings.Profiles.TryGetValue(profileId, out var boundProfile) && boundProfile != null)
        {
            resolved = boundProfile.Clone();
        }
        // 2. Fall back to DefaultProfileId
        else if (!string.IsNullOrEmpty(settings.DefaultProfileId) &&
            settings.Profiles.TryGetValue(settings.DefaultProfileId, out var defProfile) &&
            defProfile != null)
        {
            resolved = defProfile.Clone();
        }
        // 3. Fall back to first available profile or clean instance
        else
        {
            var first = settings.Profiles.Values.FirstOrDefault();
            resolved = first != null ? first.Clone() : new EqProfile { Id = "default", Name = "기본 프로필" };
        }

        // Global master enable governs the resolved profile's enabled state
        resolved.Enabled = settings.Enabled;
        return resolved;
    }
}

/// <summary>
/// Pure mathematical calculator for computing composite parametric equalizer frequency response curves.
/// </summary>
public static class EqFrequencyResponseCalculator
{
    /// <summary>
    /// Computes composite magnitude response in dB across the specified frequencies.
    /// </summary>
    public static double[] CalculateResponse(EqProfile? profile, double[] frequencies, int sampleRate = 44100)
    {
        if (frequencies == null || frequencies.Length == 0)
        {
            return Array.Empty<double>();
        }

        var response = new double[frequencies.Length];

        if (profile == null || !profile.Enabled)
        {
            return response;
        }

        double preampDb = Math.Clamp(profile.PreampDb, -12.0, 12.0);
        for (int i = 0; i < response.Length; i++)
        {
            response[i] = preampDb;
        }

        var bands = (profile.Bands ?? Enumerable.Empty<EqBandSettings>()).Take(20).ToList();
        if (bands.Count == 0)
        {
            return response;
        }

        // Same design routine the runtime filter bank uses, so the drawn curve is the response
        // the listener actually gets.
        var coeffsList = new List<Dsp.BiquadCoefficients>(bands.Count);
        foreach (var band in bands)
        {
            coeffsList.Add(Dsp.BiquadDesign.Create(band.Type, band.FrequencyHz, band.GainDb, band.Q, sampleRate));
        }

        for (int i = 0; i < frequencies.Length; i++)
        {
            double f = Math.Clamp(frequencies[i], 10.0, sampleRate * 0.499);
            double w = 2.0 * Math.PI * f / sampleRate;
            double cosW = Math.Cos(w);
            double sinW = Math.Sin(w);
            double cos2W = Math.Cos(2.0 * w);
            double sin2W = Math.Sin(2.0 * w);

            double totalBandDb = 0.0;
            foreach (var c in coeffsList)
            {
                // Coefficients arrive normalized (a0 == 1).
                double numReal = c.B0 + c.B1 * cosW + c.B2 * cos2W;
                double numImag = -c.B1 * sinW - c.B2 * sin2W;
                double denReal = 1.0 + c.A1 * cosW + c.A2 * cos2W;
                double denImag = -c.A1 * sinW - c.A2 * sin2W;

                double numMagSq = numReal * numReal + numImag * numImag;
                double denMagSq = denReal * denReal + denImag * denImag;

                if (denMagSq > 1e-12 && numMagSq > 1e-12)
                {
                    totalBandDb += 10.0 * Math.Log10(numMagSq / denMagSq);
                }
            }

            response[i] += totalBandDb;
        }

        return response;
    }

}
