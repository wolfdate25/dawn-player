using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DawnPlayer.Core.Lyrics;

public sealed record LrcLine(TimeSpan Time, string Text);

public sealed class LyricsDocument
{
    public static readonly LyricsDocument Empty = new();

    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? By { get; init; }
    public string SourcePath { get; init; } = "";
    public IReadOnlyList<LrcLine> Lines { get; init; } = Array.Empty<LrcLine>();

    public bool HasLines => Lines.Count > 0;

    /// <summary>Index of the line active at <paramref name="time"/> (-1 before the first line).</summary>
    public int LineIndexAt(TimeSpan time)
    {
        var lines = Lines;
        int lo = 0, hi = lines.Count - 1, res = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].Time <= time) { res = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return res;
    }
}

/// <summary>Parser for .lrc files (standard timestamps, multiple timestamps per line,
/// [offset:] tag, metadata tags, and enhanced word-level tags which are stripped).</summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(?:(?<hour>\d{1,2}):(?<min>\d{1,2}):(?<sec>\d{1,2})(?:[.:](?<frac>\d{1,3}))|(?<min>\d{1,3}):(?<sec>\d{1,2})(?:[.:](?<frac>\d{1,3}))?)\]")]
    private static partial Regex TimestampRegex();

    private static readonly Regex WordTagRegex = new(@"<(?:(?:\d{1,2}:)?\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?)>", RegexOptions.Compiled);

    public static LyricsDocument Parse(string text, string? sourcePath = null)
    {
        string? title = null, artist = null, album = null, by = null;
        double offsetMs = 0;
        var rawLines = new List<(TimeSpan t, string s)>();

        // Pass 1: Extract metadata and raw timestamped lines
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', '\uFEFF').Trim();
            if (line.Length == 0) continue;

            var matches = TimestampRegex().Matches(line);
            if (matches.Count == 0)
            {
                // metadata: [ti:...], [ar:...], [al:...], [by:...], [offset:±ms]
                if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
                {
                    var inner = line[1..^1];
                    var idx = inner.IndexOf(':');
                    if (idx > 0)
                    {
                        var key = inner[..idx].Trim().ToLowerInvariant();
                        var val = inner[(idx + 1)..].Trim();
                        switch (key)
                        {
                            case "ti": title = val; break;
                            case "ar": artist = val; break;
                            case "al": album = val; break;
                            case "by": by = val; break;
                            case "offset":
                                if (TryParseMs(val, out var off)) offsetMs += off;
                                break;
                        }
                    }
                }
                continue;
            }

            // strip all timestamp tags → text remainder
            var textPart = TimestampRegex().Replace(line, "").Trim();
            textPart = WordTagRegex.Replace(textPart, ""); // enhanced-lrc word tags
            if (textPart.Length == 0) textPart = "♪";

            foreach (Match m in matches)
            {
                int hour = 0;
                if (m.Groups["hour"].Success && int.TryParse(m.Groups["hour"].Value, CultureInfo.InvariantCulture, out int h))
                {
                    hour = h;
                }

                int min = int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture);
                int sec = int.Parse(m.Groups["sec"].Value, CultureInfo.InvariantCulture);
                var fracStr = m.Groups["frac"].Value;
                // frac digits: 1 → tenths, 2 → hundredths, 3 → ms
                double frac = 0;
                if (fracStr.Length > 0)
                    frac = int.Parse(fracStr, CultureInfo.InvariantCulture) / Math.Pow(10, fracStr.Length);

                var t = TimeSpan.FromSeconds(hour * 3600 + min * 60 + sec + frac);
                rawLines.Add((t, textPart));
            }
        }

        if (rawLines.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            // Treat as plain text line-by-line lyrics (unsynchronized)
            var plainLines = text.Split('\n')
                .Select(l => l.TrimEnd('\r', '\uFEFF').Trim())
                .Where(l => l.Length > 0 && !(l.StartsWith('[') && l.EndsWith(']')))
                .Select(l => new LrcLine(TimeSpan.Zero, l))
                .ToList();

            if (plainLines.Count > 0)
            {
                return new LyricsDocument
                {
                    Title = title,
                    Artist = artist,
                    Album = album,
                    By = by,
                    SourcePath = sourcePath ?? "",
                    Lines = plainLines
                };
            }
        }

        // Pass 2: Apply global offset uniformly to all lines and sort
        var finalLines = new List<(TimeSpan t, string s)>(rawLines.Count);
        var offsetSpan = TimeSpan.FromMilliseconds(offsetMs);

        foreach (var (rawTime, textPart) in rawLines)
        {
            // Positive [offset:] shifts lyrics earlier per the LRC convention.
            var t = rawTime - offsetSpan;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            finalLines.Add((t, textPart));
        }

        finalLines.Sort((a, b) => a.t.CompareTo(b.t));

        return new LyricsDocument
        {
            Title = title,
            Artist = artist,
            Album = album,
            By = by,
            SourcePath = sourcePath ?? "",
            Lines = finalLines.Select(l => new LrcLine(l.t, l.s)).ToList()
        };
    }

    public static string Format(LyricsDocument doc, double offsetMs = 0)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(doc.Title)) sb.AppendLine($"[ti:{doc.Title.Trim()}]");
        if (!string.IsNullOrWhiteSpace(doc.Artist)) sb.AppendLine($"[ar:{doc.Artist.Trim()}]");
        if (!string.IsNullOrWhiteSpace(doc.Album)) sb.AppendLine($"[al:{doc.Album.Trim()}]");
        if (!string.IsNullOrWhiteSpace(doc.By)) sb.AppendLine($"[by:{doc.By.Trim()}]");
        if (Math.Abs(offsetMs) > 0.0001) sb.AppendLine($"[offset:{(long)Math.Round(offsetMs)}]");

        foreach (var line in doc.Lines)
        {
            if (line.Time == TimeSpan.Zero && doc.Lines.All(l => l.Time == TimeSpan.Zero))
            {
                sb.AppendLine(line.Text);
            }
            else
            {
                int min = (int)line.Time.TotalMinutes;
                int sec = line.Time.Seconds;
                int ms = line.Time.Milliseconds;
                sb.AppendLine($"[{min:D2}:{sec:D2}.{ms:D3}] {line.Text}");
            }
        }
        return sb.ToString();
    }

    public static void SaveToFile(string path, string lrcContent)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(path, lrcContent, utf8WithBom);
    }

    public static bool SaveOffsetToFile(string lrcPath, double offsetMs)
    {
        if (string.IsNullOrWhiteSpace(lrcPath) || !File.Exists(lrcPath)) return false;
        try
        {
            var text = DecodeBytes(File.ReadAllBytes(lrcPath));
            var doc = Parse(text, lrcPath);
            var newContent = Format(doc, offsetMs);
            SaveToFile(lrcPath, newContent);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static LyricsDocument ParseFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text = DecodeBytes(bytes);
        return Parse(text, path);
    }

    private static string DecodeBytes(byte[] bytes)
    {
        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                return Encoding.GetEncoding("utf-32BE").GetString(bytes, 4, bytes.Length - 4);
            if (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Default.GetString(bytes);
        }
    }

    private static bool TryParseMs(string s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().Replace('−', '-').Replace("ms", "").Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
