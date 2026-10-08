using System;
using DawnPlayer.Core.Audio.Dsp;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// Gate and adversarial tests for the normalizer's approved defaults and peak-aware gain ceiling.
/// Invariants: (1) AGC time constants hold in wall-clock time regardless of channel count;
/// (2) once primed, a sustained high-crest signal's output stays under the downstream soft
/// limiter's 0.90 knee; (3) the peak envelope decays exponentially so boost recovers after a
/// transient; (4) fresh settings carry the approved defaults (off, Hybrid, -16 dBFS, +12 dB,
/// Balanced) shared by the persistence layer and the DSP snapshot fallback.
/// </summary>
public sealed class NormalizerDefaultsAndPeakCapTests
{
    private const int SampleRate = 44100;

    private static float[] CreateSine(int totalSamples, float amplitude, int channels, double frequencyHz = 1000.0)
    {
        float[] buffer = new float[totalSamples];
        double phase = 0.0;
        double step = 2.0 * Math.PI * frequencyHz / SampleRate;

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

    /// <summary>Sine with a 50% duty cycle (period 1000 samples): sustained high crest factor —
    /// full-scale-ish peaks at ~-9.9 dBFS RMS, the shape that tempts the AGC into boosting peaks
    /// into the limiter.</summary>
    private static float[] CreateGatedSine(int totalSamples, float amplitude, int channels, double frequencyHz = 1000.0)
    {
        float[] buffer = new float[totalSamples];
        double phase = 0.0;
        double step = 2.0 * Math.PI * frequencyHz / SampleRate;

        for (int i = 0; i < totalSamples; i += channels)
        {
            float val = (i / channels) % 1000 < 500 ? (float)(amplitude * Math.Sin(phase)) : 0f;
            phase += step;
            if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;

            for (int c = 0; c < channels && (i + c) < totalSamples; c++)
            {
                buffer[i + c] = val;
            }
        }
        return buffer;
    }

    private static DynamicNormalizerDspEffect CreateEffect(NormalizerSettings settings, int channels)
    {
        var norm = new DynamicNormalizerDspEffect(settings);
        norm.Initialize(SampleRate, channels);
        return norm;
    }

    /// <summary>Processes a buffer in 100 ms chunks, mimicking the sequencer's block pumping.</summary>
    private static void Pump(DynamicNormalizerDspEffect norm, float[] buffer, int channels)
    {
        int chunk = SampleRate / 10 * channels;
        for (int offset = 0; offset < buffer.Length; offset += chunk)
        {
            int count = Math.Min(chunk, buffer.Length - offset);
            norm.Process(buffer, offset, count);
        }
    }

    private static double RmsDb(float[] buffer, int channels, int startSample, int sampleCount)
    {
        double sum = 0;
        int frames = 0;
        for (int i = startSample; i < startSample + sampleCount; i += channels)
        {
            for (int c = 0; c < channels; c++)
            {
                double s = buffer[i + c];
                sum += s * s;
            }
            frames++;
        }
        double rms = Math.Sqrt(sum / (frames * channels));
        return 20.0 * Math.Log10(rms);
    }

    [Fact]
    public void FreshSettings_CarryTheApprovedDefaults()
    {
        var fresh = new NormalizerSettings();
        Assert.False(fresh.Enabled);
        Assert.Equal(NormalizerMode.Hybrid, fresh.Mode);
        Assert.Equal(-16.0, NormalizerSettings.DefaultTargetLevelDb);
        Assert.Equal(-16.0, fresh.TargetLevelDb);
        Assert.Equal(12.0, NormalizerSettings.DefaultMaxBoostDb);
        Assert.Equal(12.0, fresh.MaxBoostDb);
        Assert.Equal(NormalizerSpeed.Balanced, fresh.Speed);

        var app = new AppSettings();
        Assert.False(app.Normalizer.Enabled);
        Assert.Equal(NormalizerMode.Hybrid, app.Normalizer.Mode);
        Assert.Equal(-16.0, app.Normalizer.TargetLevelDb);
        Assert.Equal(12.0, app.Normalizer.MaxBoostDb);
        Assert.Equal(NormalizerSpeed.Balanced, app.Normalizer.Speed);
    }

    [Fact]
    public void DefaultTargetLevel_QuietSine_ConvergesBoostToMinus16DbfsOutput()
    {
        // RMS -23 dBFS sine vs the -16 dBFS default target -> +7 dB of gain (2.2387x).
        var settings = new NormalizerSettings { Enabled = true, Mode = NormalizerMode.AlwaysDynamic };
        var norm = CreateEffect(settings, channels: 2);

        float amp = (float)Math.Pow(10.0, -20.0 / 20.0); // peak -20 dBFS -> RMS -23 dBFS
        float[] buffer = CreateSine(SampleRate * 3, amp, channels: 2);
        Pump(norm, buffer, 2);

        double expectedGain = Math.Pow(10.0, 7.0 / 20.0);
        Assert.True(Math.Abs(norm.CurrentGain - expectedGain) < 0.06,
            $"Expected converged gain ~{expectedGain:F3} (+7 dB toward the -16 default), got {norm.CurrentGain:F4}");
        double outDb = RmsDb(buffer, 2, buffer.Length - SampleRate, SampleRate);
        Assert.True(Math.Abs(outDb - (-16.0)) < 0.3,
            $"Reported gain must match actual output level: output RMS {outDb:F2} dBFS, expected -16 dBFS");
    }

    [Fact]
    public void SpeedTimeConstants_AreWallClock_MonoAndStereoConvergeAtOneTau()
    {
        // RMS -21 dBFS sine, target -10 -> +11 dB desired (3.548x). After one release time
        // constant (Balanced = 600 ms) the one-pole must be at 1 + (3.548-1)*(1-e^-1) = 2.611 —
        // for mono AND stereo alike. Per-sample coefficients applied per frame made stereo run at
        // half speed (1.66), which this pins shut.
        var settings = new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.AlwaysDynamic,
            TargetLevelDb = -10.0,
        };

        float amp = (float)Math.Pow(10.0, -18.0 / 20.0); // peak -18 dBFS -> RMS -21 dBFS
        double expected = 1.0 + (Math.Pow(10.0, 11.0 / 20.0) - 1.0) * (1.0 - Math.Exp(-1.0));

        var mono = CreateEffect(settings, channels: 1);
        var stereo = CreateEffect(settings, channels: 2);
        Pump(mono, CreateSine(SampleRate * 3 / 5, amp, channels: 1), 1);
        Pump(stereo, CreateSine(SampleRate * 3 / 5, amp, channels: 2), 2);

        Assert.True(Math.Abs(mono.CurrentGain - expected) < 0.10,
            $"Mono one-tau gain {mono.CurrentGain:F4} should be ~{expected:F3}");
        Assert.True(Math.Abs(stereo.CurrentGain - expected) < 0.10,
            $"Stereo one-tau gain {stereo.CurrentGain:F4} should be ~{expected:F3} (channel count must not scale the time constant)");
        Assert.True(Math.Abs(mono.CurrentGain - stereo.CurrentGain) < 0.02,
            $"Mono {mono.CurrentGain:F4} and stereo {stereo.CurrentGain:F4} trajectories diverge");
    }

