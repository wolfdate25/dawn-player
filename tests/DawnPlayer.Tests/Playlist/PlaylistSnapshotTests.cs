using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests.Playlists;

[Collection("PlaylistConcurrencyCollection")]
public class PlaylistSnapshotTests : IDisposable
{
    private readonly string _tempPlaylistsDir;

    public PlaylistSnapshotTests()
    {
        _tempPlaylistsDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempPlaylistsDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempPlaylistsDir))
            {
                Directory.Delete(_tempPlaylistsDir, recursive: true);
            }
        }
        catch { }
    }

    private static Track CreateTrack(string id, string album = "Album", string artist = "Artist", long durationMs = 180000, int year = 2024)
    {
        return new Track
        {
            Path = $@"C:\Music\track_{id}.mp3",
            Title = $"Track {id}",
            Album = album,
            Artist = artist,
            Year = year,
            DurationMs = durationMs
        };
    }

    [Fact]
    public async Task Playlist_TotalDuration_ConcurrentMutations_NeverThrowsAndReturnsValidSum()
    {
        var playlist = new Playlist("Test Playlist");
        for (int i = 0; i < 20; i++)
        {
            playlist.Items.Add(new PlaylistItem(CreateTrack(i.ToString(), durationMs: 10000)));
        }

        bool running = true;
        var exceptions = new List<Exception>();

        // Reader tasks continuously calculating TotalDuration
        var readerTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (Volatile.Read(ref running))
            {
                try
                {
                    var dur = playlist.TotalDuration;
                    Assert.True(dur >= TimeSpan.Zero);
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }
        })).ToArray();

        // Writer task mutating the collection
        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                lock (playlist.SyncRoot)
                {
                    playlist.Items.Add(new PlaylistItem(CreateTrack(i.ToString(), durationMs: 15000)));
                    if (playlist.Items.Count > 10)
                    {
                        playlist.Items.RemoveAt(0);
                    }
                }
            }
        });

        await writerTask;
        Volatile.Write(ref running, false);
        await Task.WhenAll(readerTasks);

        Assert.Empty(exceptions);
        Assert.True(playlist.TotalDuration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task AlbumGroup_Duration_ConcurrentMutations_SafeAndConsistent()
    {
        var group = new AlbumGroup
        {
            Album = "Concurrency Album",
            Artist = "Test Artist",
            Year = 2024
        };

        for (int i = 0; i < 15; i++)
        {
            group.AddItem(new PlaylistItem(CreateTrack(i.ToString(), durationMs: 60000)));
        }

        bool running = true;
        var exceptions = new List<Exception>();

        var readerTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (Volatile.Read(ref running))
            {
                try
                {
                    var dur = group.Duration;
                    var formatted = group.DurationFormatted;
                    var info = group.Info;
                    Assert.True(dur >= TimeSpan.Zero);
                    Assert.NotEmpty(formatted);
                    Assert.NotEmpty(info);
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }
        })).ToArray();

        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                group.AddItem(new PlaylistItem(CreateTrack(i.ToString(), durationMs: 45000)));
                if (group.Items.Count > 5)
                {
                    lock (group.Items)
                    {
                        if (group.Items.Count > 5)
                            group.Items.RemoveAt(0);
                    }
                }
            }
        });

        await writerTask;
        Volatile.Write(ref running, false);
        await Task.WhenAll(readerTasks);

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task PlaylistGroupBuilder_ConcurrentMutations_ProducesCompleteClustersWithoutExceptions()
    {
        var playlist = new Playlist("GroupBuilder Concurrent");
        for (int albumIdx = 0; albumIdx < 5; albumIdx++)
        {
            for (int trackIdx = 0; trackIdx < 10; trackIdx++)
            {
                playlist.Items.Add(new PlaylistItem(CreateTrack($"{albumIdx}_{trackIdx}", album: $"Album_{albumIdx}")));
            }
        }

        bool running = true;
        var exceptions = new List<Exception>();

        var readerTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (Volatile.Read(ref running))
            {
                try
                {
                    var groups = PlaylistGroupBuilder.BuildGroups(playlist);
                    Assert.NotNull(groups);
                    foreach (var g in groups)
                    {
                        Assert.NotNull(g.Album);
                        Assert.True(g.Count >= 0);
                        Assert.True(g.Duration >= TimeSpan.Zero);
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            }
        })).ToArray();

        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                lock (playlist.SyncRoot)
                {
                    playlist.Items.Add(new PlaylistItem(CreateTrack($"new_{i}", album: $"Album_{i % 3}")));
                    if (playlist.Items.Count > 10)
                    {
                        playlist.Items.RemoveAt(0);
                    }
                }
            }
        });

        await writerTask;
        Volatile.Write(ref running, false);
        await Task.WhenAll(readerTasks);

        Assert.Empty(exceptions);
    }

    [Fact]
    public void PlaylistGroupBuilder_NullAndEmptyInputs_ReturnsEmptyListSafely()
    {
        Assert.Empty(PlaylistGroupBuilder.BuildGroups(null));
        Assert.Empty(PlaylistGroupBuilder.BuildGroups(new Playlist("Empty")));
        Assert.Empty(PlaylistGroupBuilder.BuildGroupsFromItems(null));
        Assert.Empty(PlaylistGroupBuilder.BuildGroupsFromItems(Enumerable.Empty<PlaylistItem>()));

        // Collection with null item or null track
        var items = new List<PlaylistItem?> { null, new PlaylistItem(CreateTrack("1")) };
        var groups = PlaylistGroupBuilder.BuildGroupsFromItems(items!);
        Assert.Single(groups);
        Assert.Single(groups[0]);
    }

    private sealed class MockMusicLibrary : IMusicLibrary
    {
        private readonly Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<Track> Tracks => _tracks.Values.ToList();
        public int Count => _tracks.Count;

        public event Action? TracksChanged;
        public event Action<ScanProgress>? ScanProgress { add { } remove { } }

        public void AddTrack(Track track) => _tracks[track.Path] = track;

        public Track? GetTrack(string path) =>
            _tracks.TryGetValue(path, out var t) ? t : null;

        public void UpdateStats(Track track) { }

        public void LoadFromDb() { }

        public Task ScanAsync(AppSettings settings, CancellationToken ct = default)
        {
            TracksChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task ScanAsync(CancellationToken cancellationToken = default) =>
            ScanAsync(new AppSettings(), cancellationToken);

        public void Dispose() { }
    }

    [Fact]
    public void PlaylistManager_WithMockedIMusicLibrary_OperatesWithoutSQLite()
    {
        var mockLib = new MockMusicLibrary();
        var t1 = CreateTrack("1");
        var t2 = CreateTrack("2");
        mockLib.AddTrack(t1);
        mockLib.AddTrack(t2);

        PlaylistManager manager = new PlaylistManager(mockLib);
        var pl = manager.CreatePlaylist("Mock Playlist");

        Assert.NotNull(pl);
        Assert.Equal("Mock Playlist", pl.Name);
        Assert.Contains(pl, manager.Playlists);

        // Add tracks directly
        var added = manager.AddTracks(pl, new[] { t1, t2 });
        Assert.Equal(2, added.Count);
        Assert.Equal(2, pl.Items.Count);

        // Sort
        manager.Sort(pl, PlaylistSort.Reverse);
        Assert.Equal(t2.Path, pl.Items[0].Track.Path);
        Assert.Equal(t1.Path, pl.Items[1].Track.Path);

        // Remove
        manager.RemoveItems(pl, new[] { pl.Items[0] });
        Assert.Single(pl.Items);

        // Remove playlist
        manager.RemovePlaylist(pl);
        Assert.DoesNotContain(pl, manager.Playlists);
    }

    [Fact]
    public void M3u_Write_WithImmutableSnapshot_ProducesDeterministicOutput()
    {
        string filePath = Path.Combine(_tempPlaylistsDir, "test_playlist.m3u8");
        var items = new List<PlaylistItem>
        {
            new(CreateTrack("1", durationMs: 180000)),
            new(CreateTrack("2", durationMs: 240000))
        };

        PlaylistItem[] snapshot = [.. items];
        M3u.Write(filePath, snapshot, "Test M3U");

        Assert.True(File.Exists(filePath));
        var lines = File.ReadAllLines(filePath);
        Assert.Contains("#EXTM3U", lines);
        Assert.Contains("#PLAYLIST:Test M3U", lines);

        var readEntries = M3u.Read(filePath);
        Assert.Equal(2, readEntries.Count);
        Assert.Equal(180.0, readEntries[0].DurationSeconds);
        Assert.Equal(240.0, readEntries[1].DurationSeconds);
    }

    [Fact]
    public async Task Playlist_TotalDuration_ConcurrentMutations_ZeroExceptions()
    {
        var playlist = new Playlist("High Contention Playlist");
        for (int i = 0; i < 50; i++)
        {
            playlist.Items.Add(new PlaylistItem(CreateTrack(i.ToString(), durationMs: 20000)));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var readerTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var dur = playlist.TotalDuration;
                    Assert.True(dur >= TimeSpan.Zero);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToList();

        var writerTasks = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            int id = w * 10000;
            while (!cts.Token.IsCancellationRequested)
            {
                lock (playlist.SyncRoot)
                {
                    playlist.Items.Add(new PlaylistItem(CreateTrack((id++).ToString(), durationMs: 15000)));
                    if (playlist.Items.Count > 100)
                    {
                        playlist.Items.RemoveAt(0);
                    }
                }
                Thread.Yield();
            }
        })).ToList();

        await Task.WhenAll(readerTasks.Concat(writerTasks));
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task AlbumGroup_Duration_ConcurrentAddItem_ThreadSafe()
    {
        var group = new AlbumGroup
        {
            Album = "ThreadSafe Album",
            Artist = "ThreadSafe Artist",
            Year = 2026
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var readerTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var dur = group.Duration;
                    var formatted = group.DurationFormatted;
                    var info = group.Info;
                    int count = group.Count;
                    Assert.True(dur >= TimeSpan.Zero);
                    Assert.True(count >= 0);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToList();

        var writerTasks = Enumerable.Range(0, 2).Select(w => Task.Run(() =>
        {
            int id = w * 5000;
            while (!cts.Token.IsCancellationRequested)
            {
                group.AddItem(new PlaylistItem(CreateTrack((id++).ToString(), durationMs: 30000)));
                Thread.Yield();
            }
        })).ToList();

        await Task.WhenAll(readerTasks.Concat(writerTasks));
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task PlaylistGroupBuilder_ConcurrentMutations_ZeroExceptionsAndValidClusters()
    {
        var playlist = new Playlist("Cluster Playlist");
        for (int albumIdx = 0; albumIdx < 10; albumIdx++)
        {
            for (int trackIdx = 0; trackIdx < 5; trackIdx++)
            {
                playlist.Items.Add(new PlaylistItem(CreateTrack($"{albumIdx}_{trackIdx}", album: $"Album_{albumIdx}")));
            }
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var readerTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var groups = PlaylistGroupBuilder.BuildGroups(playlist);
                    Assert.NotNull(groups);
                    foreach (var g in groups)
                    {
                        Assert.NotNull(g.Album);
                        Assert.True(g.Count >= 0);
                        Assert.True(g.Duration >= TimeSpan.Zero);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToList();

        var writerTask = Task.Run(() =>
        {
            int counter = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                lock (playlist.SyncRoot)
                {
                    playlist.Items.Add(new PlaylistItem(CreateTrack($"dyn_{counter}", album: $"Album_{counter % 10}")));
                    if (playlist.Items.Count > 60)
                    {
                        playlist.Items.RemoveAt(0);
                    }
                }
                counter++;
                Thread.Yield();
            }
        });

        await Task.WhenAll(readerTasks.Concat(new[] { writerTask }));
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task PlaylistManager_ConcurrentDebounceSave_NoGhostFileRecreation()
    {
        var mockLib = new MockMusicLibrary();
        var manager = new PlaylistManager(mockLib) { DebounceDelayMs = 50 };
        var pl = manager.CreatePlaylist("GhostTestPlaylist");

        // Add items triggering ScheduleSave debounce timer
        var t1 = CreateTrack("ghost1");
        manager.AddTracks(pl, new[] { t1 });

        // Immediately delete playlist
        manager.RemovePlaylist(pl);

        // Wait to exceed the debounce timer
        await Task.Delay(100);

        string expectedPath = Path.Combine(AppPaths.PlaylistsDir, "GhostTestPlaylist.m3u8");
        Assert.False(File.Exists(expectedPath), $"Ghost file was recreated at: {expectedPath}");
    }

    [Fact]
    public async Task PlaylistManager_ConcurrentRenameAndSave_NoIOException()
    {
        var mockLib = new MockMusicLibrary();
        var manager = new PlaylistManager(mockLib);
        var pl = manager.CreatePlaylist("ConcurrentRenameBase");
        var tracks = Enumerable.Range(0, 20).Select(i => CreateTrack(i.ToString())).ToList();
        manager.AddTracks(pl, tracks);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var saveTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    manager.SavePlaylist(pl);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
                Thread.Yield();
            }
        });

        var renameTask = Task.Run(() =>
        {
            int counter = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    manager.RenamePlaylist(pl, $"ConcurrentRename_{counter % 5}");
                    counter++;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
                Thread.Yield();
            }
        });

        await Task.WhenAll(saveTask, renameTask);
        manager.RemovePlaylist(pl);

        // Clean up any test files
        for (int i = 0; i < 5; i++)
        {
            var p = Path.Combine(AppPaths.PlaylistsDir, $"ConcurrentRename_{i}.m3u8");
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task PlaylistManager_RemoveDeadItemsAsync_ConcurrentMutations_PreservesIntegrity()
    {
        var mockLib = new MockMusicLibrary();
        var manager = new PlaylistManager(mockLib);
        var pl = manager.CreatePlaylist("DeadItemsTest");

        // Create a real temporary file on disk for live track
        string liveFilePath = Path.Combine(_tempPlaylistsDir, "live_track.mp3");
        await File.WriteAllTextAsync(liveFilePath, "audio");

        var liveTrack = new Track { Path = liveFilePath, Title = "Live", DurationMs = 1000 };
        var deadTrack1 = new Track { Path = Path.Combine(_tempPlaylistsDir, "dead1.mp3"), Title = "Dead 1", DurationMs = 1000 };
        var deadTrack2 = new Track { Path = Path.Combine(_tempPlaylistsDir, "dead2.mp3"), Title = "Dead 2", DurationMs = 1000 };

        manager.AddTracks(pl, new[] { liveTrack, deadTrack1, deadTrack2 });

        var removeTask = manager.RemoveDeadItemsAsync(pl);

        // Concurrently add another live track
        string liveFilePath2 = Path.Combine(_tempPlaylistsDir, "live_track2.mp3");
        await File.WriteAllTextAsync(liveFilePath2, "audio2");
        var liveTrack2 = new Track { Path = liveFilePath2, Title = "Live 2", DurationMs = 2000 };
        manager.AddTracks(pl, new[] { liveTrack2 });

        int deadRemoved = await removeTask;
        Assert.Equal(2, deadRemoved);

        var snap = pl.GetSnapshot();
        Assert.Contains(snap, i => i.Track.Path == liveFilePath);
        Assert.Contains(snap, i => i.Track.Path == liveFilePath2);
        Assert.DoesNotContain(snap, i => i.Track.Path == deadTrack1.Path);
        Assert.DoesNotContain(snap, i => i.Track.Path == deadTrack2.Path);

        manager.RemovePlaylist(pl);
    }

    [Fact]
    public async Task Playlist_TotalDuration_And_GroupBuilder_MassiveParallelMutations_ZeroExceptions()
    {
        var mockLib = new MockMusicLibrary();
        var manager = new PlaylistManager(mockLib);
        var pl = manager.CreatePlaylist("ExtremeContentionPlaylist");

        // Seed 100 tracks
        var initialTracks = Enumerable.Range(0, 100).Select(i => CreateTrack(i.ToString(), album: $"Album_{i % 10}")).ToList();
        manager.AddTracks(pl, initialTracks);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // 8 reader threads
        var readers = Enumerable.Range(0, 8).Select(r => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var dur = pl.TotalDuration;
                    Assert.True(dur >= TimeSpan.Zero);

                    var snap = pl.GetSnapshot();
                    Assert.NotNull(snap);

                    var groups = PlaylistGroupBuilder.BuildGroups(pl);
                    Assert.NotNull(groups);
                    foreach (var g in groups)
                    {
                        var gd = g.Duration;
                        Assert.True(gd >= TimeSpan.Zero);
                        Assert.True(g.Count >= 0);
                    }

                    var colSnap = CollectionSnapshot.Capture(pl);
                    Assert.NotNull(colSnap);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToList();

        // 6 diverse writer threads
        var writers = new List<Task>
        {
            Task.Run(() =>
            {
                int counter = 1000;
                while (!cts.Token.IsCancellationRequested)
                {
                    var trk = CreateTrack((counter++).ToString(), album: $"Album_{counter % 5}");
                    manager.AddTracks(pl, new[] { trk });
                    Thread.Yield();
                }
            }),
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var snap = pl.GetSnapshot();
                    if (snap.Length > 20)
                    {
                        var toRemove = snap.Take(5).ToList();
                        manager.RemoveItems(pl, toRemove);
                    }
                    Thread.Yield();
                }
            }),
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    manager.Sort(pl, PlaylistSort.Title);
                    Thread.Yield();
                    manager.Sort(pl, PlaylistSort.Artist);
                    Thread.Yield();
                    manager.Sort(pl, PlaylistSort.Reverse);
                    Thread.Yield();
                }
            }),
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    manager.RemoveDuplicates(pl);
                    Thread.Yield();
                }
            }),
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var snap = pl.GetSnapshot();
                    if (snap.Length > 2)
                    {
                        manager.MoveItem(pl, 0, snap.Length - 1);
                    }
                    Thread.Yield();
                }
            }),
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var snap = pl.GetSnapshot();
                    if (snap.Length > 5)
                    {
                        var selection = snap.Take(3).ToList();
                        manager.MoveSelection(pl, selection, up: false);
                    }
                    Thread.Yield();
                }
            })
        };

        await Task.WhenAll(readers.Concat(writers));
        manager.RemovePlaylist(pl);

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task AlbumGroup_Foreach_Count_Duration_HighContentionConcurrentMutations()
    {
        var group = new AlbumGroup
        {
            Album = "ContentionAlbum",
            Artist = "ContentionArtist",
            Year = 2026
        };

        for (int i = 0; i < 30; i++)
        {
            group.AddItem(new PlaylistItem(CreateTrack(i.ToString())));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    int enumeratedCount = 0;
                    foreach (var item in group)
                    {
                        if (item != null) enumeratedCount++;
                    }
                    Assert.True(enumeratedCount >= 0);

                    var dur = group.Duration;
                    var durFormatted = group.DurationFormatted;
                    var info = group.Info;
                    int count = group.Count;
                    Assert.True(dur >= TimeSpan.Zero);
                    Assert.NotEmpty(durFormatted);
                    Assert.NotEmpty(info);

                    var snap = CollectionSnapshot.Capture(group);
                    Assert.NotNull(snap);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToList();

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            int c = w * 1000;
            while (!cts.Token.IsCancellationRequested)
            {
                group.AddItem(new PlaylistItem(CreateTrack((c++).ToString())));
                Thread.Yield();
            }
        })).ToList();

        await Task.WhenAll(readers.Concat(writers));
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task PlaylistManager_MassiveDebounceSaveAndDeletionRace_ZeroGhostFiles()
    {
        var mockLib = new MockMusicLibrary();
        var manager = new PlaylistManager(mockLib) { DebounceDelayMs = 50 };

        var createdPlaylists = new List<Playlist>();
        var deletedPlaylistNames = new List<string>();

        // Create 14 playlists with tracks to trigger debounce timers (800ms)
        for (int i = 0; i < 14; i++)
        {
            var pl = manager.CreatePlaylist($"MassiveDebounce_{i}_{Guid.NewGuid():N}");
            manager.AddTracks(pl, new[] { CreateTrack($"track_{i}") });
            createdPlaylists.Add(pl);
        }

        // Concurrently delete half of the playlists during their 800ms debounce window
        var deleteTasks = createdPlaylists.Take(7).Select(pl => Task.Run(() =>
        {
            lock (deletedPlaylistNames) deletedPlaylistNames.Add(pl.Name);
            manager.RemovePlaylist(pl);
        })).ToList();

        await Task.WhenAll(deleteTasks);

        // Wait 100ms so all debounce timers have definitely fired
        await Task.Delay(100);

        // Verify that deleted playlists DO NOT exist on disk (zero ghost files)
        foreach (var deletedName in deletedPlaylistNames)
        {
            string expectedPath = Path.Combine(AppPaths.PlaylistsDir, $"{deletedName}.m3u8");
            Assert.False(File.Exists(expectedPath), $"Ghost file detected for removed playlist: {expectedPath}");
        }

        // Clean up remaining playlists
        foreach (var pl in createdPlaylists.Skip(7))
        {
            manager.RemovePlaylist(pl);
        }
    }

    [Fact]
    public async Task PlaylistManager_ConcurrentSaveAll_And_PlaylistMutations_ThreadSafe()
    {
        var mockLib = new MockMusicLibrary();
        var manager = new PlaylistManager(mockLib);

        var basePlaylists = Enumerable.Range(0, 10).Select(i =>
        {
            var pl = manager.CreatePlaylist($"SaveAllBase_{i}");
            manager.AddTracks(pl, new[] { CreateTrack($"base_{i}") });
            return pl;
        }).ToList();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Thread calling SaveAll repeatedly
        var saveAllTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    manager.SaveAll();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
                Thread.Sleep(20);
            }
        });

        // Thread modifying playlist items
        var mutateTask = Task.Run(() =>
        {
            int counter = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var pl = basePlaylists[counter % basePlaylists.Count];
                    manager.AddTracks(pl, new[] { CreateTrack($"dyn_{counter}") });
                    if (pl.Items.Count > 10)
                    {
                        var snap = pl.GetSnapshot();
                        if (snap.Length > 0) manager.RemoveItems(pl, new[] { snap[0] });
                    }
                    counter++;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
                Thread.Yield();
            }
        });

        // Thread renaming playlists
        var renameTask = Task.Run(() =>
        {
            int counter = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var pl = basePlaylists[counter % basePlaylists.Count];
                    manager.RenamePlaylist(pl, $"SaveAllBase_{counter % basePlaylists.Count}_rev{counter}");
                    counter++;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
                Thread.Yield();
            }
        });

        await Task.WhenAll(saveAllTask, mutateTask, renameTask);

        // Clean up
        foreach (var pl in basePlaylists)
        {
            manager.RemovePlaylist(pl);
        }

        Assert.Empty(exceptions);
    }

    [Fact]
    public void CollectionSnapshot_AdversarialInputs_AllTypesAndNulls_Safe()
    {
        // 1. Null inputs
        Assert.Empty(CollectionSnapshot.Capture((Playlist?)null));
        Assert.Empty(CollectionSnapshot.Capture((AlbumGroup?)null));
        Assert.Empty(CollectionSnapshot.Capture((IEnumerable<PlaylistItem>?)null));
        Assert.Empty(CollectionSnapshot.Capture<string>(null));

        // 2. Empty collections
        Assert.Empty(CollectionSnapshot.Capture(new Playlist("empty")));
        Assert.Empty(CollectionSnapshot.Capture(new AlbumGroup()));
        Assert.Empty(CollectionSnapshot.Capture(new List<PlaylistItem>()));
        Assert.Empty(CollectionSnapshot.Capture(Array.Empty<PlaylistItem>()));

        // 3. Array cloning
        var originalArr = new[] { new PlaylistItem(CreateTrack("1")), new PlaylistItem(CreateTrack("2")) };
        var capturedArr = CollectionSnapshot.Capture(originalArr);
        Assert.Equal(2, capturedArr.Length);
        Assert.NotSame(originalArr, capturedArr); // Cloned array

        // 4. AlbumGroup snapshot
        var group = new AlbumGroup();
        group.AddItem(new PlaylistItem(CreateTrack("g1")));
        var groupSnap = CollectionSnapshot.Capture(group);
        Assert.Single(groupSnap);

        // 5. Generic Capture
        var list = new List<int> { 10, 20, 30 };
        var listSnap = CollectionSnapshot.Capture(list);
        Assert.Equal(3, listSnap.Length);
        Assert.Equal(10, listSnap[0]);
    }

    [Fact]
    public async Task PlaylistManager_ExtremeConcurrent_Create_Remove_Current_Active_ThreadSafe()
    {
        var mockLib = new MockMusicLibrary();
        var manager = new PlaylistManager(mockLib);

        // Seed initial playlists
        for (int i = 0; i < 5; i++)
        {
            var pl = manager.CreatePlaylist($"Initial_{i}");
            manager.AddTracks(pl, new[] { CreateTrack($"init_{i}") });
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var createdPlaylists = new System.Collections.Concurrent.ConcurrentBag<Playlist>();

        // 1. Thread group: Rapidly calling CreatePlaylist() with default auto-generated names
        var autoCreators = Enumerable.Range(0, 4).Select(id => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var pl = manager.CreatePlaylist();
                    Assert.NotNull(pl);
                    Assert.False(string.IsNullOrWhiteSpace(pl.Name));
                    manager.AddTracks(pl, new[] { CreateTrack($"auto_{id}_{Guid.NewGuid():N}") });
                    createdPlaylists.Add(pl);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
                Thread.Sleep(1);
            }
        })).ToList();

        // 2. Thread group: Creating custom named playlists
        var customCreators = Enumerable.Range(0, 3).Select(id => Task.Run(() =>
        {
            int counter = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var name = $"Custom_{id}_{counter++}_{Guid.NewGuid():N}";
                    var pl = manager.CreatePlaylist(name);
                    Assert.NotNull(pl);
                    Assert.Equal(name, pl.Name);
                    manager.AddTracks(pl, new[] { CreateTrack($"custom_{id}") });
                    createdPlaylists.Add(pl);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
                Thread.Sleep(1);
            }
        })).ToList();

        // 3. Thread group: Removing playlists by reference
        var removersByRef = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (createdPlaylists.TryTake(out var target) && target != null)
                    {
                        manager.RemovePlaylist(target);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
                Thread.Sleep(1);
            }
        })).ToList();

        // 4. Thread group: Removing playlists by name
        var removersByName = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (createdPlaylists.TryTake(out var target) && target?.Name != null)
                    {
                        manager.RemovePlaylist(target.Name);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
                Thread.Sleep(1);
            }
        })).ToList();

        // 5. Thread group: Reading Current and switching ActivePlaylist continuously
        var currentReaders = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var cur = manager.Current;
                    Assert.NotNull(cur);
                    Assert.False(string.IsNullOrWhiteSpace(cur.Name));

                    var active = manager.ActivePlaylist;
                    Assert.NotNull(active);

                    if (createdPlaylists.TryPeek(out var pick) && pick != null)
                    {
                        manager.ActivePlaylist = pick;
                        manager.SelectPlaylist(pick);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
                Thread.Sleep(1);
            }
        })).ToList();

        var allTasks = Task.WhenAll(autoCreators
            .Concat(customCreators)
            .Concat(removersByRef)
            .Concat(removersByName)
            .Concat(currentReaders));

        // Deadlock guard, not a perf bound: the workers stop at the 100ms cancellation and
        // pace themselves with Sleep(1), so a healthy run finishes almost immediately. The
        // bound must stay generous — a 2-core CI runner once starved this test's 30s-bound
        // continuation for ~2h and failed the release build spuriously.
        var completed = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromMinutes(1)));
        if (completed != allTasks)
        {
            cts.Cancel();
            Assert.Fail("Worker tasks did not finish within 60s — possible PlaylistManager deadlock.");
        }
        await allTasks;

        Assert.Empty(exceptions);
        Assert.NotNull(manager.Current);
        Assert.True(manager.Playlists.Count >= 1);
    }
}

