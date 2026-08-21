using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

public class AlbumTrackDrawerTests
{
    private static Track CreateTrack(string title, int discNo, int trackNo, long durationMs, string? genre = "Pop")
    {
        return new Track
        {
            Title = title,
            Artist = "5 Seconds Of Summer",
            Album = "CALM",
            DiscNo = discNo,
            TrackNo = trackNo,
            DurationMs = durationMs,
            Genre = genre ?? "",
            Path = $@"C:\music\{title}.mp3"
        };
    }

    [Fact]
    public void AlbumTrackItemVm_FormatsTrackNoAndDuration_Accurately()
    {
        var track = CreateTrack("Red Desert", 1, 1, 230000); // 3m 50s
        var vm = new AlbumTrackItemVm(track);

        Assert.Equal("1.1", vm.TrackNoFormatted);
        Assert.Equal("Red Desert", vm.Title);
        Assert.Equal("3:50", vm.DurationFormatted);
        Assert.False(vm.IsPlaying);

        // Test property changed on IsPlaying
        string? changedProp = null;
        vm.PropertyChanged += (_, e) => changedProp = e.PropertyName;

        vm.IsPlaying = true;
        Assert.True(vm.IsPlaying);
        Assert.Equal(nameof(vm.IsPlaying), changedProp);
    }

    [Fact]
    public void AlbumTrackItemVm_MultiDisc_FormatsCorrectDiscAndTrack()
    {
        var track = CreateTrack("Disc 2 Song", 2, 8, 221000); // 3m 41s
        var vm = new AlbumTrackItemVm(track);

        Assert.Equal("2.8", vm.TrackNoFormatted);
        Assert.Equal("3:41", vm.DurationFormatted);
    }

    [Fact]
    public void TwoColumnTrackSplit_Balancing_CorrectAcrossOddEvenAndEdgeCounts()
    {
        // 1. 13 tracks (Odd count as in CALM screenshot: 7 left, 6 right)
        var tracks13 = Enumerable.Range(1, 13)
            .Select(i => CreateTrack($"Track {i}", 1, i, 180000))
            .ToList();

        int leftCount13 = (tracks13.Count + 1) / 2;
        var left13 = tracks13.Take(leftCount13).ToList();
        var right13 = tracks13.Skip(leftCount13).ToList();

        Assert.Equal(7, left13.Count);
        Assert.Equal(6, right13.Count);
        Assert.Equal(13, left13.Count + right13.Count);
        Assert.Equal("Track 1", left13[0].Title);
        Assert.Equal("Track 7", left13[^1].Title);
        Assert.Equal("Track 8", right13[0].Title);
        Assert.Equal("Track 13", right13[^1].Title);

        // 2. 10 tracks (Even count: 5 left, 5 right)
        var tracks10 = Enumerable.Range(1, 10)
            .Select(i => CreateTrack($"Track {i}", 1, i, 180000))
            .ToList();

        int leftCount10 = (tracks10.Count + 1) / 2;
        var left10 = tracks10.Take(leftCount10).ToList();
        var right10 = tracks10.Skip(leftCount10).ToList();

        Assert.Equal(5, left10.Count);
        Assert.Equal(5, right10.Count);

        // 3. 1 track (Single track: 1 left, 0 right)
        var tracks1 = new List<Track> { CreateTrack("Single Track", 1, 1, 120000) };
        int leftCount1 = (tracks1.Count + 1) / 2;
        var left1 = tracks1.Take(leftCount1).ToList();
        var right1 = tracks1.Skip(leftCount1).ToList();

        Assert.Single(left1);
        Assert.Empty(right1);

        // 4. 0 tracks (Empty: 0 left, 0 right)
        var tracks0 = new List<Track>();
        int leftCount0 = (tracks0.Count + 1) / 2;
        var left0 = tracks0.Take(leftCount0).ToList();
        var right0 = tracks0.Skip(leftCount0).ToList();

        Assert.Empty(left0);
        Assert.Empty(right0);
    }

