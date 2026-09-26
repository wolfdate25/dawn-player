using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// The playback state a play-order decision depends on, captured as one snapshot.
/// </summary>
/// <remarks>
/// Taking a snapshot rather than reading live fields is what lets the resolver run outside the
/// controller's state lock, and it is what makes the policy testable without an output session.
/// </remarks>
public sealed record PlayOrderContext(
    Playlist? CurrentPlaylist,
    PlaylistItem? CurrentItem,
    bool StopAfterCurrent,
    bool ManualAdvance);

/// <summary>
/// Decides which item plays next. Injectable into <see cref="PlaybackController"/> so alternate
/// orderings (e.g. weighted shuffle) are a constructor argument, not a resolver edit.
/// </summary>
public interface IPlayOrderStrategy
{
    /// <summary>
    /// Returns the next item to play, or null when the sequence should stop.
    /// <paramref name="skip"/> holds items already found unplayable in this resolution pass.
    /// </summary>
    (Playlist Playlist, PlaylistItem Item)? PeekNext(PlayOrderContext ctx, ISet<PlaylistItem> skip);
}

/// <summary>
/// Default strategy: queue first, then repeat-one, then playlist order under the active shuffle
/// and repeat modes.
/// </summary>
public sealed class PlayOrderResolver : IPlayOrderStrategy
{
    private readonly AppSettings _settings;
    private readonly IPlaybackQueue _queue;
    private readonly Func<Playlist?> _fallbackPlaylist;
    private readonly Func<int, int> _nextRandom;

    // Album-shuffle deck: distinct album keys in shuffled order for the current cycle.
    // Rebuilt whenever the playlist or its album universe changes. Guarded because
    // resolution runs on the thread pool (prefetch and Next can overlap); a lost race
    // only defers one album to the next cycle.
    private readonly object _albumLock = new();
    private Playlist? _albumDeckPlaylist;
    private HashSet<string> _albumDeckUniverse = new(StringComparer.Ordinal);
    private List<string> _albumDeck = new();

    /// <param name="fallbackPlaylist">
    /// Supplies a playlist when the context carries none. Must not create one — this runs on the
    /// thread pool, and the creating accessors mutate the UI-bound playlist collection.
    /// </param>
    /// <param name="nextRandom">
    /// Exclusive-upper-bound random source, injectable so shuffle behavior can be tested
    /// deterministically. Defaults to <see cref="Random.Shared"/>.
    /// </param>
    public PlayOrderResolver(
        AppSettings settings,
        IPlaybackQueue queue,
        Func<Playlist?> fallbackPlaylist,
        Func<int, int>? nextRandom = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _fallbackPlaylist = fallbackPlaylist ?? throw new ArgumentNullException(nameof(fallbackPlaylist));
        _nextRandom = nextRandom ?? Random.Shared.Next;
    }

    /// <summary>
    /// Returns the next item to play, or null when the sequence should stop.
    /// <paramref name="skip"/> holds items already found unplayable in this resolution pass.
    /// </summary>
    public (Playlist Playlist, PlaylistItem Item)? PeekNext(PlayOrderContext ctx, ISet<PlaylistItem> skip)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        skip ??= new HashSet<PlaylistItem>();

        if (!ctx.ManualAdvance && ctx.StopAfterCurrent) return null;

        // 1. playback queue always wins
        var queued = _queue.FirstMatching(item => !skip.Contains(item));
        if (queued?.Item != null)
        {
            var owner = queued.Playlist ?? ctx.CurrentPlaylist ?? _fallbackPlaylist();
            if (owner != null) return (owner, queued.Item);
        }

        // 2. repeat-one loops the current track (natural advance only)
        if (!ctx.ManualAdvance && _settings.Playback.Repeat == RepeatMode.One &&
            ctx.CurrentItem != null && !skip.Contains(ctx.CurrentItem) && ctx.CurrentPlaylist != null)
            return (ctx.CurrentPlaylist, ctx.CurrentItem);

        // 3. playlist order (shuffle/linear with repeat)
        var pl = ctx.CurrentPlaylist ?? _fallbackPlaylist();
        if (pl == null) return null;

        var itemsSnapshot = pl.GetSnapshot();
        if (itemsSnapshot.Length == 0 || itemsSnapshot.All(i => i == null || skip.Contains(i)))
            return null;

        int curIdx = ctx.CurrentItem != null ? Array.IndexOf(itemsSnapshot, ctx.CurrentItem) : -1;

        // 3a. Album shuffle: finish the current album in list order, then hop to the next
        // album of a shuffled per-cycle deck over distinct albums. The deck (not a memoryless
        // hop) is what visits every album once per cycle; manual Next moves within the album
        // just like natural advance.
        if (_settings.Playback.ShuffleMode == ShuffleMode.Albums && itemsSnapshot.Length > 1)
        {
            var curAlbumKey = ctx.CurrentItem?.Track?.AlbumKey ?? "";
            if (!string.IsNullOrEmpty(curAlbumKey))
            {
                var restOfAlbum = FirstInAlbum(itemsSnapshot, curAlbumKey, Math.Max(curIdx + 1, 0), skip);
                if (restOfAlbum != null) return (pl, restOfAlbum);
            }

            var nextAlbumItem = TakeNextAlbumItem(pl, itemsSnapshot, curAlbumKey, skip);
            if (nextAlbumItem != null) return (pl, nextAlbumItem);
            // Deck exhausted with nothing left to start: stop after one full coverage instead
            // of replaying albums. (Repeat.One manual escape keeps the linear tail below.)
            if (_settings.Playback.Repeat == RepeatMode.Off && !string.IsNullOrEmpty(curAlbumKey))
                return null;
        }

