using System;
using System.Threading;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>
/// Headphone crossfeed after the Chu Moy topology: a one-pole lowpass of the opposite channel is
/// blended into each side, mimicking how a speaker on the far side reaches the far ear mostly as
/// attenuated lows. Output is scaled by 1/(1+feed) so an in-phase L=R worst case cannot clip —
/// which also means this stage can never raise the level, so it does not arm the limiter.
/// Stereo only; other channel layouts pass through untouched.
/// </summary>
public sealed class CrossfeedDspEffect : IAudioDspEffect
{
    /// <summary>One published configuration; state is render-thread-only.</summary>
    private sealed class Snapshot
    {
        public readonly bool Enabled;
        public readonly int SampleRate;
        public readonly int Channels;
        public readonly float LpCoef;       // one-pole coefficient a
        public readonly float Feed;         // opposite-channel blend
        public readonly float OutputScale;  // 1 / (1 + feed)

        /// <summary>One-pole lowpass memory per channel (the filtered opposite-channel signal).</summary>
        public readonly float[] LpState;

        public Snapshot(bool enabled, int sampleRate, int channels, float lpCoef, float feed)
        {
            Enabled = enabled;
            SampleRate = sampleRate;
            Channels = channels;
            LpCoef = lpCoef;
            Feed = feed;
            OutputScale = 1.0f / (1.0f + feed);
            LpState = new float[Math.Max(0, channels)];
        }
    }

    private readonly object _configLock = new();
    private Snapshot _snapshot;
    private CrossfeedSettings? _settings;

    public string Name => "Crossfeed";
    public bool IsEnabled { get; set; } = true;

    public CrossfeedDspEffect(CrossfeedSettings? initial = null)
    {
        _settings = initial?.Clone();
        _snapshot = Build(_settings, 44100, 2);
    }

    public void Initialize(int sampleRate, int channels)
    {
        lock (_configLock)
        {
            _snapshot = Build(_settings, sampleRate > 0 ? sampleRate : 44100, channels > 0 ? channels : 2);
        }
    }

    /// <summary>Publishes new settings live, rebuilding coefficients for the format the chain
    /// initialized us with (which may not be known yet when this lands before Initialize).</summary>
    public void ApplySettings(CrossfeedSettings? settings)
    {
        lock (_configLock)
        {
            _settings = settings?.Clone();
            var old = _snapshot;
            Volatile.Write(ref _snapshot, Build(_settings, old.SampleRate, old.Channels));
        }
    }

    public void Process(float[] buffer, int offset, int count)
    {
        if (!IsEnabled || buffer == null || count <= 0) return;

        var snap = Volatile.Read(ref _snapshot);
        if (!snap.Enabled || snap.Channels != 2) return;

        float a = snap.LpCoef;
        float feed = snap.Feed;
        float scale = snap.OutputScale;
        var lp = snap.LpState;

        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int i = offset + f * 2;
            float l = buffer[i];
            float r = buffer[i + 1];

            lp[0] += a * (r - lp[0]);
            lp[1] += a * (l - lp[1]);

            buffer[i] = (l + feed * lp[0]) * scale;
            buffer[i + 1] = (r + feed * lp[1]) * scale;
        }
    }

    /// <summary>Clears the lowpass memories; allocation-free, render-thread safe.</summary>
    public void Reset()
    {
        Array.Clear(Volatile.Read(ref _snapshot).LpState);
    }

    private static Snapshot Build(CrossfeedSettings? settings, int sampleRate, int channels)
    {
        if (settings == null || !settings.Enabled || channels != 2)
        {
            return new Snapshot(false, sampleRate, channels, 0f, 0f);
        }

        // Higher presets crossfeed more signal and from further up in frequency.
        (float cutoffHz, float feed) = settings.Strength switch
        {
            CrossfeedStrength.Low => (450f, 0.30f),
            CrossfeedStrength.High => (1100f, 0.65f),
            _ => (700f, 0.45f),
        };

        float nyquist = sampleRate / 2f;
        cutoffHz = Math.Min(cutoffHz, nyquist * 0.45f);
        float lpCoef = 1f - MathF.Exp(-2f * MathF.PI * cutoffHz / sampleRate);

        return new Snapshot(true, sampleRate, channels, lpCoef, feed);
    }
}
