using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

public sealed class EolePlaybackQueueAndLyricsFeatureTests
{
    private static Track CreateTrack(
        string title = "Test Song",
        string artist = "Test Artist",
        string album = "Test Album",
        int trackNo = 1,
        int year = 2023,
        long durationMs = 247000,
        string path = @"C:\Music\test.mp3",
        string? artPath = @"C:\Music\cover.jpg")
    {
        return new Track
        {
            Title = title,
            Artist = artist,
            Album = album,
            TrackNo = trackNo,
            Year = year,
            DurationMs = durationMs,
            Path = path,
            ArtPath = artPath
        };
    }

    // ---------------- 1. PlaylistItem DurationDisplay & Dynamic Remaining Time ----------------

    [Fact]
    public void PlaylistItem_DurationDisplay_ShowsStandardDuration_WhenNotPlaying()
    {
        var track = CreateTrack(durationMs: 247000); // 4 min 7 sec
        var item = new PlaylistItem(track) { IsPlaying = false };

        Assert.Equal("4:07", item.DurationDisplay);
    }

    [Fact]
    public void PlaylistItem_DurationDisplay_ShowsHours_WhenDurationExceedsOneHour()
    {
        var track = CreateTrack(durationMs: 3725000); // 1 hr 2 min 5 sec
        var item = new PlaylistItem(track) { IsPlaying = false };

        Assert.Equal("1:02:05", item.DurationDisplay);
    }

    [Fact]
    public void PlaylistItem_DurationDisplay_ShowsRemainingTime_WhenPlayingAndRemainingSet()
    {
        var track = CreateTrack(durationMs: 247000);
        var item = new PlaylistItem(track)
        {
            IsPlaying = true,
            RemainingTimeText = "-3:40"
        };

        Assert.Equal("-3:40", item.DurationDisplay);
    }

    [Fact]
    public void PlaylistItem_DurationDisplay_FallsBackToStandardDuration_WhenPlayingButRemainingNullOrEmpty()
    {
        var track = CreateTrack(durationMs: 247000);
        var item = new PlaylistItem(track)
        {
            IsPlaying = true,
            RemainingTimeText = null
        };

        Assert.Equal("4:07", item.DurationDisplay);

        item.RemainingTimeText = "";
        Assert.Equal("4:07", item.DurationDisplay);
    }

    [Fact]
    public void PlaylistItem_NotifiesPropertyChanged_ForDurationDisplay_WhenStateChanges()
    {
        var track = CreateTrack(durationMs: 247000);
        var item = new PlaylistItem(track);
        var changedProps = new List<string>();

        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        item.IsPlaying = true;
        Assert.Contains(nameof(PlaylistItem.IsPlaying), changedProps);
        Assert.Contains(nameof(PlaylistItem.DurationDisplay), changedProps);

        changedProps.Clear();
        item.RemainingTimeText = "-2:15";
        Assert.Contains(nameof(PlaylistItem.RemainingTimeText), changedProps);
        Assert.Contains(nameof(PlaylistItem.DurationDisplay), changedProps);
    }

    // ---------------- 2. PlaylistGroupBuilder Multi-Album Grouping ----------------

    [Fact]
    public void PlaylistGroupBuilder_BuildGroups_EmptyPlaylist_ReturnsEmptyList()
    {
        var pl = new Playlist("Empty Playlist");
        var groups = PlaylistGroupBuilder.BuildGroups(pl);

        Assert.NotNull(groups);
        Assert.Empty(groups);
    }

