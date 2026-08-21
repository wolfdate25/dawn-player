using System;
using DawnPlayer.Core.Audio.Dsp;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// Behavioral tests for the live normalizer DSP effect: disabled fade-to-unity, state reset,
/// loud-signal attenuation, response speed, and the Hybrid / ReplayGainOnly tag priority modes.
/// Low-level boost, noise-floor gating, and live settings switching are covered by AudioDspChainTests.
/// </summary>
public sealed class DynamicNormalizerDspEffectTests
{
    private const int SampleRate = 44100;
    private const int Channels = 2;

    /// <summary>
    /// Fills an interleaved buffer with a sine at the given peak amplitude. The effect processes a
    /// float buffer in place, so tests own the buffer rather than pulling from a source provider.
    /// </summary>
    private static float[] CreateSine(int totalSamples, float amplitude, double frequencyHz = 1000.0, int channels = Channels, int sampleRate = SampleRate)
    {
        float[] buffer = new float[totalSamples];
        double phase = 0.0;
        double step = 2.0 * Math.PI * frequencyHz / sampleRate;

        for (int i = 0; i < totalSamples; i += channels)
        {
            float val = (float)(amplitude * Math.Sin(phase));
            phase += step;
            if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;

            for (int c = 0; c < channels && (i + c) < totalSamples; c++)
            {
                buffer[i + c] = val;
            }
        }
        return buffer;
    }

    private static DynamicNormalizerDspEffect CreateEffect(NormalizerSettings settings, int channels = Channels)
    {
        var norm = new DynamicNormalizerDspEffect(settings);
        norm.Initialize(SampleRate, channels);
        return norm;
    }

    #region 1. Disabled State & Reset

    [Fact]
    public void DisabledNormalizer_AfterFadeConverges_LeavesSignalAtUnityGain()
    {
        var norm = CreateEffect(new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.AlwaysDynamic,
            TargetLevelDb = -12.0,
            MaxBoostDb = 12.0,
            Speed = NormalizerSpeed.Fast
        });

        // Drive the gain away from unity first, so disabling has something to fade back from.
        var warmup = CreateSine(SampleRate, 0.05f);
        norm.Process(warmup, 0, warmup.Length);
        Assert.True(norm.CurrentGain > 1.1f, $"Expected warm-up gain above 1.1, but was {norm.CurrentGain}");

        norm.ApplySettings(new NormalizerSettings { Enabled = false });

        // Disabling glides gain back to unity over ~50ms instead of snapping, so only the
        // fade-converged tail of the buffer can be compared against the untouched input.
        var input = CreateSine(SampleRate * 2, 0.25f);
        var buffer = (float[])input.Clone();
        norm.Process(buffer, 0, buffer.Length);

