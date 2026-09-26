using DawnPlayer.Core.Lyrics.Online;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Lyrics;

/// <summary>Finds a matching .lrc next to the track or in the lyrics search folders.</summary>
public static class LyricsFinder
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static bool ExistsFor(Track track, AppSettings settings)
        => FindLrcPath(track, settings) is not null;

    public static string? FindLrcPath(Track track, AppSettings settings)
    {
        var candidates = BuildCandidates(track, settings);
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Loads lyrics from an external .lrc file, or falls back to embedded audio tags.</summary>
    public static LyricsDocument? LoadLyrics(Track track, AppSettings settings)
    {
        var path = FindLrcPath(track, settings);
        if (path != null)
        {
            try
            {
                return LrcParser.ParseFile(path);
            }
            catch (Exception ex) { Log.Debug($"[lyrics] parse failed for '{path}': {ex.Message}"); }
        }

        if (settings.Lyrics.ReadEmbeddedLyrics)
        {
            try
            {
                var embedded = Library.TagReader.ReadEmbeddedLyrics(track.Path);
                if (!string.IsNullOrWhiteSpace(embedded))
                {
                    return LrcParser.Parse(embedded, track.Path);
                }
            }
            catch (Exception ex) { Log.Debug($"[lyrics] embedded lyrics parse failed for '{track.Path}': {ex.Message}"); }
        }

        return null;
    }

    /// <summary>Returns the standard default .lrc file path next to the audio track.</summary>
    public static string GetDefaultLrcSavePath(Track track)
    {
        return Path.ChangeExtension(track.Path, ".lrc");
    }

    public static List<string> BuildCandidates(Track track, AppSettings settings)
    {
        var dir = Path.GetDirectoryName(track.Path) ?? "";

        var names = new List<string>();
        foreach (var pattern in settings.Lyrics.FilePatterns)
        {
            var name = LyricsTokenExpander.Expand(pattern, track);
            if (name.IndexOfAny(InvalidChars) < 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        var paths = new List<string>();
        foreach (var root in settings.Lyrics.SearchFolders.Append(dir))
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (var name in names)
                paths.Add(Path.Combine(root, name));
        }

        // Round-trip: the configured online save destination is always a candidate, so a file
        // just saved there (custom folder, subfolder template) is discoverable with the same
        // settings. Appended last to preserve existing precedence whenever an older file was
        // already found.
        try
        {
            var savePath = LyricsSavePathResolver.ResolveSavePath(track, settings.LyricsOnline);
            if (!paths.Contains(savePath, StringComparer.OrdinalIgnoreCase))
                paths.Add(savePath);
        }
        catch (Exception ex) { Log.Debug($"[lyrics] save-path candidate failed for '{track.Path}': {ex.Message}"); }
        return paths;
    }
}
