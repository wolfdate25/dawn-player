using System;
using System.Runtime.InteropServices;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Audio.Dsp;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using NAudio.Wave;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// Gate tests for the master-fader split: the pre-chain volume node carries ReplayGain source
/// correction only (boost-capable, limiter-protected), while the master fader is a plain
/// attenuating multiply applied after the DSP chain. Invariants: (1) with the normalizer engaged,
/// slider attenuation is never compensated away — final RMS = target + 20·log10(slider);
/// (2) with nothing that can raise the level, the limiter stays disarmed and the fader scales
/// samples exactly; (3) a boosting ReplayGain node stays under the soft limiter; (4) toggling the
/// normalizer hands ReplayGain from the node to the effect without a level step.
/// </summary>
public sealed class MasterFaderSplitTests
{
    private const int SampleRate = 44100;
    private const double Step = 2.0 * Math.PI * 1000.0 / SampleRate;
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

    private static PlaylistItem TrackAt(string path) => new(new Track { Path = path });

    /// <summary>Expected sample for global frame index n (matches SineSamples' continuous phase).</summary>
    private static double SineAt(int n) => Math.Sin(Math.PI * 0.25 + Step * n);

    private static byte[] ReadSeconds(SequencerStream seq, int warmupSeconds, int measureSeconds)
    {
        var block = new byte[Format.AverageBytesPerSecond / 2];
        for (int i = 0; i < warmupSeconds * 2; i++) seq.Read(block);

        var tail = new byte[Format.AverageBytesPerSecond * measureSeconds];
        int offset = 0;
        for (int i = 0; i < measureSeconds * 2; i++)
        {
            int read = seq.Read(block);
            offset += read;
            if (offset > tail.Length) throw new InvalidOperationException("reader produced more than requested");
            Array.Copy(block, 0, tail, offset - read, read);
        }
        return tail;
    }

    private static double RmsDb(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        for (int i = 0; i < samples.Length; i++) sum += (double)samples[i] * samples[i];
        return 20.0 * Math.Log10(Math.Sqrt(sum / samples.Length));
    }

    private static (SequencerStream Seq, SoftLimiterDspEffect Limiter, DynamicNormalizerDspEffect Normalizer) NewSequencer(
        NormalizerSettings normalizerSettings, Func<Track, float> nodeGainProvider)
    {
        var chain = new AudioDspChain();
        var normalizer = new DynamicNormalizerDspEffect(normalizerSettings);
        var limiter = new SoftLimiterDspEffect(0.90f);
        chain.AddEffect(normalizer);
        chain.AddEffect(limiter);

        var seq = new SequencerStream(
            Format,
            applyVolume: true,
            replayGainNodeGainProvider: nodeGainProvider,
            latencyMs: 50,
            dspChain: chain);
        return (seq, limiter, normalizer);
    }

    private static SequencerStream Play(SequencerStream seq, float sinePeak)
    {
        seq.SwitchTo(new PendingTrack
        {
            Playlist = new Playlist("MasterFaderSplit"),
            Item = TrackAt(@"C:\m\sine.wav"),
            Reader = new SineReader(sinePeak),
        });
        return seq;
    }

    [Fact]
    public void NormalizerOn_SliderAttenuation_IsNotCompensatedAway()
    {
        // Sine at RMS -16 dBFS meets the -16 dBFS target exactly, so the AGC's converged gain is
        // unity; a slider at 0.5 (-6 dB) must survive as -6 dB of output. When the AGC sat after
        // the volume node it pulled every such move back to -16 within its release constant.
        var (seq, _, _) = NewSequencer(
            new NormalizerSettings { Enabled = true, Mode = NormalizerMode.AlwaysDynamic, TargetLevelDb = -16.0 },
            _ => 1f);
        Play(seq, sinePeak: 0.22418f);

        seq.SetMasterGain(0.5f); // master slider at -6 dB

        var tail = MemoryMarshal.Cast<byte, float>(ReadSeconds(seq, warmupSeconds: 2, measureSeconds: 1).AsSpan());
        double rms = RmsDb(tail);
        Assert.True(Math.Abs(rms - (-22.0)) < 0.75,
            $"Slider 0.5 must land at -22 dBFS, measured {rms:F2} dBFS (AGC compensated the slider away)");
    }

    [Fact]
    public void PurePassthrough_LimiterStaysDisarmed_AndFaderScalesExactly()
    {
        // Normalizer off, no ReplayGain boost, no EQ: nothing can raise the level, so the limiter
        // must not arm (a memoryless shaper left armed reshapes peaks in a configuration the UI
        // calls untouched), and the 0.8 fader must scale every sample exactly.
        var (seq, limiter, _) = NewSequencer(new NormalizerSettings { Enabled = false }, _ => 1f);
        Play(seq, sinePeak: 0.5f);

        seq.SetMasterGain(0.8f);

        var tail = MemoryMarshal.Cast<byte, float>(ReadSeconds(seq, warmupSeconds: 0, measureSeconds: 1).AsSpan());
        Assert.False(limiter.IsEnabled, "Limiter must stay disarmed when nothing upstream can raise the level.");
        for (int i = 0; i < tail.Length; i++)
        {
            float expected = (float)(0.5f * SineAt(i / 2) * 0.8);
            Assert.True(Math.Abs(tail[i] - expected) < 1e-3f,
                $"Sample {i}: expected fader-exact {expected:F4}, got {tail[i]:F4}");
        }
    }

