using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Comprehensive unit tests for PlaybackQueue advanced queue manipulations:
/// 1. EnqueueNext prepending to head of queue with multiple items.
/// 2. RemoveAt with valid and invalid bounds and 1-based reindexing.
/// 3. RemoveItems batch removal (head, middle, tail) and index recovery.
/// 4. Peek and Dequeue order preservation, null-safety, and queue state transitions.
/// 5. Clear operation resetting QueueIndex to -1 and event firing.
/// 6. FirstMatching predicate lookup, including under concurrent enqueues.
/// </summary>
public class PlaybackQueueAdvancedTests
{
    private static PlaylistItem CreateItem(string title, string artist = "Artist", int trackNo = 0) =>
        new(new Track { Path = $@"C:\Music\{title}.mp3", Title = title, Artist = artist, TrackNo = trackNo });

    [Fact]
    public void EnqueueNext_PrependsItemsToHead_InSuppliedOrder()
    {
        var pl = new Playlist("pl");
        var a = CreateItem("A");
        var b = CreateItem("B");
        var c = CreateItem("C");
        var d = CreateItem("D");

        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { c, d });

        Assert.Equal(1, c.QueueIndex);
        Assert.Equal(2, d.QueueIndex);

        // Prepend A and B
        queue.EnqueueNext(pl, new[] { a, b });

        Assert.Equal(4, queue.Count);
        Assert.Equal(1, a.QueueIndex);
        Assert.Equal(2, b.QueueIndex);
        Assert.Equal(3, c.QueueIndex);
        Assert.Equal(4, d.QueueIndex);