        // 3b. Track shuffle. Falls through to linear order when the draws keep hitting skips.
        if (_settings.Playback.ShuffleMode == ShuffleMode.Tracks && itemsSnapshot.Length > 1)
        {
            for (int tries = 0; tries < 16; tries++)
            {
                int j = _nextRandom(itemsSnapshot.Length);
                var candidate = itemsSnapshot[j];
                if (j != curIdx && candidate != null && !skip.Contains(candidate))
                    return (pl, candidate);
            }
        }

        for (int i = curIdx + 1; i < itemsSnapshot.Length; i++)
        {
            var candidate = itemsSnapshot[i];
            if (candidate != null && !skip.Contains(candidate)) return (pl, candidate);
        }

        if (_settings.Playback.Repeat == RepeatMode.All)
        {
            for (int i = 0; i < itemsSnapshot.Length; i++)
            {
                var candidate = itemsSnapshot[i];
                if (candidate != null && !skip.Contains(candidate)) return (pl, candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// First unskipped track of <paramref name="albumKey"/> at or after
    /// <paramref name="start"/>, in playlist order. A forward scan (not just the adjacent
    /// index) is what keeps a fragmented album together when the playlist is not
    /// album-ordered.
    /// </summary>
    private static PlaylistItem? FirstInAlbum(PlaylistItem[] snapshot, string albumKey, int start, ISet<PlaylistItem> skip)
    {
        for (int i = start; i < snapshot.Length; i++)
        {
            var candidate = snapshot[i];
            if (candidate?.Track == null || skip.Contains(candidate)) continue;
            if (string.Equals(candidate.Track.AlbumKey, albumKey, StringComparison.Ordinal)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Pops the next album off the shuffled per-cycle deck and returns its first unskipped
    /// track, or null when the deck is exhausted (or everything left is skipped). Albums are
    /// distinct <see cref="Track.AlbumKey"/> values in first-appearance order — deliberately
    /// not <see cref="PlaylistGroupBuilder"/> runs, which split one fragmented
    /// album into several groups and biased hops toward it.
    /// </summary>
    private PlaylistItem? TakeNextAlbumItem(Playlist pl, PlaylistItem[] snapshot, string curAlbumKey, ISet<PlaylistItem> skip)
    {
        var universe = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot)
        {
            var key = item?.Track?.AlbumKey;
            if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;
            universe.Add(key);
        }

        lock (_albumLock)
        {
            if (!ReferenceEquals(_albumDeckPlaylist, pl) || !_albumDeckUniverse.SetEquals(universe))
            {
                _albumDeckPlaylist = pl;
                _albumDeckUniverse = new HashSet<string>(universe, StringComparer.Ordinal);
                _albumDeck = ShuffleAlbumKeys(universe, curAlbumKey);
            }

            var picked = PopNextAlbum(snapshot, curAlbumKey, skip);
            if (picked != null) return picked;

            if (_settings.Playback.Repeat == RepeatMode.All)
            {
                _albumDeck = ShuffleAlbumKeys(universe, curAlbumKey);
                picked = PopNextAlbum(snapshot, curAlbumKey, skip);
                if (picked != null) return picked;
                // Single-album universe: loop the album from the top.
                if (!string.IsNullOrEmpty(curAlbumKey))
                    return FirstInAlbum(snapshot, curAlbumKey, 0, skip);
            }
            return null;
        }
    }

    private PlaylistItem? PopNextAlbum(PlaylistItem[] snapshot, string curAlbumKey, ISet<PlaylistItem> skip)
    {
        while (_albumDeck.Count > 0)
        {
            var key = _albumDeck[0];
            _albumDeck.RemoveAt(0);
            if (key == curAlbumKey) continue; // stale entry after a manual jump
            var first = FirstInAlbum(snapshot, key, 0, skip);
            if (first != null) return first;
            // A fully skipped album is passed over within this same call.
        }
        return null;
    }

    /// <summary>Fisher-Yates shuffle of the distinct album keys, excluding the current one.</summary>
    private List<string> ShuffleAlbumKeys(List<string> universe, string exclude)
    {
        var keys = new List<string>(universe.Count);
        foreach (var key in universe)
        {
            if (key != exclude) keys.Add(key);
        }
        for (int i = keys.Count - 1; i > 0; i--)
        {
            int j = Math.Clamp(_nextRandom(i + 1), 0, i);
            (keys[i], keys[j]) = (keys[j], keys[i]);
        }
        return keys;
    }
}
