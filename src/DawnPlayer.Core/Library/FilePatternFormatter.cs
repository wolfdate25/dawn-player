using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Library;

/// <summary>
/// Formats file paths and names based on track metadata and customizable patterns (e.g. foobar2000 style).
/// Supports token replacement, conditional blocks [...], and Windows file system sanitization.
/// </summary>
public static class FilePatternFormatter
{
    private static readonly Regex ConditionalBlockRegex = new(@"\[([^\[\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex TokenRegex = new(@"%([a-zA-Z0-9_]+)%", RegexOptions.Compiled);

    private static readonly char[] IllegalChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Formats a track into a relative file path based on the given pattern.
    /// </summary>
    public static string Format(string pattern, Track track, string? defaultExtension = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (string.IsNullOrWhiteSpace(pattern))
            pattern = "%artist% - %title%";

        // 1. Determine file extension
        string ext = "";
        try
        {
            var rawPath = AppPaths.PhysicalPath(track.Path);
            ext = Path.GetExtension(rawPath).TrimStart('.');
        }
        catch { }
        if (string.IsNullOrEmpty(ext))
            ext = defaultExtension ?? "mp3";

        // 2. Resolve token values (sanitized individually so artist "AC/DC" won't create unexpected subfolders)
        string artist = SanitizeComponent(string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist);
        string albumArtist = SanitizeComponent(string.IsNullOrWhiteSpace(track.AlbumArtist) ? artist : track.AlbumArtist);
        string album = SanitizeComponent(string.IsNullOrWhiteSpace(track.Album) ? "Unknown Album" : track.Album);
        string title = SanitizeComponent(string.IsNullOrWhiteSpace(track.Title)
            ? Path.GetFileNameWithoutExtension(AppPaths.PhysicalPath(track.Path)) ?? "Unknown Title"
            : track.Title);
        string trackNo = track.TrackNo > 0 ? track.TrackNo.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        string trackNumber = track.TrackNo > 0 ? track.TrackNo.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) : "";
        string discNo = track.DiscNo > 0 ? track.DiscNo.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        string year = track.Year > 0 ? track.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        string genre = SanitizeComponent(track.Genre ?? "");

        string LookupToken(string key) => key.ToLowerInvariant() switch
        {
            "artist" => artist,
            "albumartist" => albumArtist,
            "album" => album,
            "title" => title,
            "track" => trackNo,
            "tracknumber" => trackNumber,
            "disc" => discNo,
            "year" => year,
            "genre" => genre,
            "ext" => ext,
            _ => ""
        };

        // 3. Process conditional blocks: [ ... ]
        // If an enclosed block contains tokens and ANY of the tokens is empty, omit the block.
        string workingPattern = ConditionalBlockRegex.Replace(pattern, match =>
        {
            string inner = match.Groups[1].Value;
            var tokens = TokenRegex.Matches(inner);
            if (tokens.Count > 0)
            {
                foreach (Match tokenMatch in tokens)
                {
                    var tokenKey = tokenMatch.Groups[1].Value;
                    var tokenVal = LookupToken(tokenKey);
                    if (string.IsNullOrEmpty(tokenVal))
                        return ""; // At least one token has no value -> omit block
                }
            }

            // Replace tokens inside this block
            return TokenRegex.Replace(inner, tm => LookupToken(tm.Groups[1].Value));
        });

        // 4. Replace remaining top-level tokens
        string formatted = TokenRegex.Replace(workingPattern, tm => LookupToken(tm.Groups[1].Value));

        // 5. Normalize directory separators and clean each segment
        var segments = formatted.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var cleanSegments = new string[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            cleanSegments[i] = CleanPathSegment(segments[i]);
        }

        string resultPath = string.Join(Path.DirectorySeparatorChar.ToString(), cleanSegments);

        // 6. Ensure extension is appended if missing
        if (!resultPath.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
        {
            resultPath += "." + ext;
        }

        return resultPath;
    }

    /// <summary>
    /// Sanitizes an individual metadata string so it contains no illegal file system characters or directory separators.
    /// </summary>
    public static string SanitizeComponent(string component)
    {
        if (string.IsNullOrWhiteSpace(component)) return "";

        var sb = new StringBuilder(component.Length);
        foreach (char c in component)
        {
            if (c == '/' || c == '\\' || c == ':')
                sb.Append('-');
            else if (c == '<' || c == '>' || c == '"' || c == '|' || c == '?' || c == '*')
                sb.Append('_');
            else if (c < 32)
                continue;
            else
                sb.Append(c);
        }

        var s = sb.ToString().Trim(' ', '.');
        return s;
    }

    private static string CleanPathSegment(string segment)
    {
        var s = SanitizeComponent(segment);
        if (string.IsNullOrWhiteSpace(s))
            return "Unknown";

        // Check reserved device names on Windows
        var baseName = s.Split('.')[0];
        if (IsReservedDeviceName(baseName))
            s = "_" + s;

        return s;
    }

    private static bool IsReservedDeviceName(string name)
    {
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(name, @"^(COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase);
    }
}
