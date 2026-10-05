using System;
using System.Globalization;
using DawnPlayer.App.Localization;
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

/// <summary>0-5 rating → star text. Rated values render filled stars (clamped to 5); an unrated
/// track renders a single outline star — the discovery affordance that tells the user the rating
/// cell exists and is clickable, instead of the previous empty string that rendered nothing.
/// The pure contract lives in <see cref="RatingCommands.DisplayText"/> (headless-testable); this
/// WinUI converter shell exists because IValueConverter cannot compile into the test project.</summary>
public sealed class RatingToStarsConverter : IValueConverter
{
    public static string DisplayText(int rating) => RatingCommands.DisplayText(rating);

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is int r ? RatingCommands.DisplayText(r) : RatingCommands.DisplayText(0);

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>0-5 rating → screen-reader text ("평점 N/5", "평점 없음" via resw). Bound dynamically
/// to the rating cell's AutomationProperties.Name — the literal-attribute form is barred by the
/// AutomationNameScan gate, dynamic data bindings are the sanctioned form. Pure contract in
/// <see cref="RatingCommands.AccessibilityText"/>.</summary>
public sealed class RatingAccessibilityConverter : IValueConverter
{
    public static string AccessibilityText(int rating) => RatingCommands.AccessibilityText(rating);

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is int r ? RatingCommands.AccessibilityText(r) : RatingCommands.AccessibilityText(0);

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
            ? Helpers.ThemeResourceHelper.GetBrush("DawnAccentTextBrush")
            : Helpers.ThemeResourceHelper.GetBrush("TextPrimaryBrush");

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public static class TextFormat
{
    public static string Time(TimeSpan t) => TimeSpanTextConverter.Convert(t);

    public static string LongDuration(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return AppStrings.Get("Time_ZeroSeconds", "0초");
        if (t.TotalDays >= 1 || t.TotalHours >= 1)
            return AppStrings.Format("Time_HoursMinutesFormat", "{0}시간 {1}분", (int)t.TotalHours, t.Minutes);
        return AppStrings.Format("Time_MinutesFormat", "{0}분", (int)t.TotalMinutes);
    }
}

/// <summary>Slider value (seconds) → the seek thumb's drag tooltip ("m:ss"/"h:mm:ss"). WinUI's
/// built-in thumb tooltip is enabled by default but renders an empty box unless a
/// ThumbToolTipValueConverter supplies content — 2026-10-05 user report. Pure contract in
/// <see cref="Controls.SliderThumbToolTipText.Time"/>, test-gated like the other converters.</summary>
public sealed class SeekSecondsThumbToolTipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => Controls.SliderThumbToolTipText.Time(SliderThumbToolTipValues.AsDouble(value));

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => 0d;  // the tooltip is read-only; the platform never converts back through it
}

/// <summary>Slider value (0-100) → the volume thumb's drag tooltip ("42%"). Same empty-tooltip
/// defect class as the seek slider. Pure contract in <see cref="Controls.SliderThumbToolTipText.Percent"/>.</summary>
public sealed class VolumePercentThumbToolTipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => Controls.SliderThumbToolTipText.Percent(SliderThumbToolTipValues.AsDouble(value));

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => 0d;  // the tooltip is read-only; the platform never converts back through it
}

internal static class SliderThumbToolTipValues
{
    /// <summary>The platform hands the tooltip converter the slider value; tolerate the boxed
    /// numeric shapes and treat anything else as unknown rather than throwing.</summary>
    public static double AsDouble(object value) => value switch
    {
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        string s when double.TryParse(s, out var parsed) => parsed,
        _ => double.NaN,
    };
}
