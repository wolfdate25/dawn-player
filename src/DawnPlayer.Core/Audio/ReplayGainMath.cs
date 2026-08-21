using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Pure mathematical calculations for ReplayGain gain multipliers, Preamp decibels,
/// and peak-based anti-clipping limits.
/// </summary>
public static class ReplayGainMath
{
    public const float MinGain = 0.0f;
    public const float MaxGain = 8.0f;

    /// <summary>
    /// Computes the linear gain multiplier based on volume, ReplayGain mode, preamp dB, and peak anti-clipping.
    /// </summary>
    public static float ComputeGain(
        Track? track,
        double volume,
        ReplayGainMode mode,
        double preampDb,
        bool preventClipping)
    {
        if (track == null) return 1.0f;

        float g = (float)volume;
        if (mode != ReplayGainMode.Off)
        {
            var gainDb = mode == ReplayGainMode.Track ? track.RgTrackGainDb : track.RgAlbumGainDb;
            var peak = mode == ReplayGainMode.Track ? track.RgTrackPeak : track.RgAlbumPeak;
            if (gainDb.HasValue)
            {
                g *= DecibelsToLinear((float)(gainDb.Value + preampDb));
                if (preventClipping && peak is > 0)
                {
                    var max = (float)(1.0 / peak.Value);
                    if (g > max) g = max;
                }
            }
        }
        return Math.Clamp(g, MinGain, MaxGain);
    }

    /// <summary>
    /// Computes the ReplayGain multiplier without master volume, or null if untagged/disabled.
    /// </summary>
    public static float? ComputeReplayGainOnly(
        Track? track,
        ReplayGainMode mode,
        double preampDb,
        bool preventClipping)
    {
        if (track == null || mode == ReplayGainMode.Off) return null;

        var gainDb = mode == ReplayGainMode.Track ? track.RgTrackGainDb : track.RgAlbumGainDb;
        var peak = mode == ReplayGainMode.Track ? track.RgTrackPeak : track.RgAlbumPeak;
        if (!gainDb.HasValue) return null;

        float g = DecibelsToLinear((float)(gainDb.Value + preampDb));
        if (preventClipping && peak is > 0)
        {
            var max = (float)(1.0 / peak.Value);
            if (g > max) g = max;
        }
        return Math.Clamp(g, MinGain, MaxGain);
    }

    /// <summary>Converts a decibel value to a linear amplitude multiplier: 10^(db / 20).</summary>
    public static float DecibelsToLinear(float db) =>
        MathF.Pow(10f, db / 20f);

    /// <summary>Converts a linear amplitude multiplier to decibels: 20 * log10(linear).</summary>
    public static float LinearToDecibels(float linear) =>
        linear > 0 ? 20f * MathF.Log10(linear) : -144f;
}
