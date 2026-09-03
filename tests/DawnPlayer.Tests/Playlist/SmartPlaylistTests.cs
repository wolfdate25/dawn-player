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
        public void UpdateRating(Track track) { }
        public void UpdateReplayGain(Track track) { }
        public void ReplaceTracks(IReadOnlyCollection<Track> tracks) { }
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

    // ---------------- user-defined (query) smart playlists ----------------

    private static Track Rated(string path, int rating, int playCount = 0, string genre = "") => new()
    {
        Path = path,
        Title = path,
        Rating = rating,
        PlayCount = playCount,
        Genre = genre,
    };

    [Fact]
    public void AddUserSmartPlaylist_QueriesTheLibrary_AndSitsAfterBuiltIns()
    {
        var manager = ManagerWithTracks(
            Rated("a", 5), Rated("b", 3), Rated("c", 1), Rated("none", 0));
        EnsureDefaults(manager);

        var pl = manager.AddUserSmartPlaylist("favorites", "%rating% GREATER 2", out var error);

        Assert.NotNull(pl);
        Assert.Null(error);
        Assert.True(pl!.IsSmart);
        Assert.Equal("%rating% GREATER 2", pl.SmartQuery);
        Assert.Equal(new[] { "a", "b" }, pl.Items.Select(i => i.Track.Path).ToArray());
        // Sits after the three built-ins (Now Playing + 3 smart = index 4).
        Assert.Equal(4, manager.Playlists.IndexOf(pl));
        Assert.Equal("많이 재생", manager.Playlists[1].Name);
    }

    [Fact]
    public void AddUserSmartPlaylist_RejectsInvalidQuery_AndDuplicateName()
    {
        var manager = ManagerWithTracks(Rated("a", 4));
        EnsureDefaults(manager);

        Assert.Null(manager.AddUserSmartPlaylist("broken", "%rating% GREAT 2", out var badQuery));
        Assert.NotNull(badQuery);
        Assert.Equal(1 + 3, manager.Playlists.Count); // nothing was inserted

        manager.CreatePlaylist("taken");
        Assert.Null(manager.AddUserSmartPlaylist("taken", "%rating% GREATER 1", out var dupName));
        Assert.NotNull(dupName);
    }

    [Fact]
    public void RemovePlaylist_CanDeleteUserSmart_ButNotBuiltInSmart()
    {
        var manager = ManagerWithTracks(Rated("a", 5));
        EnsureDefaults(manager);

        var userSmart = manager.AddUserSmartPlaylist("mine", "%rating% GREATER 3", out _);
        var builtIn = manager.Playlists.First(p => p.IsSmart && p.SmartQuery == null);

        manager.RemovePlaylist(builtIn);
        Assert.Contains(builtIn, manager.Playlists);

        manager.RemovePlaylist(userSmart!);
        Assert.DoesNotContain(userSmart, manager.Playlists);
    }

    [Fact]
    public void UserSmartPlaylistsChanged_Fires_OnAddUpdateRemove()
    {
        var manager = ManagerWithTracks(Rated("a", 5));
        int fired = 0;
        manager.UserSmartPlaylistsChanged += () => fired++;

        var pl = manager.AddUserSmartPlaylist("mine", "%rating% GREATER 3", out _);
        Assert.Equal(1, fired);

        Assert.True(manager.TryUpdateUserSmartPlaylist(pl!, "renamed", "%rating% GREATER 4", out _));
        Assert.Equal(2, fired);
        Assert.Equal("renamed", pl!.Name);

        manager.RemovePlaylist(pl);
        Assert.Equal(3, fired);
    }

    [Fact]
    public void GetUserSmartPlaylists_RoundTrips_ThroughEnsure()
    {
        var manager = ManagerWithTracks(Rated("a", 5, playCount: 2), Rated("b", 2));
        manager.AddUserSmartPlaylist("first", "%rating% GREATER 3", out _);
        manager.AddUserSmartPlaylist("second", "%play_count% GREATER 0", out _);

        var saved = manager.GetUserSmartPlaylists();
        Assert.Equal(new[] { ("first", "%rating% GREATER 3"), ("second", "%play_count% GREATER 0") }, saved);

        // Rehydrate into a fresh manager (the startup path) and the playlists come back populated.
        var revived = ManagerWithTracks(Rated("a", 5, playCount: 2), Rated("b", 2));
        revived.EnsureUserSmartPlaylists(saved);
        var second = revived.Playlists.First(p => p.Name == "second");
        Assert.True(second.IsSmart);
        Assert.Equal("%play_count% GREATER 0", second.SmartQuery);
        Assert.Equal(new[] { "a" }, second.Items.Select(i => i.Track.Path).ToArray());
    }

    [Fact]
    public void Refresh_RegeneratesUserSmart_FromLiveRatings()
    {
        var a = Rated("a", 5);
        var b = Rated("b", 1);
        var manager = ManagerWithTracks(a, b);
        var pl = manager.AddUserSmartPlaylist("faves", "%rating% GREATER 3", out _);
        Assert.Equal(new[] { "a" }, pl!.Items.Select(i => i.Track.Path).ToArray());

        // Re-rating the shared working-set instance (what the rating command does) is picked up
        // by a refresh without any rescan.
        b.Rating = 4;
        manager.RefreshSmartPlaylists();

        Assert.Equal(new[] { "a", "b" }, pl.Items.Select(i => i.Track.Path).ToArray());
    }
}
