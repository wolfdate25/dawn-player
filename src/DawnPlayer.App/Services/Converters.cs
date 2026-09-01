using System;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace DawnPlayer.App.Services;

/// <summary>TimeSpan → "m:ss" / "h:mm:ss".</summary>
public sealed class TimeSpanTextConverter : IValueConverter
{
    public static string Convert(TimeSpan t) =>
        t < TimeSpan.Zero ? "0:00" :
        t.TotalHours >= 1 ? ((int)t.TotalHours) + t.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is TimeSpan ts ? Convert(ts) : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class QueueIndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is int i and > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class QueueIndexToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is int i ? $"Q{i}" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class TrackNoFormatterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int no && no > 0)
        {
            return no < 10 ? $"0{no}" : no.ToString(CultureInfo.InvariantCulture);
        }
        return "-";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class IsPlayingToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class IsPlayingToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true
            ? Helpers.ThemeResourceHelper.GetBrush("DawnAccentBrush")
            : Helpers.ThemeResourceHelper.GetBrush("TextPrimaryBrush");

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public static class TextFormat
{
    public static string Time(TimeSpan t) => TimeSpanTextConverter.Convert(t);

    public static string LongDuration(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "0초";
        if (t.TotalDays >= 1) return $"{(int)t.TotalHours}시간 {t.Minutes}분";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}시간 {t.Minutes}분";
        return $"{(int)t.TotalMinutes}분";
    }
}
