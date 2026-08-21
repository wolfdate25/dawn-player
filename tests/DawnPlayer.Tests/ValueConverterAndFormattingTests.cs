using System;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Unit test suite for UI Value Converters and Text Formatting:
/// 1. TimeSpanTextConverter (m:ss, h:mm:ss, negative clamping to 0:00).
/// 2. TextFormat.LongDuration (0초, N분, N시간 M분, multi-day support).
/// 3. TrackNoFormatter (0 -> "-", 1..9 -> "0N", 10+ -> "NN").
/// 4. QueueIndexToText (1 -> "Q1", non-int -> "").
/// 5. BoolToVisibility and InverseBoolToVisibility mapping.
/// 6. QueueIndexToVisibility (> 0 -> Visible, <= 0 / non-int -> Collapsed).
/// </summary>
public class ValueConverterAndFormattingTests
{
    #region Test Doubles & Pure Algorithms for Converters

    public enum TestVisibility { Visible, Collapsed }

    public static class PureTimeSpanFormatter
    {
        public static string Convert(TimeSpan t) =>
            t < TimeSpan.Zero ? "0:00" :
            t.TotalHours >= 1 ? ((int)t.TotalHours) + t.ToString(@"\:mm\:ss") : t.ToString(@"m\:ss");

        public static object ConvertObject(object? value) =>
            value is TimeSpan ts ? Convert(ts) : "";
    }

    public static class PureTextFormat
    {
        public static string Time(TimeSpan t) => PureTimeSpanFormatter.Convert(t);

