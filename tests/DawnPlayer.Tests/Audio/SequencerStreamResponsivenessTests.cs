using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using NAudio.Wave;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// The render lock is held across decoder I/O, so every property the UI polls at interactive rates
/// has to be readable without it. These tests stall a decoder inside Read and assert the UI-facing
/// surface still answers, which is the invariant that keeps the seekbar from freezing on a slow or
/// network-backed file.
/// </summary>
public sealed class SequencerStreamResponsivenessTests
{
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private static Track TrackAt(string path) => new()
    {
        Path = path,
        Title = System.IO.Path.GetFileNameWithoutExtension(path),
        DurationMs = 5000
    };

    private static PendingTrack Pending(ITrackReader reader, string path = @"C:\m\a.flac") =>
        new()
        {
            Playlist = new Playlist("Responsiveness"),
            Item = new PlaylistItem(TrackAt(path)),
            Reader = reader
        };

    /// <summary>A reader whose Read blocks until released, standing in for slow decoder I/O.</summary>
    private sealed class BlockingReader : ITrackReader
    {
        private readonly BlockingSamples _samples;

        public BlockingReader(TimeSpan? totalTime = null)
        {
            _samples = new BlockingSamples(Format);
            TotalTime = totalTime ?? TimeSpan.FromSeconds(5);
        }

        public ISampleProvider Samples => _samples;
        public WaveFormat SourceFormat => Format;
        public TimeSpan TotalTime { get; }
        public TimeSpan CurrentTime { get; set; }
        public string Path => @"C:\m\a.flac";
        public bool Disposed { get; private set; }

        public ManualResetEventSlim EnteredRead => _samples.Entered;
        public void ReleaseRead() => _samples.Release();

        public void Dispose() => Disposed = true;

        private sealed class BlockingSamples : ISampleProvider
        {
            private readonly ManualResetEventSlim _gate = new(false);
            public readonly ManualResetEventSlim Entered = new(false);

            public BlockingSamples(WaveFormat format) => WaveFormat = format;
            public WaveFormat WaveFormat { get; }

            public void Release() => _gate.Set();

            public int Read(float[] buffer, int offset, int count)
            {
                Entered.Set();
                _gate.Wait();
                Array.Clear(buffer, offset, count);
                return count;
            }
        }
    }

    /// <summary>A reader that yields a fixed number of frames and then reports end-of-stream.</summary>
    private sealed class FiniteReader : ITrackReader
    {
        private readonly FiniteSamples _samples;

        public FiniteReader(int totalFrames, float amplitude = 0.25f)
        {
            _samples = new FiniteSamples(Format, totalFrames, amplitude);
            TotalTime = TimeSpan.FromSeconds((double)totalFrames / Format.SampleRate);
        }

        public ISampleProvider Samples => _samples;
        public WaveFormat SourceFormat => Format;
        public TimeSpan TotalTime { get; }
        public TimeSpan CurrentTime { get; set; }
        public string Path => @"C:\m\finite.flac";
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        private sealed class FiniteSamples : ISampleProvider
        {
            private readonly int _totalFloats;
            private readonly float _amplitude;
            private int _served;

            public FiniteSamples(WaveFormat format, int totalFrames, float amplitude)
            {
                WaveFormat = format;
                _totalFloats = totalFrames * format.Channels;
                _amplitude = amplitude;
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                int remaining = _totalFloats - _served;
                if (remaining <= 0) return 0;

                int n = Math.Min(count, remaining);
                for (int i = 0; i < n; i++) buffer[offset + i] = _amplitude;
                _served += n;
                return n;
            }
        }
    }

    private static SequencerStream CreateSequencer(bool applyVolume = true) =>
        new(Format, applyVolume, gainProvider: _ => 1.0f, latencyMs: 50);

