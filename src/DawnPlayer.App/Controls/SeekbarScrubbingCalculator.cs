namespace DawnPlayer.App.Controls;

public sealed class SeekbarScrubbingCalculator
{
    public bool IsDragging { get; private set; }

    public void BeginDrag()
    {
        IsDragging = true;
    }

    public void CancelDrag()
    {
        IsDragging = false;
    }

    public TimeSpan? CompleteDrag(double currentSliderValue, TimeSpan duration)
    {
        if (!IsDragging) return null;
        IsDragging = false;

        if (double.IsNaN(currentSliderValue) || double.IsInfinity(currentSliderValue))
            return TimeSpan.Zero;

        double maxSec = duration > TimeSpan.Zero ? duration.TotalSeconds : 0.0;
        double clamped = maxSec > 0
            ? Math.Clamp(currentSliderValue, 0.0, maxSec)
            : Math.Max(0.0, currentSliderValue);

        return TimeSpan.FromSeconds(clamped);
    }

    public static (bool UpdateMax, double NewMax, double NewValue) CalculateSliderProgress(
        TimeSpan position, TimeSpan duration, double currentSliderMax, bool isDragging)
    {
        if (isDragging)
        {
            return (false, currentSliderMax, Math.Max(0.0, position.TotalSeconds));
        }

        if (duration > TimeSpan.Zero)
        {
            double durSec = duration.TotalSeconds;
            bool updateMax = Math.Abs(currentSliderMax - durSec) > 0.5;
            double newMax = updateMax ? durSec : currentSliderMax;
            double posSec = Math.Clamp(position.TotalSeconds, 0.0, durSec);
            return (updateMax, newMax, posSec);
        }
        else
        {
            bool updateMax = Math.Abs(currentSliderMax - 100.0) > 0.001;
            return (updateMax, 100.0, 0.0);
        }
    }

    public static (double ClampedMax, double ClampedValue, string Elapsed, string Remaining) CalculateRestoreState(
        double seconds, double maxSeconds)
    {
        double validMax = double.IsNaN(maxSeconds) || double.IsInfinity(maxSeconds) ? 0.0 : maxSeconds;
        double validSec = double.IsNaN(seconds) || double.IsInfinity(seconds) ? 0.0 : seconds;

        double clampedMax = Math.Max(1.0, validMax);
        double clampedVal = Math.Clamp(validSec, 0.0, clampedMax);
        string elapsed = FormatTime(TimeSpan.FromSeconds(clampedVal));
        string remaining = validMax > 0 && clampedMax > clampedVal
            ? "-" + FormatTime(TimeSpan.FromSeconds(clampedMax - clampedVal))
            : "0:00";

        return (clampedMax, clampedVal, elapsed, remaining);
    }

    public static string FormatTime(TimeSpan time)
    {
        if (time <= TimeSpan.Zero) return "0:00";
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time:mm\\:ss}"
            : $"{time:m\\:ss}";
    }

    public static string FormatRemaining(TimeSpan position, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || position >= duration) return "0:00";
        return "-" + FormatTime(duration - position);
    }
}
