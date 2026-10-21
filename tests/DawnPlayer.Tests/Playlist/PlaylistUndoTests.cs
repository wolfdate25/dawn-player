using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests.PlaylistTests;

public class PlaylistUndoTests
{
    private static Track MakeTrack(string title, string artist = "Artist", string album = "Album")
    {
        return new Track
        {
            Path = $"C:\\Music\\{artist} - {album} - {title}.mp3",
            Title = title,
            Artist = artist,
            Album = album,
            DurationMs = 180000
        };
    }

    private static Playlist CreateSamplePlaylist(string name, int trackCount)
    {
        var pl = new Playlist(name);
        for (int i = 1; i <= trackCount; i++)
        {
            pl.Items.Add(new PlaylistItem(MakeTrack($"Track {i}")));
        }
        return pl;
    }

    [Fact]
    public void PlaylistUndo_RemoveAndRestore_RestoresExactItems()
    {
        var undoManager = new PlaylistUndoManager();
        var pl = CreateSamplePlaylist("My Playlist", 5);
        var originalItems = pl.Items.ToList();

        // Record state before deletion
        undoManager.RecordSnapshot(pl, "Remove Track 2");
        pl.Items.RemoveAt(1); // remove Track 2
        Assert.Equal(4, pl.Items.Count);
        Assert.True(undoManager.CanUndo(pl));
        Assert.False(undoManager.CanRedo(pl));

        // Undo
        bool undone = undoManager.Undo(pl);
        Assert.True(undone);
        Assert.Equal(5, pl.Items.Count);
        Assert.True(undoManager.CanRedo(pl));

        // Verify items match original instances and order
        for (int i = 0; i < originalItems.Count; i++)
        {
            Assert.Same(originalItems[i], pl.Items[i]);
            Assert.Equal(originalItems[i].Track.Title, pl.Items[i].Track.Title);
        }

        // Redo
        bool redone = undoManager.Redo(pl);
        Assert.True(redone);
        Assert.Equal(4, pl.Items.Count);
        Assert.Equal("Track 1", pl.Items[0].Track.Title);
        Assert.Equal("Track 3", pl.Items[1].Track.Title);
    }

    [Fact]
    public void PlaylistUndo_MultipleSteps_ChainsCorrectly()
    {
        var undoManager = new PlaylistUndoManager();
        var pl = CreateSamplePlaylist("Step Playlist", 3); // Track 1, 2, 3

        // Step 1: Add Track 4
        undoManager.RecordSnapshot(pl, "Add Track 4");
        pl.Items.Add(new PlaylistItem(MakeTrack("Track 4")));

        // Step 2: Move Track 4 to top
        undoManager.RecordSnapshot(pl, "Move Track 4");
        pl.Items.Move(3, 0); // [Track 4, Track 1, Track 2, Track 3]

        // Step 3: Remove Track 2
        undoManager.RecordSnapshot(pl, "Remove Track 2");
        pl.Items.RemoveAt(2); // [Track 4, Track 1, Track 3]

        Assert.Equal(3, pl.Items.Count);
        Assert.Equal("Track 4", pl.Items[0].Track.Title);
        Assert.Equal("Track 1", pl.Items[1].Track.Title);
        Assert.Equal("Track 3", pl.Items[2].Track.Title);

        // Undo Step 3 -> back to 4 items [Track 4, Track 1, Track 2, Track 3]
        Assert.True(undoManager.Undo(pl));
        Assert.Equal(4, pl.Items.Count);
        Assert.Equal("Track 4", pl.Items[0].Track.Title);
        Assert.Equal("Track 2", pl.Items[2].Track.Title);

        // Undo Step 2 -> back to [Track 1, Track 2, Track 3, Track 4]
        Assert.True(undoManager.Undo(pl));
        Assert.Equal(4, pl.Items.Count);
        Assert.Equal("Track 1", pl.Items[0].Track.Title);
        Assert.Equal("Track 4", pl.Items[3].Track.Title);

        // Undo Step 1 -> back to original 3 items [Track 1, Track 2, Track 3]
        Assert.True(undoManager.Undo(pl));
        Assert.Equal(3, pl.Items.Count);
        Assert.Equal("Track 1", pl.Items[0].Track.Title);
        Assert.Equal("Track 2", pl.Items[1].Track.Title);
        Assert.Equal("Track 3", pl.Items[2].Track.Title);
        Assert.False(undoManager.CanUndo(pl));

        // Redo Step 1 -> [Track 1, Track 2, Track 3, Track 4]
        Assert.True(undoManager.Redo(pl));
        Assert.Equal(4, pl.Items.Count);
        Assert.Equal("Track 4", pl.Items[3].Track.Title);
    }

