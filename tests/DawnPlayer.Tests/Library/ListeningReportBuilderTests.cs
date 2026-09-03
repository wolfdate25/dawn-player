using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests.Library;

/// <summary>
/// Listening report aggregation: totals, rankings, window filtering, and the determinism/tie and
/// unknown-path handling that keep the dashboard honest.
/// </summary>
public sealed class ListeningReportBuilderTests
{
    private static readonly DateTime Now = new(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Since = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Track TrackAt(string path, string artist, string album, string genre,
        long durationMs = 200_000) => new()
    {
        Path = path,
        Title = path,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        Genre = genre,
        DurationMs = durationMs,
    };

    [Fact]
    public void Builds_TotalsAndRankings_FromEvents()
    {
        var tracks = new List<Track>
        {
            TrackAt("a", "Art A", "Album X", "Jazz"),
            TrackAt("b", "Art A", "Album X", "Jazz"),
            TrackAt("c", "Art B", "Album Y", "Rock"),
        };
        var events = new List<(long, string)>
        {
            (Since.Ticks + 1, "a"),
            (Since.Ticks + 2, "a"),
            (Since.Ticks + 3, "b"),
            (Since.Ticks + 4, "c"),
        };

        var report = ListeningReportBuilder.Build(tracks, events, Since, Now);

        Assert.Equal(4, report.TotalPlays);
        Assert.Equal(3, report.UniqueTracks);
        Assert.Equal(TimeSpan.FromMilliseconds(200_000 * 4), report.TotalListenTime);

        var artist = report.TopArtists[0];
        Assert.Equal("Art A", artist.Name);
        Assert.Equal(3, artist.Plays);
        Assert.Equal(TimeSpan.FromMilliseconds(600_000), artist.ListenTime);
        Assert.Equal("Art B", report.TopArtists[1].Name);
        Assert.Equal(1, report.TopArtists[1].Plays);

        Assert.Equal("Album X", report.TopAlbums[0].Name);
        Assert.Equal(3, report.TopAlbums[0].Plays);
        Assert.Equal("Jazz", report.TopGenres[0].Name);
        // Top tracks rank by plays, then album order: 'a' (2) before 'b'/'c' (1 each).
        Assert.Equal("a", report.TopTracks[0].Name);
        Assert.Equal(2, report.TopTracks[0].Plays);
    }

    [Fact]
    public void EventsOutsideWindow_AreExcluded()
    {
        var tracks = new List<Track> { TrackAt("a", "A", "X", "g"), TrackAt("b", "B", "Y", "g") };
        var events = new List<(long, string)>
        {
            (Since.Ticks - 100, "a"),  // before the window
            (Since.Ticks + 10, "a"),
            (Now.Ticks + 100, "b"),    // after the window (clock skew guard)
        };

        var report = ListeningReportBuilder.Build(tracks, events, Since, Now);

        Assert.Equal(1, report.TotalPlays);
        Assert.Equal(new[] { "a" }, report.TopTracks.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void UnknownPaths_CountAsPlays_ButRankNothing()
    {
        var tracks = new List<Track> { TrackAt("known", "A", "X", "g") };
        var events = new List<(long, string)> { (Since.Ticks + 1, "known"), (Since.Ticks + 2, "gone") };

        var report = ListeningReportBuilder.Build(tracks, events, Since, Now);

        Assert.Equal(2, report.TotalPlays);            // the play happened even if the file left
        Assert.Equal(1, report.UniqueTracks);          // but only resolvable tracks join rankings
        Assert.Single(report.TopArtists);
        Assert.Equal(TimeSpan.FromMilliseconds(200_000), report.TotalListenTime);
    }

    [Fact]
    public void UntaggedKeys_AreSkippedInRankings_RankingStaysDeterministic()
    {
        var tracks = new List<Track>
        {
            TrackAt("a", "", "", "Jazz"),   // no artist/album → ranks nothing, no "unknown" row
            TrackAt("b", "Art", "", "Jazz"),
        };
        var events = new List<(long, string)> { (Since.Ticks + 1, "a"), (Since.Ticks + 2, "b") };

        var first = ListeningReportBuilder.Build(tracks, events, Since, Now);
        var second = ListeningReportBuilder.Build(tracks, events, Since, Now);

        // Only "b" has tags: it is the single ranked artist, with no placeholder for "a".
        var artist = Assert.Single(first.TopArtists);
        Assert.Equal("Art", artist.Name);
        Assert.Empty(first.TopAlbums);
        Assert.Equal(first.TopGenres, second.TopGenres);  // same inputs → same report
        Assert.Equal("Jazz", Assert.Single(first.TopGenres).Name);
        Assert.Equal(2, first.TopGenres[0].Plays);
    }

    [Fact]
    public void MaxEntries_CapsEachSection()
    {
        var tracks = Enumerable.Range(0, 20)
            .Select(i => TrackAt($"t{i}", $"Artist {i}", $"Album {i}", "g"))
            .ToList();
        var events = tracks.Select((t, i) => (Since.Ticks + i + 1, t.Path)).ToList();

        var report = ListeningReportBuilder.Build(tracks, events, Since, Now, maxEntries: 5);

        Assert.Equal(5, report.TopArtists.Count);
        Assert.Equal(5, report.TopAlbums.Count);
        Assert.Equal(5, report.TopTracks.Count);
        // Ties (1 play each) break by name: "Artist 0", "Artist 1", ...
        Assert.Equal("Artist 0", report.TopArtists[0].Name);
    }
}
