using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

public class QueueAndPlaylistTests
{
    private static PlaylistItem Item(string title)
    {
        return new PlaylistItem(new Track { Path = @"C:\m\" + title + ".mp3", Title = title });
    }

    [Fact]
    public void QueuePreservesOrderAndIndexes()
    {
        var a = Item("a");
        var b = Item("b");
        var pl = new Playlist("pl");
        pl.Items.Add(a);
        pl.Items.Add(b);

        var q = new PlaybackQueue();
        q.Enqueue(pl, new[] { a, b });

        Assert.Equal(1, a.QueueIndex);
        Assert.Equal(2, b.QueueIndex);

        var head = q.Dequeue();
        Assert.Same(a, head!.Item);
        Assert.Equal(-1, a.QueueIndex);
        Assert.Equal(1, b.QueueIndex); // re-indexed
    }

    [Fact]
    public void QueueSkipsDuplicates()
    {
        var a = Item("a");
        var pl = new Playlist("pl");
        var q = new PlaybackQueue();
        q.Enqueue(pl, new[] { a });
        q.Enqueue(pl, new[] { a });
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public void EnqueueNextPutsItemAtFront()
    {
        var a = Item("a");
        var b = Item("b");
        var pl = new Playlist("pl");
        var q = new PlaybackQueue();
        q.Enqueue(pl, new[] { a });
        q.EnqueueNext(pl, new[] { b });
        Assert.Same(b, q.Peek()!.Item);
    }

    [Fact]
    public void RemovingPlaylistItemsPurgesQueue()
    {
        var a = Item("a");
        var b = Item("b");
        var pl = new Playlist("pl");
        var q = new PlaybackQueue();
        q.Enqueue(pl, new[] { a, b });

        q.RemoveItems(new[] { a });
        Assert.Equal(1, q.Count);
        Assert.Same(b, q.Peek()!.Item);
        Assert.Equal(-1, a.QueueIndex);
    }

    [Fact]
    public void TotalDurationAggregates()
    {
        var pl = new Playlist("pl");
        pl.Items.Add(new PlaylistItem(new Track { Path = "x", DurationMs = 3000 }));
        pl.Items.Add(new PlaylistItem(new Track { Path = "y", DurationMs = 4000 }));
        Assert.Equal(TimeSpan.FromSeconds(7), pl.TotalDuration);
    }

    [Fact]
    public void ReplaceWithTracksReplacesPlaylistEntirely()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);
        var pl = pm.Current;

        pl.Items.Add(Item("old1"));
        pl.Items.Add(Item("old2"));
        Assert.Equal(2, pl.Items.Count);

        var newTracks = new List<Track>
        {
            new Track { Path = @"C:\m\new1.flac", Title = "new1" },
            new Track { Path = @"C:\m\new2.flac", Title = "new2" },
            new Track { Path = @"C:\m\new3.flac", Title = "new3" }
        };

        var added = pm.ReplaceWithTracks(pl, newTracks);
        Assert.Equal(3, pl.Items.Count);
        Assert.Equal(3, added.Count);
        Assert.Equal("new1", pl.Items[0].Track.Title);
        Assert.Equal("new2", pl.Items[1].Track.Title);
        Assert.Equal("new3", pl.Items[2].Track.Title);
    }

    [Fact]
    public void NowPlayingPropertyReturnsDedicatedSystemPlaylist()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);

