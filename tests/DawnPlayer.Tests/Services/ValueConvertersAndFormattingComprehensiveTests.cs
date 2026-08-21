using System;
using Xunit;

namespace DawnPlayer.Tests.Services;

public sealed class ValueConvertersAndFormattingComprehensiveTests
{
    public enum TestVisibility
    {
        Visible = 0,
        Collapsed = 1
    }

    public static class PureTimeSpanConverter
    {
        public static string Convert(TimeSpan t) =>
            t < TimeSpan.Zero ? "0:00" :
            t.TotalHours >= 1 ? ((int)t.TotalHours) + t.ToString(@"\:mm\:ss") : t.ToString(@"m\:ss");

        public static object Convert(object? value)
            => value is TimeSpan ts ? Convert(ts) : "";

        public static object ConvertBack(object? value) => throw new NotSupportedException("Two-way binding is not supported");
    }

    public static class PureBoolToVisibilityConverter
    {
        public static TestVisibility Convert(object? value)
            => value is true ? TestVisibility.Visible : TestVisibility.Collapsed;

        public static object ConvertBack(object? value) => throw new NotSupportedException();
    }

    public static class PureTrackNoFormatterConverter
    {
        public static object Convert(object? value)
        {
            if (value is int no && no > 0)
            {
                return no < 10 ? $"0{no}" : no.ToString();
            }
            return "-";
        }

        public static object ConvertBack(object? value) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(-500, "0:00")]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(3665, "1:01:05")]
    [InlineData(86400 + 3600 + 120, "25:02:00")]
    public void TimeSpanTextConverter_FormatsTimeSpanAccurately(int totalSeconds, string expected)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        Assert.Equal(expected, PureTimeSpanConverter.Convert(ts));
    }

    [Fact]
    public void BoolToVisibility_MapsValuesCorrectly()
    {
        Assert.Equal(TestVisibility.Visible, PureBoolToVisibilityConverter.Convert(true));
        Assert.Equal(TestVisibility.Collapsed, PureBoolToVisibilityConverter.Convert(false));
        Assert.Equal(TestVisibility.Collapsed, PureBoolToVisibilityConverter.Convert(null));
    }

    [Theory]
    [InlineData(0, "-")]
    [InlineData(-1, "-")]
    [InlineData(1, "01")]
    [InlineData(9, "09")]
    [InlineData(10, "10")]
    public void TrackNoFormatter_FormatsTwoDigitsOrDash(int trackNo, string expected)
    {
        Assert.Equal(expected, PureTrackNoFormatterConverter.Convert(trackNo));
    }
}
