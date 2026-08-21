using System;
using System.Collections.Generic;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Playlists;

/// <summary>
/// Thread-safe playback queue contract for foobar2000-style queued playback.
/// Queued items take precedence over linear or shuffled playlist advance.
/// </summary>
public interface IPlaybackQueue
{
    /// <summary>Immutable snapshot of all active queue entries.</summary>
    IReadOnlyList<QueueEntry> Entries { get; }

    /// <summary>Current count of queued items.</summary>
    int Count { get; }

    /// <summary>Replaces queue items starting with head at startIndex.</summary>
    void SetItems(IEnumerable<PlaylistItem> items, int startIndex);

    /// <summary>Appends items to tail of the queue (moving existing items to tail).</summary>
    void Enqueue(IEnumerable<PlaylistItem> items);

    /// <summary>Appends items to tail of the queue with playlist association.</summary>
    void Enqueue(Playlist? playlist, IEnumerable<PlaylistItem> items);

    /// <summary>Prepends items to head of the queue (moving existing items to head).</summary>
    void EnqueueNext(IEnumerable<PlaylistItem> items);

    /// <summary>Prepends items to head of the queue with playlist association.</summary>
    void EnqueueNext(Playlist? playlist, IEnumerable<PlaylistItem> items);

    /// <summary>Moves an entry from fromIndex to toIndex. Returns true if successfully moved.</summary>
    bool Move(int fromIndex, int toIndex);

    /// <summary>Removes the entry at the specified 0-based index. Returns true if removed.</summary>
    bool Remove(int index);

    /// <summary>Removes the entry at the specified 0-based index (legacy compatibility).</summary>
    void RemoveAt(int index);

    /// <summary>Batch removes all occurrences of the specified items.</summary>
    void RemoveItems(IEnumerable<PlaylistItem> items);

    /// <summary>Clears all entries from the queue and resets their QueueIndex to -1.</summary>
    void Clear();

    /// <summary>Randomizes the order of all items currently in the queue.</summary>
    void Shuffle();

    /// <summary>Returns the current head entry without removing it.</summary>
    QueueEntry? GetCurrent();

    /// <summary>Returns the next entry (index 1) without removing it.</summary>
    QueueEntry? GetNext();

    /// <summary>
    /// Returns the first entry whose item satisfies <paramref name="predicate"/>, or null when none
    /// does. Prefer this over scanning <see cref="Entries"/>, which copies the whole queue per read.
    /// The predicate is evaluated under the queue's lock and must not call back into the queue.
    /// </summary>
    QueueEntry? FirstMatching(Func<PlaylistItem, bool> predicate);

    /// <summary>Returns the head entry without removing it (foobar style).</summary>
    QueueEntry? Peek();

    /// <summary>Removes and returns the head entry.</summary>
    QueueEntry? Dequeue();

    /// <summary>
    /// Removes the entry for <paramref name="item"/> wherever it sits in the queue. Returns true
    /// when an entry was removed. Use this when a track starts playing — the started track is not
    /// necessarily the head.
    /// </summary>
    bool Consume(PlaylistItem? item);

    /// <summary>Fires whenever queue items or order change.</summary>
    event Action? Changed;
}
