using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Audio.Dsp;
using DawnPlayer.Core.Persistence;
using NAudio.Wave;
using Xunit;

namespace DawnPlayer.Tests.Audio;

public sealed class AudioDspChainTests
{
    private sealed class MockDspEffect : IAudioDspEffect
    {
        public string Name { get; }
        public bool IsEnabled { get; set; } = true;
        public int InitializedSampleRate { get; private set; }
        public int InitializedChannels { get; private set; }
        public int ProcessCallCount { get; private set; }
        public int ResetCallCount { get; private set; }
        public float GainMultiplier { get; set; } = 1.0f;

        public MockDspEffect(string name, float gain = 1.0f)
        {
            Name = name;
            GainMultiplier = gain;
        }

        public void Initialize(int sampleRate, int channels)
        {
            InitializedSampleRate = sampleRate;
            InitializedChannels = channels;
        }

        public void Process(float[] buffer, int offset, int count)
        {
            ProcessCallCount++;
            if (!IsEnabled || GainMultiplier == 1.0f) return;

            for (int i = 0; i < count; i++)
            {
                buffer[offset + i] *= GainMultiplier;
            }
        }

        public void Reset()
        {
            ResetCallCount++;
        }
    }

    private static float[] GenerateSine(int sampleRate, int channels, double freqHz, double durationSec, float amplitude = 0.5f)
    {
        int totalFrames = (int)(sampleRate * durationSec);
        float[] samples = new float[totalFrames * channels];
        for (int f = 0; f < totalFrames; f++)
        {
            float s = (float)(amplitude * Math.Sin(2.0 * Math.PI * freqHz * f / sampleRate));
            for (int c = 0; c < channels; c++)
            {
                samples[f * channels + c] = s;
            }
        }
        return samples;
    }

    [Fact]
    public void AudioDspChain_Lifecycle_InitializesProcessesAndResetsEffects()
    {
        var chain = new AudioDspChain();
        var effect1 = new MockDspEffect("Gain1", 2.0f);
        var effect2 = new MockDspEffect("Gain2", 0.5f);

        chain.AddEffect(effect1);
        chain.AddEffect(effect2);

        chain.Initialize(48000, 2);
        Assert.Equal(48000, effect1.InitializedSampleRate);
        Assert.Equal(2, effect1.InitializedChannels);
        Assert.Equal(48000, effect2.InitializedSampleRate);
        Assert.Equal(2, effect2.InitializedChannels);

        var buffer = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        chain.Process(buffer, 0, buffer.Length);

        Assert.Equal(1, effect1.ProcessCallCount);
        Assert.Equal(1, effect2.ProcessCallCount);
        // (0.1 * 2.0) * 0.5 = 0.1
        Assert.Equal(0.1f, buffer[0], 5);
        Assert.Equal(0.2f, buffer[1], 5);

        chain.Reset();
        Assert.Equal(1, effect1.ResetCallCount);
        Assert.Equal(1, effect2.ResetCallCount);
    }

    [Fact]
    public void AudioDspChain_AddRemoveInsertClear_MaintainsOrderAndRetrieval()
    {
        var chain = new AudioDspChain();
        var eq = new EqualizerDspEffect();
        var norm = new DynamicNormalizerDspEffect();
        var limiter = new SoftLimiterDspEffect();

        chain.AddEffect(eq);
        chain.AddEffect(limiter);
        chain.InsertEffect(1, norm);

        Assert.Equal(3, chain.Effects.Count);
        Assert.Same(eq, chain.Effects[0]);
        Assert.Same(norm, chain.Effects[1]);
        Assert.Same(limiter, chain.Effects[2]);

        Assert.Same(eq, chain.GetEffect<EqualizerDspEffect>());
        Assert.Same(norm, chain.GetEffect<DynamicNormalizerDspEffect>());
        Assert.Same(limiter, chain.GetEffect<SoftLimiterDspEffect>());
        Assert.Null(chain.GetEffect<MockDspEffect>());

        chain.RemoveEffect("DynamicNormalizer");
        Assert.Equal(2, chain.Effects.Count);
        Assert.Null(chain.GetEffect<DynamicNormalizerDspEffect>());

        bool removed = chain.RemoveEffect(limiter);
        Assert.True(removed);
        Assert.Single(chain.Effects);

        chain.Clear();
        Assert.Empty(chain.Effects);
    }

