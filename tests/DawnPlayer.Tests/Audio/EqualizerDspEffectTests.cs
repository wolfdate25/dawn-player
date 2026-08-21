using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio.Dsp;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.Audio;

public sealed class EqualizerDspEffectTests
{
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
    public void Bypass_WhenDisabled_PreservesSamplesExactly()
    {
        var original = GenerateSine(44100, 2, 440, 0.1, 0.75f);

        var profile = new EqProfile
        {
            Enabled = false,
            PreampDb = 6.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 440, GainDb = 10.0, Q = 1.0 }
            }
        };

        var eq = new EqualizerDspEffect(profile);
        eq.Initialize(44100, 2);
        Assert.False(eq.CanAlterLevel);

        var buffer = (float[])original.Clone();
        eq.Process(buffer, 0, buffer.Length);

        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i], buffer[i]);
        }

        // The chain-level switch must bypass even while an active profile is loaded
        var activeProfile = profile.Clone();
        activeProfile.Enabled = true;
        eq.SetProfile(activeProfile);
        eq.IsEnabled = false;
        Assert.False(eq.CanAlterLevel);

        var bypassed = (float[])original.Clone();
        eq.Process(bypassed, 0, bypassed.Length);

        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i], bypassed[i]);
        }
    }

    [Fact]
    public void FlatProfile_ZeroGainAndPreamp_PassesSignalVirtuallyIdentical()
    {
        var original = GenerateSine(44100, 2, 1000, 0.2, 0.5f);

        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = 0.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = 0.0, Q = 1.0 },
                new EqBandSettings { Type = EqFilterType.LowShelf, FrequencyHz = 200, GainDb = 0.0, Q = 1.0 },
                new EqBandSettings { Type = EqFilterType.HighShelf, FrequencyHz = 5000, GainDb = 0.0, Q = 1.0 }
            }
        };

        var eq = new EqualizerDspEffect(profile);
        eq.Initialize(44100, 2);
        Assert.True(eq.CanAlterLevel);

        var buffer = (float[])original.Clone();
        eq.Process(buffer, 0, buffer.Length);

        // After filter initialization settles, difference should be near 0
        for (int i = 200; i < buffer.Length; i++)
        {
            Assert.InRange(Math.Abs(buffer[i] - original[i]), 0.0f, 0.001f);
        }
    }

    [Fact]
    public void SineWave_PeakingEqCut_DecreasesAmplitude()
    {
        int sampleRate = 48000;
        var original = GenerateSine(sampleRate, 1, 1000, 0.2, 0.5f);

        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = 0.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = -12.0, Q = 1.414 }
            }
        };

        var eq = new EqualizerDspEffect(profile);
        eq.Initialize(sampleRate, 1);

        var buffer = (float[])original.Clone();
        eq.Process(buffer, 0, buffer.Length);

        float inPeak = original.Skip(500).Max(Math.Abs);
        float outPeak = buffer.Skip(500).Max(Math.Abs);

        // -12dB corresponds to ~0.25x peak amplitude
        double ratio = outPeak / inPeak;
        Assert.InRange(ratio, 0.20, 0.30);
    }

    [Fact]
    public void MultiChannel_IndependentChannels_NoCrossTalk()
    {
        int sampleRate = 44100;
        int totalFrames = 2000;
        float[] samples = new float[totalFrames * 2];

        // Left channel has 1kHz sine, Right channel is strictly 0.0
        for (int f = 0; f < totalFrames; f++)
        {
            samples[f * 2] = (float)Math.Sin(2.0 * Math.PI * 1000.0 * f / sampleRate);
            samples[f * 2 + 1] = 0.0f;
        }

        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = 3.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = 9.0, Q = 2.0 },
                new EqBandSettings { Type = EqFilterType.HighShelf, FrequencyHz = 8000, GainDb = 6.0, Q = 1.0 }
            }
        };

        var eq = new EqualizerDspEffect(profile);
        eq.Initialize(sampleRate, 2);

        eq.Process(samples, 0, samples.Length);

        for (int f = 0; f < totalFrames; f++)
        {
            // Right channel must remain absolute zero
            Assert.Equal(0.0f, samples[f * 2 + 1]);
        }

        // The left channel must actually be boosted (+3dB preamp, +9dB at the 1kHz center),
        // otherwise the silent right channel would prove nothing
        float leftPeak = 0.0f;
        for (int f = 500; f < totalFrames; f++)
        {
            leftPeak = Math.Max(leftPeak, Math.Abs(samples[f * 2]));
        }
        Assert.InRange(leftPeak, 2.5f, 6.0f);
    }

    [Fact]
    public void BoundaryFrequencies_NyquistAndSubAudio_AreClampedAndProduceFiniteSamples()
    {
        int sampleRate = 44100;
        var buffer = GenerateSine(sampleRate, 2, 1000, 0.1, 0.5f);

        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = 0.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.LowShelf, FrequencyHz = 5, GainDb = 12.0, Q = 0.05 },     // Sub-audio
                new EqBandSettings { Type = EqFilterType.HighShelf, FrequencyHz = 30000, GainDb = 12.0, Q = 10.0 }, // Super-Nyquist
                new EqBandSettings { Type = EqFilterType.LowPass, FrequencyHz = 10, Q = 8.0 },
                new EqBandSettings { Type = EqFilterType.HighPass, FrequencyHz = 22000, Q = 0.1 }
            }
        };

        var eq = new EqualizerDspEffect(profile);
        eq.Initialize(sampleRate, 2);

        eq.Process(buffer, 0, buffer.Length);

        for (int i = 0; i < buffer.Length; i++)
        {
            Assert.False(float.IsNaN(buffer[i]), $"Sample at {i} is NaN");
            Assert.False(float.IsInfinity(buffer[i]), $"Sample at {i} is Infinity");
        }

        // The clamped 20Hz low-pass cascaded with the clamped 20kHz high-pass leaves the 1kHz
        // tone attenuated by well over 100dB, so the settled tail must be near silence rather
        // than a self-oscillating filter that merely avoided NaN
        float tailPeak = 0.0f;
        for (int i = buffer.Length * 3 / 4; i < buffer.Length; i++)
        {
            tailPeak = Math.Max(tailPeak, Math.Abs(buffer[i]));
        }
        Assert.InRange(tailPeak, 0.0f, 0.05f);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentProcessAndProfileUpdates_NoExceptionsOrCorruptions()
    {
        int sampleRate = 48000;
        var eq = new EqualizerDspEffect();
        eq.Initialize(sampleRate, 2);

        var source = GenerateSine(sampleRate, 2, 440, 0.05, 0.5f);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var token = cts.Token;
        int activeErrors = 0;

        // Audio render thread
        var renderTask = Task.Run(() =>
        {
            var buffer = new float[512];
            int pos = 0;
            while (!token.IsCancellationRequested)
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = source[pos];
                    pos = (pos + 1) % source.Length;
                }

                try
                {
                    eq.Process(buffer, 0, buffer.Length);
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
            // The token is the loop's stop signal only — it is deliberately not passed to
            // Task.Run, because a token that is already cancelled when the work item is
            // scheduled would leave the task Canceled and make Task.WhenAll throw.
        });

        // 3 UI update threads rapidly swapping profiles and clearing filter state
        var updateTasks = Enumerable.Range(0, 3).Select(taskId => Task.Run(() =>
        {
            var rand = new Random(taskId * 100);
            int counter = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    eq.SetProfile(new EqProfile
                    {
                        Enabled = rand.Next(2) == 0,
                        PreampDb = rand.NextDouble() * 24.0 - 12.0,
                        Bands = Enumerable.Range(0, rand.Next(0, 21)).Select(_ => new EqBandSettings
                        {
                            Type = (EqFilterType)rand.Next(0, 5),
                            FrequencyHz = rand.Next(20, 20000),
                            GainDb = rand.NextDouble() * 30.0 - 15.0,
                            Q = rand.NextDouble() * 7.9 + 0.1
                        }).ToList()
                    });

                    if (counter++ % 4 == 0)
                    {
                        eq.Reset();
                    }
                }
                catch
                {
                    Interlocked.Increment(ref activeErrors);
                }
                Thread.Yield();
            }
        })).ToArray();

        await Task.WhenAll([renderTask, .. updateTasks]);
        Assert.Equal(0, activeErrors);
    }

    [Fact]
    public void Reset_OnRenderThread_ClearsDelayLinesWithoutAllocating()
    {
        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = 0.0,
            Bands = Enumerable.Range(0, 8)
                .Select(i => new EqBandSettings
                {
                    Type = EqFilterType.PeakEq,
                    FrequencyHz = 100.0 * (i + 1),
                    GainDb = 6.0,
                    Q = 1.0
                })
                .ToList()
        };

        var eq = new EqualizerDspEffect(profile);
        eq.Initialize(48000, 2);

        // Prime the delay lines so Reset has state to clear.
        var buffer = GenerateSine(48000, 2, 440, 0.05);
        eq.Process(buffer, 0, buffer.Length);

        eq.Reset();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1000; i++)
        {
            eq.Reset();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Reset runs on the render thread at every gapless boundary, so it must not rebuild the
        // filter bank — it used to reallocate the whole thing under a lock the UI could hold.
        Assert.True(allocated == 0, $"Reset allocated {allocated} bytes across 1000 calls");
    }

    [Fact]
    public void Reset_AfterImpulse_SilencesFilterTail()
    {
        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = 0.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = 12.0, Q = 4.0 }
            }
        };

        var eq = new EqualizerDspEffect(profile);
        eq.Initialize(48000, 1);

        // A resonant peak rings well past the impulse, so a non-empty tail proves state exists.
        var impulse = new float[256];
        impulse[0] = 1.0f;
        eq.Process(impulse, 0, impulse.Length);

        var tail = new float[256];
        eq.Process(tail, 0, tail.Length);
        Assert.True(tail.Any(s => Math.Abs(s) > 1e-6f), "Expected a ringing tail before Reset");

        eq.Reset();

        var afterReset = new float[256];
        eq.Process(afterReset, 0, afterReset.Length);
        Assert.All(afterReset, s => Assert.True(Math.Abs(s) < 1e-9f, $"Expected silence after Reset, got {s}"));
    }

    [Fact]
    public async Task Initialize_RacingProcess_NeverShearsChannelStride()
    {
        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = 3.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = 6.0, Q = 1.0 }
            }
        };

        var eq = new EqualizerDspEffect(profile);
        eq.Initialize(44100, 2);

        int errors = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var render = Task.Run(() =>
        {
            // 6 floats is a whole frame for 1, 2, 3 and 6 channels, so any observed stride is
            // valid; only a torn (snapshot, channel-count) pair could produce garbage or throw.
            var buffer = new float[6];
            while (!cts.IsCancellationRequested)
            {
                for (int i = 0; i < buffer.Length; i++) buffer[i] = 0.25f;
                try
                {
                    eq.Process(buffer, 0, buffer.Length);
                    foreach (var s in buffer)
                    {
                        if (!float.IsFinite(s)) Interlocked.Increment(ref errors);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });

        var reconfigure = Task.Run(() =>
        {
            int[] channelCounts = [1, 2, 6];
            int i = 0;
            while (!cts.IsCancellationRequested)
            {
                eq.Initialize(44100 + (i % 2) * 3900, channelCounts[i % channelCounts.Length]);
                i++;
                Thread.Yield();
            }
        });

        await Task.WhenAll(render, reconfigure);
        Assert.Equal(0, errors);
    }
}
