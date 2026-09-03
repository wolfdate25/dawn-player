using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Library;

/// <summary>One ranked row of the listening report (top artists, albums, ...).</summary>
public sealed record ListeningReportEntry(string Name, int Plays, TimeSpan ListenTime);

/// <summary>Aggregate listening summary over a period, built from the play-event history joined
/// back onto the working set. Pure data — constructed by <see cref="ListeningReportBuilder"/>.</summary>
public sealed record ListeningReport
{
    public DateTime SinceUtc { get; init; }
    public DateTime UntilUtc { get; init; }
    public int TotalPlays { get; init; }
    public int UniqueTracks { get; init; }
    /// <summary>Sum of the played tracks' durations — the closest available proxy for time actually
    /// listened (partial plays are not tracked at a finer granularity).</summary>
    public TimeSpan TotalListenTime { get; init; }
    public IReadOnlyList<ListeningReportEntry> TopArtists { get; init; } = Array.Empty<ListeningReportEntry>();
    public IReadOnlyList<ListeningReportEntry> TopAlbums { get; init; } = Array.Empty<ListeningReportEntry>();
    public IReadOnlyList<ListeningReportEntry> TopGenres { get; init; } = Array.Empty<ListeningReportEntry>();
    public IReadOnlyList<ListeningReportEntry> TopTracks { get; init; } = Array.Empty<ListeningReportEntry>();
}

/// <summary>
/// Folds play events into a <see cref="ListeningReport"/>. Deterministic and side-effect free:
/// ties in the rankings break by name so two runs over the same inputs produce the same report.
/// </summary>
public static class ListeningReportBuilder
{
    public static ListeningReport Build(
        IReadOnlyList<Track> tracks,
        IReadOnlyList<(long PlayedUtcTicks, string Path)> playEvents,
        DateTime sinceUtc,
        DateTime untilUtc,
        int maxEntries = 10)
    {
        maxEntries = Math.Max(1, maxEntries);

        var byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            if (t != null && !string.IsNullOrEmpty(t.Path)) byPath[t.Path] = t;
        }

        // One row per played track: (plays, duration, display keys). Events whose path is no longer
        // in the working set (file deleted, drive offline) still count toward TotalPlays.
        int totalPlays = 0;
        var playedTracks = new Dictionary<string, (Track Track, int Plays)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ticks, path) in playEvents)
        {
            var played = DateTime.FromBinary(ticks);
            if (played < sinceUtc || played > untilUtc) continue;

            totalPlays++;
            if (!byPath.TryGetValue(path, out var track)) continue;
            if (playedTracks.TryGetValue(path, out var existing)) playedTracks[path] = (track, existing.Plays + 1);
            else playedTracks[path] = (track, 1);
        }

        TimeSpan totalListenTime = TimeSpan.Zero;
        foreach (var (_, (track, plays)) in playedTracks)
        {
            totalListenTime += TimeSpan.FromTicks(track.DurationMs * TimeSpan.TicksPerMillisecond * plays);
        }

        return new ListeningReport
        {
            SinceUtc = sinceUtc,
            UntilUtc = untilUtc,
            TotalPlays = totalPlays,
            UniqueTracks = playedTracks.Count,
            TotalListenTime = totalListenTime,
            TopArtists = Rank(playedTracks, t => TrimOrNull(t.SortArtist), maxEntries),
            TopAlbums = Rank(playedTracks, t => TrimOrNull(t.Album), maxEntries),
            TopGenres = Rank(playedTracks, t => TrimOrNull(t.Genre), maxEntries),
            TopTracks = playedTracks.Values
                .OrderByDescending(v => v.Plays)
                .ThenBy(v => v.Track.AlbumSortKey)
                .ThenBy(v => v.Track.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(maxEntries)
                .Select(v => new ListeningReportEntry(
                    string.IsNullOrWhiteSpace(v.Track.Title) ? v.Track.Path : v.Track.Title,
                    v.Plays,
                    TimeSpan.FromTicks(v.Track.DurationMs * TimeSpan.TicksPerMillisecond * v.Plays)))
                .ToList(),
        };
    }

    private static string? TrimOrNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static List<ListeningReportEntry> Rank(
        IReadOnlyDictionary<string, (Track Track, int Plays)> played,
        Func<Track, string?> keyOf,
        int maxEntries)
    {
        var totals = new Dictionary<string, (int Plays, long DurationMs)>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var (_, (track, plays)) in played)
        {
            var key = keyOf(track);
            if (key == null) continue; // untagged: ranking "unknown" rows would be noise
            var (p, d) = totals.TryGetValue(key, out var existing)
                ? (existing.Plays + plays, existing.DurationMs + track.DurationMs * plays)
                : (plays, track.DurationMs * plays);
            totals[key] = (p, d);
        }

        return totals
            .OrderByDescending(kv => kv.Value.Plays)
            .ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxEntries)
            .Select(kv => new ListeningReportEntry(
                kv.Key, kv.Value.Plays, TimeSpan.FromMilliseconds(kv.Value.DurationMs)))
            .ToList();
    }
}