    [Fact]
    public void AudioDspChain_Bypass_WhenDisabled_PassesSamplesUntouched()
    {
        var chain = new AudioDspChain();
        var effect = new MockDspEffect("Gain", 5.0f) { IsEnabled = false };
        chain.AddEffect(effect);

        var buffer = new float[] { 0.1f, 0.2f, 0.3f };
        chain.Process(buffer, 0, buffer.Length);

        Assert.Equal(0.1f, buffer[0]);
        Assert.Equal(0.2f, buffer[1]);
        Assert.Equal(0.3f, buffer[2]);
    }

    [Fact]
    public async Task AudioDspChain_ConcurrentExecutionAndMutation_ThreadSafe()
    {
        var chain = new AudioDspChain();
        chain.Initialize(44100, 2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = cts.Token;
        int activeErrors = 0;

        // Mutation tasks
        var mutationTask = Task.Run(() =>
        {
            int counter = 0;
            while (!token.IsCancellationRequested)
            {
                string name = $"Effect_{counter++ % 10}";
                var effect = new MockDspEffect(name, 1.001f);
                chain.AddEffect(effect);
                if (chain.Effects.Count > 8)
                {
                    chain.RemoveEffect(name);
                }
                if (counter % 5 == 0)
                {
                    chain.Reset();
                }
                Thread.Yield();
            }
        });

        // Audio processing worker tasks
        var renderTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var localBuf = new float[512];

            while (!token.IsCancellationRequested)
            {
                for (int i = 0; i < localBuf.Length; i++) localBuf[i] = 0.5f;

                try
                {
                    chain.Process(localBuf, 0, localBuf.Length);
                    for (int i = 0; i < localBuf.Length; i++)
                    {
                        if (float.IsNaN(localBuf[i]) || float.IsInfinity(localBuf[i]))
                        {
                            Interlocked.Increment(ref activeErrors);
                        }
                    }
                }
                catch
                {
                    Interlocked.Increment(ref activeErrors);
                }
            }
        })).ToArray();

        await Task.WhenAll([mutationTask, .. renderTasks]);
        Assert.Equal(0, activeErrors);
    }

    [Fact]
    public void AudioDspChain_ZeroAllocationInRenderLoop_SteadyState()
    {
        var chain = new AudioDspChain();
        chain.AddEffect(new EqualizerDspEffect());
        chain.AddEffect(new DynamicNormalizerDspEffect());
        chain.AddEffect(new SoftLimiterDspEffect());
        chain.AddEffect(new MockDspEffect("Passthrough"));
        chain.Initialize(44100, 2);

        var buffer = new float[1024];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = 0.25f;

        // Warm up
        for (int i = 0; i < 50; i++)
        {
            chain.Process(buffer, 0, buffer.Length);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long startAlloc = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            chain.Process(buffer, 0, buffer.Length);
        }
        long endAlloc = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, endAlloc - startAlloc);
    }

    [Fact]
    public void EqualizerDspEffect_PeakEq_ModifiesCenterFrequencyGain()
    {
        var eq = new EqualizerDspEffect();
        eq.Initialize(44100, 1);

        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = 0.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = 6.0, Q = 1.0 }
            }
        };
        eq.SetProfile(profile);

        var input = GenerateSine(44100, 1, 1000, 0.1, 0.2f);
        var buffer = (float[])input.Clone();

        eq.Process(buffer, 0, buffer.Length);

        // Signal at 1000Hz should have higher RMS after +6dB boost
        float inputRms = (float)Math.Sqrt(input.Select(s => s * s).Average());
        float outputRms = (float)Math.Sqrt(buffer.Skip(500).Select(s => s * s).Average());

        Assert.True(outputRms > inputRms * 1.5f, $"Expected boosted RMS > {inputRms * 1.5f}, but got {outputRms}");
    }

    [Fact]
    public void DynamicNormalizerDspEffect_BoostsLowLevelSignalTowardsTarget()
    {
        var norm = new DynamicNormalizerDspEffect(new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.AlwaysDynamic,
            TargetLevelDb = -12.0,
            MaxBoostDb = 12.0,
            Speed = NormalizerSpeed.Fast
        });
        norm.Initialize(44100, 2);

        // Low-level sine ~ -26dBFS (amp = 0.05)
        var input = GenerateSine(44100, 2, 440, 0.5, 0.05f);
        var buffer = (float[])input.Clone();

        norm.Process(buffer, 0, buffer.Length);

        Assert.True(norm.CurrentGain > 1.2f, $"Expected normalizer gain to rise above 1.2, but was {norm.CurrentGain}");
    }

    [Fact]
    public void DynamicNormalizerDspEffect_Gating_PreventsBoostingSilence()
    {
        var norm = new DynamicNormalizerDspEffect(new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.AlwaysDynamic,
            TargetLevelDb = -12.0,
            MaxBoostDb = 12.0
        });
        norm.Initialize(44100, 1);

        // Pure silence (0.0)
        var buffer = new float[44100];
        norm.Process(buffer, 0, buffer.Length);

        // In silence, gain should remain unity (1.0)
        Assert.InRange(norm.CurrentGain, 0.99f, 1.01f);
    }

    [Fact]
    public void SoftLimiterDspEffect_CompressesOverThresholdWithoutClipping()
    {
        var limiter = new SoftLimiterDspEffect(0.90f);
        limiter.Initialize(44100, 1);

        var buffer = new float[] { 0.5f, 0.90f, 1.5f, 2.0f, -1.5f, -2.0f };
        limiter.Process(buffer, 0, buffer.Length);

        // Sub-threshold values untouched
        Assert.Equal(0.5f, buffer[0]);
        Assert.Equal(0.90f, buffer[1]);

        // Over-threshold values strictly compressed below 1.0
        Assert.True(buffer[2] > 0.90f && buffer[2] < 1.0f, $"buffer[2] was {buffer[2]}");
        Assert.True(buffer[3] > 0.90f && buffer[3] < 1.0f, $"buffer[3] was {buffer[3]}");
        Assert.True(buffer[4] < -0.90f && buffer[4] > -1.0f, $"buffer[4] was {buffer[4]}");
        Assert.True(buffer[5] < -0.90f && buffer[5] > -1.0f, $"buffer[5] was {buffer[5]}");
    }

    [Fact]
    public void SequencerStream_CustomDspChain_ProcessesAudioThroughInjectedChain()
    {
        var customChain = new AudioDspChain();
        var customGain = new MockDspEffect("CustomGain", 0.5f);
        customChain.AddEffect(customGain);

        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var seq = new SequencerStream(
            format,
            applyVolume: false,
            gainProvider: _ => 1.0f,
            latencyMs: 50,
            dspChain: customChain);

        Assert.Same(customChain, seq.DspChain);
        Assert.Same(customGain, seq.DspChain.GetEffect<MockDspEffect>());
    }

    [Fact]
    public void EqualizerDspEffect_MultipleBandsAndPreamp_AccurateProcessing()
    {
        var eq = new EqualizerDspEffect();
        eq.Initialize(44100, 2);

        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = -3.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.LowShelf, FrequencyHz = 100, GainDb = 4.0, Q = 1.0 },
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = -6.0, Q = 2.0 },
                new EqBandSettings { Type = EqFilterType.HighShelf, FrequencyHz = 10000, GainDb = 3.0, Q = 1.0 }
            }
        };
        eq.SetProfile(profile);

        var buffer = GenerateSine(44100, 2, 1000, 0.1, 0.5f);
        eq.Process(buffer, 0, buffer.Length);

        // Verify finite non-zero outputs
        Assert.All(buffer, s => Assert.True(float.IsFinite(s)));
        Assert.Contains(buffer, s => Math.Abs(s) > 0.01f);
    }

    [Fact]
    public async Task DynamicNormalizerDspEffect_LiveSettingsSwitching_NoTornCoefficients()
    {
        var norm = new DynamicNormalizerDspEffect();
        norm.Initialize(44100, 2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var token = cts.Token;
        int activeErrors = 0;

        var mutator = Task.Run(() =>
        {
            int counter = 0;
            while (!token.IsCancellationRequested)
            {
                norm.ApplySettings(new NormalizerSettings
                {
                    Enabled = (counter % 2 == 0),
                    Mode = (NormalizerMode)(counter % 3),
                    TargetLevelDb = -18.0 + (counter % 12),
                    MaxBoostDb = 6.0 + (counter % 8),
                    Speed = (NormalizerSpeed)(counter % 3)
                });
                norm.SetReplayGain(0.5f + (counter % 5) * 0.1f);
                counter++;
                Thread.Yield();
            }
            // The token is the loop's stop signal only — it is deliberately not passed to
            // Task.Run, because a token that is already cancelled when the work item is
            // scheduled would leave the task Canceled and make Task.WhenAll throw.
        });

        var processor = Task.Run(() =>
        {
            var buffer = GenerateSine(44100, 2, 440, 0.05, 0.2f);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    norm.Process(buffer, 0, buffer.Length);
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        if (!float.IsFinite(buffer[i]))
                        {
                            Interlocked.Increment(ref activeErrors);
                        }
                    }
                }
                catch
                {
                    Interlocked.Increment(ref activeErrors);
                }
            }
        });

        await Task.WhenAll(mutator, processor);
        Assert.Equal(0, activeErrors);
    }

    [Fact]
    public void SoftLimiterDspEffect_ExtremeValues_StrictlyBoundedBelowOne()
    {
        var limiter = new SoftLimiterDspEffect(0.90f);
        limiter.Initialize(44100, 1);

        var extremeInputs = new float[] { 10.0f, 100.0f, 1000.0f, -10.0f, -100.0f, -1000.0f };
        limiter.Process(extremeInputs, 0, extremeInputs.Length);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(extremeInputs[i] < 1.0f && extremeInputs[i] >= 0.90f, $"Input {i} was {extremeInputs[i]}");
        }
        for (int i = 3; i < 6; i++)
        {
            Assert.True(extremeInputs[i] > -1.0f && extremeInputs[i] <= -0.90f, $"Input {i} was {extremeInputs[i]}");
        }
    }

    [Fact]
    public void AudioDspChain_EdgeCases_NullsAndBoundsHandledSafely()
    {
        var chain = new AudioDspChain();

        // Null checks
        Assert.Throws<ArgumentNullException>(() => chain.AddEffect(null!));
        Assert.Throws<ArgumentNullException>(() => chain.InsertEffect(0, null!));
        Assert.False(chain.RemoveEffect((IAudioDspEffect)null!));

        // Non-existent remove
        chain.RemoveEffect("NonExistent");
        chain.RemoveEffect(string.Empty);
        chain.RemoveEffect((string)null!);

        // Process with null buffer or 0/negative count
        chain.Process(null!, 0, 10);
        var buf = new float[10];
        chain.Process(buf, 0, 0);
        chain.Process(buf, 0, -5);

        // Get non-existent effect
        Assert.Null(chain.GetEffect<EqualizerDspEffect>());
    }

    #region Adversarial Challenge & Stress Tests

    [Fact]
    public async Task AudioDspChain_Adversarial_MassiveMultiThreadedStress()
    {
        var chain = new AudioDspChain();
        chain.AddEffect(new EqualizerDspEffect());
        chain.AddEffect(new DynamicNormalizerDspEffect());
        chain.AddEffect(new SoftLimiterDspEffect());
        chain.AddEffect(new MockDspEffect("Passthrough"));
        chain.Initialize(44100, 2);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var token = cts.Token;
        int activeErrors = 0;

        // 4 Render worker threads
        var renderWorkers = Enumerable.Range(0, 4).Select(workerId => Task.Run(() =>
        {
            var rnd = new Random(workerId * 100);
            var buffer = new float[4096];

            while (!token.IsCancellationRequested)
            {
                int len = rnd.Next(1, buffer.Length);
                int offset = rnd.Next(0, buffer.Length - len);

                for (int i = 0; i < len; i++)
                {
                    buffer[offset + i] = (float)(rnd.NextDouble() * 2.0 - 1.0);
                }

                try
                {
                    chain.Process(buffer, offset, len);
                }
                catch
                {
                    Interlocked.Increment(ref activeErrors);
                }
            }
        })).ToArray();

        // Mutator 1: Add, Insert, Remove, Clear
        var mutatorWorker = Task.Run(() =>
        {
            int counter = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string name = $"Mock_{counter++ % 20}";
                    var mock = new MockDspEffect(name, 0.99f);
                    chain.AddEffect(mock);

                    if (counter % 3 == 0)
                    {
                        chain.InsertEffect(1, new MockDspEffect($"Insert_{counter}", 1.01f));
                    }

                    if (chain.Effects.Count > 10)
                    {
                        chain.RemoveEffect(name);
                    }

                    if (counter % 50 == 0)
                    {
                        chain.Clear();
                        chain.AddEffect(new EqualizerDspEffect());
                        chain.AddEffect(new DynamicNormalizerDspEffect());
                        chain.AddEffect(new SoftLimiterDspEffect());
                        chain.AddEffect(new MockDspEffect("Passthrough"));
                    }
                }
                catch
                {
                    Interlocked.Increment(ref activeErrors);
                }
                Thread.Yield();
            }
        });

        // Mutator 2: Toggler (Enable / Disable effects & chain)
        var togglerWorker = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var effects = chain.Effects;
                    foreach (var effect in effects)
                    {
                        effect.IsEnabled = !effect.IsEnabled;
                    }
                }
                catch
                {
                    Interlocked.Increment(ref activeErrors);
                }
                Thread.Yield();
            }
        });

        // Mutator 3: Initializer (Sample rate / channel changes)
        var initializerWorker = Task.Run(() =>
        {
            int[] rates = [44100, 48000, 88200, 96000, 192000];
            int[] channels = [1, 2, 4, 6, 8];
            int idx = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    int sr = rates[idx % rates.Length];
                    int ch = channels[idx % channels.Length];
                    chain.Initialize(sr, ch);
                    idx++;
                }
                catch
                {
                    Interlocked.Increment(ref activeErrors);
                }
                Thread.Yield();
            }
        });

        // Mutator 4: Reset caller
        var resetWorker = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    chain.Reset();
                }
                catch
                {
                    Interlocked.Increment(ref activeErrors);
                }
                Thread.Yield();
            }
        });

        await Task.WhenAll([.. renderWorkers, mutatorWorker, togglerWorker, initializerWorker, resetWorker]);

        Assert.Equal(0, activeErrors);
    }

    [Theory]
    [InlineData(0, 0, 2)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(3, 0, 2)]
    [InlineData(5, 2, 2)]
    [InlineData(7, 3, 4)]
    [InlineData(13, 0, 6)]
    [InlineData(1023, 5, 2)]
    [InlineData(1025, 0, 2)]
    [InlineData(4095, 1, 2)]
    [InlineData(4097, 0, 2)]
    [InlineData(100000, 10, 2)]
    public void AudioDspChain_Adversarial_EdgeCaseBufferSizesAndAlignments(int count, int offset, int channels)
    {
        var chain = new AudioDspChain();
        chain.AddEffect(new EqualizerDspEffect(new EqProfile
        {
            Enabled = true,
            PreampDb = 1.0,
            Bands = new() { new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = 3.0, Q = 1.0 } }
        }));
        chain.AddEffect(new DynamicNormalizerDspEffect(new NormalizerSettings
        {
            Enabled = true,
            Mode = NormalizerMode.AlwaysDynamic,
            TargetLevelDb = -12.0,
            MaxBoostDb = 6.0
        }));
        chain.AddEffect(new SoftLimiterDspEffect(0.90f));
        chain.AddEffect(new MockDspEffect("Passthrough"));
        chain.Initialize(44100, channels);

        float[] buffer = new float[offset + count + 64];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = 0.5f;
        }

        // Must not throw IndexOutOfRangeException or any exception
        var exception = Record.Exception(() => chain.Process(buffer, offset, count));
        Assert.Null(exception);
    }

    [Fact]
    public void AudioDspChain_Adversarial_NonFiniteAndExtremeValues_DoNotCrash()
    {
        var chain = new AudioDspChain();
        var eq = new EqualizerDspEffect(new EqProfile
        {
            Enabled = true,
            PreampDb = 2.0,
            Bands = new() { new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = 3.0, Q = 1.0 } }
        });
        var norm = new DynamicNormalizerDspEffect(new NormalizerSettings { Enabled = true });
        var limiter = new SoftLimiterDspEffect(0.90f);
        var tail = new MockDspEffect("Passthrough");

        chain.AddEffect(eq);
        chain.AddEffect(norm);
        chain.AddEffect(limiter);
        chain.AddEffect(tail);
        chain.Initialize(44100, 2);

        float[] nonFiniteBuffer =
        [
            float.NaN,
            float.PositiveInfinity,
            float.NegativeInfinity,
            1e-38f, // Subnormal / Denormal
            -1e-38f,
            1e30f,  // Huge positive
            -1e30f, // Huge negative
            0.0f,
            -0.0f
        ];

        // Process dirty samples
        var ex = Record.Exception(() => chain.Process(nonFiniteBuffer, 0, nonFiniteBuffer.Length));
        Assert.Null(ex);

        // Non-finite input must not short-circuit the chain: the tail effect still runs
        Assert.Equal(1, tail.ProcessCallCount);

        // Reset cleanses state
        chain.Reset();
        Assert.Equal(1, tail.ResetCallCount);

        // Normal sine wave should process cleanly after reset
        var cleanSine = GenerateSine(44100, 2, 440, 0.1, 0.5f);
        chain.Process(cleanSine, 0, cleanSine.Length);

        Assert.All(cleanSine, s => Assert.True(float.IsFinite(s)));
        Assert.Equal(2, tail.ProcessCallCount);
    }

    [Fact]
    public void EqualizerDspEffect_Adversarial_ExtremeParameters_ClampedSafely()
    {
        var eq = new EqualizerDspEffect();
        eq.Initialize(44100, 2);

        // Over 20 bands, extreme values
        var extremeProfile = new EqProfile
        {
            Enabled = true,
            PreampDb = 999.0, // Should clamp to 12.0
            Bands = Enumerable.Range(0, 30).Select(i => new EqBandSettings
            {
                Type = (EqFilterType)(i % 5),
                FrequencyHz = i % 2 == 0 ? 0.001 : 100000.0, // Should clamp to [20, Nyquist]
                GainDb = i % 2 == 0 ? -100.0 : 100.0,        // Should clamp to [-15, +15]
                Q = i % 2 == 0 ? 0.0001 : 999.0              // Should clamp to [0.1, 8.0]
            }).ToList()
        };

        var ex = Record.Exception(() => eq.SetProfile(extremeProfile));
        Assert.Null(ex);

        var buffer = GenerateSine(44100, 2, 1000, 0.05, 0.5f);
        var procEx = Record.Exception(() => eq.Process(buffer, 0, buffer.Length));
        Assert.Null(procEx);

        Assert.All(buffer, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void DynamicNormalizerDspEffect_Adversarial_ExtremeParameters_ClampedSafely()
    {
        var norm = new DynamicNormalizerDspEffect();
        norm.Initialize(44100, 2);

        var extremeSettings = new NormalizerSettings
        {
            Enabled = true,
            Mode = (NormalizerMode)99, // Unknown mode falls to default
            TargetLevelDb = -999.0,
            MaxBoostDb = 999.0,
            Speed = (NormalizerSpeed)99
        };

        var ex = Record.Exception(() => norm.ApplySettings(extremeSettings));
        Assert.Null(ex);

        var rgEx = Record.Exception(() => norm.SetReplayGain(-50.0f));
        Assert.Null(rgEx);

        var buffer = GenerateSine(44100, 2, 1000, 0.05, 0.5f);
        var procEx = Record.Exception(() => norm.Process(buffer, 0, buffer.Length));
        Assert.Null(procEx);

        Assert.All(buffer, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void SoftLimiterDspEffect_Adversarial_BoundaryAndLinearity()
    {
        var limiter = new SoftLimiterDspEffect
        {
            Threshold = 0.01f // Clamped to 0.5f
        };
        Assert.Equal(0.5f, limiter.Threshold);

        limiter.Threshold = 1.5f; // Clamped to 0.99f
        Assert.Equal(0.99f, limiter.Threshold);

        limiter.Threshold = 0.80f;

        // Sub-threshold exact linearity
        float[] subThresh = [-0.80f, -0.50f, 0.0f, 0.50f, 0.80f];
        float[] copy = (float[])subThresh.Clone();
        limiter.Process(copy, 0, copy.Length);

        for (int i = 0; i < subThresh.Length; i++)
        {
            Assert.Equal(subThresh[i], copy[i]);
        }

        // Asymptotic compression strictly bounded < 1.0 and > -1.0
        float[] superThresh = [0.81f, 1.0f, 5.0f, 50.0f, 1000.0f, -0.81f, -1.0f, -5.0f, -50.0f, -1000.0f];
        limiter.Process(superThresh, 0, superThresh.Length);

        for (int i = 0; i < 5; i++)
        {
            Assert.InRange(superThresh[i], 0.80f, 1.0f);
        }
        for (int i = 5; i < 10; i++)
        {
            Assert.InRange(superThresh[i], -1.0f, -0.80f);
        }
    }

    [Fact]
    public async Task SequencerStream_Adversarial_ConcurrentControlOperations_ThreadSafe()
    {
        var customChain = new AudioDspChain();
        customChain.AddEffect(new EqualizerDspEffect());
        customChain.AddEffect(new DynamicNormalizerDspEffect());
        customChain.AddEffect(new SoftLimiterDspEffect());

        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var seq = new SequencerStream(
            format,
            applyVolume: true,
            gainProvider: _ => 1.0f,
            latencyMs: 50,
            dspChain: customChain);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var token = cts.Token;
        int activeErrors = 0;

        var controlTask = Task.Run(() =>
        {
            int counter = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    seq.IsPaused = (counter % 2 == 0);
                    seq.SetGain(0.5f + (counter % 10) * 0.05f);
                    seq.SetEqualizer(new EqProfile { Enabled = (counter % 3 == 0), PreampDb = counter % 6 });
                    seq.SetNormalizer(new NormalizerSettings { Enabled = (counter % 2 == 0) }, 1.0f);
                    seq.Seek(TimeSpan.FromSeconds(counter % 100));
                    counter++;
                }
                catch
                {
                    Interlocked.Increment(ref activeErrors);
                }
                Thread.Yield();
            }
        });

        var readTask = Task.Run(() =>
        {
            byte[] rawBuf = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = seq.Read(rawBuf, 0, rawBuf.Length);
                    Assert.True(bytesRead >= 0);
                }
                catch
                {
                    Interlocked.Increment(ref activeErrors);
                }
            }
        });

        await Task.WhenAll(controlTask, readTask);
        Assert.Equal(0, activeErrors);
    }

    [Fact]
    public void AudioDspChain_PartialTrailingFrame_IsNotProcessedByAnyEffect()
    {
        var chain = new AudioDspChain();
        var effect = new MockDspEffect("Gain", 2.0f);
        chain.AddEffect(effect);
        chain.Initialize(48000, 2);

        // 7 floats across 2 channels is three whole frames plus one orphan sample. Stages used to
        // disagree about that orphan, so the chain truncates to whole frames for all of them.
        var buffer = new float[7];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = 0.25f;

        chain.Process(buffer, 0, buffer.Length);

        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(0.5f, buffer[i], 6);
        }
        Assert.Equal(0.25f, buffer[6], 6);
    }

    [Fact]
    public void AudioDspChain_CountSmallerThanOneFrame_ProcessesNothing()
    {
        var chain = new AudioDspChain();
        var effect = new MockDspEffect("Gain", 2.0f);
        chain.AddEffect(effect);
        chain.Initialize(48000, 2);

        var buffer = new[] { 0.25f };
        chain.Process(buffer, 0, 1);

        Assert.Equal(0.25f, buffer[0], 6);
        Assert.Equal(0, effect.ProcessCallCount);
    }

    #endregion
}
