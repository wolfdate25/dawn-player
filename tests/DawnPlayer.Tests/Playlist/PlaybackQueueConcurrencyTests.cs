using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests.Playlists;

[Collection("PlaylistConcurrencyCollection")]
public class PlaybackQueueConcurrencyTests
{
    private static PlaylistItem CreateItem(string id, string title = "Title", string artist = "Artist", long durationMs = 180000)
    {
        return new PlaylistItem(new Track
        {
            Path = $@"C:\Music\track_{id}.mp3",
            Title = $"{title}_{id}",
            Artist = artist,
            DurationMs = durationMs
        });
    }

    [Fact]
    public void IPlaybackQueue_InterfaceContract_FullConformance()
    {
        IPlaybackQueue queue = new PlaybackQueue();
        var item1 = CreateItem("1");
        var item2 = CreateItem("2");
        var item3 = CreateItem("3");

        Assert.Equal(0, queue.Count);
        Assert.Empty(queue.Entries);
        Assert.Null(queue.GetCurrent());
        Assert.Null(queue.GetNext());
        Assert.Null(queue.Peek());
        Assert.Null(queue.Dequeue());

        // Enqueue items
        queue.Enqueue(new[] { item1, item2 });
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, item1.QueueIndex);
        Assert.Equal(2, item2.QueueIndex);
        Assert.Same(item1, queue.GetCurrent()?.Item);
        Assert.Same(item2, queue.GetNext()?.Item);

        // EnqueueNext
        queue.EnqueueNext(new[] { item3 });
        Assert.Equal(3, queue.Count);
        Assert.Equal(1, item3.QueueIndex);
        Assert.Equal(2, item1.QueueIndex);
        Assert.Equal(3, item2.QueueIndex);

        // Move
        bool moved = queue.Move(0, 2);
        Assert.True(moved);
        Assert.Equal(1, item1.QueueIndex);
        Assert.Equal(2, item2.QueueIndex);
        Assert.Equal(3, item3.QueueIndex);