    [Fact]
    public void PlaylistGroupBuilder_BuildGroups_MultipleAlbums_GroupsConsecutiveTracks()
    {
        var pl = new Playlist("Multi-Album Playlist");
        var t1 = CreateTrack(title: "Track 1", album: "Album A", artist: "Artist A", year: 2020, durationMs: 180000, path: @"C:\Music\a1.mp3");
        var t2 = CreateTrack(title: "Track 2", album: "Album A", artist: "Artist A", year: 2020, durationMs: 200000, path: @"C:\Music\a2.mp3");
        var t3 = CreateTrack(title: "Track 3", album: "Album B", artist: "Artist B", year: 2021, durationMs: 210000, path: @"C:\Music\b1.mp3");

        pl.Items.Add(new PlaylistItem(t1));
        pl.Items.Add(new PlaylistItem(t2));
        pl.Items.Add(new PlaylistItem(t3));

        var groups = PlaylistGroupBuilder.BuildGroups(pl);

        Assert.Equal(2, groups.Count);

        // Group 1 (Album A)
        Assert.Equal("Album A", groups[0].Album);
        Assert.Equal("Artist A", groups[0].Artist);
        Assert.Equal(2020, groups[0].Year);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(TimeSpan.FromMilliseconds(380000), groups[0].Duration);

        // Group 2 (Album B)
        Assert.Equal("Album B", groups[1].Album);
        Assert.Equal("Artist B", groups[1].Artist);
        Assert.Equal(2021, groups[1].Year);
        Assert.Single(groups[1].Items);
        Assert.Equal(TimeSpan.FromMilliseconds(210000), groups[1].Duration);
    }

    [Fact]
    public void PlaylistGroupBuilder_BuildGroups_InterleavedAlbums_CreatesSeparateGroupsPerRun()
    {
        var pl = new Playlist("Interleaved Playlist");
        var t1 = CreateTrack(title: "Track A1", album: "Album A", path: @"C:\Music\a1.mp3");
        var t2 = CreateTrack(title: "Track B1", album: "Album B", path: @"C:\Music\b1.mp3");
        var t3 = CreateTrack(title: "Track A2", album: "Album A", path: @"C:\Music\a2.mp3");

        pl.Items.Add(new PlaylistItem(t1));
        pl.Items.Add(new PlaylistItem(t2));
        pl.Items.Add(new PlaylistItem(t3));

        var groups = PlaylistGroupBuilder.BuildGroups(pl);

        Assert.Equal(3, groups.Count);
        Assert.Equal("Album A", groups[0].Album);
        Assert.Equal("Album B", groups[1].Album);
        Assert.Equal("Album A", groups[2].Album);
    }

    [Fact]
    public void PlaylistGroupBuilder_BuildGroups_MissingMetadata_UsesFallbackStrings()
    {
        var pl = new Playlist("Missing Meta Playlist");
        var track = new Track
        {
            Title = "No Meta",
            Album = "",
            Artist = "",
            Path = @"C:\Music\test.mp3"
        };
        pl.Items.Add(new PlaylistItem(track));

        var groups = PlaylistGroupBuilder.BuildGroups(pl);

        Assert.Single(groups);
        Assert.Equal("(앨범 없음)", groups[0].Album);
        Assert.Equal("(아티스트 없음)", groups[0].Artist);
    }

    // ---------------- 3. AlbumGroup Duration & Year Formatting ----------------