        Assert.Equal(1.0f, norm.CurrentGain, 3);
        // The glide is asymptotic, so the converged gain is 1.0 to within a few ppm rather than
        // exactly 1.0. Compare on absolute error — rounding-based equality straddles a decimal
        // boundary here and fails on a difference of ~5e-6.
        for (int i = buffer.Length - 1000; i < buffer.Length; i++)
        {
            Assert.True(Math.Abs(input[i] - buffer[i]) < 1e-4f,
                $"Sample {i} drifted from unity gain: expected {input[i]}, got {buffer[i]}");
        }
    }

    [Fact]
    public void Reset_AfterGainRamp_RestoresGainToOneAndClearsAccumulator()
    {
        var settings = new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.AlwaysDynamic,
            TargetLevelDb = -12.0,
            MaxBoostDb = 12.0,
            Speed = NormalizerSpeed.Fast
        };
        var norm = CreateEffect(settings);

        var first = CreateSine(SampleRate, 0.01f);
        norm.Process(first, 0, first.Length);
        Assert.True(norm.CurrentGain > 1.0f);
        float gainAfterFirstPass = norm.CurrentGain;

        norm.Reset();
        Assert.Equal(1.0f, norm.CurrentGain);

        // A cleared RMS accumulator means an identical second pass retraces the same ramp;
        // a stale accumulator would skip the initial gated stretch and overshoot.
        var second = CreateSine(SampleRate, 0.01f);
        norm.Process(second, 0, second.Length);
        Assert.Equal(gainAfterFirstPass, norm.CurrentGain, 4);
    }

    #endregion

    #region 2. Real-Time Dynamic AGC Behavior

    [Fact]
    public void LoudSignal_AlwaysDynamicMode_AttenuatesGainTowardTargetLevel()
    {
        // Full scale sine wave (0 dBFS peak = 1.0, RMS ~ 0.707 = -3 dBFS)
        var norm = CreateEffect(new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.AlwaysDynamic,
            TargetLevelDb = -12.0, // Target RMS ~ 0.251
            MaxBoostDb = 12.0,
            Speed = NormalizerSpeed.Fast
        });

        var buffer = CreateSine(SampleRate * 2, 1.0f);
        norm.Process(buffer, 0, buffer.Length);

        // Target gain should be ~ 0.251 / 0.707 ~ 0.355 (-9 dB)
        Assert.True(norm.CurrentGain < 0.6f, $"Expected attenuation below 0.6, but gain was {norm.CurrentGain}");
        Assert.True(norm.CurrentGain > 0.2f, $"Expected gain above 0.2, but was {norm.CurrentGain}");
    }

    [Fact]
    public void ResponseSpeed_ShortSlice_FastAdaptsFasterThanSmooth()
    {
        float amp = (float)Math.Pow(10.0, -20.0 / 20.0);

        var fastNorm = CreateEffect(new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.AlwaysDynamic,
            TargetLevelDb = -12.0,
            Speed = NormalizerSpeed.Fast
        });

        var smoothNorm = CreateEffect(new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.AlwaysDynamic,
            TargetLevelDb = -12.0,
            Speed = NormalizerSpeed.Smooth
        });

        // 100ms slice at 44.1kHz stereo
        fastNorm.Process(CreateSine(8820, amp), 0, 8820);
        smoothNorm.Process(CreateSine(8820, amp), 0, 8820);

        // Fast should have advanced its gain further towards target than smooth
        Assert.True(
            fastNorm.CurrentGain > smoothNorm.CurrentGain,
            $"Fast gain {fastNorm.CurrentGain} should exceed smooth gain {smoothNorm.CurrentGain}");
    }

    #endregion

    #region 3. Hybrid & ReplayGain Priority Modes

    [Fact]
    public void HybridMode_WithReplayGainTag_UsesStaticReplayGain()
    {
        var norm = CreateEffect(new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.Hybrid,
            TargetLevelDb = -12.0
        });

        // Static ReplayGain tag of +3.0 dB (~1.4125x)
        float expectedLinear = (float)Math.Pow(10.0, 3.0 / 20.0);
        norm.SetReplayGain(expectedLinear);

        var buffer = CreateSine(SampleRate * 2, 0.5f);
        norm.Process(buffer, 0, buffer.Length);

        Assert.Equal(expectedLinear, norm.CurrentGain, 2);
    }

    [Fact]
    public void HybridMode_WithoutReplayGainTag_FallsBackToDynamicAgc()
    {
        float quietAmp = (float)Math.Pow(10.0, -26.0 / 20.0);
        var norm = CreateEffect(new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.Hybrid,
            TargetLevelDb = -12.0,
            MaxBoostDb = 12.0,
            Speed = NormalizerSpeed.Fast
        });

        norm.SetReplayGain(null);

        var buffer = CreateSine(SampleRate * 2, quietAmp);
        norm.Process(buffer, 0, buffer.Length);

        // Real-time AGC should boost quiet audio
        Assert.True(norm.CurrentGain > 2.0f, $"Expected AGC boost above 2.0, but gain was {norm.CurrentGain}");
    }

    [Fact]
    public void ReplayGainOnlyMode_WithoutTag_PassesThroughUnityGain()
    {
        var norm = CreateEffect(new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.ReplayGainOnly
        });

        norm.SetReplayGain(null);

        var input = CreateSine(SampleRate, 0.4f);
        var buffer = (float[])input.Clone();
        norm.Process(buffer, 0, buffer.Length);

        Assert.Equal(1.0f, norm.CurrentGain, 2);
        for (int i = 0; i < buffer.Length; i++)
        {
            Assert.Equal(input[i], buffer[i], 4);
        }
    }

    #endregion
}
