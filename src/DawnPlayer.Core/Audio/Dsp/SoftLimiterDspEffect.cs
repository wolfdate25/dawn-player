using System;

namespace DawnPlayer.Core.Audio.Dsp;

/// <summary>
/// A polynomial soft-saturation limiter DSP effect that eliminates harsh digital clipping
/// by smoothly compressing signal peaks approaching or exceeding full scale (+/- 1.0).
/// Signals with |x| &lt;= threshold pass through completely uncompressed with zero harmonic distortion.
/// </summary>
public sealed class SoftLimiterDspEffect : IAudioDspEffect
{
    private float _threshold = 0.90f;

    public string Name => "SoftLimiter";
    public bool IsEnabled { get; set; } = true;

    public float Threshold
    {
        get => _threshold;
        set => _threshold = Math.Clamp(value, 0.5f, 0.99f);
    }

    public SoftLimiterDspEffect(float threshold = 0.90f)
    {
        Threshold = threshold;
    }

    public void Initialize(int sampleRate, int channels)
    {
        // Format-invariant stateless processor
    }

    public void Process(float[] buffer, int offset, int count)
    {
        if (!IsEnabled || buffer == null || count <= 0) return;

        float thresh = _threshold;
        float headroom = 1.0f - thresh;

        for (int i = 0; i < count; i++)
        {
            float s = buffer[offset + i];
            if (s > thresh)
            {
                float excess = s - thresh;
                buffer[offset + i] = thresh + headroom * (excess / (headroom + excess));
            }
            else if (s < -thresh)
            {
                float excess = -s - thresh;
                buffer[offset + i] = -(thresh + headroom * (excess / (headroom + excess)));
            }
        }
    }

    public void Reset()
    {
        // Stateless processor
    }

    /// <summary>
    /// Pure mathematical helper for soft-limiting a single sample value.
    /// </summary>
    public static float Limit(float sample, float threshold = 0.90f)
    {
        float thresh = Math.Clamp(threshold, 0.5f, 0.99f);
        float headroom = 1.0f - thresh;
        if (sample > thresh)
        {
            float excess = sample - thresh;
            return thresh + headroom * (excess / (headroom + excess));
        }
        if (sample < -thresh)
        {
            float excess = -sample - thresh;
            return -(thresh + headroom * (excess / (headroom + excess)));
        }
        return sample;
    }
}
