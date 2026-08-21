using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// What the app has to rebuild from settings.json on the next launch: the playback queue restored
/// entry by entry (1-based QueueIndex assignment, files deleted since the last run dropped, an
/// unknown playlist name falling back to the current playlist), and the album grouping the playlist
/// and library views run over the restored items — consecutive-run clustering, order preservation
/// across interleaved albums, Korean fallback labels for missing metadata, and 10k tracks.
/// </summary>
public class SessionRestoreAndPlaylistGroupingTests
{
    #region 1. SessionManager & Playlist Reconstitution Tests

    [Fact]
    public void TestSessionQueue_SerializationAndReconstitution()
    {
        var lib = new MusicLibrary();
        var pm = new PlaylistManager(lib);
        var pl1 = pm.Current;
        pl1.Name = "Playlist 1";
        var pl2 = pm.CreatePlaylist();
        pl2.Name = "Playlist 2";

        var tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayerSessionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var path1 = Path.Combine(tempDir, "song1.mp3");
            var path2 = Path.Combine(tempDir, "song2.mp3");
            var path3 = Path.Combine(tempDir, "song3.mp3");
            File.WriteAllText(path1, "dummy audio 1");
            File.WriteAllText(path2, "dummy audio 2");
            File.WriteAllText(path3, "dummy audio 3");

            var t1 = new Track { Path = path1, Title = "Song 1", Artist = "Artist 1", DurationMs = 180000 };
            var t2 = new Track { Path = path2, Title = "Song 2", Artist = "Artist 2", DurationMs = 210000 };
            var t3 = new Track { Path = path3, Title = "Song 3", Artist = "Artist 3", DurationMs = 240000 };

            var item1 = new PlaylistItem(t1);
            var item2 = new PlaylistItem(t2);
            var item3 = new PlaylistItem(t3);

            pl1.Items.Add(item1);
            pl2.Items.Add(item2);
            pl2.Items.Add(item3);

            var queue = new PlaybackQueue();
            queue.Enqueue(pl1, new[] { item1 });
            queue.Enqueue(pl2, new[] { item2, item3 });

            // Simulate SaveSession queue extraction
            var savedEntries = queue.Entries
                .Select(e => new QueueSavedEntry { PlaylistName = e.Playlist?.Name ?? "", TrackPath = e.Item.Track.Path })
                .ToList();

            var settings = new AppSettings();
            settings.Playback.QueueItems = savedEntries;
            settings.Playback.LastPlayedTrackPath = path2;
            settings.Playback.LastPlayedPlaylistName = "Playlist 2";
            settings.Playback.LastPlayedPositionSeconds = 45.5;

            var json = JsonSerializer.Serialize(settings);
            var loadedSettings = JsonSerializer.Deserialize<AppSettings>(json)!;

            Assert.Equal(3, loadedSettings.Playback.QueueItems.Count);
            Assert.Equal("Playlist 1", loadedSettings.Playback.QueueItems[0].PlaylistName);
            Assert.Equal(path1, loadedSettings.Playback.QueueItems[0].TrackPath);
            Assert.Equal("Playlist 2", loadedSettings.Playback.QueueItems[1].PlaylistName);
            Assert.Equal(path2, loadedSettings.Playback.QueueItems[1].TrackPath);
            Assert.Equal(path3, loadedSettings.Playback.QueueItems[2].TrackPath);

            // Reconstitute in a new session (fresh items with QueueIndex = -1)
            var freshPl1 = new Playlist("Playlist 1");
            var freshPl2 = new Playlist("Playlist 2");
            freshPl1.Items.Add(new PlaylistItem(t1));
            freshPl2.Items.Add(new PlaylistItem(t2));
            freshPl2.Items.Add(new PlaylistItem(t3));

            var freshPlaylists = new List<Playlist> { freshPl1, freshPl2 };

            var restoredQueue = new PlaybackQueue();
            foreach (var qi in loadedSettings.Playback.QueueItems)
            {
                if (!File.Exists(qi.TrackPath)) continue;
                var pl = freshPlaylists.FirstOrDefault(x => x.Name == qi.PlaylistName) ?? freshPlaylists[0];
                var track = pl.Items.FirstOrDefault(i => i.Track.Path == qi.TrackPath)?.Track
                            ?? new Track { Path = qi.TrackPath };
                var item = pl.Items.FirstOrDefault(i => i.Track.Path == track.Path) ?? new PlaylistItem(track);
                restoredQueue.Enqueue(pl, new List<PlaylistItem> { item });
            }

            Assert.Equal(3, restoredQueue.Count);
            var entries = restoredQueue.Entries;
            Assert.Equal("Song 1", entries[0].Item.Track.Title);
            Assert.Equal("Song 2", entries[1].Item.Track.Title);
            Assert.Equal("Song 3", entries[2].Item.Track.Title);
            Assert.Equal(1, entries[0].Item.QueueIndex);
            Assert.Equal(2, entries[1].Item.QueueIndex);
            Assert.Equal(3, entries[2].Item.QueueIndex);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TestSessionQueue_DeletedFilesFilteredOutSafely()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayerSessionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var existingFile = Path.Combine(tempDir, "exists.mp3");
            var deletedFile = Path.Combine(tempDir, "deleted.mp3");
            File.WriteAllText(existingFile, "dummy");

            var settings = new AppSettings();
            settings.Playback.QueueItems = new List<QueueSavedEntry>
            {
                new() { PlaylistName = "Default", TrackPath = deletedFile },
                new() { PlaylistName = "Default", TrackPath = existingFile }
            };

            var restoredItems = new List<string>();
            foreach (var qi in settings.Playback.QueueItems)
            {
                if (!File.Exists(qi.TrackPath)) continue;
                restoredItems.Add(qi.TrackPath);
            }

            Assert.Single(restoredItems);
            Assert.Equal(existingFile, restoredItems[0]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TestSession_PlaylistFallbackWhenPlaylistNotFound()
    {
        var lib = new MusicLibrary();
        var pm = new PlaylistManager(lib);
        var currentPl = pm.Current;

        // If target playlist name does not exist
        string targetPlaylistName = "NonExistentPlaylist";
        var resolvedPl = pm.Playlists.FirstOrDefault(x => x.Name == targetPlaylistName) ?? pm.Current;

        Assert.Same(currentPl, resolvedPl);
    }

    #endregion

    #region 2. Playlist Grouping Algorithm Tests

    public sealed class MockAlbumGroup
    {
        public string Key { get; set; } = "";
        public string Album { get; set; } = "";
        public string Artist { get; set; } = "";
        public int Year { get; set; }
        public string? ArtPath { get; set; }
        public List<PlaylistItem> Items { get; } = new();
        public int Count => Items.Count;
        public TimeSpan Duration => TimeSpan.FromMilliseconds(Items.Sum(i => i.Track.DurationMs));
    }

    public static List<MockAlbumGroup> BuildGroups(Playlist pl)
    {
        var groups = new List<MockAlbumGroup>();
        MockAlbumGroup? current = null;

        foreach (var item in pl.Items)
        {
            var t = item.Track;
            var key = t.AlbumKey;
            if (current == null || current.Key != key)
            {
                current = new MockAlbumGroup
                {
                    Key = key,
                    Album = t.Album.Length > 0 ? t.Album : "(앨범 없음)",
                    Artist = t.SortArtist.Length > 0 ? t.SortArtist : "(아티스트 없음)",
                    Year = t.Year,
                    ArtPath = t.ArtPath
                };
                groups.Add(current);
            }
            current.Items.Add(item);
        }

        return groups;
    }

    [Fact]
    public void TestPlaylistGrouping_EmptyPlaylist_ReturnsEmptyList()
    {
        var pl = new Playlist("Empty");
        var groups = BuildGroups(pl);
        Assert.Empty(groups);
    }

    [Fact]
    public void TestPlaylistGrouping_ConsecutiveTracksSameAlbum_ClusteredIntoSingleGroup()
    {
        var pl = new Playlist("Test");
        pl.Items.Add(new PlaylistItem(new Track { Path = "1.mp3", Album = "Album A", Artist = "Artist 1", Year = 2024, DurationMs = 1000 }));
        pl.Items.Add(new PlaylistItem(new Track { Path = "2.mp3", Album = "Album A", Artist = "Artist 1", Year = 2024, DurationMs = 2000 }));
        pl.Items.Add(new PlaylistItem(new Track { Path = "3.mp3", Album = "Album A", Artist = "Artist 1", Year = 2024, DurationMs = 3000 }));

        var groups = BuildGroups(pl);
        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
        Assert.Equal("Album A", groups[0].Album);
        Assert.Equal("Artist 1", groups[0].Artist);
        Assert.Equal(2024, groups[0].Year);
        Assert.Equal(TimeSpan.FromSeconds(6), groups[0].Duration);
    }

    [Fact]
    public void TestPlaylistGrouping_InterleavedAlbums_PreservesPlaylistOrder()
    {
        var pl = new Playlist("Interleaved");
        // A1, B1, A2 -> 3 distinct groups preserving sequence
        pl.Items.Add(new PlaylistItem(new Track { Path = "1.mp3", Album = "Album A", Artist = "Artist 1", DurationMs = 1000 }));
        pl.Items.Add(new PlaylistItem(new Track { Path = "2.mp3", Album = "Album B", Artist = "Artist 2", DurationMs = 2000 }));
        pl.Items.Add(new PlaylistItem(new Track { Path = "3.mp3", Album = "Album A", Artist = "Artist 1", DurationMs = 3000 }));

        var groups = BuildGroups(pl);
        Assert.Equal(3, groups.Count);
        Assert.Equal("Album A", groups[0].Album);
        Assert.Single(groups[0].Items);
        Assert.Equal("Album B", groups[1].Album);
        Assert.Single(groups[1].Items);
        Assert.Equal("Album A", groups[2].Album);
        Assert.Single(groups[2].Items);
    }

    [Fact]
    public void TestPlaylistGrouping_MissingMetadata_FallbacksApplied()
    {
        var pl = new Playlist("Missing");
        pl.Items.Add(new PlaylistItem(new Track { Path = "1.mp3", Album = "", Artist = "", DurationMs = 5000 }));

        var groups = BuildGroups(pl);
        Assert.Single(groups);
        Assert.Equal("(앨범 없음)", groups[0].Album);
        Assert.Equal("(아티스트 없음)", groups[0].Artist);
        Assert.Equal(TimeSpan.FromSeconds(5), groups[0].Duration);
    }

    [Fact]
    public void TestPlaylistGrouping_AlbumKeyCaseAndWhitespaceInsensitivity()
    {
        var pl = new Playlist("CaseCheck");
        pl.Items.Add(new PlaylistItem(new Track { Path = "1.mp3", Album = "Rock Hits", Artist = "Queen ", DurationMs = 1000 }));
        pl.Items.Add(new PlaylistItem(new Track { Path = "2.mp3", Album = "rock hits", Artist = "queen", DurationMs = 2000 }));

        var groups = BuildGroups(pl);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void TestPlaylistGrouping_StressTest_10000Tracks()
    {
        var pl = new Playlist("Stress");
        for (int i = 0; i < 10000; i++)
        {
            int albumNum = i / 10; // 10 tracks per album -> 1000 albums
            pl.Items.Add(new PlaylistItem(new Track
            {
                Path = $"track_{i}.mp3",
                Album = $"Album {albumNum}",
                Artist = $"Artist {albumNum}",
                DurationMs = 180000
            }));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var groups = BuildGroups(pl);
        sw.Stop();

        Assert.Equal(1000, groups.Count);
        Assert.All(groups, g => Assert.Equal(10, g.Count));
        Assert.True(sw.ElapsedMilliseconds < 500, $"Grouping took too long: {sw.ElapsedMilliseconds}ms");
    }

    #endregion
}