        var entries = queue.Entries;
        Assert.Same(a, entries[0].Item);
        Assert.Same(b, entries[1].Item);
        Assert.Same(c, entries[2].Item);
        Assert.Same(d, entries[3].Item);
    }

    [Fact]
    public void EnqueueNext_MovesAlreadyQueuedItemToHead()
    {
        var pl = new Playlist("pl");
        var a = CreateItem("A");
        var b = CreateItem("B");
        var c = CreateItem("C");

        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { a, b, c });

        Assert.Equal(1, a.QueueIndex);
        Assert.Equal(2, b.QueueIndex);
        Assert.Equal(3, c.QueueIndex);

        // Move 'c' (index 3) to the head of the queue
        queue.EnqueueNext(pl, new[] { c });

        Assert.Equal(3, queue.Count);
        Assert.Equal(1, c.QueueIndex);
        Assert.Equal(2, a.QueueIndex);
        Assert.Equal(3, b.QueueIndex);

        var entries = queue.Entries;
        Assert.Same(c, entries[0].Item);
        Assert.Same(a, entries[1].Item);
        Assert.Same(b, entries[2].Item);
    }

    [Fact]
    public void Enqueue_MovesAlreadyQueuedItemToTail()
    {
        var pl = new Playlist("pl");
        var a = CreateItem("A");
        var b = CreateItem("B");
        var c = CreateItem("C");

        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { a, b, c });

        Assert.Equal(1, a.QueueIndex);
        Assert.Equal(2, b.QueueIndex);
        Assert.Equal(3, c.QueueIndex);

        // Move 'a' (index 1) to the tail of the queue
        queue.Enqueue(pl, new[] { a });

        Assert.Equal(3, queue.Count);
        Assert.Equal(1, b.QueueIndex);
        Assert.Equal(2, c.QueueIndex);
        Assert.Equal(3, a.QueueIndex);

        var entries = queue.Entries;
        Assert.Same(b, entries[0].Item);
        Assert.Same(c, entries[1].Item);
        Assert.Same(a, entries[2].Item);
    }

    [Fact]
    public void Enqueue_BatchWithMixedNewAndExistingItems_MovesExistingToTailInOrder()
    {
        var pl = new Playlist("pl");
        var a = CreateItem("A");
        var b = CreateItem("B");
        var c = CreateItem("C");
        var d = CreateItem("D");

        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { a, b });

        // Enqueue batch: [b, c, d] (b already exists at index 2)
        queue.Enqueue(pl, new[] { b, c, d });

        Assert.Equal(4, queue.Count);
        Assert.Equal(1, a.QueueIndex);
        Assert.Equal(2, b.QueueIndex);
        Assert.Equal(3, c.QueueIndex);
        Assert.Equal(4, d.QueueIndex);

        var entries = queue.Entries;
        Assert.Same(a, entries[0].Item);
        Assert.Same(b, entries[1].Item);
        Assert.Same(c, entries[2].Item);
        Assert.Same(d, entries[3].Item);
    }

    [Fact]
    public void RemoveAt_RemovesItem_AndReindexesRemaining()
    {
        var pl = new Playlist("pl");
        var a = CreateItem("A");
        var b = CreateItem("B");
        var c = CreateItem("C");

        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { a, b, c });

        // Remove middle item B (index 1)
        queue.RemoveAt(1);

        Assert.Equal(2, queue.Count);
        Assert.Equal(-1, b.QueueIndex);
        Assert.Equal(1, a.QueueIndex);
        Assert.Equal(2, c.QueueIndex);

        var entries = queue.Entries;
        Assert.Same(a, entries[0].Item);
        Assert.Same(c, entries[1].Item);
    }

    [Fact]
    public void RemoveAt_OutOfBounds_DoesNotModifyQueue()
    {
        var pl = new Playlist("pl");
        var a = CreateItem("A");

        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { a });

        queue.RemoveAt(-1);
        queue.RemoveAt(5);

        Assert.Equal(1, queue.Count);
        Assert.Equal(1, a.QueueIndex);
    }

    [Fact]
    public void RemoveItems_BatchRemoves_HeadMiddleTail()
    {
        var pl = new Playlist("pl");
        var a = CreateItem("A");
        var b = CreateItem("B");
        var c = CreateItem("C");
        var d = CreateItem("D");
        var e = CreateItem("E");

        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { a, b, c, d, e });

        // Remove Head (A), Middle (C), Tail (E)
        queue.RemoveItems(new[] { a, c, e });

        Assert.Equal(2, queue.Count);
        Assert.Equal(-1, a.QueueIndex);
        Assert.Equal(-1, c.QueueIndex);
        Assert.Equal(-1, e.QueueIndex);

        Assert.Equal(1, b.QueueIndex);
        Assert.Equal(2, d.QueueIndex);

        var entries = queue.Entries;
        Assert.Same(b, entries[0].Item);
        Assert.Same(d, entries[1].Item);
    }

    [Fact]
    public void PlaybackQueue_LargeQueueStress_1000Items_MaintainsIndexIntegrity()
    {
        var queue = new PlaybackQueue();
        var pl = new Playlist("test");

        var items = Enumerable.Range(1, 1000)
            .Select(i => new PlaylistItem(new Track { Path = $"song{i}.mp3" }))
            .ToList();

        queue.Enqueue(pl, items);
        Assert.Equal(1000, queue.Count);

        // Remove 200 items from odd positions
        var toRemove = items.Where((_, idx) => idx % 5 == 0).ToList();
        queue.RemoveItems(toRemove);

        Assert.Equal(800, queue.Count);

        // Verify that all removed items have QueueIndex = -1
        foreach (var r in toRemove)
        {
            Assert.Equal(-1, r.QueueIndex);
        }

        // Verify remaining items are strictly 1..800
        var remaining = queue.Entries;
        for (int i = 0; i < remaining.Count; i++)
        {
            Assert.Equal(i + 1, remaining[i].Item.QueueIndex);
        }
    }

    [Fact]
    public void PeekAndDequeue_PreservesFifoOrder_AndManagesIndexes()
    {
        var pl = new Playlist("pl");
        var a = CreateItem("A");
        var b = CreateItem("B");

        var queue = new PlaybackQueue();

        // Empty checks
        Assert.Null(queue.Peek());
        Assert.Null(queue.Dequeue());

        queue.Enqueue(pl, new[] { a, b });

        // Peek should not alter count or indexes
        var peeked = queue.Peek();
        Assert.NotNull(peeked);
        Assert.Same(a, peeked!.Item);
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, a.QueueIndex);

        // Dequeue first
        var deq1 = queue.Dequeue();
        Assert.NotNull(deq1);
        Assert.Same(a, deq1!.Item);
        Assert.Equal(-1, a.QueueIndex);
        Assert.Equal(1, b.QueueIndex);
        Assert.Equal(1, queue.Count);

        // Dequeue second
        var deq2 = queue.Dequeue();
        Assert.NotNull(deq2);
        Assert.Same(b, deq2!.Item);
        Assert.Equal(-1, b.QueueIndex);
        Assert.Equal(0, queue.Count);

        // Dequeue on now-empty queue
        Assert.Null(queue.Dequeue());
    }

    [Fact]
    public void Clear_ResetsAllIndexes_AndFiresChangedEvent()
    {
        var pl = new Playlist("pl");
        var a = CreateItem("A");
        var b = CreateItem("B");

        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { a, b });

        int changedCount = 0;
        queue.Changed += () => changedCount++;

        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.Equal(-1, a.QueueIndex);
        Assert.Equal(-1, b.QueueIndex);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void FirstMatching_HeadSatisfiesPredicate_ReturnsHeadEntry()
    {
        var pl = new Playlist("pl");
        var a = CreateItem("A");
        var b = CreateItem("B");

        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { a, b });

        var hit = queue.FirstMatching(i => i.Track.Title == "A");

        Assert.NotNull(hit);
        Assert.Same(a, hit!.Item);
        Assert.Same(pl, hit.Playlist);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void FirstMatching_NonMatchingLeadingEntries_ReturnsFirstMatchInQueueOrder()
    {
        var pl = new Playlist("pl");
        var a = CreateItem("A");
        var b = CreateItem("B", trackNo: 4);
        var c = CreateItem("C", trackNo: 6);

        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { a, b, c });

        var hit = queue.FirstMatching(i => i.Track.TrackNo % 2 == 0 && i.Track.TrackNo > 0);

        Assert.NotNull(hit);
        Assert.Same(b, hit!.Item);
    }

    [Fact]
    public void FirstMatching_EmptyQueue_ReturnsNull()
    {
        var queue = new PlaybackQueue();

        Assert.Null(queue.FirstMatching(_ => true));
    }

    [Fact]
    public void FirstMatching_NoEntrySatisfiesPredicate_ReturnsNull()
    {
        var pl = new Playlist("pl");
        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { CreateItem("A"), CreateItem("B") });

        Assert.Null(queue.FirstMatching(i => i.Track.Title == "Z"));
    }

    [Fact]
    public void FirstMatching_NullPredicate_ReturnsNull()
    {
        var pl = new Playlist("pl");
        var queue = new PlaybackQueue();
        queue.Enqueue(pl, new[] { CreateItem("A") });

        Assert.Null(queue.FirstMatching(null!));
    }

    [Fact]
    public async Task FirstMatching_UnderConcurrentEnqueues_NeverThrowsAndOnlyReturnsMatches()
    {
        var pl = new Playlist("pl");
        var queue = new PlaybackQueue();
        var items = Enumerable.Range(0, 300).Select(i => CreateItem($"T{i}", trackNo: i)).ToList();

        var exceptions = new ConcurrentBag<Exception>();
        bool enqueuingComplete = false;

        var enqueuer = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    queue.Enqueue(pl, new[] { items[i] });
                    Thread.Yield();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
            finally
            {
                Volatile.Write(ref enqueuingComplete, true);
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!Volatile.Read(ref enqueuingComplete))
                {
                    var odd = queue.FirstMatching(i => i.Track.TrackNo % 2 == 1);
                    if (odd != null)
                    {
                        Assert.Equal(1, odd.Item.Track.TrackNo % 2);
                    }

                    Assert.Null(queue.FirstMatching(_ => false));
                    Thread.Yield();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToList();

        await Task.WhenAll(readers.Concat(new[] { enqueuer }));

        Assert.Empty(exceptions);
        Assert.Equal(items.Count, queue.Count);

        // Enqueue appends, so the first item enqueued is still the head.
        var head = queue.FirstMatching(_ => true);
        Assert.NotNull(head);
        Assert.Same(items[0], head!.Item);
        Assert.Same(items[1], queue.FirstMatching(i => i.Track.TrackNo % 2 == 1)!.Item);
    }
}
