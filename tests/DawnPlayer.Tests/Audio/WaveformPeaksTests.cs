using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// Waveform peak scanning: bucket count/shape, the tone's expected envelope, cue-range sub-scans,
/// and the mtime-keyed in-memory cache.
/// </summary>
public sealed class WaveformPeaksTests
{
    [Fact]
    public void Scan_ProducesNormalizedBuckets_ForConstantTone()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "tone.wav");
            File.WriteAllBytes(file, MinimalWav(44100, 1, 440.0, 1.0, amplitude: 0.5));

            var peaks = WaveformPeaks.GetOrScan(file, buckets: 100);
            Assert.NotNull(peaks);
            Assert.Equal(100, peaks!.Length);

            // A constant-amplitude sine fills every bucket near the same level.
            float max = peaks.Max();
            Assert.True(max > 0.4f && max <= 0.6f, $"max {max}");
            Assert.All(peaks, p => Assert.True(p > 0.3f, $"flat bucket {p}"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Scan_OfCueRange_CoversOnlyTheFragment()
    {
        var dir = NewTempDir();
        try
        {
            // A decaying tone: the first half is loud, the second half is silence. A cue range
            // over the loud half must show loud buckets, one over the quiet half near-zero ones.
            var file = Path.Combine(dir, "fade.wav");
            File.WriteAllBytes(file, MinimalWav(22050, 1, 440.0, 2.0, amplitude: 0.8, fadeOutAfterSeconds: 1.0));

            var loud = WaveformPeaks.GetOrScan(AppPaths.MakeCuePath(file, 0, 1000), buckets: 50)!;
            var quiet = WaveformPeaks.GetOrScan(AppPaths.MakeCuePath(file, 1000, 2000), buckets: 50)!;

            Assert.True(loud.Max() > 0.5f, $"loud half max {loud.Max()}");
            Assert.True(quiet.Max() < 0.05f, $"quiet half max {quiet.Max()}");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Cache_ReturnsSameInstance_UntilFileChanges()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "tone.wav");
            File.WriteAllBytes(file, MinimalWav(44100, 1, 440.0, 0.5));

            var first = WaveformPeaks.GetOrScan(file);
            var second = WaveformPeaks.GetOrScan(file);
            Assert.Same(first, second);

            // Bump the mtime: the cache must re-scan, not hand back the stale envelope.
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow + TimeSpan.FromSeconds(2));
            var third = WaveformPeaks.GetOrScan(file);
            Assert.NotSame(first, third);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Scan_ReturnsNull_ForUnopenablePaths()
    {
        Assert.Null(WaveformPeaks.GetOrScan(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav")));
        Assert.Null(WaveformPeaks.GetOrScan(""));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DawnPlayer_Waveform_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static byte[] MinimalWav(int sampleRate, int channels, double freqHz, double seconds,
        double amplitude = 0.5, double? fadeOutAfterSeconds = null)
    {
        int frames = (int)(sampleRate * seconds);
        const short bits = 16;
        int dataBytes = frames * channels * (bits / 8);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF".ToCharArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * (bits / 8));
        w.Write((short)(channels * (bits / 8)));
        w.Write(bits);
        w.Write("data".ToCharArray());
        w.Write(dataBytes);

        var samples = new short[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            double t = (double)f / sampleRate;
            double amp = fadeOutAfterSeconds.HasValue && t >= fadeOutAfterSeconds.Value ? 0.0 : amplitude;
            short s = (short)(amp * short.MaxValue * Math.Sin(2.0 * Math.PI * freqHz * f / sampleRate));
            for (int c = 0; c < channels; c++) samples[f * channels + c] = s;
        }
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
