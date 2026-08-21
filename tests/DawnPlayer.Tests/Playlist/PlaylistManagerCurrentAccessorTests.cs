using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests.Playlists;

/// <summary>
/// <c>Current</c> and <c>NowPlaying</c> create a playlist and insert it into the UI-bound
/// collection, so background threads must use <c>TryGetCurrent</c> instead. These tests pin the
/// difference: the creating accessors still create, and the non-creating one never does.
/// </summary>
public sealed class PlaylistManagerCurrentAccessorTests
{
    private sealed class EmptyLibrary : IMusicLibrary
    {
        public IReadOnlyList<Track> Tracks { get; } = Array.Empty<Track>();
        public int Count => 0;
        public event Action? TracksChanged;
        public event Action<ScanProgress>? ScanProgress;
        public Track? GetTrack(string path) => null;
        public void LoadFromDb() { }
        public Task ScanAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }

        // Keeps the compiler from warning about events this stub never raises.
        internal void Unused() { TracksChanged?.Invoke(); ScanProgress?.Invoke(default!); }
    }

    private static PlaylistManager NewManager() => new(new EmptyLibrary());

    [Fact]
    public void TryGetCurrent_WithNoPlaylists_ReturnsNullAndCreatesNothing()
    {
        var manager = NewManager();

        Assert.Null(manager.TryGetCurrent());
        Assert.Empty(manager.Playlists);
    }

    [Fact]
    public void TryGetCurrent_AfterUserPlaylistExists_ReturnsIt()
    {
        var manager = NewManager();
        var created = manager.CreatePlaylist("Mine");

        Assert.Same(created, manager.TryGetCurrent());
        Assert.Single(manager.Playlists);
    }

    [Fact]
    public void TryGetCurrent_MatchesCurrent_OnceAPlaylistExists()
    {
        var manager = NewManager();
        manager.CreatePlaylist("Mine");

        Assert.Same(manager.Current, manager.TryGetCurrent());
    }

    [Fact]
    public void TryGetCurrent_FollowsSelection()
    {
        var manager = NewManager();
        manager.CreatePlaylist("First");
        var second = manager.CreatePlaylist("Second");

        manager.SelectPlaylist(second);

        Assert.Same(second, manager.TryGetCurrent());
    }

    [Fact]
    public void TryGetCurrent_WithOnlyTheSystemPlaylist_ReturnsIt()
    {
        var manager = NewManager();
        var nowPlaying = manager.NowPlaying; // creating accessor, on this (UI-equivalent) thread

        Assert.Same(nowPlaying, manager.TryGetCurrent());
    }

    [Fact]
    public void Current_WithNoPlaylists_StillCreatesTheSystemPlaylist()
    {
        // The UI paths depend on this; only the background paths were changed.
        var manager = NewManager();

        var current = manager.Current;

        Assert.NotNull(current);
        Assert.Contains(current, manager.Playlists);
    }

    [Fact]
    public async Task TryGetCurrent_CalledConcurrently_NeverMutatesThePlaylistCollection()
    {
        var manager = NewManager();
        manager.CreatePlaylist("Mine");
        int countBefore = manager.Playlists.Count;

        int changes = 0;
        manager.Playlists.CollectionChanged += (_, _) => Interlocked.Increment(ref changes);

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                var current = manager.TryGetCurrent();
                Assert.NotNull(current);
            }
        }));
        await Task.WhenAll(readers);

        Assert.Equal(0, changes);
        Assert.Equal(countBefore, manager.Playlists.Count);
    }
}