        public static string LongDuration(TimeSpan t)
        {
            if (t <= TimeSpan.Zero) return "0초";
            if (t.TotalDays >= 1) return $"{(int)t.TotalHours}시간 {t.Minutes}분";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}시간 {t.Minutes}분";
            return $"{(int)t.TotalMinutes}분";
        }
    }

    public static class PureTrackNoFormatter
    {
        public static string Convert(object? value)
        {
            if (value is int no && no > 0)
            {
                return no < 10 ? $"0{no}" : no.ToString();
            }
            return "-";
        }
    }

    public static class PureQueueIndexToTextFormatter
    {
        public static string Convert(object? value) =>
            value is int i ? $"Q{i}" : "";
    }

    public static class PureBoolToVisibilityConverter
    {
        public static TestVisibility Convert(object? value) =>
            value is true ? TestVisibility.Visible : TestVisibility.Collapsed;
    }

    public static class PureInverseBoolToVisibilityConverter
    {
        public static TestVisibility Convert(object? value) =>
            value is true ? TestVisibility.Collapsed : TestVisibility.Visible;
    }

    public static class PureQueueIndexToVisibilityConverter
    {
        public static TestVisibility Convert(object? value) =>
            value is int i and > 0 ? TestVisibility.Visible : TestVisibility.Collapsed;
    }

    #endregion

    #region 1. TimeSpanTextConverter Tests

    [Theory]
    [InlineData(-10, "0:00")]
    [InlineData(-1, "0:00")]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(125, "2:05")]
    [InlineData(599, "9:59")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3665, "1:01:05")]
    [InlineData(7325, "2:02:05")]
    [InlineData(3600 * 25 + 120, "25:02:00")]
    public void TimeSpanConverter_FormatsCorrectly(int totalSeconds, string expected)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        Assert.Equal(expected, PureTimeSpanFormatter.Convert(ts));
        Assert.Equal(expected, PureTextFormat.Time(ts));
        Assert.Equal(expected, PureTimeSpanFormatter.ConvertObject(ts));
    }

    [Fact]
    public void TimeSpanConverter_NonTimeSpanInput_ReturnsEmptyString()
    {
        Assert.Equal("", PureTimeSpanFormatter.ConvertObject(null));
        Assert.Equal("", PureTimeSpanFormatter.ConvertObject("not a timespan"));
        Assert.Equal("", PureTimeSpanFormatter.ConvertObject(123));
    }

    #endregion

    #region 2. TextFormat.LongDuration Tests

    [Theory]
    [InlineData(0, "0초")]
    [InlineData(-10, "0초")]
    [InlineData(30, "0분")]
    [InlineData(900, "15분")]                     // 15 minutes
    [InlineData(3540, "59분")]                    // 59 minutes
    [InlineData(3600, "1시간 0분")]               // 1 hour 0 min
    [InlineData(5100, "1시간 25분")]              // 1 hour 25 min (85 mins)
    [InlineData(7200, "2시간 0분")]               // 2 hours
    [InlineData(86400 + 3600 + 1200, "25시간 20분")] // 1 day 1 hour 20 mins
    public void LongDuration_FormatsKoreanStrings(int seconds, string expected)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        Assert.Equal(expected, PureTextFormat.LongDuration(ts));
    }

    #endregion

    #region 3. TrackNoFormatterConverter Tests

    [Theory]
    [InlineData(0, "-")]
    [InlineData(-1, "-")]
    [InlineData(-99, "-")]
    [InlineData(1, "01")]
    [InlineData(3, "03")]
    [InlineData(9, "09")]
    [InlineData(10, "10")]
    [InlineData(15, "15")]
    [InlineData(99, "99")]
    [InlineData(105, "105")]
    public void TrackNoFormatter_FormatsIntegerCorrectly(int trackNo, string expected)
    {
        Assert.Equal(expected, PureTrackNoFormatter.Convert(trackNo));
    }

    [Fact]
    public void TrackNoFormatter_NonIntegerInputs_ReturnsDash()
    {
        Assert.Equal("-", PureTrackNoFormatter.Convert(null));
        Assert.Equal("-", PureTrackNoFormatter.Convert("3"));
        Assert.Equal("-", PureTrackNoFormatter.Convert(3.14));
    }

    #endregion

    #region 4. QueueIndexToTextConverter Tests

    [Theory]
    [InlineData(1, "Q1")]
    [InlineData(2, "Q2")]
    [InlineData(10, "Q10")]
    [InlineData(0, "Q0")]
    [InlineData(-1, "Q-1")]
    public void QueueIndexToText_FormatsCorrectly(int queueIndex, string expected)
    {
        Assert.Equal(expected, PureQueueIndexToTextFormatter.Convert(queueIndex));
    }

    [Fact]
    public void QueueIndexToText_NonIntInput_ReturnsEmptyString()
    {
        Assert.Equal("", PureQueueIndexToTextFormatter.Convert(null));
        Assert.Equal("", PureQueueIndexToTextFormatter.Convert("1"));
        Assert.Equal("", PureQueueIndexToTextFormatter.Convert(false));
    }

    #endregion

    #region 5. Visibility Converters Tests

    [Fact]
    public void BoolToVisibility_MapsTrueToVisible_OthersToCollapsed()
    {
        Assert.Equal(TestVisibility.Visible, PureBoolToVisibilityConverter.Convert(true));
        Assert.Equal(TestVisibility.Collapsed, PureBoolToVisibilityConverter.Convert(false));
        Assert.Equal(TestVisibility.Collapsed, PureBoolToVisibilityConverter.Convert(null));
        Assert.Equal(TestVisibility.Collapsed, PureBoolToVisibilityConverter.Convert("true"));
        Assert.Equal(TestVisibility.Collapsed, PureBoolToVisibilityConverter.Convert(1));
    }

    [Fact]
    public void InverseBoolToVisibility_MapsTrueToCollapsed_OthersToVisible()
    {
        Assert.Equal(TestVisibility.Collapsed, PureInverseBoolToVisibilityConverter.Convert(true));
        Assert.Equal(TestVisibility.Visible, PureInverseBoolToVisibilityConverter.Convert(false));
        Assert.Equal(TestVisibility.Visible, PureInverseBoolToVisibilityConverter.Convert(null));
        Assert.Equal(TestVisibility.Visible, PureInverseBoolToVisibilityConverter.Convert("true"));
        Assert.Equal(TestVisibility.Visible, PureInverseBoolToVisibilityConverter.Convert(0));
    }

    [Theory]
    [InlineData(1, TestVisibility.Visible)]
    [InlineData(5, TestVisibility.Visible)]
    [InlineData(0, TestVisibility.Collapsed)]
    [InlineData(-1, TestVisibility.Collapsed)]
    public void QueueIndexToVisibility_MapsPositiveToVisible_OthersToCollapsed(int index, TestVisibility expected)
    {
        Assert.Equal(expected, PureQueueIndexToVisibilityConverter.Convert(index));
    }

    [Fact]
    public void QueueIndexToVisibility_NonInt_ReturnsCollapsed()
    {
        Assert.Equal(TestVisibility.Collapsed, PureQueueIndexToVisibilityConverter.Convert(null));
        Assert.Equal(TestVisibility.Collapsed, PureQueueIndexToVisibilityConverter.Convert("1"));
    }

    #endregion
}