    [Fact]
    public void AlbumTrackSorting_OrdersByDiscNoThenTrackNoThenTitle()
    {
        var unsorted = new List<Track>
        {
            CreateTrack("Song C", 1, 3, 100000),
            CreateTrack("Song A", 1, 1, 100000),
            CreateTrack("Bonus Disc Track", 2, 1, 100000),
            CreateTrack("Song B", 1, 2, 100000)
        };

        var sorted = unsorted
            .OrderBy(t => t.DiscNo > 0 ? t.DiscNo : 1)
            .ThenBy(t => t.TrackNo > 0 ? t.TrackNo : 1)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Assert.Equal("Song A", sorted[0].Title);
        Assert.Equal("Song B", sorted[1].Title);
        Assert.Equal("Song C", sorted[2].Title);
        Assert.Equal("Bonus Disc Track", sorted[3].Title);
    }

    [Fact]
    public void EoleDurationFormatting_MatchesScreenshotFormat()
    {
        // 43 min 32 s
        var ts = new TimeSpan(0, 43, 32);
        string formatted = AlbumGroup.FormatEoleDuration(ts);

        Assert.Equal("43 min 32 s", formatted);

        // 23 h 44 min 24 s
        var tsHours = new TimeSpan(23, 44, 24);
        string formattedHours = AlbumGroup.FormatEoleDuration(tsHours);

        Assert.Equal("23 h 44 min 24 s", formattedHours);
    }

    [Fact]
    public void AlbumTrackItemVm_Title_FallsBackToFileName_WhenTitleEmpty()
    {
        var trackWithoutTitle = new Track
        {
            Title = "",
            Artist = "Artist",
            Path = @"C:\Music\AwesomeTrack01.flac"
        };
        var vm = new AlbumTrackItemVm(trackWithoutTitle);
        Assert.Equal("AwesomeTrack01", vm.Title);

        var trackWithWhitespace = new Track
        {
            Title = "   ",
            Artist = "Artist",
            Path = @"C:\Music\WhitespaceTrack.mp3"
        };
        var vm2 = new AlbumTrackItemVm(trackWithWhitespace);
        Assert.Equal("WhitespaceTrack", vm2.Title);
    }

    [Fact]
    public void AlbumTrackItemVm_TrackNoFormatted_ZeroOrNegative_DefaultsToOne()
    {
        var trackZero = new Track
        {
            Title = "No Track Number",
            DiscNo = 0,
            TrackNo = 0,
            Path = @"C:\Music\song.mp3"
        };
        var vm = new AlbumTrackItemVm(trackZero);
        Assert.Equal("1.1", vm.TrackNoFormatted);
    }

    [Fact]
    public void AlbumTrackItemVm_DurationFormatted_OverOneHour_IncludesHoursMinutesSeconds()
    {
        var trackLong = new Track
        {
            Title = "Long Symphonic Track",
            DurationMs = 3725000, // 1h 2m 5s
            Path = @"C:\Music\symphony.flac"
        };
        var vm = new AlbumTrackItemVm(trackLong);
        Assert.Equal("1:02:05", vm.DurationFormatted);
    }

    [Fact]
    public void FormatEolePlaylistStats_HandlesZeroOneAndManyItems()
    {
        // 0 items
        Assert.Equal("0 items", DawnPlayer.App.Controls.PlaybackUiHelper.FormatEolePlaylistStats(0, TimeSpan.Zero));
        Assert.Equal("0 items", DawnPlayer.App.Controls.PlaybackUiHelper.FormatEolePlaylistStats(null));

        // 1 item, 3 min 20 s
        var ts1 = new TimeSpan(0, 3, 20);
        Assert.Equal("3 min 20s, 1 items", DawnPlayer.App.Controls.PlaybackUiHelper.FormatEolePlaylistStats(1, ts1));

        // 24 items, 1 h 12 min 30 s
        var ts2 = new TimeSpan(1, 12, 30);
        Assert.Equal("1 h 12 min 30s, 24 items", DawnPlayer.App.Controls.PlaybackUiHelper.FormatEolePlaylistStats(24, ts2));
    }