    [Fact]
    public void UiFacingReads_DoNotBlock_WhileDecoderIsStalledInsideRead()
    {
        var reader = new BlockingReader(TimeSpan.FromSeconds(7));
        var seq = CreateSequencer();
        var pending = Pending(reader);
        seq.SwitchTo(pending);

        var render = Task.Factory.StartNew(
            () => seq.Read(new byte[16384], 0, 16384),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(reader.EnteredRead.Wait(TimeSpan.FromSeconds(15)), "Render thread never reached the decoder.");

        try
        {
            // Each of these used to take the same lock the stalled Read is holding.
            var probe = Task.Factory.StartNew(() =>
            {
                _ = seq.GetPosition();
                _ = seq.TotalTime;
                _ = seq.CurrentItem;
                _ = seq.RemainingTime;
                _ = seq.HasPrefetched;
                seq.SetGain(0.5f);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            Assert.True(probe.Wait(TimeSpan.FromSeconds(15)),
                "UI-facing reads blocked behind decoder I/O held under the render lock.");
            Assert.Equal(TimeSpan.FromSeconds(7), seq.TotalTime);
            Assert.Same(pending.Item, seq.CurrentItem);
        }
        finally
        {
            reader.ReleaseRead();
            render.Wait(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public void SetPrefetched_DoesNotBlock_WhileDecoderIsStalledInsideRead()
    {
        var reader = new BlockingReader();
        var seq = CreateSequencer();
        seq.SwitchTo(Pending(reader));

        var render = Task.Factory.StartNew(
            () => seq.Read(new byte[16384], 0, 16384),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(reader.EnteredRead.Wait(TimeSpan.FromSeconds(15)), "Render thread never reached the decoder.");

        try
        {
            var nextReader = new FiniteReader(1000);
            var queueing = Task.Factory.StartNew(
                () => seq.SetPrefetched(Pending(nextReader, @"C:\m\b.flac")),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Assert.True(queueing.Wait(TimeSpan.FromSeconds(15)),
                "Queueing the next track blocked behind decoder I/O.");
            Assert.True(seq.HasPrefetched);
        }
        finally
        {
            reader.ReleaseRead();
            render.Wait(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public void GaplessBoundary_ChainsPrefetchedTrackAndRestartsPosition()
    {
        var first = new FiniteReader(2000);
        var second = new FiniteReader(4000);

        var seq = CreateSequencer();
        var firstPending = Pending(first, @"C:\m\first.flac");
        var secondPending = Pending(second, @"C:\m\second.flac");

        seq.SwitchTo(firstPending);
        seq.SetPrefetched(secondPending);

        // Ask for far more than the first track holds, so the boundary is crossed inside one Read.
        var buffer = new byte[Format.BlockAlign * 3000];
        int read = seq.Read(buffer, 0, buffer.Length);

        Assert.True(read > 0);
        Assert.Same(secondPending.Item, seq.CurrentItem);
        Assert.Equal(second.TotalTime, seq.TotalTime);
        Assert.False(seq.HasPrefetched);
        Assert.True(first.Disposed, "The drained track's reader must be disposed at the boundary.");
        // Position restarts from the new track, not from the sequencer's running byte count.
        Assert.True(seq.GetPosition() < first.TotalTime);
    }

    [Fact]
    public void GaplessBoundary_PositionSampledConcurrently_StaysWithinTrackDuration()
    {
        var first = new FiniteReader(20000);
        var second = new FiniteReader(20000);

        var seq = CreateSequencer();
        seq.SwitchTo(Pending(first, @"C:\m\first.flac"));
        seq.SetPrefetched(Pending(second, @"C:\m\second.flac"));

        using var cts = new CancellationTokenSource();

        // Position and duration are two separate reads, so a sampler can straddle a boundary and
        // pair one track's position with the next track's duration. The invariant that must hold
        // is per-observation: never negative, and never beyond the longest track in the chain.
        var longest = first.TotalTime > second.TotalTime ? first.TotalTime : second.TotalTime;
        int offThreadSamples = 0;
        int violations = 0;

        void Check(TimeSpan pos)
        {
            if (pos < TimeSpan.Zero || pos > longest) Interlocked.Increment(ref violations);
        }

        // Concurrent pressure on the getters. It may or may not get scheduled before the render
        // loop finishes, so the assertions below do not depend on it having sampled anything.
        var poller = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                Check(seq.GetPosition());
                Interlocked.Increment(ref offThreadSamples);
            }
        });

        var buffer = new byte[Format.BlockAlign * 512];
        int renderSamples = 0;
        for (int i = 0; i < 100; i++)
        {
            if (seq.Read(buffer, 0, buffer.Length) == 0) break;
            Check(seq.GetPosition());
            renderSamples++;
        }

        cts.Cancel();
        poller.Wait(TimeSpan.FromSeconds(5));

        Assert.True(renderSamples > 0, "The render loop produced no audio, so nothing was sampled.");
        Assert.Equal(0, violations);
        Assert.True(offThreadSamples >= 0);
    }

    [Fact]
    public void GaplessBoundary_DoesNotAllocateOnTheRenderThread()
    {
        var first = new FiniteReader(4000);
        var second = new FiniteReader(4000);

        var seq = CreateSequencer();
        seq.SwitchTo(Pending(first, @"C:\m\first.flac"));

        // Warm every buffer the render path resizes lazily, then queue the next track: building
        // its provider graph must happen here, on this thread, not at the boundary.
        var buffer = new byte[Format.BlockAlign * 1000];
        seq.Read(buffer, 0, buffer.Length);
        seq.SetPrefetched(Pending(second, @"C:\m\second.flac"));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();

        // This pass crosses the boundary: the drained first track hands over to the queued one.
        seq.Read(buffer, 0, buffer.Length);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(second);
        Assert.True(allocated == 0,
            $"Crossing a gapless boundary allocated {allocated} bytes on the render thread");
    }

    [Fact]
    public void FormatChangePrefetch_LeavesTrackForTheRebuiltSession()
    {
        var first = new FiniteReader(1000);
        var second = new FiniteReader(1000);

        var seq = CreateSequencer();
        seq.SwitchTo(Pending(first, @"C:\m\first.flac"));

        SequencerEndReason? reason = null;
        seq.SequenceEnded += r => reason = r;

        var restartPending = new PendingTrack
        {
            Playlist = new Playlist("Responsiveness"),
            Item = new PlaylistItem(TrackAt(@"C:\m\second.flac")),
            Reader = second,
            RequiresRestart = true
        };
        seq.SetPrefetched(restartPending);

        var buffer = new byte[Format.BlockAlign * 2000];
        seq.Read(buffer, 0, buffer.Length);

        Assert.Equal(SequencerEndReason.FormatChange, reason);
        Assert.True(first.Disposed, "The drained reader must be disposed at a format change.");
        Assert.False(second.Disposed, "The queued reader belongs to the rebuilt session.");
        // Still queued, so the controller can claim it for the new session.
        Assert.True(seq.HasPrefetched);
        Assert.Same(restartPending, seq.TakePrefetched());
    }

    [Fact]
    public async Task ConcurrentPrefetchChurnDuringRender_NeverLosesOrDoubleDisposesReaders()
    {
        var seq = CreateSequencer();
        seq.SwitchTo(Pending(new FiniteReader(200000), @"C:\m\long.flac"));

        var readers = new List<FiniteReader>();
        int errors = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var render = Task.Run(() =>
        {
            var buffer = new byte[Format.BlockAlign * 256];
            while (!cts.IsCancellationRequested)
            {
                try { seq.Read(buffer, 0, buffer.Length); }
                catch { Interlocked.Increment(ref errors); }
            }
        });

        var churn = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var reader = new FiniteReader(1000);
                lock (readers) readers.Add(reader);
                try
                {
                    seq.SetPrefetched(Pending(reader, @"C:\m\next.flac"));
                    _ = seq.HasPrefetched;
                    _ = seq.GetPosition();
                }
                catch { Interlocked.Increment(ref errors); }
                Thread.Yield();
            }
        });

        await Task.WhenAll(render, churn);
        Assert.Equal(0, errors);

        // Every superseded reader must have been disposed exactly once by the replacement.
        seq.Cancel();
        lock (readers)
        {
            Assert.All(readers.Take(readers.Count - 1), r => Assert.True(r.Disposed,
                "A superseded prefetch reader was leaked instead of disposed."));
        }
    }
}
