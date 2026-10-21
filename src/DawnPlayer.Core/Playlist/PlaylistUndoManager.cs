using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Playlists;

/// <summary>
/// Manages multi-level undo and redo histories for playlists.
/// Tracks snapshots of <see cref="PlaylistItem"/> lists before destructive edits.
/// Automatically drops histories when playlists are garbage collected using <see cref="ConditionalWeakTable{TKey, TValue}"/>.
/// Smart playlists (IsSmart) are query-generated and excluded from undo/redo tracking.
/// </summary>
public sealed class PlaylistUndoManager
{
    private sealed class HistoryRecord
    {
        public string Description { get; }
        public IReadOnlyList<PlaylistItem> Items { get; }

        public HistoryRecord(string description, IReadOnlyList<PlaylistItem> items)
        {
            Description = description;
            Items = items;
        }
    }

    private sealed class PlaylistHistory
    {
        public readonly object SyncRoot = new();
        public readonly LinkedList<HistoryRecord> UndoStack = new();
        public readonly LinkedList<HistoryRecord> RedoStack = new();
    }

    private readonly int _maxHistory;
    private readonly ConditionalWeakTable<Playlist, PlaylistHistory> _histories = new();

    public PlaylistUndoManager(int maxHistory = 30)
    {
        _maxHistory = Math.Max(1, maxHistory);
    }

    /// <summary>
    /// Records the current state of the playlist before a mutating operation takes place.
    /// Resets the redo stack for this playlist.
    /// Smart playlists are ignored.
    /// </summary>
    public void RecordSnapshot(Playlist pl, string description = "")
    {
        if (pl == null || pl.IsSmart) return;

        var history = _histories.GetOrCreateValue(pl);
        var snapshot = pl.GetSnapshot();

        lock (history.SyncRoot)
        {
            history.UndoStack.AddLast(new HistoryRecord(description, snapshot));
            while (history.UndoStack.Count > _maxHistory)
            {
                history.UndoStack.RemoveFirst();
            }

            history.RedoStack.Clear();
        }
    }

    /// <summary>
    /// Returns true if an undo operation is available for the given playlist.
    /// </summary>
    public bool CanUndo(Playlist pl)
    {
        if (pl == null || pl.IsSmart) return false;
        if (!_histories.TryGetValue(pl, out var history)) return false;

        lock (history.SyncRoot)
        {
            return history.UndoStack.Count > 0;
        }
    }

    /// <summary>
    /// Returns true if a redo operation is available for the given playlist.
    /// </summary>
    public bool CanRedo(Playlist pl)
    {
        if (pl == null || pl.IsSmart) return false;
        if (!_histories.TryGetValue(pl, out var history)) return false;

        lock (history.SyncRoot)
        {
            return history.RedoStack.Count > 0;
        }
    }

    /// <summary>
    /// Undoes the last operation on the playlist, restoring its previous item list.
    /// Returns true if successfully reverted.
    /// </summary>
    public bool Undo(Playlist pl)
    {
        if (pl == null || pl.IsSmart) return false;
        if (!_histories.TryGetValue(pl, out var history)) return false;

        HistoryRecord? target;
        var currentSnapshot = pl.GetSnapshot();

        lock (history.SyncRoot)
        {
            if (history.UndoStack.Count == 0) return false;

            target = history.UndoStack.Last?.Value;
            if (target == null) return false;
            history.UndoStack.RemoveLast();

            history.RedoStack.AddLast(new HistoryRecord(target.Description, currentSnapshot));
            while (history.RedoStack.Count > _maxHistory)
            {
                history.RedoStack.RemoveFirst();
            }
        }

        lock (pl.SyncRoot)
        {
            pl.Items.ReplaceAll(target.Items);
        }

        return true;
    }

    /// <summary>
    /// Redoes the previously undone operation on the playlist.
    /// Returns true if successfully reapplied.
    /// </summary>
    public bool Redo(Playlist pl)
    {
        if (pl == null || pl.IsSmart) return false;
        if (!_histories.TryGetValue(pl, out var history)) return false;

        HistoryRecord? target;
        var currentSnapshot = pl.GetSnapshot();

        lock (history.SyncRoot)
        {
            if (history.RedoStack.Count == 0) return false;

            target = history.RedoStack.Last?.Value;
            if (target == null) return false;
            history.RedoStack.RemoveLast();

            history.UndoStack.AddLast(new HistoryRecord(target.Description, currentSnapshot));
            while (history.UndoStack.Count > _maxHistory)
            {
                history.UndoStack.RemoveFirst();
            }
        }

        lock (pl.SyncRoot)
        {
            pl.Items.ReplaceAll(target.Items);
        }

        return true;
    }

    /// <summary>
    /// Clears both undo and redo histories for the specified playlist.
    /// </summary>
    public void Clear(Playlist pl)
    {
        if (pl == null) return;
        if (_histories.TryGetValue(pl, out var history))
        {
            lock (history.SyncRoot)
            {
                history.UndoStack.Clear();
                history.RedoStack.Clear();
            }
        }
    }
}