    [Fact]
    public void AlbumTrackItemVm_NegativeDuration_FormatsToZero()
    {
        var trackNegative = new Track
        {
            Title = "Corrupted Duration Track",
            DurationMs = -5000,
            Path = @"C:\Music\corrupt.mp3"
        };
        var vm = new AlbumTrackItemVm(trackNegative);
        Assert.Equal("0:00", vm.DurationFormatted);
    }

    [Fact]
    public void AlbumTrackItemVm_EmptyTitleAndEmptyPath_FallsBackToUnknownTitle()
    {
        var trackEmpty = new Track
        {
            Title = "",
            Path = ""
        };
        var vm = new AlbumTrackItemVm(trackEmpty);
        Assert.Equal("(Unknown Title)", vm.Title);
    }

    [Fact]
    public void AlbumTrackItemVm_DrawerTrackList_SplittingAndLivePlayingUpdate_WorkCorrectly()
    {
        var tracks = new List<Track>
        {
            CreateTrack("Track 1", 1, 1, 180000, "Rock"),
            CreateTrack("Track 2", 1, 2, 200000, "Rock"),
            CreateTrack("Track 3", 1, 3, 210000, "Rock")
        };

        // Track sorting
        var sorted = tracks
            .OrderBy(t => t.DiscNo > 0 ? t.DiscNo : 1)
            .ThenBy(t => t.TrackNo > 0 ? t.TrackNo : 1)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        string currentPlayingPath = @"C:\music\Track 2.mp3";
        var vms = sorted.Select(t => new AlbumTrackItemVm(t, !string.IsNullOrEmpty(currentPlayingPath) && string.Equals(t.Path, currentPlayingPath, StringComparison.OrdinalIgnoreCase))).ToList();

        // 3 tracks: 2 left, 1 right
        int leftCount = (vms.Count + 1) / 2;
        var left = vms.Take(leftCount).ToList();
        var right = vms.Skip(leftCount).ToList();

        Assert.Equal(2, left.Count);
        Assert.Single(right);
        Assert.False(left[0].IsPlaying);
        Assert.True(left[1].IsPlaying); // Track 2 is playing
        Assert.False(right[0].IsPlaying);

        // Live playing track change to Track 3
        string newPath = @"C:\music\Track 3.mp3";
        foreach (var item in left) item.IsPlaying = string.Equals(item.Track?.Path, newPath, StringComparison.OrdinalIgnoreCase);
        foreach (var item in right) item.IsPlaying = string.Equals(item.Track?.Path, newPath, StringComparison.OrdinalIgnoreCase);

        Assert.False(left[0].IsPlaying);
        Assert.False(left[1].IsPlaying);
        Assert.True(right[0].IsPlaying); // Track 3 is playing
    }

    [Fact]
    public async Task AlbumGroup_CollectionSnapshot_CaptureSafe_UnderConcurrentWrites_DoesNotThrow()
    {
        var group = new AlbumGroup
        {
            Album = "Concurrent Album",
            Artist = "Concurrent Artist"
        };

        using var cts = new System.Threading.CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        var token = cts.Token;

        var tasks = new List<Task>();

        // 4 writers adding tracks
        for (int i = 0; i < 4; i++)
        {
            int workerId = i;
            tasks.Add(Task.Run(() =>
            {
                int counter = 0;
                do
                {
                    var item = new PlaylistItem(new Track { Title = $"T_{workerId}_{counter}", Path = $@"C:\m\{workerId}_{counter}.mp3", DurationMs = 1000 });
                    group.AddItem(item);
                    counter++;
                    Thread.Yield();
                } while (!token.IsCancellationRequested);
            }));
        }

        // 4 readers capturing snapshots
        for (int i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    var snap1 = CollectionSnapshot.Capture(group);
                    var snap2 = group.GetSnapshot();
                    var dur = group.Duration;
                    Assert.NotNull(snap1);
                    Assert.NotNull(snap2);
                    Thread.Yield();
                }
            }));
        }

        await Task.WhenAll(tasks);
        Assert.True(group.Count > 0);
    }
}