    [Fact]
    public void PlaylistUndo_NewActionClearsRedo_StandardContract()
    {
        var undoManager = new PlaylistUndoManager();
        var pl = CreateSamplePlaylist("Branch Playlist", 3);

        undoManager.RecordSnapshot(pl, "Action 1");
        pl.Items.RemoveAt(0);

        undoManager.RecordSnapshot(pl, "Action 2");
        pl.Items.RemoveAt(0);

        Assert.Single(pl.Items); // 1 item left

        // Undo once
        undoManager.Undo(pl);
        Assert.Equal(2, pl.Items.Count);
        Assert.True(undoManager.CanRedo(pl));

        // New modification occurs
        undoManager.RecordSnapshot(pl, "Branch Action");
        pl.Items.Add(new PlaylistItem(MakeTrack("New Branch Track")));

        // Redo stack must be cleared!
        Assert.False(undoManager.CanRedo(pl));
    }

    [Fact]
    public void PlaylistUndo_MaxCapacity_DropsOldestSnapshots()
    {
        var undoManager = new PlaylistUndoManager(maxHistory: 5);
        var pl = CreateSamplePlaylist("Capacity Playlist", 1);

        for (int i = 1; i <= 10; i++)
        {
            undoManager.RecordSnapshot(pl, $"Add {i}");
            pl.Items.Add(new PlaylistItem(MakeTrack($"Track Added {i}")));
        }

        // We can only undo 5 times because maxHistory = 5
        int undoCount = 0;
        while (undoManager.CanUndo(pl))
        {
            undoManager.Undo(pl);
            undoCount++;
        }

        Assert.Equal(5, undoCount);
        Assert.False(undoManager.CanUndo(pl));
    }

    [Fact]
    public void PlaylistUndo_SmartPlaylist_Ignored()
    {
        var undoManager = new PlaylistUndoManager();
        var smartPl = new Playlist("Smart One") { IsSmart = true };
        smartPl.Items.Add(new PlaylistItem(MakeTrack("Smart Track")));

        undoManager.RecordSnapshot(smartPl, "Attempt on Smart");
        Assert.False(undoManager.CanUndo(smartPl));
        Assert.False(undoManager.Undo(smartPl));
    }

    [Fact]
    public void PlaylistManager_UndoRedo_Integration()
    {
        var manager = new PlaylistManager(new DawnPlayer.Core.Library.MusicLibrary());
        var pl = manager.CreatePlaylist("Integration PL");

        var tracks = new[]
        {
            MakeTrack("Song A"),
            MakeTrack("Song B"),
            MakeTrack("Song C")
        };

        manager.AddTracks(pl, tracks);
        Assert.Equal(3, pl.Items.Count);
        Assert.True(manager.CanUndo(pl));

        // Sort reverse
        manager.Sort(pl, PlaylistSort.Reverse);
        Assert.Equal("Song C", pl.Items[0].Track.Title);
        Assert.Equal("Song A", pl.Items[2].Track.Title);

        // Undo sort
        Assert.True(manager.Undo(pl));
        Assert.Equal("Song A", pl.Items[0].Track.Title);
        Assert.Equal("Song C", pl.Items[2].Track.Title);

        // Redo sort
        Assert.True(manager.Redo(pl));
        Assert.Equal("Song C", pl.Items[0].Track.Title);

        // Remove item
        manager.RemoveItems(pl, new[] { pl.Items[0] });
        Assert.Equal(2, pl.Items.Count);
        Assert.Equal("Song B", pl.Items[0].Track.Title);

        // Undo removal
        Assert.True(manager.Undo(pl));
        Assert.Equal(3, pl.Items.Count);
        Assert.Equal("Song C", pl.Items[0].Track.Title);
    }
}
