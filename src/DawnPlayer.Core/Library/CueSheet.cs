using System.Globalization;
using System.Text;

namespace DawnPlayer.Core.Library;

/// <summary>One playable track of a cue sheet: the parent audio file plus a start offset.</summary>
public sealed record CueEntry(string AudioPath, int TrackNumber, string Title, string Performer, TimeSpan Start);

/// <summary>A parsed cue sheet: album-level metadata plus its playable tracks.</summary>
public sealed record CueDocument(string? AlbumTitle, string? AlbumPerformer, IReadOnlyList<CueEntry> Entries);

/// <summary>
/// Minimal CUE sheet parser: FILE/TRACK/INDEX commands with TITLE/PERFORMER at album and track
/// level. Enough for the standard album-image rips (EAC/XLD style); REM and PREGAP/POSTGAP lines
/// are ignored. Track ends (needed to build play ranges) are derived by the caller from the next
/// entry's start or the audio file's duration.
/// </summary>
public static class CueSheet
{
    /// <summary>Parses a cue file. Returns null when nothing playable is in it.</summary>
    public static CueDocument? TryParseFile(string cuePath)
    {
        try
        {
            string text;
            // CUE sheets from EAC/XLD are overwhelmingly UTF-8 (with or without BOM) or the system
            // code page; M3u.DetectEncoding already solved this exact problem, but it lives on the
            // persistence side, so keep a small local probe: BOM → strict-UTF8 → system ANSI.
            using (var stream = File.OpenRead(cuePath))
            using (var reader = new StreamReader(stream, DetectEncoding(stream), detectEncodingFromByteOrderMarks: false))
            {
                text = reader.ReadToEnd();
            }
            return Parse(text, cuePath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses cue text. <paramref name="cuePath"/> resolves relative FILE entries.</summary>
    public static CueDocument? Parse(string text, string cuePath)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(cuePath)) return null;

        string? albumTitle = null;
        string? albumPerformer = null;
        string? currentFile = null;
        int? trackNumber = null;
        string? trackTitle = null;
        string? trackPerformer = null;
        TimeSpan? index01 = null;
        var entries = new List<CueEntry>();

        var dir = Path.GetDirectoryName(cuePath) ?? string.Empty;

        void FlushTrack()
        {
            if (trackNumber is > 0 && index01.HasValue && currentFile != null)
            {
                entries.Add(new CueEntry(
                    ResolveFile(dir, currentFile),
                    trackNumber.Value,
                    string.IsNullOrWhiteSpace(trackTitle) ? "" : trackTitle!,
                    string.IsNullOrWhiteSpace(trackPerformer) ? albumPerformer ?? "" : trackPerformer!,
                    index01.Value));
            }
            trackNumber = null;
            trackTitle = null;
            trackPerformer = null;
            index01 = null;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var (command, argument) = SplitCommand(line);
            switch (command)
            {
                case "FILE":
                    FlushTrack();
                    currentFile = Unquote(argument.TakeFirstToken());
                    break;

                case "TRACK":
                    FlushTrack();
                    var tokens = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 2 &&
                        int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) &&
                        string.Equals(tokens[1], "AUDIO", StringComparison.OrdinalIgnoreCase))
                    {
                        trackNumber = num;
                    }
                    break;

                case "INDEX":
                    // After the command word: "<number> <mm:ss:ff>". INDEX 01 is where the track
                    // becomes audible; INDEX 00 is the pregap and is ignored.
                    var indexTokens = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (indexTokens.Length == 2 &&
                        int.TryParse(indexTokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var indexNo) &&
                        indexNo == 1 &&
                        TryParseTime(indexTokens[1..], out var t))
                    {
                        index01 = t;
                    }
                    break;

                case "TITLE":
                    // Track-level TITLE appears after TRACK; album-level before any TRACK.
                    if (trackNumber != null) trackTitle = Unquote(argument);
                    else albumTitle = Unquote(argument);
                    break;

                case "PERFORMER":
                    if (trackNumber != null) trackPerformer = Unquote(argument);
                    else albumPerformer = Unquote(argument);
                    break;
            }
        }
        FlushTrack();

        return entries.Count > 0
            ? new CueDocument(albumTitle, albumPerformer, entries)
            : null;
    }

    /// <summary>Parses a CUE time token "mm:ss:ff" (ff = 1/75 s CD frames), tolerating the
    /// "mm:ss.ff" variant writers emit and a bare "mm:ss".</summary>
    public static bool TryParseTime(string[] parts, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (parts.Length != 1) return false;
        var fields = parts[0].Trim().Split(':', '.');
        if (fields.Length is not (2 or 3)) return false;
        if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm) ||
            !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ss))
        {
            return false;
        }
        int frames = 0;
        if (fields.Length == 3 &&
            !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out frames))
        {
            return false;
        }
        if (mm < 0 || ss < 0 || frames < 0) return false;
        time = TimeSpan.FromSeconds(mm * 60 + ss) + TimeSpan.FromTicks((long)Math.Round(frames * TimeSpan.TicksPerSecond / 75.0));
        return true;
    }

    private static (string Command, string Argument) SplitCommand(string line)
    {
        int space = line.IndexOf(' ');
        if (space < 0) return (line.ToUpperInvariant(), "");
        return (line[..space].ToUpperInvariant(), line[(space + 1)..].Trim());
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s[1..^1];
        return s;
    }

    /// <summary>CUE FILE lines carry exactly one argument (the name); the type word follows a space
    /// outside the quotes. Splitting on the first space after any closing quote is overkill — the
    /// first token is enough because the type is never part of a quoted name in practice.</summary>
    private static string TakeFirstToken(this string argument)
    {
        int space = argument.IndexOf(' ');
        return space < 0 ? argument : argument[..space];
    }

    private static string ResolveFile(string cueDir, string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return file;
        var unescaped = file.Replace('\\', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(unescaped)
            ? unescaped
            : Path.GetFullPath(Path.Combine(string.IsNullOrEmpty(cueDir) ? "." : cueDir, unescaped));
    }

    private static Encoding DetectEncoding(FileStream stream)
    {
        try
        {
            Span<byte> bom = stackalloc byte[4];
            stream.Position = 0;
            int read = stream.Read(bom);
            stream.Position = 0;

            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
            if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;

            // Strict validation: only commit to UTF-8 when the bytes really decode.
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var buffer = new byte[Math.Min(stream.Length, 1 << 16)];
            int got = stream.Read(buffer, 0, buffer.Length);
            stream.Position = 0;
            try
            {
                strict.GetString(buffer, 0, got);
                return Encoding.UTF8;
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Default;
            }
        }
        catch
        {
            stream.Position = 0;
            return Encoding.UTF8;
        }
    }
}