        // Shuffle
        queue.Shuffle();
        Assert.Equal(3, queue.Count);
        var entries = queue.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            Assert.Equal(i + 1, entries[i].Item.QueueIndex);
        }

        // Remove
        bool removed = queue.Remove(1);
        Assert.True(removed);
        Assert.Equal(2, queue.Count);

        // SetItems
        var item4 = CreateItem("4");
        var item5 = CreateItem("5");
        queue.SetItems(new[] { item4, item5 }, startIndex: 1);
        Assert.Equal(2, queue.Count);
        Assert.Same(item5, queue.GetCurrent()?.Item);
        Assert.Same(item4, queue.GetNext()?.Item);
        Assert.Equal(1, item5.QueueIndex);
        Assert.Equal(2, item4.QueueIndex);

        // Clear
        queue.Clear();
        Assert.Equal(0, queue.Count);
        Assert.Equal(-1, item4.QueueIndex);
        Assert.Equal(-1, item5.QueueIndex);
    }

    [Fact]
    public async Task ConcurrentEnqueueDequeue_HighContention_MaintainsIndexIntegrity()
    {
        var queue = new PlaybackQueue();
        const int enqueuerCount = 4;
        const int dequeuerCount = 4;
        const int itemsPerThread = 50;
        var allItems = Enumerable.Range(0, enqueuerCount * itemsPerThread)
                                 .Select(i => CreateItem(i.ToString()))
                                 .ToList();

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        bool enqueuingComplete = false;
        var enqueuerTasks = new List<Task>();
        var dequeuerTasks = new List<Task>();

        // Enqueuers
        for (int t = 0; t < enqueuerCount; t++)
        {
            int threadId = t;
            enqueuerTasks.Add(Task.Run(() =>
            {
                try
                {
                    var chunk = allItems.Skip(threadId * itemsPerThread).Take(itemsPerThread).ToList();
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (i % 2 == 0)
                            queue.Enqueue(new[] { chunk[i] });
                        else
                            queue.EnqueueNext(new[] { chunk[i] });
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }

        // Dequeuers & Shufflers
        for (int t = 0; t < dequeuerCount; t++)
        {
            dequeuerTasks.Add(Task.Run(() =>
            {
                try
                {
                    while (!Volatile.Read(ref enqueuingComplete) || queue.Count > 0)
                    {
                        queue.Dequeue();
                        if (queue.Count > 5)
                        {
                            queue.Shuffle();
                        }
                        Thread.Yield();
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }

        await Task.WhenAll(enqueuerTasks);
        Volatile.Write(ref enqueuingComplete, true);
        await Task.WhenAll(dequeuerTasks);

        Assert.Empty(exceptions);

        // Drain remaining items and verify index consistency
        var remaining = queue.Entries;
        Assert.Equal(queue.Count, remaining.Count);
        for (int i = 0; i < remaining.Count; i++)
        {
            Assert.Equal(i + 1, remaining[i].Item.QueueIndex);
        }

        queue.Clear();
        Assert.Equal(0, queue.Count);
        for (int i = 0; i < remaining.Count; i++)
        {
            Assert.Equal(-1, remaining[i].Item.QueueIndex);
        }
    }

    [Fact]
    public void LockInversion_PropertyChangedSubscriberReentersQueue_DoesNotDeadlock()
    {
        var queue = new PlaybackQueue();
        var item1 = CreateItem("1");
        var item2 = CreateItem("2");
        var item3 = CreateItem("3");

        int reentrantCount = 0;

        // When item1's QueueIndex changes, subscriber immediately queries and mutates the queue
        item1.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistItem.QueueIndex))
            {
                reentrantCount++;
                // These calls MUST NOT deadlock even though they are invoked during queue dispatch
                int count = queue.Count;
                var current = queue.GetCurrent();
                var entries = queue.Entries;
                Assert.NotNull(entries);
            }
        };

        queue.Enqueue(new[] { item1, item2 });
        Assert.True(reentrantCount > 0);

        queue.EnqueueNext(new[] { item3 });
        queue.Move(0, 2);
        queue.Shuffle();
        queue.Remove(0);
        queue.Clear();

        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task RapidQueueIndexChanges_MultiThreadedObserver_NeverObservesCorruptIndex()
    {
        var queue = new PlaybackQueue();
        var items = Enumerable.Range(0, 50).Select(i => CreateItem(i.ToString())).ToList();
        queue.Enqueue(items);

        bool running = true;
        var errors = new List<string>();

        var observerTask = Task.Run(() =>
        {
            while (Volatile.Read(ref running))
            {
                var entries = queue.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    int expectedIndex = i + 1;
                    int actualIndex = entries[i].Item.QueueIndex;
                    // The actual index could be transitioning, but should never be 0 or < -1
                    if (actualIndex == 0 || actualIndex < -1)
                    {
                        lock (errors)
                        {
                            errors.Add($"Invalid index {actualIndex} observed for item {entries[i].Item.Track.Title}");
                        }
                    }
                }
            }
        });

        var mutatorTask = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                queue.Shuffle();
                if (queue.Count > 5)
                {
                    queue.Move(0, 3);
                    queue.Move(2, 1);
                }
            }
        });

        await mutatorTask;
        Volatile.Write(ref running, false);
        await observerTask;

        Assert.Empty(errors);
        var finalEntries = queue.Entries;
        for (int i = 0; i < finalEntries.Count; i++)
        {
            Assert.Equal(i + 1, finalEntries[i].Item.QueueIndex);
        }
    }

    [Fact]
    public async Task MassiveQueueStress_1000Items_PreservesFifoAndIndexInvariants()
    {
        var queue = new PlaybackQueue();
        var items = Enumerable.Range(0, 1000).Select(i => CreateItem(i.ToString())).ToList();

        // Enqueue batch
        queue.Enqueue(items);
        Assert.Equal(1000, queue.Count);

        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(i + 1, items[i].QueueIndex);
        }

        // Concurrent move, shuffle, dequeue
        var tasks = new Task[4];
        tasks[0] = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++) queue.Shuffle();
        });
        tasks[1] = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++) queue.Move(0, Math.Min(i, queue.Count - 1));
        });
        tasks[2] = Task.Run(() =>
        {
            for (int i = 0; i < 150; i++) queue.Dequeue();
        });
        tasks[3] = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++) queue.Remove(Math.Min(i, queue.Count - 1));
        });

        await Task.WhenAll(tasks);

        var finalEntries = queue.Entries;
        Assert.Equal(queue.Count, finalEntries.Count);
        for (int i = 0; i < finalEntries.Count; i++)
        {
            Assert.Equal(i + 1, finalEntries[i].Item.QueueIndex);
        }
    }

    [Fact]
    public void PlaybackQueue_EdgeCases_BoundaryAndNullSafety()
    {
        var queue = new PlaybackQueue();

        // Null checks
        queue.Enqueue(null!);
        queue.Enqueue(null, null!);
        queue.EnqueueNext(null!);
        queue.EnqueueNext(null, null!);
        queue.RemoveItems(null!);
        queue.SetItems(null!, 0);

        Assert.Equal(0, queue.Count);

        // Out of bounds Move & Remove on empty
        Assert.False(queue.Move(-1, 0));
        Assert.False(queue.Move(0, -1));
        Assert.False(queue.Move(0, 5));
        Assert.False(queue.Remove(-1));
        Assert.False(queue.Remove(0));
        Assert.False(queue.Remove(10));

        // Single item shuffle
        var single = CreateItem("single");
        queue.Enqueue(new[] { single });
        Assert.Equal(1, queue.Count);
        Assert.Equal(1, single.QueueIndex);

        queue.Shuffle();
        Assert.Equal(1, queue.Count);
        Assert.Equal(1, single.QueueIndex);

        // Move to same index
        Assert.True(queue.Move(0, 0));
        Assert.Equal(1, single.QueueIndex);

        // RemoveAt legacy
        queue.RemoveAt(-5);
        queue.RemoveAt(5);
        Assert.Equal(1, queue.Count);

        queue.RemoveAt(0);
        Assert.Equal(0, queue.Count);
        Assert.Equal(-1, single.QueueIndex);
    }

    [Fact]
    public async Task Concurrent_ReadWriteAndIteration_ZeroExceptionsAndConsistentIndices()
    {
        var queue = new PlaybackQueue();
        var items = Enumerable.Range(0, 100).Select(i => CreateItem($"Track_{i}")).ToList();
        queue.Enqueue(items);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var readerTasks = new List<Task>();
        var writerTasks = new List<Task>();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // 4 Reader Threads
        for (int r = 0; r < 4; r++)
        {
            readerTasks.Add(Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var entries = queue.Entries;
                        int count = queue.Count;
                        var current = queue.GetCurrent();
                        var next = queue.GetNext();

                        // Validate index bounds and consistency
                        for (int i = 0; i < entries.Count; i++)
                        {
                            int idx = entries[i].Item.QueueIndex;
                            Assert.True(idx >= -1, $"Observed invalid QueueIndex: {idx}");
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        // 4 Writer Threads
        writerTasks.Add(Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                queue.Shuffle();
                Thread.Yield();
            }
        }));
        writerTasks.Add(Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (queue.Count > 4) queue.Move(0, queue.Count - 1);
                Thread.Yield();
            }
        }));
        writerTasks.Add(Task.Run(() =>
        {
            int id = 1000;
            while (!cts.Token.IsCancellationRequested)
            {
                queue.Enqueue(new[] { CreateItem($"Dynamic_{id++}") });
                if (queue.Count > 150) queue.Remove(queue.Count - 1);
                Thread.Yield();
            }
        }));
        writerTasks.Add(Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (queue.Count > 2) queue.Dequeue();
                Thread.Yield();
            }
        }));

        await Task.WhenAll(writerTasks.Concat(readerTasks));
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ConcurrentReentrancy_MutatingInEventHandler_ZeroDeadlock()
    {
        var queue = new PlaybackQueue();
        var items = Enumerable.Range(0, 50).Select(i => CreateItem($"Reentrant_{i}")).ToList();

        foreach (var item in items)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlaylistItem.QueueIndex))
                {
                    int count = queue.Count;
                    var cur = queue.GetCurrent();
                    var entries = queue.Entries;
                }
            };
        }

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                queue.Enqueue(items.Skip(i % 10).Take(5));
                queue.Shuffle();
                if (queue.Count > 5) queue.Move(0, 3);
                queue.Dequeue();
            }
        })).ToArray();

        var allTasks = Task.WhenAll(tasks);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
        var completed = await Task.WhenAny(allTasks, timeoutTask);
        Assert.Same(allTasks, completed);
    }

    [Fact]
    public void MonotonicVersioning_RejectsStaleUpdates_AndPreservesLatestIndex()
    {
        var item = CreateItem("Item_1");

        // Version 10 sets index to 3
        bool updated10 = item.UpdateQueueIndex(3, version: 10);
        Assert.True(updated10);
        Assert.Equal(3, item.QueueIndex);

        // Stale Version 5 attempts to set index to 1 -> Must be rejected!
        bool updated5 = item.UpdateQueueIndex(1, version: 5);
        Assert.False(updated5);
        Assert.Equal(3, item.QueueIndex); // Remains 3

        // Version 11 sets index to 5 -> Must succeed
        bool updated11 = item.UpdateQueueIndex(5, version: 11);
        Assert.True(updated11);
        Assert.Equal(5, item.QueueIndex);

        // Version 11 with same index -> Succeeds
        bool updated11Same = item.UpdateQueueIndex(5, version: 11);
        Assert.True(updated11Same);
        Assert.Equal(5, item.QueueIndex);
    }

    [Fact]
    public void Enqueue_BatchWithInternalDuplicates_DeduplicatesAndMaintainsContinuousIndices()
    {
        var queue = new PlaybackQueue();
        var a = CreateItem("A");
        var b = CreateItem("B");

        // Batch contains duplicate references: [a, b, a, b]
        queue.Enqueue(new[] { a, b, a, b });

        Assert.Equal(2, queue.Count);
        Assert.Equal(1, a.QueueIndex);
        Assert.Equal(2, b.QueueIndex);

        var entries = queue.Entries;
        Assert.Equal(2, entries.Count);
        Assert.Same(a, entries[0].Item);
        Assert.Same(b, entries[1].Item);
    }

    [Fact]
    public async Task PlaybackAdvanceLoop_WithConcurrentShuffleAndSetItems_MaintainsQueueInvariants()
    {
        var queue = new PlaybackQueue();
        var pool = Enumerable.Range(0, 200).Select(i => CreateItem($"Track_{i}")).ToList();
        queue.SetItems(pool.Take(50), 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Audio playback simulator thread
        int replenishId = 5000;
        var playbackTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var head = queue.Peek();
                if (head != null)
                {
                    var deq = queue.Dequeue();
                    if (deq != null)
                    {
                        Assert.Equal(-1, deq.Item.QueueIndex);
                    }
                }
                else
                {
                    // Replenish with fresh items
                    var fresh = Enumerable.Range(0, 20).Select(_ => CreateItem($"Replenish_{replenishId++}")).ToList();
                    queue.Enqueue(fresh);
                }
                Thread.Yield();
            }
        });

        // User UI shuffle & reorder thread
        var uiTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                queue.Shuffle();
                if (queue.Count > 10)
                {
                    queue.Move(queue.Count - 1, 0);
                }
                Thread.Yield();
            }
        });

        await Task.WhenAll(playbackTask, uiTask);

        // Final consistency check
        var remaining = queue.Entries;
        for (int i = 0; i < remaining.Count; i++)
        {
            Assert.Equal(i + 1, remaining[i].Item.QueueIndex);
        }
    }

    [Fact]
    public void BoundaryAndInvalidBounds_ConcurrencySafety()
    {
        var queue = new PlaybackQueue();

        Assert.Null(queue.GetCurrent());
        Assert.Null(queue.GetNext());
        Assert.Null(queue.Peek());
        Assert.Null(queue.Dequeue());
        Assert.False(queue.Remove(-1));
        Assert.False(queue.Remove(0));
        Assert.False(queue.Move(-1, 0));
        Assert.False(queue.Move(0, -1));

        queue.Shuffle();
        Assert.Equal(0, queue.Count);

        queue.Clear();
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task ExtremeHighConcurrency_32Threads_AllOperations_ZeroDeadlocksZeroExceptions()
    {
        var queue = new PlaybackQueue();
        const int poolSize = 150;
        var pool = Enumerable.Range(0, poolSize).Select(i => CreateItem($"Item_{i}")).ToList();

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var validationErrors = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        // Attach property changed listeners across items
        foreach (var item in pool)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlaylistItem.QueueIndex))
                {
                    int idx = item.QueueIndex;
                    if (idx != -1 && (idx < 1 || idx > poolSize))
                    {
                        validationErrors.Add($"Torn or out of bounds QueueIndex observed: {idx}");
                    }
                }
            };
        }

        // Attach Changed listener on queue - atomically inspects snapshot
        queue.Changed += () =>
        {
            try
            {
                var snapshot = queue.Entries;
                for (int i = 0; i < snapshot.Count; i++)
                {
                    if (snapshot[i]?.Item?.Track == null)
                    {
                        validationErrors.Add("Null entry observed during Changed callback");
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        };

        var tasks = new List<Task>();

        // 2 Enqueue threads
        for (int t = 0; t < 2; t++)
        {
            int tid = t;
            tasks.Add(Task.Run(() =>
            {
                var rand = new Random(tid * 100);
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        int start = rand.Next(0, poolSize - 10);
                        int count = rand.Next(1, 10);
                        queue.Enqueue(pool.Skip(start).Take(count));
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            }));
        }

        // 2 EnqueueNext threads
        for (int t = 0; t < 2; t++)
        {
            int tid = t;
            tasks.Add(Task.Run(() =>
            {
                var rand = new Random(tid * 200 + 1);
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        int start = rand.Next(0, poolSize - 10);
                        int count = rand.Next(1, 10);
                        queue.EnqueueNext(pool.Skip(start).Take(count));
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            }));
        }

        // 2 Dequeue threads
        for (int t = 0; t < 2; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        queue.Dequeue();
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            }));
        }

        // 2 Move threads (including random negative / out-of-bounds indices)
        for (int t = 0; t < 2; t++)
        {
            int tid = t;
            tasks.Add(Task.Run(() =>
            {
                var rand = new Random(tid * 300 + 2);
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        int c = queue.Count;
                        int from = rand.Next(-2, Math.Max(c + 5, 2));
                        int to = rand.Next(-2, Math.Max(c + 5, 2));
                        queue.Move(from, to);
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            }));
        }

        // 2 Remove threads (indices & items)
        for (int t = 0; t < 2; t++)
        {
            int tid = t;
            tasks.Add(Task.Run(() =>
            {
                var rand = new Random(tid * 400 + 3);
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (rand.Next(2) == 0)
                        {
                            int c = queue.Count;
                            int idx = rand.Next(-2, Math.Max(c + 5, 2));
                            queue.Remove(idx);
                        }
                        else
                        {
                            int start = rand.Next(0, poolSize - 5);
                            queue.RemoveItems(pool.Skip(start).Take(5));
                        }
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            }));
        }

        // 2 Shuffle threads
        for (int t = 0; t < 2; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        queue.Shuffle();
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            }));
        }

        // 2 SetItems & Clear threads
        for (int t = 0; t < 2; t++)
        {
            int tid = t;
            tasks.Add(Task.Run(() =>
            {
                var rand = new Random(tid * 500 + 4);
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (rand.Next(10) == 0)
                        {
                            queue.Clear();
                        }
                        else
                        {
                            int start = rand.Next(0, poolSize - 20);
                            int count = rand.Next(5, 20);
                            int startIdx = rand.Next(-5, count + 5);
                            queue.SetItems(pool.Skip(start).Take(count), startIdx);
                        }
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            }));
        }

        // 2 Snapshot Inspection threads
        for (int t = 0; t < 2; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var entries = queue.Entries;
                        for (int i = 0; i < entries.Count; i++)
                        {
                            var entry = entries[i];
                            if (entry?.Item?.Track == null)
                            {
                                validationErrors.Add("Null entry or track observed in Entries snapshot");
                            }
                        }
                        Thread.Yield();
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
        Assert.Empty(validationErrors);

        // Final sanity check: once mutations cease, all queue items have strictly monotonic 1..Count indices
        var finalEntries = queue.Entries;
        Assert.Equal(queue.Count, finalEntries.Count);
        for (int i = 0; i < finalEntries.Count; i++)
        {
            Assert.Equal(i + 1, finalEntries[i].Item.QueueIndex);
        }

        // Items not in queue should have -1
        var inQueueSet = new HashSet<PlaylistItem>(finalEntries.Select(e => e.Item));
        foreach (var item in pool)
        {
            if (!inQueueSet.Contains(item))
            {
                Assert.Equal(-1, item.QueueIndex);
            }
        }
    }

    [Fact]
    public async Task AdversarialEventReentrancy_24Threads_CascadingMutationsInCallbacks_ZeroDeadlocks()
    {
        var queue = new PlaybackQueue();
        var pool = Enumerable.Range(0, 100).Select(i => CreateItem($"Cascade_{i}")).ToList();
        var reentrancyDepth = new AsyncLocal<int>();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        foreach (var item in pool)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlaylistItem.QueueIndex))
                {
                    if (reentrancyDepth.Value < 2)
                    {
                        reentrancyDepth.Value++;
                        try
                        {
                            // Re-entrant queue calls during notification
                            int count = queue.Count;
                            var cur = queue.GetCurrent();
                            var entries = queue.Entries;
                            if (count > 2)
                            {
                                queue.Move(0, 1);
                            }
                            else
                            {
                                queue.Enqueue(new[] { item });
                            }
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                        finally
                        {
                            reentrancyDepth.Value--;
                        }
                    }
                }
            };
        }

        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    queue.Enqueue(pool.Skip((t + i) % 80).Take(5));
                    queue.Shuffle();
                    queue.Dequeue();
                    if (queue.Count > 10) queue.Remove(0);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToArray();

        var allTasks = Task.WhenAll(tasks);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
        var completed = await Task.WhenAny(allTasks, timeoutTask);
        Assert.Same(allTasks, completed);
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task MonotonicVersioning_ConcurrentInterleavedDispatches_AlwaysConvergesCorrectly()
    {
        var queue = new PlaybackQueue();
        const int itemCount = 50;
        var items = Enumerable.Range(0, itemCount).Select(i => CreateItem($"Conv_{i}")).ToList();

        queue.SetItems(items, 0);

        var tasks = new List<Task>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // 8 threads constantly modifying queue
        for (int i = 0; i < 8; i++)
        {
            int tid = i;
            tasks.Add(Task.Run(() =>
            {
                var rand = new Random(tid * 777);
                while (!cts.Token.IsCancellationRequested)
                {
                    int op = rand.Next(5);
                    switch (op)
                    {
                        case 0: queue.Shuffle(); break;
                        case 1: if (queue.Count > 2) queue.Move(0, queue.Count - 1); break;
                        case 2: queue.EnqueueNext(new[] { items[rand.Next(items.Count)] }); break;
                        case 3: queue.Dequeue(); break;
                        case 4: queue.Remove(rand.Next(0, Math.Max(1, queue.Count))); break;
                    }
                    Thread.Yield();
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Allow microsecond for any pending dispatches to complete
        await Task.Delay(50);

        var finalEntries = queue.Entries;
        Assert.Equal(queue.Count, finalEntries.Count);
        for (int i = 0; i < finalEntries.Count; i++)
        {
            Assert.Equal(i + 1, finalEntries[i].Item.QueueIndex);
        }

        var activeSet = new HashSet<PlaylistItem>(finalEntries.Select(e => e.Item));
        foreach (var it in items)
        {
            if (!activeSet.Contains(it))
            {
                Assert.Equal(-1, it.QueueIndex);
            }
        }
    }

    [Fact]
    public async Task ConcurrentEventSubscriptionUnsubscription_ThreadSafe()
    {
        var queue = new PlaybackQueue();
        var items = Enumerable.Range(0, 30).Select(i => CreateItem($"Sub_{i}")).ToList();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var tasks = new List<Task>();

        // 4 Mutation threads
        for (int i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    queue.Enqueue(items.Take(10));
                    queue.Shuffle();
                    queue.Clear();
                    Thread.Yield();
                }
            }));
        }

        // 4 Subscriber / Unsubscriber threads
        for (int i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    Action handler = () => { var count = queue.Count; };
                    queue.Changed += handler;
                    Thread.Yield();
                    queue.Changed -= handler;
                }
            }));
        }

        await Task.WhenAll(tasks);
    }
}
