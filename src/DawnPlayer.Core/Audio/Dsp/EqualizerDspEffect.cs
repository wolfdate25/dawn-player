using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>
/// A real-time parametric equalizer DSP effect operating on 32-bit float samples.
/// Supports up to 20 biquad filter bands per profile, preamp gain adjustment, channel isolation,
/// lock-free atomic parameter snapshot replacement, and zero-distortion bypass when disabled.
/// </summary>
public sealed class EqualizerDspEffect : IAudioDspEffect
{
    /// <summary>
    /// One published configuration. Coefficients are immutable; <see cref="State"/> holds the
    /// filter delay lines and is touched by the render thread only.
    /// </summary>
    private sealed class EqDspSnapshot
    {
        public readonly bool Enabled;
        public readonly float PreampGain;
        public readonly int Channels;
        public readonly int SampleRate;

        /// <summary>Coefficients per band; channel-independent, so stored once per band.</summary>
        public readonly BiquadCoefficients[] Bands;

        /// <summary>z1/z2 per (channel, band), laid out as [(c * Bands.Length + b) * 2 + 0|1].</summary>
        public readonly float[] State;

        public EqDspSnapshot(bool enabled, float preampGain, int sampleRate, int channels, BiquadCoefficients[] bands)
        {
            Enabled = enabled;
            PreampGain = preampGain;
            SampleRate = sampleRate;
            Channels = channels;
            Bands = bands;
            State = new float[Math.Max(0, channels) * bands.Length * 2];
        }
    }

    private readonly object _configLock = new();
    private EqDspSnapshot _snapshot;
    private EqProfile? _currentProfile;
    private int _sampleRate = 44100;
    private int _channels = 2;

    public string Name => "Equalizer";
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// True when a profile is active, i.e. this stage can change the signal level. Distinct from
    /// <see cref="IsEnabled"/>, which is the chain-level switch and is always on in practice.
    /// </summary>
    public bool CanAlterLevel => IsEnabled && Volatile.Read(ref _snapshot).Enabled;

    public EqProfile? Profile
    {
        get
        {
            lock (_configLock) return _currentProfile?.Clone();
        }
    }

    public EqualizerDspEffect(EqProfile? initialProfile = null)
    {
        _currentProfile = initialProfile?.Clone();
        _snapshot = BuildSnapshot(_sampleRate, _channels, _currentProfile);
    }

    public void Initialize(int sampleRate, int channels)
    {
        lock (_configLock)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 44100;
            _channels = channels > 0 ? channels : 2;
            Volatile.Write(ref _snapshot, BuildSnapshot(_sampleRate, _channels, _currentProfile));
        }
    }

    /// <summary>
    /// Updates the equalizer profile live without interrupting playback or locking the audio render thread.
    /// </summary>
    public void SetProfile(EqProfile? profile)
    {
        lock (_configLock)
        {
            _currentProfile = profile?.Clone();
            Volatile.Write(ref _snapshot, BuildSnapshot(_sampleRate, _channels, _currentProfile));
        }
    }

    /// <summary>
    /// Processes interleaved 32-bit float audio samples in-place.
    /// Operates lock-free with zero heap allocation.
    /// </summary>
    public void Process(float[] buffer, int offset, int count)
    {
        if (!IsEnabled || buffer == null || count <= 0) return;

        var snap = Volatile.Read(ref _snapshot);
        if (!snap.Enabled) return;

        // Channel count comes from the snapshot, not a separate field: reading the two
        // independently could pair a fresh filter bank with a stale channel count and shear the
        // frame arithmetic below.
        int channels = snap.Channels;
        if (channels <= 0) return;

        int frames = count / channels;
        float preamp = snap.PreampGain;
        var bands = snap.Bands;
        int bandCount = bands.Length;

        if (preamp == 1.0f && bandCount == 0) return;

        var state = snap.State;

        for (int f = 0; f < frames; f++)
        {
            int baseIdx = offset + f * channels;
            for (int c = 0; c < channels; c++)
            {
                float s = buffer[baseIdx + c] * preamp;

                int stateBase = c * bandCount * 2;
                for (int b = 0; b < bandCount; b++)
                {
                    var k = bands[b];
                    int z = stateBase + b * 2;

                    // Transposed direct form II: two state words per section, and clearing them
                    // is all a filter reset needs.
                    double x = s;
                    double y = k.B0 * x + state[z];
                    state[z] = (float)(k.B1 * x - k.A1 * y + state[z + 1]);
                    state[z + 1] = (float)(k.B2 * x - k.A2 * y);
                    s = (float)y;
                }

                buffer[baseIdx + c] = s;
            }
        }
    }

    /// <summary>
    /// Clears internal delay lines of the biquad filters. Allocation-free and lock-free: it runs on
    /// the render thread at every gapless track boundary.
    /// </summary>
    public void Reset()
    {
        Array.Clear(Volatile.Read(ref _snapshot).State);
    }

    private static EqDspSnapshot BuildSnapshot(int sampleRate, int channels, EqProfile? profile)
    {
        if (profile == null || !profile.Enabled)
        {
            return new EqDspSnapshot(false, 1.0f, sampleRate, channels, Array.Empty<BiquadCoefficients>());
        }

        float preampDb = (float)Math.Clamp(profile.PreampDb, -12.0, 12.0);
        float preampGain = (float)Math.Pow(10.0, preampDb / 20.0);

        var validBands = (profile.Bands ?? Enumerable.Empty<EqBandSettings>())
            .Take(20)
            .ToList();

        var bands = new BiquadCoefficients[validBands.Count];
        for (int b = 0; b < validBands.Count; b++)
        {
            var band = validBands[b];
            bands[b] = BiquadDesign.Create(band.Type, band.FrequencyHz, band.GainDb, band.Q, sampleRate);
        }

        return new EqDspSnapshot(true, preampGain, sampleRate, channels, bands);
    }
}