    [Fact]
    public void PeakEnvelope_CapsSustainedHighCrestBoost_UnderLimiterKnee()
    {
        // Gated sine: peaks ~0.9, RMS -9.9 dBFS. Target -6 wants +3.9 dB (1.575x) which would
        // push peaks to ~1.42; the peak envelope must hold gain at 0.89/0.9 ~= 0.99 so the output
        // never reaches the soft limiter's 0.90 knee.
        var settings = new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.AlwaysDynamic,
            TargetLevelDb = -6.0,
            MaxBoostDb = 18.0,
        };

        var mono = CreateEffect(settings, channels: 1);
        var stereo = CreateEffect(settings, channels: 2);
        float[] monoBuf = CreateGatedSine(SampleRate * 2, 0.9f, channels: 1);
        float[] stereoBuf = CreateGatedSine(SampleRate * 2, 0.9f, channels: 2);
        Pump(mono, monoBuf, 1);
        Pump(stereo, stereoBuf, 2);

        Assert.True(stereo.CurrentGain < 1.2f,
            $"Peak cap inactive: gain {stereo.CurrentGain:F4} ran toward the RMS target 1.575");
        float maxOut = 0f;
        for (int i = stereoBuf.Length - SampleRate; i < stereoBuf.Length; i++)
        {
            float mag = Math.Abs(stereoBuf[i]);
            if (mag > maxOut) maxOut = mag;
        }
        Assert.True(maxOut < 0.92f,
            $"Output peak {maxOut:F4} reached the limiter knee; the AGC must shed boost before it");
        Assert.True(Math.Abs(mono.CurrentGain - stereo.CurrentGain) < 0.03f,
            $"Peak cap timing is channel-dependent: mono {mono.CurrentGain:F4} vs stereo {stereo.CurrentGain:F4}");
    }

    [Fact]
    public void PeakEnvelope_DecaysAfterTransientPasses_AndBoostRecovers()
    {
        var settings = new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.AlwaysDynamic,
            TargetLevelDb = -6.0,
            MaxBoostDb = 18.0,
        };
        var norm = CreateEffect(settings, channels: 2);

        // Phase A: sustained high-crest material holds the gain under the cap (~1.0).
        Pump(norm, CreateGatedSine(SampleRate / 2, 0.9f, channels: 2), 2);
        Assert.True(norm.CurrentGain < 1.2f, $"Cap should be engaged, gain was {norm.CurrentGain:F4}");

        // Phase B: the peaks go away (quiet sine, RMS -30 dBFS). The envelope must decay (+18 dB
        // desired, capped by MaxBoost to 7.943x); a stuck envelope would pin the gain near 1.0.
        float amp = (float)(Math.Pow(10.0, -30.0 / 20.0) * Math.Sqrt(2.0));
        Pump(norm, CreateSine(SampleRate * 5 / 2, amp, channels: 2), 2);

        Assert.True(norm.CurrentGain > 5.0f,
            $"Envelope failed to decay: gain {norm.CurrentGain:F4} after 2.5 s, expected recovery toward 7.94");
    }
}
