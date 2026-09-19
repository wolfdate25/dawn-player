namespace DawnPlayer.App.Controls;

/// <summary>
/// U4 geometry and decimation math for the fullscreen waveform seek canvas. Fractions (0..1 of
/// the canvas/track) are the common currency so time↔pixel conversions stay trivially testable;
/// the window maps positions in/out. Pure logic, no UI dependency.
/// </summary>
public static class WaveformLayout
{
    public const int DefaultScanBuckets = 480;

    /// <summary>Pointer x (0..width) → track fraction, clamped.</summary>
    public static double XToFraction(double x, double width) =>
        width <= 0 ? 0 : Math.Clamp(x / width, 0.0, 1.0);

    /// <summary>Track fraction → x (0..width).</summary>
    public static double FractionToX(double fraction, double width) =>
        Math.Clamp(fraction, 0.0, 1.0) * width;

    /// <summary>Position within duration → fraction; zero/negative duration maps to 0
    /// (streams and unopened sessions render an empty envelope rather than NaN).</summary>
    public static double FractionOfTrack(TimeSpan position, TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 0.0 : Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0.0, 1.0);

    /// <summary>
    /// Downsamples scan buckets to display bars with max-abs, so a thin display never hides the
    /// loudest peak of a group. Bars &gt;= buckets returns the input unchanged (by copy when
    /// trimming, else the same reference — treat as read-only).
    /// </summary>
    public static float[] Decimate(float[] peaks, int bars)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        ArgumentOutOfRangeException.ThrowIfZero(peaks.Length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bars);
        if (bars >= peaks.Length) return peaks;

        var result = new float[bars];
        double group = peaks.Length / (double)bars;
        for (int b = 0; b < bars; b++)
        {
            int start = (int)(b * group);
            int end = Math.Min(peaks.Length, (int)((b + 1) * group));
            float max = 0f;
            for (int i = start; i < end; i++)
            {
                float a = Math.Abs(peaks[i]);
                if (a > max) max = a;
            }
            result[b] = max;
        }
        return result;
    }

    /// <summary>Bar i's center x for <paramref name="bars"/> bars across <paramref name="width"/>.</summary>
    public static double BarCenterX(int bar, int bars, double width) =>
        (bar + 0.5) * width / bars;
}
