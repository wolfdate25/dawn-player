using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

// NOTE: deliberately NOT namespace DawnPlayer.Tests.Playlist — creating that namespace would
// shadow the Playlist type for every test that references it unqualified.
namespace DawnPlayer.Tests;

/// <summary>
/// Smart playlists: creation/placement, query semantics for each kind, and the guards that keep
/// the generated playlists out of the user-playlist lifecycle (rename/delete/current fallback).
/// </summary>
public sealed class SmartPlaylistTests
{
    private sealed class MemoryLibrary : IMusicLibrary
    {
        public List<Track> TracksList { get; } = new();
        public IReadOnlyList<Track> Tracks => TracksList;
        public int Count => TracksList.Count;
        public event Action? TracksChanged;
#pragma warning disable CS0067
        public event Action<ScanProgress>? ScanProgress;
#pragma warning restore CS0067
        public Track? GetTrack(string path) => TracksList.FirstOrDefault(t => t.Path == path);
        public void UpdateStats(Track track) { }
        public void LoadFromDb() => TracksChanged?.Invoke();
        public Task ScanAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static Track TrackAt(string path, int playCount = 0, int skipCount = 0,
        long lastPlayed = 0, long firstSeen = 0) => new()
    {
        Path = path,
        Title = path,
        PlayCount = playCount,
        SkipCount = skipCount,
        LastPlayedUtcTicks = lastPlayed,
        FirstSeenUtcTicks = firstSeen,
    };

    private static PlaylistManager ManagerWithTracks(params Track[] tracks)
    {
        var library = new MemoryLibrary();
        library.TracksList.AddRange(tracks);
        return new PlaylistManager(library);
    }

    private static void EnsureDefaults(PlaylistManager manager) =>
        manager.EnsureSmartPlaylists(new[]
        {
            (SmartPlaylistKind.MostPlayed, "많이 재생"),
            (SmartPlaylistKind.RecentlyAdded, "최근 추가"),
            (SmartPlaylistKind.NotRecentlyPlayed, "한동안 안 들은"),
        });

    [Fact]
    public void EnsureSmartPlaylists_SitsDirectlyUnderNowPlaying()
    {
        var manager = ManagerWithTracks();
        EnsureDefaults(manager);

        Assert.Equal(4, manager.Playlists.Count);
        Assert.True(manager.Playlists[0].IsSystem);
        Assert.False(manager.Playlists[0].IsSmart);
        Assert.Equal(new[] { "많이 재생", "최근 추가", "한동안 안 들은" },
            manager.Playlists.Skip(1).Select(p => p.Name).ToArray());
        Assert.All(manager.Playlists.Skip(1), p => Assert.True(p.IsSmart));
    }

    [Fact]
    public void MostPlayed_OrdersByCountDesc_AndExcludesUnplayed()
    {
        var manager = ManagerWithTracks(
            TrackAt("a", playCount: 3),
            TrackAt("b", playCount: 10),
            TrackAt("c"), // never played → excluded
            TrackAt("d", playCount: 10, lastPlayed: 99));
        EnsureDefaults(manager);

        var mostPlayed = manager.Playlists.First(p => p.Name == "많이 재생");
        Assert.Equal(new[] { "d", "b", "a" }, mostPlayed.Items.Select(i => i.Track.Path).ToArray());
    }

    [Fact]
    public void RecentlyAdded_OrdersByFirstSeenDesc()
    {
        var manager = ManagerWithTracks(
            TrackAt("old", firstSeen: 10),
            TrackAt("newest", firstSeen: 30),
            TrackAt("mid", firstSeen: 20));
        EnsureDefaults(manager);

        var recent = manager.Playlists.First(p => p.Name == "최근 추가");
        Assert.Equal(new[] { "newest", "mid", "old" }, recent.Items.Select(i => i.Track.Path).ToArray());
    }

    [Fact]
    public void NotRecentlyPlayed_NeverPlayedSortsFirst()
    {
        var manager = ManagerWithTracks(
            TrackAt("long-ago", playCount: 5, lastPlayed: 100),
            TrackAt("never"), // last_played = 0 → the most forgotten
            TrackAt("yesterday", playCount: 5, lastPlayed: 900));
        EnsureDefaults(manager);

        var forgotten = manager.Playlists.First(p => p.Name == "한동안 안 들은");
        Assert.Equal(new[] { "never", "long-ago", "yesterday" },
            forgotten.Items.Select(i => i.Track.Path).ToArray());
    }

    [Fact]
    public void SmartPlaylists_AreImmuneToRenameAndDelete()
    {
        var manager = ManagerWithTracks(TrackAt("a", playCount: 1));
        EnsureDefaults(manager);
        var smart = manager.Playlists.First(p => p.IsSmart);
        int before = manager.Playlists.Count;

        manager.RenamePlaylist(smart, "hijacked");
        manager.RemovePlaylist(smart);

        Assert.Equal(before, manager.Playlists.Count);
        Assert.Equal("많이 재생", smart.Name);
    }

    [Fact]
    public void Current_FallsBackToUserPlaylist_NotSmart()
    {
        var manager = ManagerWithTracks(TrackAt("a", playCount: 1));
        EnsureDefaults(manager);
        var user = manager.CreatePlaylist("mine");

        // Selecting a smart playlist makes it current, but the fallback (used when the selected
        // one goes away) must skip to a real user playlist.
        manager.SelectPlaylist(manager.Playlists.First(p => p.IsSmart));
        manager.RemovePlaylist(user);

        Assert.False(manager.Current.IsSmart, "fallback must not land on a generated playlist");
    }

    [Fact]
    public void Refresh_RegeneratesContents_FromLiveStats()
    {
        var manager = ManagerWithTracks(TrackAt("a", playCount: 1));
        EnsureDefaults(manager);

        var mostPlayed = manager.Playlists.First(p => p.Name == "많이 재생");
        var track = mostPlayed.Items.Single().Track;

        // A later play bumps the count on the shared Track instance; a refresh must reflect it
        // even though no new file was scanned.
        track.PlayCount = 42;
        manager.RefreshSmartPlaylists();

        Assert.Equal(42, mostPlayed.Items.Single().Track.PlayCount);
    }
}
