using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DawnPlayer.App.Controls;

public sealed class LrcLineVm : INotifyPropertyChanged
{
    public TimeSpan Time { get; init; }
    public string Text { get; init; } = "";

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent != value)
            {
                _isCurrent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(FontSize));
                OnPropertyChanged(nameof(Opacity));
            }
        }
    }

    public bool IsActive => IsCurrent;

    public double BaseFontSize { get; set; } = 13.5;
    public double ActiveFontSize { get; set; } = 16.5;
    public bool EnableFocusEffect { get; set; } = true;

    public double FontSize => IsCurrent ? ActiveFontSize : BaseFontSize;
    public double Opacity => IsCurrent ? 1.0 : (EnableFocusEffect ? 0.40 : 0.85);

    public string FontFamily { get; set; } = "Segoe UI Variable, Malgun Gothic";
    public int CharacterSpacing { get; set; } = 0;
    public double LineHeight { get; set; } = 24.0;
    public string TextAlignment { get; set; } = "Center";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void NotifyTypographyChanged()
    {
        OnPropertyChanged(nameof(FontSize));
        OnPropertyChanged(nameof(Opacity));
        OnPropertyChanged(nameof(FontFamily));
        OnPropertyChanged(nameof(CharacterSpacing));
        OnPropertyChanged(nameof(LineHeight));
        OnPropertyChanged(nameof(TextAlignment));
    }
}

public static class LyricsScrollSynchronizer
{
    public static int FindActiveLineIndex(IReadOnlyList<LrcLineVm>? lines, TimeSpan playbackPosition, double offsetMs)
    {
        if (lines == null || lines.Count == 0)
            return -1;

        if (double.IsNaN(offsetMs) || double.IsInfinity(offsetMs))
            offsetMs = 0;

        var effectivePos = playbackPosition - TimeSpan.FromMilliseconds(offsetMs);

        int low = 0;
        int high = lines.Count - 1;
        int result = -1;

        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (lines[mid].Time <= effectivePos)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    public static double StepOffset(double currentOffsetMs, double deltaMs, double minOffsetMs = -10000, double maxOffsetMs = 10000)
    {
        if (double.IsNaN(currentOffsetMs) || double.IsInfinity(currentOffsetMs)) currentOffsetMs = 0;
        if (double.IsNaN(deltaMs) || double.IsInfinity(deltaMs)) deltaMs = 0;

        double min = Math.Min(minOffsetMs, maxOffsetMs);
        double max = Math.Max(minOffsetMs, maxOffsetMs);

        return Math.Clamp(currentOffsetMs + deltaMs, min, max);
    }

    public static string FormatOffsetLabel(double offsetMs)
    {
        if (double.IsNaN(offsetMs) || double.IsInfinity(offsetMs) || Math.Abs(offsetMs) < 0.001)
            return "오프셋 0.0s";

        double sec = offsetMs / 1000.0;
        string sign = offsetMs > 0 ? "+" : "";
        return $"오프셋 {sign}{sec:0.#}s";
    }

    public static TimeSpan CalculateSeekTarget(TimeSpan lineTimestamp, double offsetMs)
    {
        if (double.IsNaN(offsetMs) || double.IsInfinity(offsetMs))
            offsetMs = 0;

        var target = lineTimestamp + TimeSpan.FromMilliseconds(offsetMs);
        return target < TimeSpan.Zero ? TimeSpan.Zero : target;
    }

    public static bool UpdateActiveLineState(IReadOnlyList<LrcLineVm>? lines, ref int currentIndex, int targetIndex)
    {
        if (lines == null || currentIndex == targetIndex)
            return false;

        if (currentIndex >= 0 && currentIndex < lines.Count)
        {
            lines[currentIndex].IsCurrent = false;
        }

        currentIndex = targetIndex;

        if (targetIndex >= 0 && targetIndex < lines.Count)
        {
            lines[targetIndex].IsCurrent = true;
        }

        return true;
    }
}
