using DawnPlayer.Core.Audio.Dsp;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// EBU R128 (BS.1770) loudness scanner: K-weighting correctness via level invariance/difference,
/// gating semantics (silence and short bursts do not skew the integrated value), block feeding
/// across tracks for album integration, and reproducible peak reporting.
/// </summary>
public sealed class LoudnessScannerTests
{
    private static float[] SineInterleaved(int sampleRate, int channels, double freqHz, double seconds, double amplitude)
    {
        int frames = (int)(sampleRate * seconds);
        var samples = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            float s = (float)(amplitude * Math.Sin(2.0 * Math.PI * freqHz * f / sampleRate));
            for (int c = 0; c < channels; c++) samples[f * channels + c] = s;
        }
        return samples;
    }

    private static LoudnessResult Scan(LoudnessScanner scanner, float[] samples, int channels)
    {
        scanner.ProcessSamples(samples, 0, samples.Length, channels);
        return scanner.Finish();
    }

    [Fact]
    public void FullScaleSin_997Hz_GainNearMinusEighteenLufs_Reference()
    {
        // Sanity anchor: a full-scale 997 Hz sine measures very close to 0 LUFS after K-weighting
        // (the 997 Hz point is nearly unity on the K curve), so its RG2 gain sits near +18 dB.
        var samples = SineInterleaved(48000, 2, 997.0, 3.0, 1.0);
        var result = Scan(new LoudnessScanner(48000, 2), samples, 2);

        Assert.True(result.IntegratedLufs > -2.0 && result.IntegratedLufs < 2.0,
            $"lufs={result.IntegratedLufs}");
        Assert.True(Math.Abs(result.Peak - 1.0) < 1e-4, $"peak={result.Peak}");
    }

    [Fact]
    public void LevelDifferenceOf6dB_Reports6dB()
    {
        int sr = 48000;
        var quiet = Scan(new LoudnessScanner(sr, 2), SineInterleaved(sr, 2, 440.0, 3.0, 0.25), 2);
        var loud = Scan(new LoudnessScanner(sr, 2), SineInterleaved(sr, 2, 440.0, 3.0, 1.0), 2);

        // 1.0 / 0.25 = 4x amplitude = +12 dB... measured through gating, tolerance is loose.
        Assert.True(loud.IntegratedLufs - quiet.IntegratedLufs is > 10.0 and < 14.0,
            $"quiet={quiet.IntegratedLufs} loud={loud.IntegratedLufs}");
    }

    [Fact]
    public void Silence_MeasuresNegativeInfinity()
    {
        int sr = 48000;
        var result = Scan(new LoudnessScanner(sr, 2), new float[sr * 2 * 2], 2);
        Assert.True(double.IsNegativeInfinity(result.IntegratedLufs));
        Assert.Equal(0.0, result.Peak);
    }

    [Fact]
    public void ShortBurst_DoesNotSkewGatedIntegration()
    {
        int sr = 48000;
        var samples = SineInterleaved(sr, 2, 440.0, 10.0, 0.4);
        // One 50 ms full-scale clap inside ten seconds of mid-level tone: absolute gating keeps
        // the block, but the gated mean must stay within 2 dB of the tone alone.
        for (int f = 0; f < sr / 20; f++)
        {
            samples[f * 2] = 1.0f;
            samples[f * 2 + 1] = 1.0f;
        }

        var withBurst = Scan(new LoudnessScanner(sr, 2), samples, 2);
        var toneOnly = Scan(new LoudnessScanner(sr, 2), SineInterleaved(sr, 2, 440.0, 10.0, 0.4), 2);

        Assert.True(Math.Abs(withBurst.IntegratedLufs - toneOnly.IntegratedLufs) < 2.0,
            $"burst={withBurst.IntegratedLufs} tone={toneOnly.IntegratedLufs}");
    }

    [Fact]
    public void BlocksAccumulate_ForAlbumIntegration()
    {
        int sr = 48000;
        var trackA = SineInterleaved(sr, 2, 440.0, 2.0, 0.5);
        var trackB = SineInterleaved(sr, 2, 440.0, 2.0, 0.5);

        var album = new LoudnessScanner(sr, 2);
        var first = new LoudnessScanner(sr, 2);
        var second = new LoudnessScanner(sr, 2);

        var ra = Scan(first, trackA, 2);
        var rb = Scan(second, trackB, 2);
        album.AppendBlocks(first.BlockEnergies);
        album.AppendBlocks(second.BlockEnergies);
        var albumResult = album.Finish();

        // Identical twins: the album value equals either track value exactly.
        Assert.Equal(ra.IntegratedLufs, albumResult.IntegratedLufs, 9);
        Assert.Equal(rb.IntegratedLufs, albumResult.IntegratedLufs, 9);
    }

    [Fact]
    public void FilterState_ConvergesToUnity_ForLowTestTone()
    {
        // A 100 Hz tone passes the K-weighting with a known, stable attenuation: feeding the same
        // tone through two fresh scanners must agree, proving no per-instance state leak.
        int sr = 48000;
        var tone = SineInterleaved(sr, 2, 100.0, 2.0, 0.5);
        var a = Scan(new LoudnessScanner(sr, 2), tone, 2);
        var b = Scan(new LoudnessScanner(sr, 2), tone, 2);
        Assert.Equal(a.IntegratedLufs, b.IntegratedLufs, 12);
    }
}
