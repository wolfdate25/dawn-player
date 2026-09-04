using DawnPlayer.Core.Audio.Dsp;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// Convolution DSP: FFT round-trip accuracy, and the convolver's behavior against mathematically
/// defined impulses — a delta IR must pass the signal through (with one block of latency), and a
/// delayed delta must delay it by exactly that many samples.
/// </summary>
public sealed class ConvolutionDspEffectTests
{
    private const int Channels = 2;
    private const int Rate = 44100;

    [Fact]
    public void Fft_RoundTrips_WithinTolerance()
    {
        var fft = new Fft(1024);
        var re = new float[1024];
        var im = new float[1024];
        var refRe = new float[1024];
        for (int i = 0; i < 1024; i++)
        {
            re[i] = MathF.Sin(2 * MathF.PI * 37 * i / 1024f);
            refRe[i] = re[i];
        }

        fft.Forward(re, im);
        fft.Inverse(re, im);

        for (int i = 0; i < 1024; i++)
        {
            Assert.True(MathF.Abs(re[i] - refRe[i]) < 1e-4f, $"bin {i}: {re[i]} vs {refRe[i]}");
        }
    }

    [Fact]
    public void DeltaImpulse_PassesThrough_AfterOneBlockLatency()
    {
        var conv = new ConvolutionDspEffect();
        conv.Initialize(Rate, Channels);

        // A single 1.0 sample at t=0 is the identity IR.
        conv.SetImpulse(new float[] { 1f });
        conv.IsEnabled = true;

        var output = Run(conv, 4 * ConvolutionDspEffect.BlockSamples * Channels, TestTone);

        // The first block is latency cover (dry passthrough); after that, a delta IR reproduces
        // the input shifted by exactly one block. The IR is peak-normalized to 0.5, and a delta
        // IR's only peak is its single sample, so the effective gain is 0.5.
        int latency = ConvolutionDspEffect.BlockSamples;
        for (int frame = latency; frame < 4 * ConvolutionDspEffect.BlockSamples; frame += 16)
        {
            float got = output[frame * Channels];
            float want = 0.5f * TestTone(frame - latency);
            Assert.True(MathF.Abs(got - want) < 2e-3f, $"frame {frame}: {got} vs {want}");
        }
    }

    [Fact]
    public void DelayedDeltaImpulse_DelayedByExactSamples()
    {
        var conv = new ConvolutionDspEffect();
        conv.Initialize(Rate, Channels);

        const int delay = 1000;
        var impulse = new float[delay + 1];
        impulse[delay] = 1f;
        conv.SetImpulse(impulse);
        conv.IsEnabled = true;

        int frames = 6 * ConvolutionDspEffect.BlockSamples;
        var output = Run(conv, frames * Channels, TestTone);

        // After the one-block pipeline latency, the output equals input delayed by `delay`
        // (scaled by the 0.5 peak normalization, as above).
        int pipeline = ConvolutionDspEffect.BlockSamples + delay;
        for (int frame = pipeline; frame < frames; frame += 16)
        {
            float got = output[frame * Channels];
            float want = 0.5f * TestTone(frame - pipeline);
            Assert.True(MathF.Abs(got - want) < 2e-3f, $"frame {frame}: {got} vs {want}");
        }
    }

    [Fact]
    public void Reset_ClearsTail_NoOldSignalAfterSilence()
    {
        var conv = new ConvolutionDspEffect();
        conv.Initialize(Rate, Channels);

        // A long noisy IR; feed signal (a 1 s noise IR rings far past one block), then reset.
        var random = new Random(7);
        var impulse = new float[Rate]; // 1 s
        for (int i = 0; i < impulse.Length; i++) impulse[i] = (float)(random.NextDouble() - 0.5);
        conv.SetImpulse(impulse);
        conv.IsEnabled = true;

        Run(conv, 2 * ConvolutionDspEffect.BlockSamples * Channels, TestTone);

        // After a reset, SILENCE in must be silence out — the pre-reset signal's reverberant
        // tail (held in the partition history and the prev-block accumulator) is what a seek
        // needs gone.
        conv.Reset();
        var after = Run(conv, 4 * ConvolutionDspEffect.BlockSamples * Channels, _ => 0f);
        float peak = 0;
        foreach (float v in after) peak = MathF.Max(peak, MathF.Abs(v));
        Assert.True(peak < 1e-3f, $"tail survived reset: peak {peak}");
    }

    [Fact]
    public void Initialize_DropsImpulse_AtNewFormat()
    {
        var conv = new ConvolutionDspEffect();
        conv.Initialize(Rate, Channels);
        conv.SetImpulse(new float[] { 1f });
        Assert.True(conv.HasImpulse);

        conv.Initialize(48000, 2);
        Assert.False(conv.HasImpulse, "IR prepared for the old rate must not survive a format change");
    }

    // ---------------- helpers ----------------

    /// <summary>Feeds an input generator through the effect in typical render-block sizes.</summary>
    private static float[] Run(ConvolutionDspEffect conv, int totalSamples, Func<int, float> input)
    {
        var output = new float[totalSamples];
        var buffer = new float[512 * Channels];
        int done = 0;
        while (done < totalSamples)
        {
            int count = Math.Min(buffer.Length, totalSamples - done);
            for (int i = 0; i < count; i += Channels)
            {
                float s = input((done + i) / Channels);
                buffer[i] = s;
                buffer[i + 1] = s;
            }
            conv.Process(buffer, 0, count);
            Array.Copy(buffer, 0, output, done, count);
            done += count;
        }
        return output;
    }

    private static float TestTone(int frame) =>
        MathF.Sin(2 * MathF.PI * 1000 * frame / (float)Rate);
}
