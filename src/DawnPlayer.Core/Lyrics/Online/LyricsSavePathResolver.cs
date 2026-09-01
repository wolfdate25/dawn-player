using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.Core.Lyrics.Online;

public enum LyricsSaveResult { Saved, SkippedExisting, Failed }

/// <summary>Outcome of saving one online lyrics document offline.</summary>
public sealed record LyricsSaveOutcome(LyricsSaveResult Result, string? Path = null, string? Error = null)
{
    public static LyricsSaveOutcome Saved(string path) => new(LyricsSaveResult.Saved, path);
    public static LyricsSaveOutcome Skipped(string path) => new(LyricsSaveResult.SkippedExisting, path);
    public static LyricsSaveOutcome Fail(string error) => new(LyricsSaveResult.Failed, null, error);
}

/// <summary>
/// Resolves where an online lyrics file is written and writes it. The template may name
/// subfolders (e.g. "%album%\%trackno%. %title%.lrc"); it is expanded against the save root —
/// the track's folder, or the user's chosen folder — with every path segment sanitized and
/// directory traversal ("..") stripped so a template can never escape the root.
/// </summary>
public static class LyricsSavePathResolver
{
    /// <summary>Expands the configured template against the track and resolves it against the save root.</summary>
    public static string ResolveSavePath(Track track, LyricsOnlineSettings settings)
    {
        var template = string.IsNullOrWhiteSpace(settings.SaveFileNameTemplate)
            ? "%filename%.lrc"
            : settings.SaveFileNameTemplate;

        var root = settings.SaveLocation == LyricsSaveLocation.CustomFolder && !string.IsNullOrWhiteSpace(settings.CustomSaveFolder)
            ? settings.CustomSaveFolder
            : Path.GetDirectoryName(track.Path) ?? ".";

        var combined = Path.Combine(root, SanitizeRelativePath(LyricsTokenExpander.Expand(template, track)));

        // Belt and braces: even a sanitized template could re-root the path (drive letters,
        // UNC prefixes), and Path.Combine silently returns a rooted second argument.
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(combined);
        return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(fullRoot, Path.GetFileName(full));
    }

    /// <summary>Saves <paramref name="document"/> to the resolved path unless it exists and overwrite is off.</summary>
    public static LyricsSaveOutcome Save(Track track, LyricsDocument document, LyricsOnlineSettings settings)
    {
        string path;
        try
        {
            path = ResolveSavePath(track, settings);
        }
        catch (Exception ex)
        {
            return LyricsSaveOutcome.Fail($"경로 계산 실패: {ex.Message}");
        }

        if (File.Exists(path) && !settings.OverwriteExisting)
            return LyricsSaveOutcome.Skipped(path);

        try
        {
            LrcParser.SaveToFile(path, LrcParser.Format(document));
            return LyricsSaveOutcome.Saved(path);
        }
        catch (Exception ex)
        {
            return LyricsSaveOutcome.Fail($"저장 실패: {ex.Message}");
        }
    }

    /// <summary>Sanitizes each segment of a relative path, dropping traversal/drive/root segments.</summary>
    private static string SanitizeRelativePath(string relative)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            // ".", ".." traverse; a ':' drive letter would re-root the combined path (c:\evil).
            .Where(s => s.Length > 0 && s != "." && s != ".." && !s.Contains(':'))
            .Select(s => new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.'));
        var joined = string.Join(Path.DirectorySeparatorChar.ToString(), segments.Where(s => s.Length > 0));
        return joined.Length > 0 ? joined : "lyrics.lrc";
    }
}
