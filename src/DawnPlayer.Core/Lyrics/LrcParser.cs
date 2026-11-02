using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DawnPlayer.Core.Lyrics;

/// <summary>One word segment of an enhanced-LRC line: when it starts and the exact text run it owns.</summary>
public sealed record LrcWord(TimeSpan Start, string Text);

/// <summary>
/// One lyric line. <paramref name="Words"/> carries enhanced-LRC word timings when present
/// (Text is always the exact tag-stripped line text: prelude + concatenated word segments).
/// <paramref name="Translation"/> is a second line written at the same timestamp — the dominant
/// on-disk translation convention — merged at parse time; null when the line has none.
/// </summary>
public sealed record LrcLine(TimeSpan Time, string Text, IReadOnlyList<LrcWord>? Words = null, string? Translation = null);

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

    /// <summary>True when any line carries enhanced-LRC word timings (drives karaoke rendering).</summary>
    public bool HasWordTimings
    {
        get
        {
            var lines = Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Words is { Count: > 0 }) return true;
            }
            return false;
        }
    }

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
/// [offset:] tag, metadata tags, enhanced word-level tags, and duplicate-timestamp translation
/// lines which are merged into the first line of the pair). Formatting a parsed document
/// round-trips all of it losslessly.</summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(?:(?<hour>\d{1,2}):(?<min>\d{1,2}):(?<sec>\d{1,2})(?:[.:](?<frac>\d{1,3}))|(?<min>\d{1,3}):(?<sec>\d{1,2})(?:[.:](?<frac>\d{1,3}))?)\]")]
    private static partial Regex TimestampRegex();

    private static readonly Regex WordTagRegex = new(
        @"<(?:(?<hour>\d{1,2}):)?(?<min>\d{1,3}):(?<sec>\d{1,2})(?:[.:](?<frac>\d{1,3}))?>",
        RegexOptions.Compiled);

    public static LyricsDocument Parse(string text, string? sourcePath = null, bool pairTranslationLines = true)
    {
        string? title = null, artist = null, album = null, by = null;
        double offsetMs = 0;
        var rawLines = new List<(TimeSpan t, string s, IReadOnlyList<LrcWord>? w)>();

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

            // strip all timestamp tags → text remainder, keeping word-level tags as segments
            var stripped = TimestampRegex().Replace(line, "").Trim();
            var lineTime = matches.Count > 0 && TryParseTimestampMatch(matches[0], out var first) ? first : TimeSpan.Zero;
            var (textPart, words) = ExtractWordTimings(stripped, lineTime);
            if (textPart.Length == 0) textPart = "♪";

            foreach (Match m in matches)
            {
                if (!TryParseTimestampMatch(m, out var t)) continue;
                rawLines.Add((t, textPart, words));
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

        // Pass 2: Apply global offset uniformly to all lines (and their word timings) and sort
        var finalLines = new List<(TimeSpan t, string s, IReadOnlyList<LrcWord>? w, bool clamped)>(rawLines.Count);
        var offsetSpan = TimeSpan.FromMilliseconds(offsetMs);

        foreach (var (rawTime, textPart, words) in rawLines)
        {
            // Positive [offset:] shifts lyrics earlier per the LRC convention.
            var t = rawTime - offsetSpan;
            bool clamped = t < TimeSpan.Zero;
            if (clamped) t = TimeSpan.Zero;

            IReadOnlyList<LrcWord>? finalWords = null;
            if (words is { Count: > 0 })
            {
                // The word times shift by the same uniform offset; the re-clamp per line keeps
                // the invariant "words never precede their line" for duplicated timestamps too
                // (a line copied to a later stamp highlights from that stamp, not from the past).
                var clampedWords = new List<LrcWord>(words.Count);
                foreach (var w in words)
                {
                    var ws = w.Start - offsetSpan;
                    if (ws < TimeSpan.Zero) ws = TimeSpan.Zero;
                    if (ws < t) ws = t;
                    clampedWords.Add(w with { Start = ws });
                }
                finalWords = clampedWords;
            }

            finalLines.Add((t, textPart, finalWords, clamped));
        }

        // OrderBy is a documented stable sort: duplicate timestamps (duet lines, dual-timestamp
        // notation) keep their input order — List.Sort reordered them run to run.
        finalLines = finalLines.OrderBy(l => l.t).ToList();

        var lines = new List<LrcLine>(finalLines.Count);
        // Pairing needs a timeline: an all-zero "document" is unsynced text where duplicate
        // adjacent lines are just more text, never a translation pair. Offset-clamp collisions
        // (several lines pushed to zero) are artifacts, not translations, and are excluded too.
        bool anyTimed = finalLines.Any(l => l.t > TimeSpan.Zero);
        if (pairTranslationLines && anyTimed)
        {
            int i = 0;
            while (i < finalLines.Count)
            {
                var cur = finalLines[i];
                int j = i + 1;
                while (j < finalLines.Count && finalLines[j].t == cur.t) j++;

                if (j - i == 2 && TryMergeTranslation(cur, finalLines[i + 1], out var merged))
                {
                    lines.Add(merged);
                    i += 2;
                    continue;
                }

                for (int k = i; k < j; k++)
                {
                    lines.Add(new LrcLine(finalLines[k].t, finalLines[k].s, finalLines[k].w));
                }
                i = j;
            }
        }
        else
        {
            lines.AddRange(finalLines.Select(l => new LrcLine(l.t, l.s, l.w)));
        }

        return new LyricsDocument
        {
            Title = title,
            Artist = artist,
            Album = album,
            By = by,
            SourcePath = sourcePath ?? "",
            Lines = lines
        };
    }

    /// <summary>
    /// Strict translation-pair predicate (plan §10.3, I4): the candidate must be an un-timed
    /// second line with a normalized-different text. Duet lines, repeated chorus (identical
    /// text) and 3+ line groups all stay flat — an over-eager merge would corrupt real lyrics.
    /// The "♪" instrumental placeholder is never a translation candidate or a translatable
    /// original (a bare timestamp line decays to "♪", which would otherwise pair as one).
    /// </summary>
    private static bool TryMergeTranslation(
        (TimeSpan t, string s, IReadOnlyList<LrcWord>? w, bool clamped) primary,
        (TimeSpan t, string s, IReadOnlyList<LrcWord>? w, bool clamped) candidate,
        out LrcLine merged)
    {
        merged = new LrcLine(primary.t, primary.s, primary.w);
        if (primary.clamped || candidate.clamped) return false;
        if (candidate.w is { Count: > 0 }) return false;
        if (primary.s.Length == 0 || candidate.s.Length == 0) return false;
        if (string.Equals(primary.s, "♪", StringComparison.Ordinal) ||
            string.Equals(candidate.s, "♪", StringComparison.Ordinal)) return false;
        if (string.Equals(NormalizeForPairing(primary.s), NormalizeForPairing(candidate.s), StringComparison.Ordinal)) return false;
        merged = new LrcLine(primary.t, primary.s, primary.w, candidate.s);
        return true;
    }

    internal static string NormalizeForPairing(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>
    /// Splits enhanced-LRC word tags out of the timestamp-stripped line text. Returns the exact
    /// tag-stripped text (prelude + concatenated segments — identical to a plain tag strip) and
    /// the word list, or null when the line has no usable word tags. Word starts are clamped to
    /// be non-decreasing and never precede the line time (invariant I2).
    /// </summary>
    private static (string CleanText, IReadOnlyList<LrcWord>? Words) ExtractWordTimings(string text, TimeSpan lineTime)
    {
        var matches = WordTagRegex.Matches(text);
        if (matches.Count == 0) return (text, null);

        var builder = new StringBuilder(text.Length);
        var words = new List<LrcWord>(matches.Count);
        int cursor = 0;
        var prevStart = lineTime;

        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            builder.Append(text, cursor, m.Index - cursor);
            cursor = m.Index + m.Length;

            int segEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var segment = text[cursor..segEnd];
            if (segment.Length == 0) continue;
            if (!TryParseTimestampMatch(m, out var start)) continue;

            if (start < prevStart) start = prevStart;
            prevStart = start;
            words.Add(new LrcWord(start, segment));
        }
        builder.Append(text, cursor, text.Length - cursor);

        return (builder.ToString(), words.Count > 0 ? words : null);
    }

    private static bool TryParseTimestampMatch(Match m, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        int hour = 0;
        if (m.Groups["hour"].Success && int.TryParse(m.Groups["hour"].Value, CultureInfo.InvariantCulture, out int h))
        {
            hour = h;
        }

        if (!int.TryParse(m.Groups["min"].Value, CultureInfo.InvariantCulture, out int min)) return false;
        if (!int.TryParse(m.Groups["sec"].Value, CultureInfo.InvariantCulture, out int sec)) return false;

        // frac digits: 1 → tenths, 2 → hundredths, 3 → ms
        double frac = 0;
        var fracStr = m.Groups["frac"].Value;
        if (fracStr.Length > 0)
            frac = int.Parse(fracStr, CultureInfo.InvariantCulture) / Math.Pow(10, fracStr.Length);

        time = TimeSpan.FromSeconds(hour * 3600 + min * 60 + sec + frac);
        return true;
    }

    public static string Format(LyricsDocument doc, double offsetMs = 0)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(doc.Title)) sb.AppendLine(CultureInfo.InvariantCulture, $"[ti:{doc.Title.Trim()}]");
        if (!string.IsNullOrWhiteSpace(doc.Artist)) sb.AppendLine(CultureInfo.InvariantCulture, $"[ar:{doc.Artist.Trim()}]");
        if (!string.IsNullOrWhiteSpace(doc.Album)) sb.AppendLine(CultureInfo.InvariantCulture, $"[al:{doc.Album.Trim()}]");
        if (!string.IsNullOrWhiteSpace(doc.By)) sb.AppendLine(CultureInfo.InvariantCulture, $"[by:{doc.By.Trim()}]");
        if (Math.Abs(offsetMs) > 0.0001) sb.AppendLine(CultureInfo.InvariantCulture, $"[offset:{(long)Math.Round(offsetMs)}]");

        // Unsynced documents (every line at zero) are written without timestamps; a translation
        // then re-emits as the following text line, which parses back identically.
        bool plainMode = doc.Lines.Count > 0 && doc.Lines.All(l => l.Time == TimeSpan.Zero);

        foreach (var line in doc.Lines)
        {
            if (plainMode)
            {
                sb.AppendLine(line.Text);
                if (!string.IsNullOrEmpty(line.Translation)) sb.AppendLine(line.Translation);
                continue;
            }

            int min = (int)line.Time.TotalMinutes;
            int sec = line.Time.Seconds;
            int ms = line.Time.Milliseconds;
            var stamp = string.Create(CultureInfo.InvariantCulture, $"[{min:D2}:{sec:D2}.{ms:D3}] ");

            sb.Append(stamp);
            AppendLineBody(sb, line);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(line.Translation))
            {
                sb.Append(stamp).AppendLine(line.Translation);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Writes a line body, re-emitting enhanced word tags when the line carries them. The stored
    /// Text must be exactly prelude + concatenated word segments — verified by comparing the tail
    /// of Text with the segments; a foreign construction (e.g. the editor re-typing the text of a
    /// word-timed line) falls back to plain text rather than emitting a corrupt split.
    /// </summary>
    private static void AppendLineBody(StringBuilder sb, LrcLine line)
    {
        var words = line.Words;
        if (words is not { Count: > 0 })
        {
            sb.Append(line.Text);
            return;
        }

        int wordChars = 0;
        var concat = new StringBuilder(words.Sum(w => w.Text.Length));
        foreach (var w in words)
        {
            wordChars += w.Text.Length;
            concat.Append(w.Text);
        }
        if (wordChars > line.Text.Length ||
            !string.Equals(line.Text[^wordChars..], concat.ToString(), StringComparison.Ordinal))
        {
            sb.Append(line.Text);
            return;
        }

        sb.Append(line.Text, 0, line.Text.Length - wordChars);
        foreach (var w in words)
        {
            sb.Append('<').Append(FormatTimestamp(w.Start)).Append('>').Append(w.Text);
        }
    }

    private static string FormatTimestamp(TimeSpan time)
    {
        int min = (int)time.TotalMinutes;
        int sec = time.Seconds;
        int ms = time.Milliseconds;
        return string.Create(CultureInfo.InvariantCulture, $"{min:D2}:{sec:D2}.{ms:D3}");
    }

    public static void SaveToFile(string path, string lrcContent)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        // Write-then-replace: a plain overwrite left a truncated file behind when the process
        // died (or the power went) mid-write, destroying a curated .lrc. The temp file lives on
        // the same volume, so the replace is atomic.
        var tempPath = path + ".dawn-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, lrcContent, utf8WithBom);
            if (File.Exists(path)) File.Replace(tempPath, path, destinationBackupFileName: null);
            else File.Move(tempPath, path);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
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
            // Not UTF-8. .NET's Encoding.Default is ALWAYS UTF-8, and the system ANSI page only
            // matches the file's encoding when the machine locale happens to — a CP949 lyric on
            // an en-US system decoded as 1252 was still mojibake. The shared decoder decides by
            // content (Hangul ratio), so it works on every system locale.
            return DawnPlayer.Core.Util.LegacyTextDecoder.DecodeNonUtf8(bytes);
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
