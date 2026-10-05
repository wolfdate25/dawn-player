using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests.Controls;

/// <summary>
/// WinUI's built-in Slider thumb tooltip is unusable for us: its content is a DataContext
/// binding and the platform creates the ToolTip without ever giving it a DataContext
/// (dxaml Slider_Partial.cpp / ToolTip_Partial.cpp) — with the app's null DataContext the
/// converter is never invoked and a blank box floats above the thumb (2026-10-05 user report).
/// The seek and volume sliders therefore draw app-owned bubbles; these tests gate the pure
/// formats/geometry and the XAML wiring that must not regress to the dead platform tooltip.
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

    // ---------- pure geometry (BubbleLeft) ----------

    [Fact]
    public void BubbleLeft_CentersOnTheThumb()
    {
        // thumb center at fraction 0.5 of a 200px track = 6 + 0.5*188 = 100 → 100 - 40/2
        Assert.Equal(80.0, DawnPlayer.App.Controls.SliderThumbToolTipText.BubbleLeft(0.5, 200.0, 40.0));
    }

    [Fact]
    public void BubbleLeft_ClampsToTheTrackBounds()
    {
        // left edge: thumb center 6 would push the bubble to -14 → clamp to 0
        Assert.Equal(0.0, DawnPlayer.App.Controls.SliderThumbToolTipText.BubbleLeft(0.0, 200.0, 40.0));
        // right edge: thumb center 194 would overflow past 200-40 → clamp to 160
        Assert.Equal(160.0, DawnPlayer.App.Controls.SliderThumbToolTipText.BubbleLeft(1.0, 200.0, 40.0));
        // track narrower than the bubble: nothing can fit → pinned at 0
        Assert.Equal(0.0, DawnPlayer.App.Controls.SliderThumbToolTipText.BubbleLeft(0.5, 30.0, 40.0));
    }

    // ---------- XAML wiring (bubbles in, dead platform tooltip out) ----------

    [Fact]
    public void NowPlayingBar_Sliders_DisableBuiltInToolTip_AndCarryBubbles()
    {
        var xaml = ReadAppSource(Path.Combine("Controls", "NowPlayingBar.xaml"));

        foreach (var slider in new[] { "SeekSlider", "VolumeSlider" })
        {
            var tag = Regex.Match(xaml, $@"<Slider\b[^>]*?x:Name=""{slider}""[^>]*?>",
                RegexOptions.Singleline);
            Assert.True(tag.Success, $"NowPlayingBar.xaml must define <Slider x:Name=\"{slider}\">.");
            Assert.True(tag.Value.Contains("IsThumbToolTipEnabled=\"False\""),
                $"{slider} must disable WinUI's built-in thumb tooltip — with our null DataContext " +
                "it only ever renders an empty box.");
            Assert.True(!tag.Value.Contains("ThumbToolTipValueConverter="),
                $"{slider} must not feed ThumbToolTipValueConverter — the platform never invokes it " +
                "without a DataContext (dead API for this app).");
        }

        Assert.Contains("x:Name=\"SeekBubbleOverlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"VolumeBubbleOverlay\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void BubbleUpdaters_ReadThePureContracts_AndConverterShellsStayRemoved()
    {
        var codeBehind = ReadAppSource(Path.Combine("Controls", "NowPlayingBar.xaml.cs"));
        Assert.Contains("SliderThumbToolTipText.Time(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SliderThumbToolTipText.Percent(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SliderThumbToolTipText.BubbleLeft(", codeBehind, StringComparison.Ordinal);

        // The ThumbToolTipValueConverter shells from the first (unworkable) fix attempt must
        // stay deleted — the platform cannot call them without a DataContext.
        var converters = ReadAppSource(Path.Combine("Services", "Converters.cs"));
        Assert.True(!converters.Contains("ThumbToolTipValueConverter", StringComparison.Ordinal) &&
                    !converters.Contains("ThumbToolTip", StringComparison.Ordinal),
            "Converters.cs must not carry Slider thumb-tooltip converter shells — they are " +
            "unreachable without a DataContext; the bubbles in NowPlayingBar own the readout.");
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
