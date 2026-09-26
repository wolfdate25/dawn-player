using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// Play-order policy: queue precedence, repeat modes, and the two shuffle modes. The random source
/// is injected, so shuffle decisions are asserted exactly rather than statistically.
/// </summary>
public sealed class PlayOrderResolverTests
{
    private static Track TrackAt(string path, string artist = "Artist", string album = "Album") => new()
    {
        Path = path,
        Title = System.IO.Path.GetFileNameWithoutExtension(path),
        Artist = artist,
        Album = album,
        DurationMs = 200_000
    };

    private static Playlist PlaylistOf(string name, params PlaylistItem[] items)
    {
        var pl = new Playlist(name);
        foreach (var item in items) pl.Items.Add(item);
        return pl;
    }

    private static PlaylistItem Item(string path, string artist = "Artist", string album = "Album") =>
        new(TrackAt(path, artist, album));

    private static AppSettings Settings(
        RepeatMode repeat = RepeatMode.Off,
        ShuffleMode shuffle = ShuffleMode.Off)
    {
        var s = AppSettings.CreateDefault();
        s.Playback.Repeat = repeat;
        s.Playback.ShuffleMode = shuffle;
        return s;
    }

    /// <summary>Deterministic stand-in for Random.Shared.Next(exclusiveUpperBound).</summary>
    private static Func<int, int> Draws(params int[] values)
    {
        int i = 0;
        return _ => values[Math.Min(i++, values.Length - 1)];
    }

    private static PlayOrderResolver Resolver(
        AppSettings settings,
        PlaybackQueue queue,
        Playlist? fallback = null,
        Func<int, int>? random = null) =>
        new(settings, queue, () => fallback, random ?? Draws(0));

    // ---------------- queue precedence ----------------

    [Fact]
    public void QueuedEntry_WinsOverPlaylistOrder()
    {
        var a = Item(@"C:\m\a.flac");
        var b = Item(@"C:\m\b.flac");
        var queued = Item(@"C:\m\queued.flac");
        var pl = PlaylistOf("Main", a, b);

        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { queued });

        var next = Resolver(Settings(), queue, pl)
            .PeekNext(new PlayOrderContext(pl, a, StopAfterCurrent: false, ManualAdvance: false), new HashSet<PlaylistItem>());

