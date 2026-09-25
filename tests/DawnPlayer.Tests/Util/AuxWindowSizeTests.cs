using System;
using System.Text.Json;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests.Util;

/// <summary>
/// Aux-window (lyrics search / editor) size persistence math: validation, DIP conversion,
/// clamping, and multi-cycle drift. Real implementation (Core.Util.AuxWindowSize), no window.
/// </summary>
public class AuxWindowSizeTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(800.0, null, false)]
    [InlineData(null, 600.0, false)]
    [InlineData(0.0, 600.0, false)]
    [InlineData(800.0, -1.0, false)]
    [InlineData(double.NaN, 600.0, false)]
    [InlineData(800.0, double.PositiveInfinity, false)]
    [InlineData(800.0, 600.0, true)]
    public void HasValidSize_AcceptsOnlyPositiveFinitePairs(double? w, double? h, bool expected)
    {
        Assert.Equal(expected, AuxWindowSize.HasValidSize(w, h));
    }

    [Theory]
    [InlineData(800.0, 600.0, 800.0, 600.0)]       // in range: identity
    [InlineData(100.0, 100.0, 480.0, 320.0)]       // below minimums
    [InlineData(99999.0, 99999.0, 3840.0, 2160.0)] // above maximums
    [InlineData(-50.0, 100000.0, 480.0, 2160.0)]   // hand-edited garbage both ends
    public void Clamp_KeepsSizeInsideGuardRails(double w, double h, double expW, double expH)
    {
        var (cw, ch) = AuxWindowSize.Clamp(w, h);
        Assert.Equal(expW, cw);
        Assert.Equal(expH, ch);
    }

    [Theory]
    [InlineData(800.0, 600.0, 1.0, 800, 600)]
    [InlineData(800.0, 600.0, 1.5, 1200, 900)]
    [InlineData(800.0, 600.0, 2.0, 1600, 1200)]
    [InlineData(800.0, 600.0, 0.0, 800, 600)]      // broken scale falls back to 1.0
    [InlineData(800.0, 600.0, double.NaN, 800, 600)]
    public void ToPhysical_ConvertsDipAtScale(double w, double h, double scale, int expW, int expH)
    {
        var (pw, ph) = AuxWindowSize.ToPhysical(w, h, scale);
        Assert.Equal(expW, pw);
        Assert.Equal(expH, ph);
    }

    [Theory]
    [InlineData(1200, 900, 1.5, 800.0, 600.0)]
    [InlineData(800, 600, 1.0, 800.0, 600.0)]
    [InlineData(800, 600, 0.0, 800.0, 600.0)]
    public void ToDip_ConvertsPhysicalAtScale(int wPx, int hPx, double scale, double expW, double expH)
    {
        var (w, h) = AuxWindowSize.ToDip(wPx, hPx, scale);
        Assert.Equal(expW, w, precision: 9);
        Assert.Equal(expH, h, precision: 9);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void SaveRestoreRoundTrip_DoesNotDrift(double scale)
    {
        // save(DIP->px) -> restore(px->DIP) x100, like shutdown/startup pairs across monitors.
        double w = 917.0, h = 633.0;
        for (int i = 0; i < 100; i++)
        {
            var (pw, ph) = AuxWindowSize.ToPhysical(w, h, scale);
            (w, h) = AuxWindowSize.ToDip(pw, ph, scale);
        }

        Assert.True(Math.Abs(w - 917) <= 1, $"width drifted to {w} at scale {scale}");
        Assert.True(Math.Abs(h - 633) <= 1, $"height drifted to {h} at scale {scale}");
    }

    [Fact]
    public void AuxWindowSizeFields_DefaultToNull()
    {
        var ui = new AppSettings().Ui;
        Assert.Null(ui.LyricsSearchWidth);
        Assert.Null(ui.LyricsSearchHeight);
        Assert.Null(ui.LyricsEditorWidth);
        Assert.Null(ui.LyricsEditorHeight);
    }

    [Fact]
    public void AuxWindowSizeFields_SurviveJsonRoundTrip()
    {
        var settings = new AppSettings();
        settings.Ui.LyricsSearchWidth = 900;
        settings.Ui.LyricsSearchHeight = 640;
        settings.Ui.LyricsEditorWidth = 1024;
        settings.Ui.LyricsEditorHeight = 700;

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(restored);
        Assert.Equal(900, restored.Ui.LyricsSearchWidth);
        Assert.Equal(640, restored.Ui.LyricsSearchHeight);
        Assert.Equal(1024, restored.Ui.LyricsEditorWidth);
        Assert.Equal(700, restored.Ui.LyricsEditorHeight);
    }
}
