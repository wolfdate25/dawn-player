using System;
using System.Threading;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>
/// A real-time intelligent dynamic normalizer / AGC (Automatic Gain Control) DSP effect.
/// Smoothly monitors RMS loudness and continuously adjusts gain towards a target dBFS level
/// with attack/release time constants, noise floor gating, and hybrid ReplayGain tag integration.
/// Parameter changes are published via immutable snapshots, eliminating torn reads during live tuning.
/// </summary>
public sealed class DynamicNormalizerDspEffect : IAudioDspEffect
{
    private sealed class NormalizerSnapshot
    {
        public readonly bool Enabled;
        public readonly NormalizerMode Mode;
        public readonly float TargetLinear;
        public readonly float MaxBoostLinear;
        public readonly float MinGainLinear;
        public readonly float NoiseFloorRms;
        public readonly float BetaRms;
        public readonly float AlphaAttack;
        public readonly float AlphaRelease;
        public readonly float? StaticReplayGainLinear;
        public readonly int SampleRate;
        public readonly int Channels;

        public NormalizerSnapshot(
            bool enabled,
            NormalizerMode mode,
            float targetLinear,
            float maxBoostLinear,
            float minGainLinear,
            float noiseFloorRms,
            float betaRms,
            float alphaAttack,
            float alphaRelease,
            float? staticReplayGainLinear,
            int sampleRate,
            int channels)
        {
            Enabled = enabled;
            Mode = mode;
            TargetLinear = targetLinear;
            MaxBoostLinear = maxBoostLinear;
            MinGainLinear = minGainLinear;
            NoiseFloorRms = noiseFloorRms;
            BetaRms = betaRms;
            AlphaAttack = alphaAttack;
            AlphaRelease = alphaRelease;
            StaticReplayGainLinear = staticReplayGainLinear;
            SampleRate = sampleRate;
            Channels = channels;
        }
    }

    private readonly object _configLock = new();
    private NormalizerSnapshot _snapshot;
    private NormalizerSettings? _currentSettings;
    private float? _staticReplayGainLinear;
    private int _sampleRate = 44100;
    private int _channels = 2;

    // Dynamic State (updated exclusively on audio processing thread)
    private float _powerRms;
    private float _currentGain = 1.0f;

    public string Name => "DynamicNormalizer";
    public bool IsEnabled { get; set; } = true;

    public float CurrentGain => _currentGain;

    /// <summary>
    /// True when this stage can change the signal level — either dynamic normalization is on, or a
    /// static ReplayGain multiplier other than unity is applied. Used to decide whether a limiter
    /// is needed downstream.
    /// </summary>
    public bool CanAlterLevel
    {
        get
        {
            var snap = Volatile.Read(ref _snapshot);
            if (snap == null) return false;
            if (snap.Enabled) return true;
            var rg = snap.StaticReplayGainLinear;
            return rg.HasValue && Math.Abs(rg.Value - 1f) > 1e-4f;
        }
    }

    public DynamicNormalizerDspEffect(NormalizerSettings? initialSettings = null)
    {
        _currentSettings = initialSettings?.Clone();
        _snapshot = BuildSnapshot(_sampleRate, _channels, _currentSettings, _staticReplayGainLinear);
    }