        Assert.NotNull(next);
        Assert.Same(queued, next!.Value.Item);
    }

    [Fact]
    public void QueuedEntryInSkipSet_IsPassedOverForTheNextQueuedEntry()
    {
        var a = Item(@"C:\m\a.flac");
        var dead = Item(@"C:\m\dead.flac");
        var alive = Item(@"C:\m\alive.flac");
        var pl = PlaylistOf("Main", a);

        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { dead, alive });

        var next = Resolver(Settings(), queue, pl)
            .PeekNext(new PlayOrderContext(pl, a, false, false), new HashSet<PlaylistItem> { dead });

        Assert.Same(alive, next!.Value.Item);
    }

    [Fact]
    public void QueuedEntryWithoutOwningPlaylist_FallsBackToTheSuppliedPlaylist()
    {
        var fallback = PlaylistOf("Fallback", Item(@"C:\m\x.flac"));
        var orphan = Item(@"C:\m\orphan.flac");

        var queue = new PlaybackQueue();
        queue.Enqueue(null, new[] { orphan });

        var next = Resolver(Settings(), queue, fallback)
            .PeekNext(new PlayOrderContext(null, null, false, false), new HashSet<PlaylistItem>());

        Assert.NotNull(next);
        Assert.Same(orphan, next!.Value.Item);
        Assert.Same(fallback, next.Value.Playlist);
    }

    [Fact]
    public void NoPlaylistAnywhere_ReturnsNullRatherThanCreatingOne()
    {
        // The creating accessors mutate the UI-bound collection, so resolution must tolerate null.
        var next = Resolver(Settings(), new PlaybackQueue(), fallback: null)
            .PeekNext(new PlayOrderContext(null, null, false, false), new HashSet<PlaylistItem>());

        Assert.Null(next);
    }

    // ---------------- stop-after-current / repeat ----------------

    [Fact]
    public void StopAfterCurrent_EndsNaturalAdvanceButNotManualNext()
    {
        var a = Item(@"C:\m\a.flac");
        var b = Item(@"C:\m\b.flac");
        var pl = PlaylistOf("Main", a, b);
        var resolver = Resolver(Settings(), new PlaybackQueue(), pl);

        Assert.Null(resolver.PeekNext(
            new PlayOrderContext(pl, a, StopAfterCurrent: true, ManualAdvance: false), new HashSet<PlaylistItem>()));

        var manual = resolver.PeekNext(
            new PlayOrderContext(pl, a, StopAfterCurrent: true, ManualAdvance: true), new HashSet<PlaylistItem>());
        Assert.Same(b, manual!.Value.Item);
    }

    [Fact]
    public void RepeatOne_LoopsCurrentOnNaturalAdvanceOnly()
    {
        var a = Item(@"C:\m\a.flac");
        var b = Item(@"C:\m\b.flac");
        var pl = PlaylistOf("Main", a, b);
        var resolver = Resolver(Settings(RepeatMode.One), new PlaybackQueue(), pl);

        var natural = resolver.PeekNext(new PlayOrderContext(pl, a, false, false), new HashSet<PlaylistItem>());
        Assert.Same(a, natural!.Value.Item);

        // Manual next must escape the loop, otherwise the Next button does nothing.
        var manual = resolver.PeekNext(new PlayOrderContext(pl, a, false, true), new HashSet<PlaylistItem>());
        Assert.Same(b, manual!.Value.Item);
    }

    [Fact]
    public void LinearOrder_AtEndOfPlaylist_StopsWhenRepeatIsOff()
    {
        var a = Item(@"C:\m\a.flac");
        var b = Item(@"C:\m\b.flac");
        var pl = PlaylistOf("Main", a, b);

        var next = Resolver(Settings(), new PlaybackQueue(), pl)
            .PeekNext(new PlayOrderContext(pl, b, false, false), new HashSet<PlaylistItem>());

        Assert.Null(next);
    }

    [Fact]
    public void LinearOrder_AtEndOfPlaylist_WrapsWhenRepeatIsAll()
    {
        var a = Item(@"C:\m\a.flac");
        var b = Item(@"C:\m\b.flac");
        var pl = PlaylistOf("Main", a, b);

        var next = Resolver(Settings(RepeatMode.All), new PlaybackQueue(), pl)
            .PeekNext(new PlayOrderContext(pl, b, false, false), new HashSet<PlaylistItem>());

        Assert.Same(a, next!.Value.Item);
    }

    [Fact]
    public void UnknownCurrentItem_StartsFromTheTopOfThePlaylist()
    {
        var a = Item(@"C:\m\a.flac");
        var b = Item(@"C:\m\b.flac");
        var pl = PlaylistOf("Main", a, b);
        var stranger = Item(@"C:\m\elsewhere.flac");

        var next = Resolver(Settings(), new PlaybackQueue(), pl)
            .PeekNext(new PlayOrderContext(pl, stranger, false, false), new HashSet<PlaylistItem>());

        Assert.Same(a, next!.Value.Item);
    }

    // ---------------- album shuffle ----------------

    [Fact]
    public void AlbumShuffle_NaturalAdvance_StaysInsideTheCurrentAlbum()
    {
        var a1 = Item(@"C:\m\a1.flac", "Artist A", "Album A");
        var a2 = Item(@"C:\m\a2.flac", "Artist A", "Album A");
        var b1 = Item(@"C:\m\b1.flac", "Artist B", "Album B");
        var pl = PlaylistOf("Main", a1, a2, b1);

        var next = Resolver(Settings(shuffle: ShuffleMode.Albums), new PlaybackQueue(), pl)
            .PeekNext(new PlayOrderContext(pl, a1, false, false), new HashSet<PlaylistItem>());

        Assert.Same(a2, next!.Value.Item);
    }

    [Fact]
    public void AlbumShuffle_WhenAlbumFinishes_HopsToADifferentAlbum()
    {
        var a1 = Item(@"C:\m\a1.flac", "Artist A", "Album A");
        var a2 = Item(@"C:\m\a2.flac", "Artist A", "Album A");
        var b1 = Item(@"C:\m\b1.flac", "Artist B", "Album B");
        var b2 = Item(@"C:\m\b2.flac", "Artist B", "Album B");
        var pl = PlaylistOf("Main", a1, a2, b1, b2);

        // a2 is the last track of album A, so the resolver picks another album group.
        var next = Resolver(Settings(shuffle: ShuffleMode.Albums), new PlaybackQueue(), pl, Draws(0))
            .PeekNext(new PlayOrderContext(pl, a2, false, false), new HashSet<PlaylistItem>());

        Assert.NotNull(next);
        Assert.NotEqual(a2.Track.AlbumKey, next!.Value.Item.Track.AlbumKey);
        Assert.Same(b1, next.Value.Item);
    }

    [Fact]
    // Spec change (approved): manual Next moves within the album like natural advance
    // (it only escapes repeat-one looping and ignores stop-after-current). The previous
    // expectation - manual Next abandoning the album immediately - is superseded.
    public void AlbumShuffle_ManualAdvance_StaysInsideTheCurrentAlbum()
    {
        var a1 = Item(@"C:\m\a1.flac", "Artist A", "Album A");
        var a2 = Item(@"C:\m\a2.flac", "Artist A", "Album A");
        var b1 = Item(@"C:\m\b1.flac", "Artist B", "Album B");
        var pl = PlaylistOf("Main", a1, a2, b1);

        var next = Resolver(Settings(shuffle: ShuffleMode.Albums), new PlaybackQueue(), pl, Draws(0))
            .PeekNext(new PlayOrderContext(pl, a1, false, ManualAdvance: true), new HashSet<PlaylistItem>());

        Assert.Same(a2, next!.Value.Item);
    }

    [Fact]
    public void AlbumShuffle_FragmentedPlaylist_StaysInsideTheAlbum()
    {
        // The playlist is NOT album-ordered (title sort, library insertion order, ...): the
        // next track of the current album is not adjacent, so adjacency-only advance would
        // abandon the album after a single track.
        var a1 = Item(@"C:\m\a1.flac", "Artist A", "Album A");
        var b1 = Item(@"C:\m\b1.flac", "Artist B", "Album B");
        var a2 = Item(@"C:\m\a2.flac", "Artist A", "Album A");
        var b2 = Item(@"C:\m\b2.flac", "Artist B", "Album B");
        var pl = PlaylistOf("Main", a1, b1, a2, b2);

        var next = Resolver(Settings(shuffle: ShuffleMode.Albums), new PlaybackQueue(), pl, Draws(0))
            .PeekNext(new PlayOrderContext(pl, a1, false, false), new HashSet<PlaylistItem>());

        Assert.Same(a2, next!.Value.Item);
    }

    [Fact]
    public void AlbumShuffle_HopStartsAtTheChosenAlbumsFirstTrack()
    {
        // Hopping to the next album starts at its first track in list order, not mid-album.
        var a1 = Item(@"C:\m\a1.flac", "Artist A", "Album A");
        var a2 = Item(@"C:\m\a2.flac", "Artist A", "Album A");
        var b1 = Item(@"C:\m\b1.flac", "Artist B", "Album B");
        var b2 = Item(@"C:\m\b2.flac", "Artist B", "Album B");
        var c1 = Item(@"C:\m\c1.flac", "Artist C", "Album C");
        var pl = PlaylistOf("Main", a1, a2, b1, b2, c1);

        // Draws(1) keeps the Fisher-Yates order [B, C], so B is hopped to first.
        var next = Resolver(Settings(RepeatMode.All, ShuffleMode.Albums), new PlaybackQueue(), pl, Draws(1))
            .PeekNext(new PlayOrderContext(pl, a2, false, false), new HashSet<PlaylistItem>());

        Assert.Same(b1, next!.Value.Item);
    }

    [Fact]
    public void AlbumShuffle_VisitsEveryAlbumOncePerCycle()
    {
        // A shuffled per-cycle deck over distinct albums: one full coverage without repeating
        // an album, instead of the old memoryless hop (which could replay one album while
        // starving another). Draws(0) fixes the Fisher-Yates order deterministically:
        // candidates [B, C] -> swap(1, 0) -> [C, B], so C comes first.
        var a1 = Item(@"C:\m\a1.flac", "Artist A", "Album A");
        var a2 = Item(@"C:\m\a2.flac", "Artist A", "Album A");
        var b1 = Item(@"C:\m\b1.flac", "Artist B", "Album B");
        var c1 = Item(@"C:\m\c1.flac", "Artist C", "Album C");
        var pl = PlaylistOf("Main", a1, a2, b1, c1);

        var resolver = Resolver(Settings(RepeatMode.All, ShuffleMode.Albums), new PlaybackQueue(), pl, Draws(0));
        var skip = new HashSet<PlaylistItem>();

        Assert.Same(a2, resolver.PeekNext(new PlayOrderContext(pl, a1, false, false), skip)!.Value.Item);
        Assert.Same(c1, resolver.PeekNext(new PlayOrderContext(pl, a2, false, false), skip)!.Value.Item);
        Assert.Same(b1, resolver.PeekNext(new PlayOrderContext(pl, c1, false, false), skip)!.Value.Item);

        // Deck exhausted -> a new cycle starts (Repeat.All), landing on a different album
        // than the just-finished one rather than replaying it.
        var cycleRestart = resolver.PeekNext(new PlayOrderContext(pl, b1, false, false), skip);
        Assert.NotNull(cycleRestart);
        Assert.NotEqual(b1.Track.AlbumKey, cycleRestart!.Value.Item.Track.AlbumKey);
    }

    [Fact]
    public void AlbumShuffle_RepeatOff_StopsAfterFullCoverage()
    {
        var a1 = Item(@"C:\m\a1.flac", "Artist A", "Album A");
        var a2 = Item(@"C:\m\a2.flac", "Artist A", "Album A");
        var b1 = Item(@"C:\m\b1.flac", "Artist B", "Album B");
        var pl = PlaylistOf("Main", a1, a2, b1);

        var resolver = Resolver(Settings(RepeatMode.Off, ShuffleMode.Albums), new PlaybackQueue(), pl, Draws(0));
        var skip = new HashSet<PlaylistItem>();

        Assert.Same(a2, resolver.PeekNext(new PlayOrderContext(pl, a1, false, false), skip)!.Value.Item);
        Assert.Same(b1, resolver.PeekNext(new PlayOrderContext(pl, a2, false, false), skip)!.Value.Item);
        Assert.Null(resolver.PeekNext(new PlayOrderContext(pl, b1, false, false), skip));
    }

    [Fact]
    public void AlbumShuffle_SingleAlbumRepeatOff_PlaysThroughOnceThenStops()
    {
        var a1 = Item(@"C:\m\a1.flac", "Artist A", "Album A");
        var a2 = Item(@"C:\m\a2.flac", "Artist A", "Album A");
        var pl = PlaylistOf("Main", a1, a2);

        var resolver = Resolver(Settings(RepeatMode.Off, ShuffleMode.Albums), new PlaybackQueue(), pl, Draws(0));
        var skip = new HashSet<PlaylistItem>();

        Assert.Same(a2, resolver.PeekNext(new PlayOrderContext(pl, a1, false, false), skip)!.Value.Item);
        Assert.Null(resolver.PeekNext(new PlayOrderContext(pl, a2, false, false), skip));
    }

    // ---------------- track shuffle ----------------

    [Fact]
    public void TrackShuffle_DrawsFromTheDeckAndNeverRepeatsTheCurrentIndex()
    {
        var items = Enumerable.Range(0, 4).Select(i => Item($@"C:\m\{i}.flac")).ToArray();
        var pl = PlaylistOf("Main", items);

        // First draw lands on the current index and must be rejected; the second is taken.
        var next = Resolver(Settings(shuffle: ShuffleMode.Tracks), new PlaybackQueue(), pl, Draws(1, 3))
            .PeekNext(new PlayOrderContext(pl, items[1], false, false), new HashSet<PlaylistItem>());

        Assert.Same(items[3], next!.Value.Item);
    }

    [Fact]
    public void TrackShuffle_WhenEveryDrawIsSkipped_FallsBackToLinearOrder()
    {
        var items = Enumerable.Range(0, 3).Select(i => Item($@"C:\m\{i}.flac")).ToArray();
        var pl = PlaylistOf("Main", items);

        // Every draw returns a skipped item, so the 16 attempts are exhausted and linear wins.
        var skip = new HashSet<PlaylistItem> { items[2] };
        var next = Resolver(Settings(shuffle: ShuffleMode.Tracks), new PlaybackQueue(), pl, Draws(2))
            .PeekNext(new PlayOrderContext(pl, items[0], false, false), skip);

        Assert.Same(items[1], next!.Value.Item);
    }

    [Fact]
    public void EveryItemSkipped_ReturnsNull()
    {
        var a = Item(@"C:\m\a.flac");
        var b = Item(@"C:\m\b.flac");
        var pl = PlaylistOf("Main", a, b);

        var next = Resolver(Settings(RepeatMode.All), new PlaybackQueue(), pl)
            .PeekNext(new PlayOrderContext(pl, a, false, false), new HashSet<PlaylistItem> { a, b });

        Assert.Null(next);
    }

    [Fact]
    public void EmptyPlaylist_ReturnsNull()
    {
        var pl = PlaylistOf("Empty");

        var next = Resolver(Settings(RepeatMode.All, ShuffleMode.Tracks), new PlaybackQueue(), pl)
            .PeekNext(new PlayOrderContext(pl, null, false, false), new HashSet<PlaylistItem>());

        Assert.Null(next);
    }
}
