using DawnPlayer.App.Calculators;
using DawnPlayer.App.Controls;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>U4 waveform geometry gates: fraction clamping, zero-duration handling, and
/// max-abs decimation (a thin display never hides the loudest peak of a group).</summary>
public class WaveformLayoutTests
{
    [Fact]
    public void FractionOfTrack_Clamps_And_HandlesZeroDuration()
    {
        Assert.Equal(0.0, WaveformLayout.FractionOfTrack(TimeSpan.Zero, TimeSpan.FromSeconds(100)));
        Assert.Equal(0.5, WaveformLayout.FractionOfTrack(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(100)));
        Assert.Equal(1.0, WaveformLayout.FractionOfTrack(TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(100)));
        Assert.Equal(0.0, WaveformLayout.FractionOfTrack(TimeSpan.FromSeconds(1), TimeSpan.Zero)); // streams
        Assert.Equal(0.0, WaveformLayout.FractionOfTrack(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void XFraction_Conversions_RoundTrip()
    {
        Assert.Equal(0.0, WaveformLayout.XToFraction(-5, 200));
        Assert.Equal(0.5, WaveformLayout.XToFraction(100, 200));
        Assert.Equal(1.0, WaveformLayout.XToFraction(500, 200)); // overflow clamps
        Assert.Equal(0.0, WaveformLayout.XToFraction(10, 0)); // degenerate width

        Assert.Equal(100.0, WaveformLayout.FractionToX(0.5, 200));
        Assert.Equal(0.0, WaveformLayout.FractionToX(-1, 200));
        Assert.Equal(200.0, WaveformLayout.FractionToX(2, 200));
    }

    [Fact]
    public void Decimate_PreservesGroupMaxAbs()
    {
        var peaks = new float[] { 0.1f, -0.9f, 0.2f, 0.0f, 0.5f, 0.3f, -0.4f, 0.6f };
        var bars = WaveformLayout.Decimate(peaks, 4);
        Assert.Equal(4, bars.Length);
        Assert.Equal(0.9f, bars[0]); // max-abs of {0.1, -0.9}
        Assert.Equal(0.2f, bars[1]);
        Assert.Equal(0.5f, bars[2]);
        Assert.Equal(0.6f, bars[3]);
    }

    [Fact]
    public void Decimate_Identity_WhenBarsExceedBuckets()
    {
        var peaks = new float[] { 0.25f, -0.5f, 0.75f };
        Assert.Same(peaks, WaveformLayout.Decimate(peaks, 8));
    }

    [Fact]
    public void Decimate_Rejects_BadArguments()
    {
        Assert.Throws<ArgumentNullException>(() => WaveformLayout.Decimate(null!, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveformLayout.Decimate(Array.Empty<float>(), 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveformLayout.Decimate(new float[] { 1f }, 0));
    }

    [Fact]
    public void BarCenterX_FillsWidthEvenly()
    {
        Assert.Equal(12.5, WaveformLayout.BarCenterX(0, 4, 100));
        Assert.Equal(87.5, WaveformLayout.BarCenterX(3, 4, 100));
    }
}
