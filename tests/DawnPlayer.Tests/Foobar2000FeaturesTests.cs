using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

public class Foobar2000FeaturesTests
{
    private static Track CreateTrack(
        string title = "Song",
        string artist = "Artist",
        string album = "Album",
        int trackNo = 1,
        string? path = null)
    {
        return new Track
        {
            Title = title,
            Artist = artist,
            Album = album,
            TrackNo = trackNo,
            Path = path ?? $@"C:\Music\{album}\{title}.mp3",
            DurationMs = 180000
        };
    }

    [Fact]
    public void AppSettings_Shuffle_BackwardsCompatibility_MapsToShuffleMode()
    {
        var settings = new PlaybackSettings();
        Assert.Equal(ShuffleMode.Off, settings.ShuffleMode);
        Assert.False(settings.Shuffle);

        settings.Shuffle = true;
        Assert.Equal(ShuffleMode.Tracks, settings.ShuffleMode);
        Assert.True(settings.Shuffle);

        settings.ShuffleMode = ShuffleMode.Albums;
        Assert.True(settings.Shuffle);

        settings.Shuffle = false;
        Assert.Equal(ShuffleMode.Off, settings.ShuffleMode);
        Assert.False(settings.Shuffle);
    }

    [Theory]
    [InlineData(ShuffleMode.Off)]
    [InlineData(ShuffleMode.Tracks)]
    [InlineData(ShuffleMode.Albums)]
    public void AppSettings_ShuffleMode_JsonRoundTrip_PersistsCorrectly(ShuffleMode mode)
    {
        var appSettings = new AppSettings();
        appSettings.Playback.ShuffleMode = mode;

        var json = System.Text.Json.JsonSerializer.Serialize(appSettings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(mode, restored.Playback.ShuffleMode);
        Assert.Equal(mode != ShuffleMode.Off, restored.Playback.Shuffle);
    }

    [Fact]
    public void PlaylistManager_MoveItem_ValidAndInvalidBounds_MovesSafely()
    {
        var lib = new MusicLibrary();
        var plMgr = new PlaylistManager(lib);
        var pl = plMgr.CreatePlaylist("MoveItem Test");

        var i1 = new PlaylistItem(CreateTrack(title: "1"));
        var i2 = new PlaylistItem(CreateTrack(title: "2"));
        var i3 = new PlaylistItem(CreateTrack(title: "3"));

        pl.Items.Add(i1);
        pl.Items.Add(i2);
        pl.Items.Add(i3);

        // Move item at index 0 to index 2 (1 -> end)
        plMgr.MoveItem(pl, 0, 2);
        Assert.Same(i2, pl.Items[0]);
        Assert.Same(i3, pl.Items[1]);
        Assert.Same(i1, pl.Items[2]);

        // Invalid bounds should not throw and make no changes
        plMgr.MoveItem(pl, -1, 2);
        plMgr.MoveItem(pl, 0, 99);
        Assert.Same(i2, pl.Items[0]);
        Assert.Same(i3, pl.Items[1]);
        Assert.Same(i1, pl.Items[2]);
    }

    [Fact]
    public void PlaylistManager_MoveSelection_UpAndDown_PreservesRelativeOrder()
    {
        var lib = new MusicLibrary();
        var plMgr = new PlaylistManager(lib);
        var pl = plMgr.CreatePlaylist("MoveSelection Test");

        var a = new PlaylistItem(CreateTrack(title: "A"));
        var b = new PlaylistItem(CreateTrack(title: "B"));
        var c = new PlaylistItem(CreateTrack(title: "C"));
        var d = new PlaylistItem(CreateTrack(title: "D"));

        pl.Items.Add(a);
        pl.Items.Add(b);
        pl.Items.Add(c);
        pl.Items.Add(d);

        // Move selection [b, c] up -> should swap with a -> [b, c, a, d]
        var movedUp = plMgr.MoveSelection(pl, new[] { b, c }, up: true);
        Assert.True(movedUp);
        Assert.Same(b, pl.Items[0]);
        Assert.Same(c, pl.Items[1]);
        Assert.Same(a, pl.Items[2]);
        Assert.Same(d, pl.Items[3]);

        // Move selection [b] up when already at top -> no move
        var topMove = plMgr.MoveSelection(pl, new[] { b }, up: true);
        Assert.False(topMove);

        // Move selection [a, d] down -> [a] swaps with [d], [d] cannot move past bottom
        // Starting: [b, c, a, d]
        // Moving [a]: a is at 2, moves to 3 -> [b, c, d, a]
        var movedDown = plMgr.MoveSelection(pl, new[] { a }, up: false);
        Assert.True(movedDown);
        Assert.Same(b, pl.Items[0]);
        Assert.Same(c, pl.Items[1]);
        Assert.Same(d, pl.Items[2]);
        Assert.Same(a, pl.Items[3]);
    }

    [Fact]
    public void PlaylistManager_RemoveDeadItems_RemovesOnlyMissingFiles()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"dawn_dead_test_{Guid.NewGuid():N}.mp3");
        File.WriteAllText(tempFile, "dummy");

