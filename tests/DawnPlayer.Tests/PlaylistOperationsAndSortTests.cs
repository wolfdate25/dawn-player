using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Comprehensive unit tests for PlaylistManager operations, name collisions, sorting, and deduplication:
/// 1. UniqueName generation on collision ("재생목록", "재생목록 2", "재생목록 3").
/// 2. Sorting across Title, Artist, Album, TrackNo, Path, and Reverse.
/// 3. RemoveDuplicates case-insensitive path deduplication.
/// 4. Insertion at index, item removal, and event notification.
/// </summary>
public class PlaylistOperationsAndSortTests : IDisposable
{
    private readonly MusicLibrary _library;
    private readonly PlaylistManager _manager;

    public PlaylistOperationsAndSortTests()
    {
        _library = new MusicLibrary();
        _manager = new PlaylistManager(_library);
    }

    public void Dispose()
    {
        _library.Dispose();
    }

    private static PlaylistItem CreateItem(
        string title, string artist, string album, int discNo, int trackNo, string path)
    {
        return new PlaylistItem(new Track
        {
            Title = title,
            Artist = artist,
            Album = album,
            DiscNo = discNo,
            TrackNo = trackNo,
            Path = path
        });
    }

    #region 1. UniqueName Collision Tests

    [Fact]
    public void CreatePlaylist_GeneratesUniqueNamesOnCollision()
    {
        // First playlist created by default or explicit null name
        var pl1 = _manager.CreatePlaylist();
        Assert.Equal("재생목록", pl1.Name);

        var pl2 = _manager.CreatePlaylist();
        Assert.Equal("재생목록 2", pl2.Name);

        var pl3 = _manager.CreatePlaylist();
        Assert.Equal("재생목록 3", pl3.Name);

        // Remove pl2 -> creating another should reuse available "재생목록 2"
        _manager.RemovePlaylist(pl2);
        var pl4 = _manager.CreatePlaylist();
        Assert.Equal("재생목록 2", pl4.Name);
    }

    #endregion

    #region 2. PlaylistManager.Sort Tests

    [Fact]
    public void Sort_ByTitle_SortsCaseInsensitively()
    {
        var pl = _manager.CreatePlaylist("TestSort");
        pl.Items.Add(CreateItem("Zebra", "Artist", "Album", 1, 1, @"C:\m\z.mp3"));
        pl.Items.Add(CreateItem("apple", "Artist", "Album", 1, 2, @"C:\m\a.mp3"));
        pl.Items.Add(CreateItem("Banana", "Artist", "Album", 1, 3, @"C:\m\b.mp3"));

        _manager.Sort(pl, PlaylistSort.Title);

        var titles = pl.Items.Select(i => i.Track.Title).ToList();
        Assert.Equal(new[] { "apple", "Banana", "Zebra" }, titles);
    }

    [Fact]
    public void Sort_ByArtist_SortsAlphabetically()
    {
        var pl = _manager.CreatePlaylist("TestSortArtist");
        pl.Items.Add(CreateItem("Song 1", "Radiohead", "Album", 1, 1, @"C:\m\1.mp3"));
        pl.Items.Add(CreateItem("Song 2", "Beatles", "Album", 1, 2, @"C:\m\2.mp3"));
        pl.Items.Add(CreateItem("Song 3", "Coldplay", "Album", 1, 3, @"C:\m\3.mp3"));

        _manager.Sort(pl, PlaylistSort.Artist);

        var artists = pl.Items.Select(i => i.Track.Artist).ToList();
        Assert.Equal(new[] { "Beatles", "Coldplay", "Radiohead" }, artists);
    }

    [Fact]
    public void Sort_ByAlbum_SortsByAlbumAndAlbumSortKey()
    {
        var pl = _manager.CreatePlaylist("TestSortAlbum");
        pl.Items.Add(CreateItem("Track B2", "Artist", "Beta", 1, 2, @"C:\m\b2.mp3"));
        pl.Items.Add(CreateItem("Track A1", "Artist", "Alpha", 1, 1, @"C:\m\a1.mp3"));
        pl.Items.Add(CreateItem("Track B1", "Artist", "Beta", 1, 1, @"C:\m\b1.mp3"));
        pl.Items.Add(CreateItem("Track A2", "Artist", "Alpha", 1, 2, @"C:\m\a2.mp3"));

        _manager.Sort(pl, PlaylistSort.Album);

        var titles = pl.Items.Select(i => i.Track.Title).ToList();
        Assert.Equal(new[] { "Track A1", "Track A2", "Track B1", "Track B2" }, titles);
    }

    [Fact]
    public void Sort_ByTrackNo_SortsAcrossMultiDiscBitwiseKey()
    {
        var pl = _manager.CreatePlaylist("TestSortTrackNo");
        pl.Items.Add(CreateItem("D2T1", "Artist", "Album", 2, 1, @"C:\m\d2t1.mp3"));
        pl.Items.Add(CreateItem("D1T10", "Artist", "Album", 1, 10, @"C:\m\d1t10.mp3"));
        pl.Items.Add(CreateItem("D1T2", "Artist", "Album", 1, 2, @"C:\m\d1t2.mp3"));
        pl.Items.Add(CreateItem("D1T1", "Artist", "Album", 1, 1, @"C:\m\d1t1.mp3"));

        _manager.Sort(pl, PlaylistSort.TrackNo);

        var titles = pl.Items.Select(i => i.Track.Title).ToList();
        Assert.Equal(new[] { "D1T1", "D1T2", "D1T10", "D2T1" }, titles);
    }