        var np = pm.NowPlaying;
        Assert.NotNull(np);
        Assert.Equal(PlaylistManager.NowPlayingPlaylistName, np.Name);
        Assert.Same(np, pm.NowPlaying);
    }

    [Fact]
    public async Task PlayAlbumNowPlayingAsync_ProtectsUserPlaylists()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);

        // User crafts a custom playlist
        var userPl = pm.CreatePlaylist("My Favorites");
        pm.AddTracks(userPl, new[]
        {
            new Track { Path = @"C:\m\fav1.flac", Title = "Favorite 1" },
            new Track { Path = @"C:\m\fav2.flac", Title = "Favorite 2" }
        });

        Assert.Equal(2, userPl.Items.Count);

        // User plays an album from Library
        var albumTracks = new List<Track>
        {
            new Track { Path = @"C:\m\album_t1.flac", Title = "Album T1" },
            new Track { Path = @"C:\m\album_t2.flac", Title = "Album T2" },
            new Track { Path = @"C:\m\album_t3.flac", Title = "Album T3" }
        };

        var items = await DawnPlayer.App.Controls.PlaybackUiHelper.PlayAlbumNowPlayingAsync(pm, null, albumTracks, 0);

        // Now Playing has 3 tracks
        Assert.Equal(3, pm.NowPlaying.Items.Count);
        Assert.Equal("Album T1", pm.NowPlaying.Items[0].Track.Title);

        // User playlist is 100% untouched and preserved!
        Assert.Equal(2, userPl.Items.Count);
        Assert.Equal("Favorite 1", userPl.Items[0].Track.Title);
        Assert.Equal("Favorite 2", userPl.Items[1].Track.Title);
    }

    [Fact]
    public void EnqueueAlbumNowPlaying_AppendsWithoutClearingUserPlaylists()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);

        var userPl = pm.CreatePlaylist("Workout");
        pm.AddTracks(userPl, new[] { new Track { Path = @"C:\m\w1.flac", Title = "W1" } });

        var album1 = new[] { new Track { Path = @"C:\m\a1.flac", Title = "A1" } };
        var album2 = new[] { new Track { Path = @"C:\m\a2.flac", Title = "A2" } };

        DawnPlayer.App.Controls.PlaybackUiHelper.EnqueueAlbumNowPlaying(pm, null, album1);
        Assert.Single(pm.NowPlaying.Items);

        DawnPlayer.App.Controls.PlaybackUiHelper.EnqueueAlbumNowPlaying(pm, null, album2);
        Assert.Equal(2, pm.NowPlaying.Items.Count);
        Assert.Equal("A1", pm.NowPlaying.Items[0].Track.Title);
        Assert.Equal("A2", pm.NowPlaying.Items[1].Track.Title);

        // User playlist remains untouched
        Assert.Single(userPl.Items);
        Assert.Equal("W1", userPl.Items[0].Track.Title);
    }

    [Fact]
    public void PlaylistManager_Current_ReturnsUserPlaylistWhenAvailable_AndNowPlayingWhenEmpty()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);

        // When no user playlists exist, Current returns NowPlaying
        pm.Playlists.Clear();
        var cur = pm.Current;
        Assert.Same(pm.NowPlaying, cur);

        // When a user playlist is added, Current prefers user playlist
        var userPl = pm.CreatePlaylist("My Custom Playlist");
        Assert.Same(userPl, pm.Current);

        // Even when NowPlaying is accessed, Current remains the user playlist
        _ = pm.NowPlaying;
        Assert.Same(userPl, pm.Current);
    }

    [Fact]
    public async Task PlaylistManager_ConcurrentNowPlayingAndUserPlaylistMutations_ThreadSafe()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);
        var userPl = pm.CreatePlaylist("Concurrent User Pl");

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = cts.Token;

        var tasks = new List<Task>();

        // 4 writers adding to NowPlaying
        for (int i = 0; i < 4; i++)
        {
            int workerId = i;
            tasks.Add(Task.Run(() =>
            {
                int counter = 0;
                while (!token.IsCancellationRequested || counter < 5)
                {
                    var track = new Track { Title = $"NP Track {workerId}_{counter}", Path = $@"C:\music\np_{workerId}_{counter}.flac" };
                    DawnPlayer.App.Controls.PlaybackUiHelper.EnqueueAlbumNowPlaying(pm, null, new[] { track });
                    counter++;
                    if (counter >= 50) break;
                    Thread.Yield();
                }
            }));
        }

        // 4 writers mutating user playlist
        for (int i = 0; i < 4; i++)
        {
            int workerId = i;
            tasks.Add(Task.Run(() =>
            {
                int counter = 0;
                while (!token.IsCancellationRequested || counter < 5)
                {
                    var track = new Track { Title = $"User Track {workerId}_{counter}", Path = $@"C:\music\u_{workerId}_{counter}.flac" };
                    pm.AddTracks(userPl, new[] { track });
                    counter++;
                    if (counter >= 50) break;
                    Thread.Yield();
                }
            }));
        }

        // 4 readers taking snapshots
        for (int i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                int reads = 0;
                while (!token.IsCancellationRequested || reads < 5)
                {
                    var npSnap = pm.NowPlaying.GetSnapshot();
                    var uSnap = userPl.GetSnapshot();
                    Assert.NotNull(npSnap);
                    Assert.NotNull(uSnap);
                    reads++;
                    if (reads >= 50) break;
                    Thread.Yield();
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.True(pm.NowPlaying.Items.Count > 0);
        Assert.True(userPl.Items.Count > 0);
    }

    [Fact]
    public async Task PlaylistManager_UserNamedNowPlaying_DoesNotCollideWithSystemNowPlaying()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);

        // User explicitly crafts a custom playlist with the exact same name "Now Playing"
        var userPl = pm.CreatePlaylist("Now Playing");
        pm.AddTracks(userPl, new[]
        {
            new Track { Path = @"C:\m\user_np_track.flac", Title = "User Track in Now Playing" }
        });

        // The system NowPlaying playlist
        var systemNp = pm.NowPlaying;

        Assert.True(systemNp.IsSystem);
        Assert.False(userPl.IsSystem);
        Assert.NotSame(systemNp, userPl);

        // Current should prioritize the user's playlist
        Assert.Same(userPl, pm.Current);

        // Ad-hoc library playback must target system NowPlaying and NEVER touch user's "Now Playing" playlist
        var libraryTracks = new[]
        {
            new Track { Path = @"C:\m\lib1.flac", Title = "Lib 1" },
            new Track { Path = @"C:\m\lib2.flac", Title = "Lib 2" }
        };

        await DawnPlayer.App.Controls.PlaybackUiHelper.PlayAlbumNowPlayingAsync(pm, null, libraryTracks, 0);

        // System queue has the 2 library tracks
        Assert.Equal(2, systemNp.Items.Count);
        Assert.Equal("Lib 1", systemNp.Items[0].Track.Title);

        // User's custom "Now Playing" playlist is 100% untouched!
        Assert.Single(userPl.Items);
        Assert.Equal("User Track in Now Playing", userPl.Items[0].Track.Title);
    }

    [Fact]
    public void PlaylistManager_RemovePlaylist_OnNowPlaying_ClearsItemsWithoutRemovingFromPlaylists()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);

        var np = pm.NowPlaying;
        pm.AddTracks(np, new[] { new Track { Path = @"C:\m\t1.flac", Title = "T1" } });
        Assert.Single(np.Items);

        // Removing system playlist should clear its items, not destroy the reference or drop it
        pm.RemovePlaylist(np);
        Assert.Empty(np.Items);
        Assert.Same(np, pm.NowPlaying);
        Assert.Contains(np, pm.Playlists);
    }

    [Fact]
    public void PlaylistManager_RemovePlaylistByName_RemovesUserPlaylist_WhenUserPlaylistNamedNowPlayingExists()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);

        var systemNp = pm.NowPlaying;
        pm.AddTracks(systemNp, new[] { new Track { Path = @"C:\m\sys1.flac", Title = "Sys 1" } });

        // Requesting a name that is already taken now yields a distinct one: two playlists sharing
        // a name also share a .m3u8 file, and the second one's save silently destroyed the first.
        var userPl = pm.CreatePlaylist("Now Playing");
        pm.AddTracks(userPl, new[] { new Track { Path = @"C:\m\user1.flac", Title = "User 1" } });

        Assert.Equal(2, pm.Playlists.Count);
        Assert.NotEqual(PlaylistManager.NowPlayingPlaylistName, userPl.Name);

        // Name-based removal takes the user playlist and leaves the system queue alone.
        pm.RemovePlaylist(userPl.Name);

        Assert.Equal(2, pm.Playlists.Count);
        Assert.Same(systemNp, pm.Playlists[0]);
        Assert.DoesNotContain(userPl, pm.Playlists);
        Assert.Single(systemNp.Items);
        Assert.Equal("Sys 1", systemNp.Items[0].Track.Title);

        // And removing the system queue by name only clears it — it is never deleted.
        pm.RemovePlaylist(PlaylistManager.NowPlayingPlaylistName);
        Assert.Contains(systemNp, pm.Playlists);
        Assert.Empty(systemNp.Items);
    }

    [Fact]
    public void PlaylistManager_RenamePlaylist_GuardsSystemNowPlayingFromRenaming()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);

        var systemNp = pm.NowPlaying;
        Assert.Equal(PlaylistManager.NowPlayingPlaylistName, systemNp.Name);

        pm.RenamePlaylist(systemNp, "Hacked Name");
        Assert.Equal(PlaylistManager.NowPlayingPlaylistName, systemNp.Name);
    }

    [Fact]
    public void PlaylistManager_SelectPlaylist_NowPlaying_IsPreservedByCurrent()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);

        var userPl = pm.CreatePlaylist("User Playlist");
        Assert.Same(userPl, pm.Current);

        pm.SelectPlaylist(pm.NowPlaying);
        Assert.Same(pm.NowPlaying, pm.Current);
    }

    [Fact]
    public void PlaylistManager_RemovePlaylist_WhenOnlyOneUserPlaylistExists_DeletesUserPlaylistAndCreatesFreshDefault()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);

        // Ensure system NowPlaying is created
        var np = pm.NowPlaying;
        var userPl = pm.CreatePlaylist("My Custom Playlist");
        pm.AddTracks(userPl, new[] { new Track { Path = @"C:\m\user1.flac", Title = "User 1" } });

        Assert.Equal(2, pm.Playlists.Count);
        Assert.Same(userPl, pm.Current);

        // Deleting the only user playlist
        pm.RemovePlaylist(userPl);

        // User playlist is deleted, and a new default user playlist is created
        Assert.Equal(2, pm.Playlists.Count);
        Assert.Contains(np, pm.Playlists);
        Assert.DoesNotContain(userPl, pm.Playlists);

        var defaultPl = pm.Playlists.First(p => !p.IsSystem);
        Assert.NotNull(defaultPl);
        Assert.Equal("재생목록", defaultPl.Name);
        Assert.Empty(defaultPl.Items);
        Assert.Same(defaultPl, pm.Current);
    }

    [Fact]
    public void PlaylistManager_RemovePlaylist_MultipleUserPlaylists_DeletesTargetAndSwitchesToAnotherUserPlaylist()
    {
        var lib = new DawnPlayer.Core.Library.MusicLibrary();
        var pm = new PlaylistManager(lib);

        var pl1 = pm.CreatePlaylist("Playlist 1");
        var pl2 = pm.CreatePlaylist("Playlist 2");
        pm.SelectPlaylist(pl1);

        Assert.Same(pl1, pm.Current);

        pm.RemovePlaylist(pl1);

        Assert.DoesNotContain(pl1, pm.Playlists);
        Assert.Contains(pl2, pm.Playlists);
        Assert.Same(pl2, pm.Current);
    }
}

