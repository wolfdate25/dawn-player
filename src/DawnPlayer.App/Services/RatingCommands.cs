using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Util;

namespace DawnPlayer.App.Services;

/// <summary>
/// L11 pure rating-command layer: every decision a rating command makes before touching the
/// database, file tags, or the UI. Headless-testable — <see cref="AppServices.RateTracks"/> is
/// the thin side-effect shell around these decisions. Invariants (RatingCommandsTests):
/// stream URLs are never rateable, duplicate paths merge to the first occurrence
/// (case-insensitive), already-equal ratings are skipped so a fully unchanged batch is a
/// complete no-op, and cue-fragment paths write tags through the physical file only.
/// </summary>
public static class RatingCommands
{
    /// <summary>Clamps a raw star input to the 0-5 rating domain.</summary>
    public static int Normalize(int stars) => Math.Clamp(stars, 0, 5);

    /// <summary>Whether a rating can target this track at all: a file-backed track with a path.
    /// Stream URLs (radio, and by contract any http(s) remote source) live in playlists only and
    /// carry no tags — the UI hides their rating affordances and this guard is the second wall.</summary>
    public static bool IsRateable(Track? track) =>
        track != null && !string.IsNullOrEmpty(track.Path) && !RadioTrack.IsStreamUrl(track.Path);

    /// <summary>The file a tag write touches for a track path: cue-fragment virtual paths
    /// address a range inside a physical file, and the physical file carries the tag.</summary>
    public static string TagWritePath(string path) => AppPaths.PhysicalPath(path);

    /// <summary>Selects the tracks a rating command should write: rateable tracks only, duplicate
    /// paths merged to the first occurrence (case-insensitive, matching the library key), and
    /// tracks whose rating already equals the clamped value skipped — rewriting an unchanged
    /// rating would churn the DB, the file tags, and every bound proxy for nothing. An all-
    /// unchanged batch yields an empty list, which the caller treats as a full no-op.</summary>
    public static List<Track> SelectTargets(IEnumerable<Track?>? tracks, int stars)
    {
        var clamped = Normalize(stars);
        if (tracks == null) return [];

        return tracks
            .Where(t => t != null && !string.IsNullOrEmpty(t.Path) && !RadioTrack.IsStreamUrl(t.Path))
            .Select(t => t!)
            .GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(t => t.Rating != clamped)
            .ToList();
    }

    /// <summary>The user-facing notice for failed tag writes, or null when every write
    /// succeeded — success stays silent (the refreshed stars are the feedback).</summary>
    public static string? FormatTagWriteFailure(int failedCount)
    {
        if (failedCount <= 0) return null;
        return Localization.AppStrings.Format(
            "Msg_RatingTagWriteFailed",
            "파일 태그에 평점을 쓰지 못했습니다 ({0}곡) — 평점은 앱 라이브러리에만 저장되었습니다.",
            failedCount);
    }

    /// <summary>Row-cell display text for a rating: filled stars (clamped to 5), or a single
    /// outline star when unrated — the discovery affordance that tells the user the rating cell
    /// exists and is clickable. Pure display contract; the XAML converters delegate to it.</summary>
    public static string DisplayText(int rating) =>
        rating > 0 ? new string(FilledStar, Math.Min(rating, 5)) : OutlineStar.ToString();

    /// <summary>Screen-reader text for a rating cell ("평점 N/5" / "평점 없음" via resw).</summary>
    public static string AccessibilityText(int rating)
    {
        var r = Normalize(rating);
        return r == 0
            ? Localization.AppStrings.Get("Rating_Unrated", "평점 없음")
            : Localization.AppStrings.Format("Rating_Accessibility_Format", "평점 {0}/5", r);
    }

    private const char FilledStar = '\u2605';   // ★
    private const char OutlineStar = '\u2606';  // ☆
}
