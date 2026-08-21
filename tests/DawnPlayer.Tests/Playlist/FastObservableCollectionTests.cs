using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests.Playlists;

public class FastObservableCollectionTests
{
    [Fact]
    public void AddRange_FiresSingleResetNotification()
    {
        var coll = new FastObservableCollection<string>();
        int collectionChangedCount = 0;
        NotifyCollectionChangedAction? lastAction = null;
        int countChangedCount = 0;

        coll.CollectionChanged += (_, e) =>
        {
            collectionChangedCount++;
            lastAction = e.Action;
        };

        ((INotifyPropertyChanged)coll).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(coll.Count))
                countChangedCount++;
        };

        coll.AddRange(new[] { "A", "B", "C", "D", "E" });

        Assert.Equal(5, coll.Count);
        Assert.Equal(1, collectionChangedCount);
        Assert.Equal(NotifyCollectionChangedAction.Reset, lastAction);
        Assert.Equal(1, countChangedCount);
        Assert.Equal(new[] { "A", "B", "C", "D", "E" }, coll);
    }

    [Fact]
    public void InsertRange_InsertsAtTargetIndexAndFiresSingleReset()
    {
        var coll = new FastObservableCollection<string>(new[] { "A", "D" });
        int collectionChangedCount = 0;

        coll.CollectionChanged += (_, e) => collectionChangedCount++;

        coll.InsertRange(1, new[] { "B", "C" });

        Assert.Equal(4, coll.Count);
        Assert.Equal(1, collectionChangedCount);
        Assert.Equal(new[] { "A", "B", "C", "D" }, coll);
    }

    [Fact]
    public void RemoveRange_RemovesMatchingItemsAndFiresSingleReset()
    {
        var coll = new FastObservableCollection<string>(new[] { "A", "B", "C", "D", "E" });
        int collectionChangedCount = 0;

        coll.CollectionChanged += (_, e) => collectionChangedCount++;

        coll.RemoveRange(new[] { "B", "D" });

        Assert.Equal(3, coll.Count);
        Assert.Equal(1, collectionChangedCount);
        Assert.Equal(new[] { "A", "C", "E" }, coll);
    }

    [Fact]
    public void ReplaceAll_ReplacesItemsAndFiresSingleReset()
    {
        var coll = new FastObservableCollection<string>(new[] { "A", "B" });
        int collectionChangedCount = 0;

        coll.CollectionChanged += (_, e) => collectionChangedCount++;

        coll.ReplaceAll(new[] { "X", "Y", "Z" });

        Assert.Equal(3, coll.Count);
        Assert.Equal(1, collectionChangedCount);
        Assert.Equal(new[] { "X", "Y", "Z" }, coll);
    }

    [Fact]
    public void SingleItemOperations_MaintainStandardNotifications()
    {
        var coll = new FastObservableCollection<int>();
        int addEventCount = 0;

        coll.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                addEventCount++;
        };

        coll.Add(1);
        coll.Add(2);

        Assert.Equal(2, addEventCount);
        Assert.Equal(2, coll.Count);
    }
}
