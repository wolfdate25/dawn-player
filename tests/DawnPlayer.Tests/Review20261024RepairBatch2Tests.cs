using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Defensive gates for the second repair batch (2026-10-24, groups A/B/D — remaining-items
/// approval). Each test pins the exact defect a repair closed.
/// </summary>
public sealed class Review20261024RepairBatch2Tests : IDisposable
{
    private readonly string _tempDir;

    public Review20261024RepairBatch2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_RepairGates2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
        SettingsWriter.DiscardPending();
        SettingsWriter.WriteSink = null;
    }

    // ---------------- A22: DSF bit-packing variants ----------------

    /// <summary>Builds a minimal DSF. bitsPerSample: 1 (spec packing) or 8 (per-byte variant).</summary>
    private static byte[] Dsf(int sampleRate, int channels, int bytesPerChannel, int bitsPerSample)
    {
        int blockSize = 4096;
        int blocks = (int)Math.Ceiling(bytesPerChannel / (double)blockSize);
        int dataBytes = blocks * blockSize * channels + 8;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        long fileSize = 28 + 52 + dataBytes;

        w.Write("DSD ".ToCharArray());
        w.Write(28L);
        w.Write(fileSize);
        w.Write(0L);

        w.Write("fmt ".ToCharArray());
        w.Write(52L);
        w.Write(1);
        w.Write(0);
        w.Write(channels == 1 ? 1 : 2);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(bitsPerSample);
        w.Write((long)bytesPerChannel * 8);
        w.Write(blockSize);
        w.Write(0);

        w.Write("data".ToCharArray());
        w.Write((long)dataBytes);
        for (int i = 0; i < dataBytes - 8; i++) w.Write((byte)(i % 2 == 0 ? 0xAA : 0x55));
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void Dsf_BitsPerSample8_IsRejectedWithClearError()
    {
        string path = Path.Combine(_tempDir, "packed8.dsf");
        File.WriteAllBytes(path, Dsf(2822400, 1, 8192, bitsPerSample: 8));

        var ex = Assert.Throws<AudioOpenException>(() => new DsfTrackReader(path));
        Assert.Contains("bitsPerSample", ex.Message);
    }

    // ---------------- A3: truncated DSF ----------------

    [Fact]
    public void Dsf_TruncatedFile_ClampsToRealDataAndDrainsCleanly()
    {
        string path = Path.Combine(_tempDir, "truncated.dsf");
        var full = Dsf(2822400, 1, bytesPerChannel: 8192, bitsPerSample: 1);
        // Cut the file in half while the header still declares the full data length.
        File.WriteAllBytes(path, full.Take(full.Length / 2).ToArray());

        using var reader = new DsfTrackReader(path);
        // Total time derives from the CLAMPED byte count: half the declared data remains.
        double expectedSeconds = (reader.SourceFormat.SampleRate > 0) ? reader.TotalTime.TotalSeconds : 0;
        double declaredFullSeconds = 8192.0 * 8 / 2822400;
        Assert.True(expectedSeconds < declaredFullSeconds,
            $"Total time must clamp to the real file: got {expectedSeconds:F3}s, declared {declaredFullSeconds:F3}s");

        // The provider drains to true EOF instead of repeating stale buffer bytes forever.
        var buf = new float[4096];
        int totalFrames = 0;
        while (true)
        {
            int got = reader.Samples.Read(buf);
            if (got <= 0) break;
            totalFrames += got;
            Assert.True(totalFrames < 48000 * 10, "truncated DSF must terminate");
        }
    }

    // ---------------- A25: atomic .lrc save ----------------

    [Fact]
    public void LrcParser_SaveToFile_ReplacesContentAndLeavesNoTemp()
    {
        string path = Path.Combine(_tempDir, "song.lrc");
        File.WriteAllText(path, "[00:01.00]old");

        LrcParser.SaveToFile(path, "[00:02.00]new");
        Assert.Equal("[00:02.00]new", File.ReadAllText(path));
        Assert.False(Directory.GetFiles(_tempDir, "*.dawn-tmp-*").Any(), "temp files must be cleaned up");

        // A brand-new path works too (no existing file to replace).
        string fresh = Path.Combine(_tempDir, "fresh.lrc");
        LrcParser.SaveToFile(fresh, "[00:03.00]fresh");
        Assert.Equal("[00:03.00]fresh", File.ReadAllText(fresh));
    }

    // ---------------- B8: unresolved playlist entries keep their position ----------------

    [Fact]
    public void M3u_UnresolvedEntries_RoundTripAtTheirOriginalPositions()
    {
        var t2 = new DawnPlayer.Core.Models.Track
        {
            Path = Path.Combine(_tempDir, "b.mp3"),
            Title = "B",
            DurationMs = 1000,
        };
        var items = new List<DawnPlayer.Core.Models.PlaylistItem> { new(t2) };
        string path = Path.Combine(_tempDir, "pl.m3u8");

        M3u.Write(path, items, "PL", new List<(int, string)>
        {
            (0, @"X:\offline\a.mp3"),  // before the resolved item
            (1, @"X:\offline\c.mp3"),  // after it
        });

        var entries = M3u.Read(path);
        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].Unresolved, "first entry must be the unresolved marker");
        Assert.Equal(@"X:\offline\a.mp3", entries[0].Path);
        Assert.False(entries[1].Unresolved);
        Assert.Equal(t2.Path, entries[1].Path);
        Assert.True(entries[2].Unresolved, "last entry must be the unresolved marker");
        Assert.Equal(@"X:\offline\c.mp3", entries[2].Path);
    }

    // ---------------- B1: IR resampling ----------------

    [Fact]
    public void ImpulseResponse_ResampleLinear_ScalesLengthAndPreservesShape()
    {
        // 441 samples of a ramp at 44.1 kHz = 10 ms → at 48 kHz the same 10 ms is 480 samples.
        var input = Enumerable.Range(0, 441).Select(i => i / 441f).ToArray();

        var output = ImpulseResponse.ResampleLinear(input, 44100, 48000);

        Assert.InRange(output.Length, 470, 490);
        Assert.InRange(output[0], 0f, 0.02f);
        Assert.InRange(output[^1], 0.98f, 1.01f);
    }

    [Fact]
    public void ImpulseResponse_LoadMono_TruncatesBeyondBudget()
    {
        // A synthetic WAV at 48 kHz, 3 s of DC — the 2 s budget must truncate and say so.
        string path = Path.Combine(_tempDir, "ir.wav");
        int rate = 48000;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            int dataBytes = rate * 3 * 2; // 3 s mono 16-bit
            w.Write("RIFF".ToCharArray());
            w.Write(36 + dataBytes);
            w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray());
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data".ToCharArray());
            w.Write(dataBytes);
            for (int i = 0; i < rate * 3; i++) w.Write((short)0);
            w.Flush();
            File.WriteAllBytes(path, ms.ToArray());
        }

        var loaded = ImpulseResponse.LoadMono(path, 48000);

        Assert.NotNull(loaded);
        Assert.True(loaded!.Truncated, "a 3 s IR must be reported as truncated");
        Assert.Equal((int)(ImpulseResponse.MaxSeconds * 48000), loaded.Samples.Length);
    }

    // ---------------- B14: settings flush ordering ----------------

    [Fact]
    public void SettingsWriter_ConcurrentFlushes_LandInSnapshotOrder()
    {
        SettingsWriter.DiscardPending();
        var written = new List<string>();
        var firstWriteStarted = new ManualResetEventSlim(false);
        var releaseFirstWrite = new ManualResetEventSlim(false);
        var flush2Done = new ManualResetEventSlim(false);
        int firstWriterSeen = 0;

        SettingsWriter.WriteSink = json =>
        {
            if (Interlocked.CompareExchange(ref firstWriterSeen, 1, 0) == 0)
            {
                firstWriteStarted.Set();
                // The older snapshot's write stalls mid-flight, exactly like a slow disk.
                releaseFirstWrite.Wait(3000);
            }
            lock (written) written.Add(json);
        };

        var s1 = AppSettings.CreateDefault();
        s1.Playback.Volume = 0.25;
        var s2 = AppSettings.CreateDefault();
        s2.Playback.Volume = 0.75;

        try
        {
            SettingsWriter.Schedule(s1);
            var flush1 = Task.Run(() => SettingsWriter.Flush());
            Assert.True(firstWriteStarted.Wait(2000), "first flush must reach the sink");

            // A newer snapshot is scheduled and flushed while the older write is in flight.
            _ = Task.Run(() =>
            {
                SettingsWriter.Schedule(s2);
                SettingsWriter.Flush();
                flush2Done.Set();
            });

            // Without sequence-ordered writes, flush 2 could land first and be overwritten by
            // the slow older write afterwards.
            Assert.False(flush2Done.Wait(400), "the newer flush must wait for the older snapshot's write");

            releaseFirstWrite.Set();
            Assert.True(flush2Done.Wait(3000), "newer flush must proceed once the older one lands");
            Assert.True(flush1.Wait(1000));

            lock (written) Assert.Equal(2, written.Count);
        }
        finally
        {
            releaseFirstWrite.Set();
        }
    }
}