    [Theory]
    [InlineData(1, 17, 44, "1 h 17 min 44 s")]
    [InlineData(2, 0, 5, "2 h 0 min 5 s")]
    [InlineData(0, 3, 32, "3 min 32 s")]
    [InlineData(0, 0, 45, "45 s")]
    [InlineData(0, 0, 0, "0 s")]
    public void AlbumGroup_FormatEoleDuration_FormatsCorrectly(int hours, int minutes, int seconds, string expected)
    {
        var ts = new TimeSpan(hours, minutes, seconds);
        var result = AlbumGroup.FormatEoleDuration(ts);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void AlbumGroup_YearFormatted_ReturnsYearStringOrEmpty()
    {
        var g1 = new AlbumGroup { Year = 2023 };
        var g2 = new AlbumGroup { Year = 0 };

        Assert.Equal("2023", g1.YearFormatted);
        Assert.Equal("", g2.YearFormatted);
    }

    // ---------------- 4. Lyrics Settings & Candidate Patterns ----------------

    [Fact]
    public void LyricsSettings_DefaultPatterns_ContainStandardTokens()
    {
        var settings = new LyricsSettings();

        Assert.NotNull(settings.FilePatterns);
        Assert.Equal(3, settings.FilePatterns.Count);
        Assert.Contains("%filename%.lrc", settings.FilePatterns);
        Assert.Contains("%artist% - %title%.lrc", settings.FilePatterns);
        Assert.Contains("%title%.lrc", settings.FilePatterns);
    }

    [Fact]
    public void LyricsFinder_BuildCandidates_ReplacesAllSupportedTokens()
    {
        var track = CreateTrack(
            title: "Celebrity",
            artist: "IU",
            album: "LILAC",
            path: @"C:\Music\01 - Celebrity.flac"
        );

        var settings = new AppSettings();
        settings.Lyrics.FilePatterns = new List<string>
        {
            "%filename%.lrc",
            "%artist% - %title%.lrc",
            "%album% - %title%.lrc",
            "%title%.lrc"
        };

        var candidates = LyricsFinder.BuildCandidates(track, settings);

        Assert.Contains(@"C:\Music\01 - Celebrity.lrc", candidates);
        Assert.Contains(@"C:\Music\IU - Celebrity.lrc", candidates);
        Assert.Contains(@"C:\Music\LILAC - Celebrity.lrc", candidates);
        Assert.Contains(@"C:\Music\Celebrity.lrc", candidates);
    }

    [Fact]
    public void LyricsFinder_BuildCandidates_CustomSearchDirectories_IncludesSubfolders()
    {
        var track = CreateTrack(
            title: "Song",
            artist: "Singer",
            path: @"C:\Music\track.mp3"
        );

        var settings = new AppSettings();
        settings.Lyrics.FilePatterns = new List<string> { "%filename%.lrc" };
        settings.Lyrics.SearchFolders = new List<string> { @"D:\LyricsStore" };

        var candidates = LyricsFinder.BuildCandidates(track, settings);

        Assert.Contains(@"C:\Music\track.lrc", candidates);
        Assert.Contains(@"D:\LyricsStore\track.lrc", candidates);
    }

    // ---------------- 5. Playback Queue IsPlaying Synchronization ----------------

    [Fact]
    public void PlaybackQueue_MultipleItems_SyncsIsPlayingState()
    {
        var pl = new Playlist("Queue Sync Test");
        var t1 = CreateTrack(title: "Track 1", path: @"C:\Music\1.mp3");
        var t2 = CreateTrack(title: "Track 2", path: @"C:\Music\2.mp3");
        var t3 = CreateTrack(title: "Track 3", path: @"C:\Music\3.mp3");

        var item1 = new PlaylistItem(t1);
        var item2 = new PlaylistItem(t2);
        var item3 = new PlaylistItem(t3);

        pl.Items.Add(item1);
        pl.Items.Add(item2);
        pl.Items.Add(item3);

        // Simulate playing track 2
        var currentPlayingPath = t2.Path;
        foreach (var pi in pl.Items)
        {
            pi.IsPlaying = string.Equals(pi.Track.Path, currentPlayingPath, StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(item1.IsPlaying);
        Assert.True(item2.IsPlaying);
        Assert.False(item3.IsPlaying);

        // Simulate switching to track 3
        currentPlayingPath = t3.Path;
        foreach (var pi in pl.Items)
        {
            pi.IsPlaying = string.Equals(pi.Track.Path, currentPlayingPath, StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(item1.IsPlaying);
        Assert.False(item2.IsPlaying);
        Assert.True(item3.IsPlaying);
    }

    [Fact]
    public void AlbumGroup_ImplementsIReadOnlyList_AllowsDirectIndexingAndEnumeration()
    {
        var group = new AlbumGroup();
        var t1 = CreateTrack(title: "Song 1", path: @"C:\Music\s1.mp3");
        var t2 = CreateTrack(title: "Song 2", path: @"C:\Music\s2.mp3");
        var pi1 = new PlaylistItem(t1);
        var pi2 = new PlaylistItem(t2);

        group.Items.Add(pi1);
        group.Items.Add(pi2);

        // Test IReadOnlyList properties
        IReadOnlyList<PlaylistItem> readOnlyList = group;
        Assert.Equal(2, readOnlyList.Count);
        Assert.Same(pi1, readOnlyList[0]);
        Assert.Same(pi2, readOnlyList[1]);

        // Test IEnumerable enumeration
        var enumerated = new List<PlaylistItem>();
        foreach (var item in group)
        {
            enumerated.Add(item);
        }
        Assert.Equal(2, enumerated.Count);
        Assert.Same(pi1, enumerated[0]);
        Assert.Same(pi2, enumerated[1]);
    }

    [Fact]
    public void PlaylistGroupBuilder_BuildGroups_NullOrEmpty_ReturnsEmptyList()
    {
        var nullResult = PlaylistGroupBuilder.BuildGroups((Playlist?)null);
        Assert.NotNull(nullResult);
        Assert.Empty(nullResult);

        var emptyPl = new Playlist("Empty");
        var emptyResult = PlaylistGroupBuilder.BuildGroups(emptyPl);
        Assert.NotNull(emptyResult);
        Assert.Empty(emptyResult);
    }

    [Fact]
    public void AlbumGroup_AddItem_And_InvalidateDuration_CalculatesDurationAccurately()
    {
        var group = new AlbumGroup { Album = "Test Album", Artist = "Test Artist", Year = 2024 };
        Assert.Equal(TimeSpan.Zero, group.Duration);

        var t1 = CreateTrack(durationMs: 120000); // 2 min
        var t2 = CreateTrack(durationMs: 180000); // 3 min

        group.AddItem(new PlaylistItem(t1));
        Assert.Equal(TimeSpan.FromMinutes(2), group.Duration);

        group.AddItem(new PlaylistItem(t2));
        Assert.Equal(TimeSpan.FromMinutes(5), group.Duration);
        Assert.Equal("5 min 0 s", group.DurationFormatted);

        // Invalidate and re-query
        group.InvalidateDuration();
        Assert.Equal(TimeSpan.FromMinutes(5), group.Duration);
    }

    [Fact]
    public void AlbumGroup_Info_FormatsCleanlyWithVariousCombinations()
    {
        var g1 = new AlbumGroup { Artist = "IU", Year = 2021 };
        g1.AddItem(new PlaylistItem(CreateTrack(durationMs: 240000)));
        Assert.Equal("IU  •  2021  •  1곡  •  4 min 0 s", g1.Info);

        var g2 = new AlbumGroup { Artist = "Unknown", Year = 0 };
        g2.AddItem(new PlaylistItem(CreateTrack(durationMs: 30000)));
        Assert.Equal("Unknown  •  1곡  •  30 s", g2.Info);
    }

    [Fact]
    public void PlaylistGroupBuilder_BuildGroupsFromItems_ClustersConsecutiveAlbums()
    {
        var t1 = CreateTrack(album: "Album 1", artist: "Artist 1");
        var t2 = CreateTrack(album: "Album 1", artist: "Artist 1");
        var t3 = CreateTrack(album: "Album 2", artist: "Artist 2");

        var items = new List<PlaylistItem> { new(t1), new(t2), new(t3) };

        var groups = PlaylistGroupBuilder.BuildGroupsFromItems(items);
        Assert.Equal(2, groups.Count);
        Assert.Equal("Album 1", groups[0].Album);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal("Album 2", groups[1].Album);
        Assert.Single(groups[1].Items);

        // Null safety
        Assert.Empty(PlaylistGroupBuilder.BuildGroupsFromItems(null));
    }

    [Fact]
    public void PlaylistGroupBuilder_LargePlaylist_HighPerformanceClustering()
    {
        // 5,000 items grouped into 50 albums of 100 tracks each
        var items = new List<PlaylistItem>(5000);
        for (int albumIdx = 1; albumIdx <= 50; albumIdx++)
        {
            for (int trackIdx = 1; trackIdx <= 100; trackIdx++)
            {
                var track = CreateTrack(
                    title: $"Track {trackIdx}",
                    album: $"Album {albumIdx}",
                    artist: $"Artist {albumIdx}",
                    year: 2000 + albumIdx,
                    durationMs: 180000
                );
                items.Add(new PlaylistItem(track));
            }
        }

        var groups = PlaylistGroupBuilder.BuildGroupsFromItems(items);
        Assert.Equal(50, groups.Count);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal($"Album {i + 1}", groups[i].Album);
            Assert.Equal(100, groups[i].Count);
            Assert.Equal(TimeSpan.FromMilliseconds(100 * 180000), groups[i].Duration);
        }
    }

    [Fact]
    public void PlaylistItem_UpdateQueueIndex_MonotonicVersioning_RejectsStaleUpdates()
    {
        var track = CreateTrack();
        var item = new PlaylistItem(track);

        var changedProps = new List<string?>();
        item.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        // Initial version 1 -> index 5
        bool updated1 = item.UpdateQueueIndex(5, 1);
        Assert.True(updated1);
        Assert.Equal(5, item.QueueIndex);
        Assert.Contains(nameof(PlaylistItem.QueueIndex), changedProps);

        changedProps.Clear();

        // Stale version 0 -> should be rejected
        bool updatedStale = item.UpdateQueueIndex(2, 0);
        Assert.False(updatedStale);
        Assert.Equal(5, item.QueueIndex);
        Assert.Empty(changedProps);

        // Newer version 2 -> index 10
        bool updated2 = item.UpdateQueueIndex(10, 2);
        Assert.True(updated2);
        Assert.Equal(10, item.QueueIndex);
        Assert.Contains(nameof(PlaylistItem.QueueIndex), changedProps);
    }

    [Fact]
    public void PlaylistItem_ConcurrentPropertyModifications_MaintainsConsistentState()
    {
        var track = CreateTrack();
        var item = new PlaylistItem(track);

        Parallel.For(0, 100, i =>
        {
            item.IsPlaying = (i % 2 == 0);
            item.QueueIndex = i;
            item.RemainingTimeText = $"-{i}:00";
            var durDisp = item.DurationDisplay;
            Assert.NotNull(durDisp);
        });

        Assert.True(item.QueueIndex >= 0);
    }

    [Fact]
    public void Playlist_TotalDuration_RecalculatesOnCollectionChange()
    {
        var pl = new Playlist("Duration Test");
        var changedProps = new List<string?>();
        pl.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        Assert.Equal(TimeSpan.Zero, pl.TotalDuration);

        var item1 = new PlaylistItem(CreateTrack(durationMs: 60000));
        var item2 = new PlaylistItem(CreateTrack(durationMs: 120000));

        pl.Items.Add(item1);
        Assert.Equal(TimeSpan.FromMinutes(1), pl.TotalDuration);
        Assert.Contains(nameof(Playlist.TotalDuration), changedProps);

        changedProps.Clear();
        pl.Items.Add(item2);
        Assert.Equal(TimeSpan.FromMinutes(3), pl.TotalDuration);
        Assert.Contains(nameof(Playlist.TotalDuration), changedProps);

        changedProps.Clear();
        pl.Items.Remove(item1);
        Assert.Equal(TimeSpan.FromMinutes(2), pl.TotalDuration);
        Assert.Contains(nameof(Playlist.TotalDuration), changedProps);
    }

    [Fact]
    public void AlbumGroup_DirectItemsAddWithoutAddItem_UpdatesDurationAccurately()
    {
        var group = new AlbumGroup { Album = "Direct Add Album", Artist = "Direct Artist" };
        var t1 = CreateTrack(durationMs: 60000);
        var t2 = CreateTrack(durationMs: 120000);

        group.Items.Add(new PlaylistItem(t1));
        Assert.Equal(TimeSpan.FromMinutes(1), group.Duration);

        // Access duration so it caches
        var cached = group.Duration;
        Assert.Equal(TimeSpan.FromMinutes(1), cached);

        // Directly add to Items (bypassing AddItem)
        group.Items.Add(new PlaylistItem(t2));

        // Duration should immediately recalculate and not return stale cache
        Assert.Equal(TimeSpan.FromMinutes(3), group.Duration);
        Assert.Equal("3 min 0 s", group.DurationFormatted);
    }

    [Fact]
    public void AlbumGroup_DirectItemsRemoveWithoutInvalidate_UpdatesDurationAccurately()
    {
        var group = new AlbumGroup { Album = "Remove Album", Artist = "Artist" };
        var t1 = CreateTrack(durationMs: 60000);
        var t2 = CreateTrack(durationMs: 120000);
        var pi1 = new PlaylistItem(t1);
        var pi2 = new PlaylistItem(t2);

        group.AddItem(pi1);
        group.AddItem(pi2);
        Assert.Equal(TimeSpan.FromMinutes(3), group.Duration);

        // Directly remove item from Items list
        group.Items.Remove(pi1);
        Assert.Equal(TimeSpan.FromMinutes(2), group.Duration);
    }

    [Fact]
    public async Task Playlist_TotalDuration_ConcurrentMutations_IsSafe()
    {
        var pl = new Playlist("Concurrent Duration Test");
        for (int i = 0; i < 50; i++)
        {
            pl.Items.Add(new PlaylistItem(CreateTrack(durationMs: 1000)));
        }

        bool running = true;
        var readerTask = Task.Run(() =>
        {
            while (running)
            {
                var dur = pl.TotalDuration;
                Assert.True(dur >= TimeSpan.Zero);
            }
        });

        for (int i = 0; i < 50; i++)
        {
            pl.Items.Add(new PlaylistItem(CreateTrack(durationMs: 2000)));
            if (pl.Items.Count > 10) pl.Items.RemoveAt(0);
        }

        running = false;
        await readerTask;
    }

    [Fact]
    public void PlaylistItem_PureDomainEventEmission_RaisesStandardPropertyChanged()
    {
        var track = CreateTrack();
        var item = new PlaylistItem(track);

        var events = new List<string?>();
        item.PropertyChanged += (_, e) => events.Add(e.PropertyName);

        item.IsPlaying = true;
        item.RemainingTimeText = "-1:00";
        item.QueueIndex = 3;

        Assert.Contains(nameof(PlaylistItem.IsPlaying), events);
        Assert.Contains(nameof(PlaylistItem.DurationDisplay), events);
        Assert.Contains(nameof(PlaylistItem.RemainingTimeText), events);
        Assert.Contains(nameof(PlaylistItem.QueueIndex), events);
    }

    [Fact]
    public void AlbumGroup_InPlaceItemReplacementWithoutCountChange_RecalculatesDurationAccurately()
    {
        var group = new AlbumGroup { Album = "In Place Test", Artist = "Artist" };
        var t1 = CreateTrack(durationMs: 60000); // 1 min
        var t2 = CreateTrack(durationMs: 120000); // 2 min
        var pi1 = new PlaylistItem(t1);
        var pi2 = new PlaylistItem(t2);

        group.AddItem(pi1);
        group.AddItem(pi2);
        Assert.Equal(TimeSpan.FromMinutes(3), group.Duration);

        // Replace pi1 (1 min) in-place with a 5-minute track (300000ms) without changing Count
        var t3 = CreateTrack(durationMs: 300000);
        group.Items[0] = new PlaylistItem(t3);

        Assert.Equal(2, group.Count);
        // Duration must accurately reflect 5 + 2 = 7 minutes
        Assert.Equal(TimeSpan.FromMinutes(7), group.Duration);
        Assert.Equal("7 min 0 s", group.DurationFormatted);
    }
}