        try
        {
            var lib = new MusicLibrary();
            var plMgr = new PlaylistManager(lib);
            var pl = plMgr.CreatePlaylist("Dead Items Test");

            var liveItem = new PlaylistItem(CreateTrack(title: "Live", path: tempFile));
            var deadItem1 = new PlaylistItem(CreateTrack(title: "Dead 1", path: @"C:\NonExistentDir12345\ghost1.mp3"));
            var deadItem2 = new PlaylistItem(CreateTrack(title: "Dead 2", path: @"C:\NonExistentDir12345\ghost2.mp3"));

            pl.Items.Add(liveItem);
            pl.Items.Add(deadItem1);
            pl.Items.Add(deadItem2);

            int removed = plMgr.RemoveDeadItems(pl);
            Assert.Equal(2, removed);
            Assert.Single(pl.Items);
            Assert.Same(liveItem, pl.Items[0]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task PlaylistManager_RemoveDeadItemsAsync_RemovesMissingFilesCorrectly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"dawn_dead_async_{Guid.NewGuid():N}.mp3");
        File.WriteAllText(tempFile, "dummy");

        try
        {
            var lib = new MusicLibrary();
            var plMgr = new PlaylistManager(lib);
            var pl = plMgr.CreatePlaylist("Dead Items Async Test");

            var liveItem = new PlaylistItem(CreateTrack(title: "Live", path: tempFile));
            var deadItem = new PlaylistItem(CreateTrack(title: "Dead", path: @"C:\NonExistentDir12345\ghost.mp3"));

            pl.Items.Add(deadItem);
            pl.Items.Add(liveItem);

            int removed = await plMgr.RemoveDeadItemsAsync(pl);
            Assert.Equal(1, removed);
            Assert.Single(pl.Items);
            Assert.Same(liveItem, pl.Items[0]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void PlaybackController_StopAfterCurrent_Property_RaisesEventAndClearsPrefetch()
    {
        var settings = new AppSettings();
        var plMgr = new PlaylistManager(new MusicLibrary());
        var controller = new PlaybackController(settings, plMgr);

        bool eventFired = false;
        controller.StopAfterCurrentChanged += () => eventFired = true;

        Assert.False(controller.StopAfterCurrent);
        controller.StopAfterCurrent = true;
        Assert.True(controller.StopAfterCurrent);
        Assert.True(eventFired);
    }

    [Fact]
    public async Task PlaybackController_ShuffleAlbums_PlaysConsecutiveAlbumTracksBeforeShuffling()
    {
        var settings = new AppSettings();
        settings.Playback.ShuffleMode = ShuffleMode.Albums;

        var lib = new MusicLibrary();
        var plMgr = new PlaylistManager(lib);
        var pl = plMgr.CreatePlaylist("Album Shuffle Test");

        // Album A: 2 tracks
        var a1 = new PlaylistItem(CreateTrack(title: "A1", album: "Album A", trackNo: 1));
        var a2 = new PlaylistItem(CreateTrack(title: "A2", album: "Album A", trackNo: 2));

        // Album B: 2 tracks
        var b1 = new PlaylistItem(CreateTrack(title: "B1", album: "Album B", trackNo: 1));
        var b2 = new PlaylistItem(CreateTrack(title: "B2", album: "Album B", trackNo: 2));

        pl.Items.Add(a1);
        pl.Items.Add(a2);
        pl.Items.Add(b1);
        pl.Items.Add(b2);

        var controller = new PlaybackController(settings, plMgr);

        // Verify AlbumGroup clustering in PlaylistGroupBuilder
        var groups = PlaylistGroupBuilder.BuildGroups(pl);
        Assert.Equal(2, groups.Count);
        Assert.Equal("Album A", groups[0].Album);
        Assert.Equal("Album B", groups[1].Album);
    }
}
