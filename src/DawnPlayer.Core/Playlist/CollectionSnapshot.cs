using System;
using System.Collections.Generic;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Playlists;

/// <summary>
/// Thread-safe collection snapshot utility that guarantees atomic, immutable array capture
/// under concurrent collection modifications without exception-swallowing spin loops.
/// </summary>
public static class CollectionSnapshot
{
    /// <summary>
    /// Captures an immutable snapshot array from a playlist.
    /// </summary>
    public static PlaylistItem[] Capture(Playlist? pl) => pl?.GetSnapshot() ?? Array.Empty<PlaylistItem>();

    /// <summary>
    /// Captures an immutable snapshot array from an album group.
    /// </summary>
    public static PlaylistItem[] Capture(AlbumGroup? g)
    {
        if (g == null) return Array.Empty<PlaylistItem>();
        lock (g.SyncRoot) { return CaptureSafe(g.Items); }
    }

    /// <summary>
    /// Captures an immutable snapshot array from an items collection.
    /// </summary>
    public static PlaylistItem[] Capture(IEnumerable<PlaylistItem>? items)
    {
        if (items == null) return Array.Empty<PlaylistItem>();
        if ((object)items is Playlist pl) return pl.GetSnapshot();
        if (items is AlbumGroup g)
        {
            lock (g.SyncRoot) { return CaptureSafe(g.Items); }
        }
        if (items is PlaylistItem[] arr) return (PlaylistItem[])arr.Clone();
        return CaptureSafe(items);
    }

    /// <summary>
    /// Captures a generic immutable snapshot array from a collection.
    /// </summary>
    public static T[] Capture<T>(IEnumerable<T>? items)
    {
        if (items == null) return Array.Empty<T>();
        if (items is T[] arr) return (T[])arr.Clone();
        return CaptureSafe(items);
    }

    /// <summary>
    /// Captures a snapshot under <paramref name="syncRoot"/> — the lock the collection's own
    /// mutators take — so the copy is atomic and no retry is needed.
    /// </summary>
    public static T[] CaptureSafe<T>(IEnumerable<T> items, object syncRoot)
    {
        if (items == null) return Array.Empty<T>();
        if (syncRoot == null) return CaptureSafe(items);

        lock (syncRoot)
        {
            if (items is IList<T> list)
            {
                int count = list.Count;
                if (count == 0) return Array.Empty<T>();

                var result = new T[count];
                for (int i = 0; i < count; i++)
                {
                    result[i] = list[i];
                }
                return result;
            }
            return System.Linq.Enumerable.ToArray(items);
        }
    }

    /// <summary>
    /// Best-effort snapshot for callers that have no sync root to offer: it can only lock whatever
    /// ICollection.SyncRoot the collection exposes, which for an ObservableCollection is its inner
    /// list rather than the object the owning type's mutators take. Concurrent modification is
    /// therefore still possible, hence the retry and the bounded indexing. Prefer the overload
    /// taking an explicit sync root.
    /// </summary>
    public static T[] CaptureSafe<T>(IEnumerable<T> items)
    {
        object syncRoot = (items as System.Collections.ICollection)?.SyncRoot ?? items;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                lock (syncRoot)
                {
                    if (items is IList<T> list)
                    {
                        int count = list.Count;
                        var result = new List<T>(count);
                        for (int i = 0; i < count; i++)
                        {
                            if (i < list.Count)
                            {
                                result.Add(list[i]);
                            }
                        }
                        return result.ToArray();
                    }
                    return System.Linq.Enumerable.ToArray(items);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                // Transient concurrent modification during unsynchronized collection read; retry
            }
        }

        // Fallback: element-by-element copy
        try
        {
            lock (syncRoot)
            {
                var fallback = new List<T>();
                if (items is IList<T> list)
                {
                    int count = list.Count;
                    for (int i = 0; i < count; i++)
                    {
                        try
                        {
                            if (i < list.Count) fallback.Add(list[i]);
                        }
                        catch (ArgumentOutOfRangeException) { break; }
                    }
                }
                else
                {
                    using var enumerator = items.GetEnumerator();
                    while (true)
                    {
                        try
                        {
                            if (!enumerator.MoveNext()) break;
                            fallback.Add(enumerator.Current);
                        }
                        catch (InvalidOperationException) { break; }
                    }
                }
                return fallback.ToArray();
            }
        }
        catch
        {
            return Array.Empty<T>();
        }
    }
}

