using System;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Audio.Dsp;
using DawnPlayer.Core.Persistence;
using NAudio.Wave;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// The exclusive-mode rate-mismatch policy matrix, pinned as pure decisions: which combinations
/// of policy, negotiated format and running session format restart the session (audible gap,
/// bit-perfect) or keep it running (seamless, resampled). These used to be inline conditions in
/// the prefetch and hot-swap paths; the tests are why they are pure functions now.
/// </summary>
public sealed class ExclusiveRateMismatchPolicyTests
{
    private static WaveFormat Fmt(int rate, int bits = 24, int channels = 2) =>
        new WaveFormat(rate, bits, channels);

    [Fact]
    public void RestartSessionPolicy_Restarts_OnRateMismatch()
    {
        var session = Fmt(44100);
        var negotiated = Fmt(96000);
        Assert.True(PlaybackController.ExclusiveSessionRestartNeeded(
            ExclusiveRateMismatchPolicy.RestartSession, negotiated, session));
    }

    [Fact]
    public void RestartSessionPolicy_Restarts_WhenNegotiationFails()
    {
        Assert.True(PlaybackController.ExclusiveSessionRestartNeeded(
            ExclusiveRateMismatchPolicy.RestartSession, null, Fmt(44100)));
    }

    [Fact]
    public void RestartSessionPolicy_Continues_OnFormatMatch()
    {
        var fmt = Fmt(44100);
        Assert.False(PlaybackController.ExclusiveSessionRestartNeeded(
            ExclusiveRateMismatchPolicy.RestartSession, fmt, fmt));
    }

    [Fact]
    public void ResamplePolicy_NeverRestarts_EvenOnRateMismatchOrFailedNegotiation()
    {
        var session = Fmt(44100);
        Assert.False(PlaybackController.ExclusiveSessionRestartNeeded(
            ExclusiveRateMismatchPolicy.ResampleToCurrent, Fmt(96000), session));
        Assert.False(PlaybackController.ExclusiveSessionRestartNeeded(
            ExclusiveRateMismatchPolicy.ResampleToCurrent, null, session));
    }

    [Fact]
    public void ResamplePolicy_AcceptsAnyTrack_ForHotSwap()
    {
        var session = Fmt(44100);
        Assert.True(PlaybackController.ExclusiveSessionAcceptsTrack(
            ExclusiveRateMismatchPolicy.ResampleToCurrent, null, session));
        Assert.True(PlaybackController.ExclusiveSessionAcceptsTrack(
            ExclusiveRateMismatchPolicy.ResampleToCurrent, Fmt(192000, bits: 16, channels: 6), session));
    }

    [Fact]
    public void RestartSessionPolicy_HotSwap_AcceptsOnlyExactFormatMatch()
    {
        var session = Fmt(44100);
        Assert.True(PlaybackController.ExclusiveSessionAcceptsTrack(
            ExclusiveRateMismatchPolicy.RestartSession, Fmt(44100), session));
        Assert.False(PlaybackController.ExclusiveSessionAcceptsTrack(
            ExclusiveRateMismatchPolicy.RestartSession, Fmt(48000), session));
        Assert.False(PlaybackController.ExclusiveSessionAcceptsTrack(
            ExclusiveRateMismatchPolicy.RestartSession, null, session));
    }

    [Fact]
    public void DefaultSettings_KeepTheBitPerfectBehavior()
    {
        // The historical behavior must remain the default: existing installs must not silently
        // switch to resampling.
        Assert.Equal(ExclusiveRateMismatchPolicy.RestartSession, new AppSettings().Output.ExclusiveRateMismatch);
    }
}

/// <summary>
/// ResetForSeek vs Reset. A seek (or A-B loop bounce) invalidates delay-line, convolution and
/// limiter state but NOT the loudness normalizer's converged gain — that gain describes the
/// track, so resetting it pumped the volume after every scrub. The normalizer's gain is observed
/// behaviorally: after convergence, the multiplier applied to a fresh loud block must be
/// preserved by ResetForSeek and returned to unity by Reset.
/// </summary>
public sealed class DspChainSeekResetTests
{
    private sealed class RecordingEffect : IAudioDspEffect
    {
        public int Resets;
        public string Name => "recording";
        public bool IsEnabled { get; set; } = true;
        public void Initialize(int sampleRate, int channels) { }
        public void Process(float[] buffer, int offset, int count) { }
        public void Reset() => Resets++;
    }

    private static (AudioDspChain Chain, DynamicNormalizerDspEffect Normalizer, RecordingEffect Recording)
        NewChain(int sampleRate = 48000, int channels = 2)
    {
        var chain = new AudioDspChain();
        var normalizer = new DynamicNormalizerDspEffect();
        normalizer.Initialize(sampleRate, channels);
        normalizer.ApplySettings(new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.AlwaysDynamic,
            Speed = NormalizerSpeed.Fast,
        });
        var recording = new RecordingEffect();
        chain.AddEffect(normalizer);
        chain.AddEffect(recording);
        return (chain, normalizer, recording);
    }

    private static float GainEnteringLoudBlock(DynamicNormalizerDspEffect normalizer, float[] buffer)
    {
        // Feed one loud block and report the multiplier applied to its FIRST sample pair: that
        // ratio is the gain the normalizer carried INTO the block, i.e. the state the chain was
        // left in by whatever reset (or non-reset) preceded it. Later samples adapt within the
        // block and say nothing about the prior state.
        for (int i = 0; i < buffer.Length; i += 2)
        {
            buffer[i] = 0.9f;
            buffer[i + 1] = 0.9f;
        }
        var input = (float[])buffer.Clone();
        normalizer.Process(buffer, 0, buffer.Length);
        return input[0] == 0 ? 1f : buffer[0] / input[0];
    }

    [Fact]
    public void ResetForSeek_PreservesConvergedNormalizerGain_AndResetsOtherEffects()
    {
        var (chain, normalizer, recording) = NewChain();
        var buffer = new float[4800];

        // Converge the normalizer on loud material (private state; ~a second of Fast reaction).
        for (int i = 0; i < 20; i++)
        {
            var block = new float[4800];
            for (int s = 0; s < block.Length; s += 2)
            {
                block[s] = 0.9f;
                block[s + 1] = 0.9f;
            }
            normalizer.Process(block, 0, block.Length);
        }
        float converged = GainEnteringLoudBlock(normalizer, buffer);
        Assert.True(converged < 0.98f, $"normalizer should have engaged, entering gain was {converged}");
        int resetsBefore = recording.Resets;

        chain.ResetForSeek();

        Assert.Equal(resetsBefore + 1, recording.Resets);
        float afterSeek = GainEnteringLoudBlock(normalizer, buffer);
        Assert.True(Math.Abs(afterSeek - converged) < 0.05f,
            $"converged gain {converged} must survive a seek-reset, got {afterSeek}");
    }

    [Fact]
    public void FullReset_ReturnsNormalizerToUnity_AndResetsOtherEffects()
    {
        var (chain, normalizer, recording) = NewChain();
        var buffer = new float[4800];

        for (int i = 0; i < 20; i++)
        {
            var block = new float[4800];
            for (int s = 0; s < block.Length; s += 2)
            {
                block[s] = 0.9f;
                block[s + 1] = 0.9f;
            }
            normalizer.Process(block, 0, block.Length);
        }

        chain.Reset();

        Assert.Equal(1, recording.Resets);
        float afterReset = GainEnteringLoudBlock(normalizer, buffer);
        Assert.True(afterReset > 0.98f,
            $"full reset must return the gain to ~unity, got {afterReset}");
    }
}
