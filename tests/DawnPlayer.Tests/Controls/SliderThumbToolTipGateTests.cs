using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests.Controls;

/// <summary>
/// WinUI's Slider shows its built-in tooltip above the thumb while dragging, but the tooltip
/// content is empty unless a ThumbToolTipValueConverter supplies text — the seek and volume
/// sliders dragged a blank box (2026-10-05 user report). These tests gate the pure tooltip
/// formats and the XAML wiring that feeds them.
/// </summary>
public sealed class SliderThumbToolTipGateTests
{
    // ---------- pure formats (SliderThumbToolTipText) ----------

    [Theory]
    [InlineData(0.0, "0:00")]
    [InlineData(75.0, "1:15")]
    [InlineData(3675.0, "1:01:15")]
    [InlineData(-5.0, "0:00")]
    public void Time_FormatsSeconds_AsPositionReadout(double seconds, string expected)
        => Assert.Equal(expected, DawnPlayer.App.Controls.SliderThumbToolTipText.Time(seconds));

    [Fact]
    public void Time_NaNOrInfinity_ClampsToZero()
    {
        Assert.Equal("0:00", DawnPlayer.App.Controls.SliderThumbToolTipText.Time(double.NaN));
        Assert.Equal("0:00", DawnPlayer.App.Controls.SliderThumbToolTipText.Time(double.PositiveInfinity));
    }

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(41.6, "42%")]
    [InlineData(120.0, "100%")]
    [InlineData(-3.0, "0%")]
    public void Percent_FormatsVolume_AsRoundedPercent(double value, string expected)
        => Assert.Equal(expected, DawnPlayer.App.Controls.SliderThumbToolTipText.Percent(value));

    // ---------- XAML wiring (both sliders must feed the built-in tooltip) ----------

    [Fact]
    public void NowPlayingBar_Sliders_CarryThumbToolTipValueConverters()
    {
        var xaml = ReadAppSource(Path.Combine("Controls", "NowPlayingBar.xaml"));

        var missing = new List<string>();
        foreach (var slider in new[] { "SeekSlider", "VolumeSlider" })
        {
            var tag = Regex.Match(xaml, $@"<Slider\b[^>]*?x:Name=""{slider}""[^>]*?>",
                RegexOptions.Singleline);
            Assert.True(tag.Success, $"NowPlayingBar.xaml must define <Slider x:Name=\"{slider}\">.");

            if (!tag.Value.Contains("ThumbToolTipValueConverter="))
            {
                missing.Add(slider);
            }
        }

        Assert.True(missing.Count == 0,
            "Slider(s) without a ThumbToolTipValueConverter drag an empty tooltip: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void ConverterShells_DelegateToThePureContracts()
    {
        var converters = ReadAppSource(Path.Combine("Services", "Converters.cs"));

        Assert.Contains("class SeekSecondsThumbToolTipConverter", converters, StringComparison.Ordinal);
        Assert.Contains("class VolumePercentThumbToolTipConverter", converters, StringComparison.Ordinal);
        Assert.Contains("SliderThumbToolTipText.Time(", converters, StringComparison.Ordinal);
        Assert.Contains("SliderThumbToolTipText.Percent(", converters, StringComparison.Ordinal);
    }

    /// <summary>Walks up from the test output directory to the checkout owning DawnPlayer.slnx.</summary>
    private static string ReadAppSource(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx")))
            {
                continue;
            }

            var path = Path.Combine(dir.FullName, "src", "DawnPlayer.App", relativePath);
            Assert.True(File.Exists(path), $"App source not found: {path}");
            return File.ReadAllText(path);
        }

        Assert.Fail("DawnPlayer.slnx not found above the test output directory; the source-scan gate needs a source checkout.");
        return "";
    }
}