    [Fact]
    public void ReplayGainNodeBoost_StaysUnderTheSoftLimiter()
    {
        // Normalizer off, node carries a +6 dB tag gain, loud sine: the boost rides the pre-chain
        // node and must land exactly on the soft limiter's knee curve — engaged (not bypassed),
        // bounded below full scale, and not double-applied (which would read higher).
        var (seq, limiter, _) = NewSequencer(new NormalizerSettings { Enabled = false }, _ => 2f);
        Play(seq, sinePeak: 0.85f);

        var tail = MemoryMarshal.Cast<byte, float>(ReadSeconds(seq, warmupSeconds: 0, measureSeconds: 1).AsSpan());
        Assert.True(limiter.IsEnabled, "A boosting ReplayGain node must arm the limiter.");
        float expectedPeak = SoftLimiterDspEffect.Limit(0.85f * 2f);
        float max = MaxMagnitude(tail);
        Assert.True(Math.Abs(max - expectedPeak) < 0.005f,
            $"Boosted peaks measured {max:F4}, expected the soft limiter's {expectedPeak:F4} (unguarded: 1.70, lost node: 0.85).");
    }

    [Fact]
    public void NormalizerToggle_HandsReplayGainFromNodeToEffect_WithoutLevelStep()
    {
        // Node applies the +6 dB tag while the normalizer is off; enabling it hands the tag to the
        // normalizer's static path and drops the node to unity. Output level must not step, and
        // the limiter must stay armed throughout (the normalizer can still raise the level).
        var (seq, limiter, _) = NewSequencer(new NormalizerSettings { Enabled = false }, _ => 2f);
        Play(seq, sinePeak: 0.22418f); // RMS -16 dBFS

        var before = RmsDb(MemoryMarshal.Cast<byte, float>(ReadSeconds(seq, warmupSeconds: 1, measureSeconds: 1).AsSpan()));

        seq.SetNormalizer(
            new NormalizerSettings { Enabled = true, Mode = NormalizerMode.Hybrid, TargetLevelDb = -16.0 },
            staticReplayGainLinear: 2f);
        seq.SetReplayGainNode(1f);

        var after = RmsDb(MemoryMarshal.Cast<byte, float>(ReadSeconds(seq, warmupSeconds: 1, measureSeconds: 1).AsSpan()));
        Assert.True(Math.Abs(before - (-10.0)) < 0.75,
            $"Node-applied ReplayGain should land at -10 dBFS, measured {before:F2}");
        Assert.True(Math.Abs(after - before) < 0.75,
            $"ReplayGain handoff must not step the level: {before:F2} dBFS -> {after:F2} dBFS (double-apply or loss?)");
        Assert.True(limiter.IsEnabled, "Normalizer ownership keeps the limiter armed.");
    }

    [Fact]
    public void MasterFader_AppliesInstantly_AndNeverArmsTheLimiter()
    {
        var (seq, limiter, _) = NewSequencer(new NormalizerSettings { Enabled = false }, _ => 1f);
        Play(seq, sinePeak: 0.5f);

        seq.SetMasterGain(0.25f);
        float[] block1 = ReadOneBlock(seq);
        Assert.Equal(0.25f, SampleRatio(block1), precision: 2);

        seq.SetMasterGain(0.5f);
        float[] block2 = ReadOneBlock(seq);
        Assert.Equal(0.5f, SampleRatio(block2), precision: 2);

        Assert.False(limiter.IsEnabled, "The fader alone must never arm the limiter.");
    }

    /// <summary>Mean of output/sample-input ratios — the fader value when the block is the fader
    /// scaled sine. Uses frame indexing (both channels of frame n carry <see cref="SineAt"/>(n)).</summary>
    private static float SampleRatio(float[] block)
    {
        double sum = 0;
        for (int i = 0; i < block.Length; i++) sum += block[i] / (0.5f * (float)SineAt(i / 2));
        return (float)(sum / block.Length);
    }

    private static float MaxMagnitude(ReadOnlySpan<float> samples)
    {
        float max = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float mag = Math.Abs(samples[i]);
            if (mag > max) max = mag;
        }
        return max;
    }

    private static float[] ReadOneBlock(SequencerStream seq)
    {
        var bytes = new byte[Format.AverageBytesPerSecond / 2];
        int read = seq.Read(bytes);
        var floats = new float[read / 4];
        MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, read)).CopyTo(floats);
        return floats;
    }

    /// <summary>Continuous-phase sine source: sample n equals <see cref="SineAt"/>(n) for the
    /// global frame index n regardless of how the sequencer chunks its reads.</summary>
    private sealed class SineReader : ITrackReader
    {
        private sealed class SineSamples : ISampleProvider
        {
            private readonly float _peak;
            private double _frame;

            public SineSamples(float peak) => _peak = peak;

            public WaveFormat WaveFormat { get; } = Format;

            public int Read(Span<float> buffer)
            {
                for (int i = 0; i < buffer.Length; i += 2)
                {
                    float val = (float)(_peak * SineAt((int)_frame));
                    buffer[i] = val;
                    buffer[i + 1] = val;
                    _frame += 1;
                }
                return buffer.Length;
            }
        }

        public SineReader(float peak) => Samples = new SineSamples(peak);

        public ISampleProvider Samples { get; }

        public WaveFormat SourceFormat { get; } = Format;
        public TimeSpan TotalTime => TimeSpan.FromSeconds(30);
        public TimeSpan CurrentTime { get; set; }
        public string Path => @"C:\m\sine.wav";

        public void Dispose() { }
    }
}