    public void Initialize(int sampleRate, int channels)
    {
        lock (_configLock)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 44100;
            _channels = channels > 0 ? channels : 2;
            Volatile.Write(ref _snapshot, BuildSnapshot(_sampleRate, _channels, _currentSettings, _staticReplayGainLinear));
        }
    }

    public void ApplySettings(NormalizerSettings settings)
    {
        if (settings == null) return;
        lock (_configLock)
        {
            _currentSettings = settings.Clone();
            Volatile.Write(ref _snapshot, BuildSnapshot(_sampleRate, _channels, _currentSettings, _staticReplayGainLinear));
        }
    }

    public void SetReplayGain(float? staticReplayGainLinear)
    {
        lock (_configLock)
        {
            _staticReplayGainLinear = staticReplayGainLinear;
            Volatile.Write(ref _snapshot, BuildSnapshot(_sampleRate, _channels, _currentSettings, _staticReplayGainLinear));
        }
    }

    public void Reset()
    {
        _powerRms = 0.0f;
        _currentGain = 1.0f;
    }

    public void Process(float[] buffer, int offset, int count)
    {
        if (!IsEnabled || buffer == null || count <= 0) return;

        var snap = Volatile.Read(ref _snapshot);
        // Format comes from the snapshot, not from separate fields: reading them independently
        // could pair fresh coefficients with a stale channel count and shear the frame stride.
        int sampleRate = snap.SampleRate;
        int ch = snap.Channels;
        if (ch <= 0 || sampleRate <= 0) return;

        // If Disabled, smoothly fade gain back towards unity (1.0)
        if (!snap.Enabled)
        {
            if (Math.Abs(_currentGain - 1.0f) < 0.001f)
            {
                _currentGain = 1.0f;
                return;
            }

            float fadeRate = (float)(1.0 - Math.Exp(-1.0 / (sampleRate * 0.050)));
            for (int i = 0; i < count; i++)
            {
                _currentGain += fadeRate * (1.0f - _currentGain);
                buffer[offset + i] *= _currentGain;
            }
            return;
        }

        // Case 1: ReplayGainOnly mode
        if (snap.Mode == NormalizerMode.ReplayGainOnly)
        {
            float targetGain = snap.StaticReplayGainLinear ?? 1.0f;
            float smoothAlpha = (float)(1.0 - Math.Exp(-1.0 / (sampleRate * 0.040)));
            for (int i = 0; i < count; i++)
            {
                _currentGain += smoothAlpha * (targetGain - _currentGain);
                buffer[offset + i] *= _currentGain;
            }
            return;
        }

        // Case 2: Hybrid mode with valid static ReplayGain tag
        if (snap.Mode == NormalizerMode.Hybrid && snap.StaticReplayGainLinear.HasValue)
        {
            float targetGain = snap.StaticReplayGainLinear.Value;
            float smoothAlpha = (float)(1.0 - Math.Exp(-1.0 / (sampleRate * 0.040)));
            for (int i = 0; i < count; i++)
            {
                _currentGain += smoothAlpha * (targetGain - _currentGain);
                buffer[offset + i] *= _currentGain;
            }
            return;
        }

        // Case 3: AlwaysDynamic mode or Hybrid mode without tags -> Real-time AGC
        float beta = snap.BetaRms;
        float alphaAtt = snap.AlphaAttack;
        float alphaRel = snap.AlphaRelease;
        float targetLin = snap.TargetLinear;
        float maxBoost = snap.MaxBoostLinear;
        float minGain = snap.MinGainLinear;
        float noiseFloor = snap.NoiseFloorRms;

        for (int i = 0; i < count; i += ch)
        {
            float frameSqSum = 0.0f;
            for (int c = 0; c < ch && (i + c) < count; c++)
            {
                float s = buffer[offset + i + c];
                frameSqSum += s * s;
            }
            float framePower = frameSqSum / ch;

            // Update RMS power accumulator
            _powerRms += beta * (framePower - _powerRms);
            float currentRms = (float)Math.Sqrt(Math.Max(0.0f, _powerRms));

            float desiredGain;
            if (currentRms < noiseFloor)
            {
                desiredGain = 1.0f;
            }
            else
            {
                desiredGain = Math.Clamp(targetLin / currentRms, minGain, maxBoost);
            }

            // Smooth gain transition
            float stepAlpha = (desiredGain < _currentGain) ? alphaAtt : alphaRel;
            _currentGain += stepAlpha * (desiredGain - _currentGain);

            // Apply gain in-place
            for (int c = 0; c < ch && (i + c) < count; c++)
            {
                buffer[offset + i + c] *= _currentGain;
            }
        }
    }

    private static NormalizerSnapshot BuildSnapshot(int sampleRate, int channels, NormalizerSettings? settings, float? staticReplayGainLinear)
    {
        bool enabled = settings?.Enabled ?? false;
        var mode = settings?.Mode ?? NormalizerMode.Hybrid;
        double targetDb = Math.Clamp(settings?.TargetLevelDb ?? -12.0, -24.0, -6.0);
        double maxBoostDb = Math.Clamp(settings?.MaxBoostDb ?? 12.0, 0.0, 18.0);
        var speed = settings?.Speed ?? NormalizerSpeed.Balanced;

        float targetLinear = (float)Math.Pow(10.0, targetDb / 20.0);
        float maxBoostLinear = (float)Math.Pow(10.0, maxBoostDb / 20.0);
        float minGainLinear = (float)Math.Pow(10.0, -18.0 / 20.0);
        float noiseFloorRms = (float)Math.Pow(10.0, -65.0 / 20.0);

        int sr = sampleRate > 0 ? sampleRate : 44100;
        double tauRms = 0.050;
        float betaRms = (float)(1.0 - Math.Exp(-1.0 / (sr * tauRms)));

        double attackSec;
        double releaseSec;

        switch (speed)
        {
            case NormalizerSpeed.Fast:
                attackSec = 0.020;
                releaseSec = 0.200;
                break;
            case NormalizerSpeed.Smooth:
                attackSec = 0.080;
                releaseSec = 1.500;
                break;
            case NormalizerSpeed.Balanced:
            default:
                attackSec = 0.040;
                releaseSec = 0.600;
                break;
        }

        float alphaAttack = (float)(1.0 - Math.Exp(-1.0 / (sr * attackSec)));
        float alphaRelease = (float)(1.0 - Math.Exp(-1.0 / (sr * releaseSec)));

        return new NormalizerSnapshot(
            enabled,
            mode,
            targetLinear,
            maxBoostLinear,
            minGainLinear,
            noiseFloorRms,
            betaRms,
            alphaAttack,
            alphaRelease,
            staticReplayGainLinear,
            sr,
            channels > 0 ? channels : 2);
    }
}