    [Fact]
    public void Sort_ByPath_SortsOrdinalIgnoreCase()
    {
        var pl = _manager.CreatePlaylist("TestSortPath");
        pl.Items.Add(CreateItem("T3", "A", "A", 1, 1, @"C:\Music\Z.mp3"));
        pl.Items.Add(CreateItem("T1", "A", "A", 1, 1, @"C:\music\a.mp3"));
        pl.Items.Add(CreateItem("T2", "A", "A", 1, 1, @"C:\Music\M.mp3"));

        _manager.Sort(pl, PlaylistSort.Path);

        var paths = pl.Items.Select(i => i.Track.Path).ToList();
        Assert.Equal(new[] { @"C:\music\a.mp3", @"C:\Music\M.mp3", @"C:\Music\Z.mp3" }, paths);
    }

    [Fact]
    public void Sort_Reverse_ReversesCurrentOrder()
    {
        var pl = _manager.CreatePlaylist("TestReverse");
        pl.Items.Add(CreateItem("One", "A", "A", 1, 1, @"C:\m\1.mp3"));
        pl.Items.Add(CreateItem("Two", "A", "A", 1, 2, @"C:\m\2.mp3"));
        pl.Items.Add(CreateItem("Three", "A", "A", 1, 3, @"C:\m\3.mp3"));

        _manager.Sort(pl, PlaylistSort.Reverse);

        var titles = pl.Items.Select(i => i.Track.Title).ToList();
        Assert.Equal(new[] { "Three", "Two", "One" }, titles);
    }

    #endregion

    #region 3. RemoveDuplicates Deduplication Tests

    [Fact]
    public void RemoveDuplicates_RemovesCaseInsensitiveDuplicatePaths_PreservingFirstOccurrence()
    {
        var pl = _manager.CreatePlaylist("TestDupes");
        var item1 = CreateItem("Song 1", "Artist", "Album", 1, 1, @"C:\Music\Track1.mp3");
        var item2 = CreateItem("Song 2", "Artist", "Album", 1, 2, @"C:\Music\Track2.mp3");
        var item3 = CreateItem("Song 1 Dup Case", "Artist", "Album", 1, 3, @"c:\music\track1.mp3");
        var item4 = CreateItem("Song 3", "Artist", "Album", 1, 4, @"C:\Music\Track3.mp3");
        var item5 = CreateItem("Song 2 Dup Upper", "Artist", "Album", 1, 5, @"C:\MUSIC\TRACK2.MP3");

        pl.Items.Add(item1);
        pl.Items.Add(item2);
        pl.Items.Add(item3);
        pl.Items.Add(item4);
        pl.Items.Add(item5);

        _manager.RemoveDuplicates(pl);

        Assert.Equal(3, pl.Items.Count);
        Assert.Same(item1, pl.Items[0]);
        Assert.Same(item2, pl.Items[1]);
        Assert.Same(item4, pl.Items[2]);
    }

    #endregion

    #region 4. Playlist Add / Insert / Remove Operations Tests

    [Fact]
    public void AddTracks_WithInsertAt_InsertsAtTargetIndex()
    {
        var pl = _manager.CreatePlaylist("TestInsert");
        var item1 = CreateItem("Item 1", "A", "A", 1, 1, "1.mp3");
        var item2 = CreateItem("Item 2", "A", "A", 1, 2, "2.mp3");
        pl.Items.Add(item1);
        pl.Items.Add(item2);

        var middleTrack = new Track { Title = "Inserted", Path = "ins.mp3" };
        _manager.AddTracks(pl, new[] { middleTrack }, insertAt: 1);

        Assert.Equal(3, pl.Items.Count);
        Assert.Equal("Item 1", pl.Items[0].Track.Title);
        Assert.Equal("Inserted", pl.Items[1].Track.Title);
        Assert.Equal("Item 2", pl.Items[2].Track.Title);
    }

    [Fact]
    public void RemoveItems_RaisesItemsRemovedEvent()
    {
        var pl = _manager.CreatePlaylist("TestEvents");
        var item1 = CreateItem("Item 1", "A", "A", 1, 1, "1.mp3");
        var item2 = CreateItem("Item 2", "A", "A", 1, 2, "2.mp3");
        pl.Items.Add(item1);
        pl.Items.Add(item2);

        IReadOnlyList<PlaylistItem>? removedPayload = null;
        _manager.ItemsRemoved += (playlist, items) =>
        {
            if (playlist == pl) removedPayload = items;
        };

        _manager.RemoveItems(pl, new[] { item1 });

        Assert.Single(pl.Items);
        Assert.Same(item2, pl.Items[0]);
        Assert.NotNull(removedPayload);
        Assert.Single(removedPayload!);
        Assert.Same(item1, removedPayload![0]);
    }

    #endregion
}
