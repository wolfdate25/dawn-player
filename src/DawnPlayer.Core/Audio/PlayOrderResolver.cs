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
/// Decides which item plays next: queue first, then repeat-one, then playlist order under the
/// active shuffle and repeat modes.
/// </summary>
public sealed class PlayOrderResolver
{
    private readonly AppSettings _settings;
    private readonly IPlaybackQueue _queue;
    private readonly Func<Playlist?> _fallbackPlaylist;
    private readonly Func<int, int> _nextRandom;

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

        // 3a. Album shuffle: stay inside the current album, then hop to a random other one.
        if (_settings.Playback.ShuffleMode == ShuffleMode.Albums && itemsSnapshot.Length > 1)
        {
            var curAlbumKey = ctx.CurrentItem?.Track?.AlbumKey ?? "";
            if (!string.IsNullOrEmpty(curAlbumKey) && curIdx >= 0)
            {
                if (!ctx.ManualAdvance && curIdx + 1 < itemsSnapshot.Length)
                {
                    var nextInSameAlbum = itemsSnapshot[curIdx + 1];
                    if (nextInSameAlbum != null && nextInSameAlbum.Track.AlbumKey == curAlbumKey && !skip.Contains(nextInSameAlbum))
                    {
                        return (pl, nextInSameAlbum);
                    }
                }
            }

            var albumGroups = PlaylistGroupBuilder.BuildGroupsFromItems(itemsSnapshot);
            var otherGroups = albumGroups.Where(g => g.Key != curAlbumKey && g.Any(i => !skip.Contains(i))).ToList();
            if (otherGroups.Count > 0)
            {
                var chosenGroup = otherGroups[_nextRandom(otherGroups.Count)];
                var firstPlayable = chosenGroup.FirstOrDefault(i => !skip.Contains(i));
                if (firstPlayable != null) return (pl, firstPlayable);
            }
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
}
