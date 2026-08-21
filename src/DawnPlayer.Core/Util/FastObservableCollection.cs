using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DawnPlayer.Core.Util;

/// <summary>
/// A high-performance <see cref="ObservableCollection{T}"/> subclass that supports batch operations
/// (AddRange, InsertRange, RemoveRange, ReplaceAll) with single collection-changed notification firing,
/// avoiding notification storms and O(N^2) UI rebinding overhead.
/// </summary>
/// <typeparam name="T">The type of elements in the collection.</typeparam>
public class FastObservableCollection<T> : ObservableCollection<T>
{
    public FastObservableCollection() : base() { }

    public FastObservableCollection(IEnumerable<T> collection) : base(collection) { }

    public FastObservableCollection(List<T> list) : base(list) { }

    /// <summary>
    /// Adds a range of items to the end of the collection, firing a single notification.
    /// </summary>
    public void AddRange(IEnumerable<T> collection)
    {
        if (collection == null) return;
        InsertRange(Count, collection);
    }

    /// <summary>
    /// Inserts a range of items at the specified index, firing a single notification.
    /// </summary>
    public void InsertRange(int index, IEnumerable<T> collection)
    {
        if (collection == null) return;

        var itemsToAdd = collection is IReadOnlyList<T> roList ? roList : [.. collection];
        if (itemsToAdd.Count == 0) return;

        CheckReentrancy();

        var targetIndex = Math.Clamp(index, 0, Count);

        // Mutating Items (the raw backing list) deliberately bypasses the per-item
        // notifications; one Reset is raised at the end instead.
        for (int i = 0; i < itemsToAdd.Count; i++)
        {
            Items.Insert(targetIndex + i, itemsToAdd[i]);
        }

        RaiseResetEvents();
    }

    /// <summary>
    /// Removes a set of items from the collection in batch, firing a single notification.
    /// </summary>
    public void RemoveRange(IEnumerable<T> collection)
    {
        if (collection == null) return;

        var set = collection as HashSet<T> ?? new HashSet<T>(collection);
        if (set.Count == 0) return;

        CheckReentrancy();

        bool removedAny = false;
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (set.Contains(Items[i]))
            {
                Items.RemoveAt(i);
                removedAny = true;
            }
        }

        if (removedAny)
        {
            RaiseResetEvents();
        }
    }

    /// <summary>
    /// Replaces the entire collection with new items, firing a single notification.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> collection)
    {
        CheckReentrancy();

        var newItems = collection is IReadOnlyList<T> roList ? roList : (collection != null ? [.. collection] : Array.Empty<T>());

        Items.Clear();
        for (int i = 0; i < newItems.Count; i++)
        {
            Items.Add(newItems[i]);
        }

        RaiseResetEvents();
    }

    private void RaiseResetEvents()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
