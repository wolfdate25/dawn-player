using DawnPlayer.Core.Audio.Dsp;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// Crossfeed (blends lowpassed opposite-channel content; in-phase worst case cannot clip) and
/// mono downmix (folds every channel to the average).
/// </summary>
public sealed class SpatialDspEffectTests
{
    [Fact]
    public void Crossfeed_Disabled_PreservesSamplesExactly()
    {
        var input = new float[] { 0.5f, -0.25f, 0.1f, 0.9f };
        var effect = new CrossfeedDspEffect(new CrossfeedSettings { Enabled = true });
        effect.Initialize(44100, 2);
        effect.IsEnabled = false;

        var buffer = (float[])input.Clone();
        effect.Process(buffer, 0, buffer.Length);

        Assert.Equal(input, buffer);
    }

    [Fact]
    public void Crossfeed_NullsEnabled_InPhaseNeverClips()
    {
        var effect = new CrossfeedDspEffect(new CrossfeedSettings { Enabled = true, Strength = CrossfeedStrength.High });
        effect.Initialize(44100, 2);

        // In-phase full-scale on both channels is the clipping worst case: LP converges to the
        // same value, so y = (1 + feed * 1) / (1 + feed) = 1 exactly.
        var buffer = new float[4410 * 2];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = 1.0f;
        effect.Process(buffer, 0, buffer.Length);

        foreach (var sample in buffer)
        {
            Assert.True(sample <= 1.0f + 1e-5f, $"clipped: {sample}");
        }
    }

    [Fact]
    public void Crossfeed_BleedsOppositeChannel_ForLowFrequencies()
    {
        var effect = new CrossfeedDspEffect(new CrossfeedSettings { Enabled = true, Strength = CrossfeedStrength.Normal });
        effect.Initialize(48000, 2);

        // Left silent, right a low 100 Hz tone for two seconds: the LP state converges to the
        // tone's moving average, so the left channel must eventually carry a sizable fraction.
        int frames = 48000 * 2;
        var buffer = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            buffer[f * 2] = 0f;
            buffer[f * 2 + 1] = (float)(0.9 * Math.Sin(2.0 * Math.PI * 100 * f / 48000));
        }

        effect.Process(buffer, 0, buffer.Length);

        double leftEnergy = 0, rightEnergy = 0;
        int tail = frames - frames / 4;
        for (int f = tail; f < frames; f++)
        {
            leftEnergy += buffer[f * 2] * buffer[f * 2];
            rightEnergy += buffer[f * 2 + 1] * buffer[f * 2 + 1];
        }
        Assert.True(leftEnergy > rightEnergy * 0.05, $"left={leftEnergy} right={rightEnergy}");
    }

    [Fact]
    public void Crossfeed_ResetsToPreSwitchState()
    {
        var effect = new CrossfeedDspEffect(new CrossfeedSettings { Enabled = true });
        effect.Initialize(44100, 2);

        var loud = new float[4410 * 2];
        Array.Fill(loud, 0.8f);
        effect.Process(loud, 0, loud.Length);

        effect.Reset();
        var probe = new float[] { 0.8f, 0f };
        effect.Process(probe, 0, 2);

        // Fresh LP memory: left stays (almost) purely the right-channel leak of one sample.
        Assert.True(Math.Abs(probe[0] - 0.8f / 1.45f) < 0.01, $"left={probe[0]}");
    }

    [Fact]
    public void Crossfeed_PassesThroughNonStereo()
    {
        var effect = new CrossfeedDspEffect(new CrossfeedSettings { Enabled = true });
        effect.Initialize(44100, 1);

        var buffer = new float[] { 0.5f, -0.25f };
        var copy = (float[])buffer.Clone();
        effect.Process(buffer, 0, buffer.Length);
        Assert.Equal(copy, buffer);
    }

    [Fact]
    public void MonoDownmix_AveragesAllChannels()
    {
        var effect = new MonoDownmixDspEffect(true);
        effect.Initialize(48000, 4);

        var buffer = new float[] { 1.0f, 0.5f, 0.0f, -0.5f };
        effect.Process(buffer, 0, buffer.Length);

        Assert.Equal(new float[] { 0.25f, 0.25f, 0.25f, 0.25f }, buffer);
    }

    [Fact]
    public void MonoDownmix_Disabled_PreservesSamplesExactly()
    {
        var effect = new MonoDownmixDspEffect(false);
        effect.Initialize(48000, 2);

        var buffer = new float[] { 1.0f, -1.0f, 0.3f, 0.2f };
        var copy = (float[])buffer.Clone();
        effect.Process(buffer, 0, buffer.Length);
        Assert.Equal(copy, buffer);
    }
}
