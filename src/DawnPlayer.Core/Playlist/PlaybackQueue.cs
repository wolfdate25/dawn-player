using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Playlists;

public sealed record QueueEntry(Playlist? Playlist, PlaylistItem Item, string Title, string Subtitle);

/// <summary>
/// foobar2000-style playback queue: queued items play (in order) before
/// normal playlist advance resumes. Entries reference concrete playlist items.
/// Conforms to <see cref="IPlaybackQueue"/> with zero lock inversion.
/// </summary>
public sealed class PlaybackQueue : IPlaybackQueue
{
    private readonly List<QueueEntry> _entries = new();
    private readonly object _lock = new();
    private long _version;

    public event Action? Changed;

    public ReadOnlyCollection<QueueEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList().AsReadOnly();
            }
        }
    }

    IReadOnlyList<QueueEntry> IPlaybackQueue.Entries => Entries;

    public int Count
    {
        get
        {
            lock (_lock) return _entries.Count;
        }
    }

    public void Enqueue(IEnumerable<PlaylistItem> items) => Enqueue(null, items);

    public void Enqueue(Playlist? playlist, IEnumerable<PlaylistItem> items)
    {
        if (items == null) return;
        List<(PlaylistItem Item, int Index)> updates;
        long opVersion;

        lock (_lock)
        {
            var validItems = items.Where(i => i?.Track != null).Distinct().ToList();
            if (validItems.Count == 0) return;

            // Remove any existing entries for these items to move them to tail
            var itemSet = new HashSet<PlaylistItem>(validItems);
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (itemSet.Contains(_entries[i].Item))
                {
                    _entries.RemoveAt(i);
                }
            }

            // Append to the tail in relative order
            foreach (var item in validItems)
            {
                _entries.Add(new QueueEntry(playlist, item, item.Track.Title, item.Track.Artist));
            }

            opVersion = ++_version;
            updates = CollectIndexUpdatesLocked();
        }

        DispatchUpdates(updates, opVersion);
    }

    /// <summary>Puts items at the front so they play as soon as possible, moving already queued items to head.</summary>
    public void EnqueueNext(IEnumerable<PlaylistItem> items) => EnqueueNext(null, items);

    public void EnqueueNext(Playlist? playlist, IEnumerable<PlaylistItem> items)
    {
        if (items == null) return;
        List<(PlaylistItem Item, int Index)> updates;
        long opVersion;

        lock (_lock)
        {
            var validItems = items.Where(i => i?.Track != null).Distinct().ToList();
            if (validItems.Count == 0) return;

            // Remove any existing entries for these items
            var itemSet = new HashSet<PlaylistItem>(validItems);
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (itemSet.Contains(_entries[i].Item))
                {
                    _entries.RemoveAt(i);
                }
            }

            // Prepend to the head in relative order
            _entries.InsertRange(0, validItems.Select(i => new QueueEntry(playlist, i, i.Track.Title, i.Track.Artist)));
            opVersion = ++_version;
            updates = CollectIndexUpdatesLocked();
        }

        DispatchUpdates(updates, opVersion);
    }

    public QueueEntry? Peek() => GetCurrent();

    public QueueEntry? GetCurrent()
    {
        lock (_lock) return _entries.Count > 0 ? _entries[0] : null;
    }

    public QueueEntry? GetNext()
    {
        lock (_lock) return _entries.Count > 1 ? _entries[1] : null;
    }

    /// <summary>
    /// First entry whose item satisfies <paramref name="predicate"/>, or null when none does.
    /// Scans in place under the queue lock, so the play-order path can look up an entry without
    /// the full-queue copy that <see cref="Entries"/> performs on every read. The predicate runs
    /// while the lock is held and must therefore not call back into the queue.
    /// </summary>
    public QueueEntry? FirstMatching(Func<PlaylistItem, bool> predicate)
    {
        if (predicate == null) return null;

        lock (_lock)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Item != null && predicate(entry.Item)) return entry;
            }
        }
        return null;
    }

    /// <summary>Removes and returns the head entry (called when it starts playing).</summary>
    public QueueEntry? Dequeue()
    {
        QueueEntry? head;
        List<(PlaylistItem Item, int Index)> updates;
        long opVersion;

        lock (_lock)
        {
            if (_entries.Count == 0) return null;
            head = _entries[0];
            _entries.RemoveAt(0);

            opVersion = ++_version;
            updates = CollectIndexUpdatesLocked();
            updates.Add((head.Item, -1));
        }

        DispatchUpdates(updates, opVersion);
        return head;
    }

    /// <summary>
    /// Removes the entry for <paramref name="item"/> wherever it sits, and reports whether one was
    /// found. Used when a track actually starts playing: the track that starts is not always the
    /// head (an unreadable head gets skipped, and the user can reorder the queue while the next
    /// track is being prefetched), so consuming the head by identity would strand entries and let
    /// a dead file at the head trap playback on a single track forever.
    /// </summary>
    public bool Consume(PlaylistItem? item)
    {
        if (item == null) return false;

        QueueEntry? removed = null;
        List<(PlaylistItem Item, int Index)> updates;
        long opVersion;

        lock (_lock)
        {
            int index = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (ReferenceEquals(_entries[i].Item, item)) { index = i; break; }
            }
            if (index < 0) return false;

            removed = _entries[index];
            _entries.RemoveAt(index);

            opVersion = ++_version;
            updates = CollectIndexUpdatesLocked();
            updates.Add((removed.Item, -1));
        }

        DispatchUpdates(updates, opVersion);
        return true;
    }

    public bool Remove(int index)
    {
        QueueEntry? removed;
        List<(PlaylistItem Item, int Index)> updates;
        long opVersion;

        lock (_lock)
        {
            if (index < 0 || index >= _entries.Count) return false;
            removed = _entries[index];
            _entries.RemoveAt(index);

            opVersion = ++_version;
            updates = CollectIndexUpdatesLocked();
            updates.Add((removed.Item, -1));
        }

        DispatchUpdates(updates, opVersion);
        return true;
    }

    public void RemoveAt(int index) => Remove(index);

    public void RemoveItems(IEnumerable<PlaylistItem> items)
    {
        if (items == null) return;
        var set = new HashSet<PlaylistItem>(items.Where(i => i != null));
        if (set.Count == 0) return;

        List<PlaylistItem> removedItems = new();
        List<(PlaylistItem Item, int Index)> updates;
        long opVersion;

        lock (_lock)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (set.Contains(_entries[i].Item))
                {
                    removedItems.Add(_entries[i].Item);
                    _entries.RemoveAt(i);
                }
            }

            if (removedItems.Count == 0) return;

            opVersion = ++_version;
            updates = CollectIndexUpdatesLocked();
            foreach (var r in removedItems)
            {
                updates.Add((r, -1));
            }
        }

        DispatchUpdates(updates, opVersion);
    }

    public bool Move(int fromIndex, int toIndex)
    {
        List<(PlaylistItem Item, int Index)> updates;
        long opVersion;

        lock (_lock)
        {
            if (fromIndex < 0 || fromIndex >= _entries.Count || toIndex < 0 || toIndex >= _entries.Count)
                return false;

            if (fromIndex == toIndex) return true;

            var entry = _entries[fromIndex];
            _entries.RemoveAt(fromIndex);
            _entries.Insert(toIndex, entry);

            opVersion = ++_version;
            updates = CollectIndexUpdatesLocked();
        }

        DispatchUpdates(updates, opVersion);
        return true;
    }

    public void Shuffle()
    {
        List<(PlaylistItem Item, int Index)> updates;
        long opVersion;

        lock (_lock)
        {
            if (_entries.Count <= 1) return;

            for (int i = _entries.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (_entries[i], _entries[j]) = (_entries[j], _entries[i]);
            }

            opVersion = ++_version;
            updates = CollectIndexUpdatesLocked();
        }

        DispatchUpdates(updates, opVersion);
    }

    public void SetItems(IEnumerable<PlaylistItem> items, int startIndex)
    {
        if (items == null) return;
        List<PlaylistItem> oldItems = new();
        List<(PlaylistItem Item, int Index)> updates;
        long opVersion;

        lock (_lock)
        {
            var validItems = items.Where(i => i?.Track != null).Distinct().ToList();
            if (validItems.Count == 0 && _entries.Count == 0) return;

            oldItems.AddRange(_entries.Select(e => e.Item));
            _entries.Clear();

            if (validItems.Count > 0)
            {
                int start = Math.Clamp(startIndex, 0, validItems.Count - 1);
                var reordered = validItems.Skip(start).Concat(validItems.Take(start));
                foreach (var item in reordered)
                {
                    _entries.Add(new QueueEntry(null, item, item.Track.Title, item.Track.Artist));
                }
            }

            opVersion = ++_version;
            updates = CollectIndexUpdatesLocked();

            var newSet = new HashSet<PlaylistItem>(_entries.Select(e => e.Item));
            foreach (var old in oldItems)
            {
                if (!newSet.Contains(old))
                {
                    updates.Add((old, -1));
                }
            }
        }

        DispatchUpdates(updates, opVersion);
    }

    public void Clear()
    {
        List<(PlaylistItem Item, int Index)> updates;
        long opVersion;

        lock (_lock)
        {
            if (_entries.Count == 0) return;
            updates = _entries.Select(e => (e.Item, -1)).ToList();
            _entries.Clear();
            opVersion = ++_version;
        }

        DispatchUpdates(updates, opVersion);
    }

    private List<(PlaylistItem Item, int Index)> CollectIndexUpdatesLocked()
    {
        var updates = new List<(PlaylistItem Item, int Index)>(_entries.Count);
        for (int i = 0; i < _entries.Count; i++)
        {
            updates.Add((_entries[i].Item, i + 1));
        }
        return updates;
    }

    private void DispatchUpdates(List<(PlaylistItem Item, int Index)> updates, long version)
    {
        // 1. Dispatch INotifyPropertyChanged outside lock
        for (int i = 0; i < updates.Count; i++)
        {
            updates[i].Item.UpdateQueueIndex(updates[i].Index, version);
        }

        // 2. Dispatch Changed event outside lock
        Changed?.Invoke();
    }
}
