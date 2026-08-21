using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Calculators;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.ViewModels;

public sealed class EqVisualizerCalculatorTests
{
    [Fact]
    public void XFromFreq_BoundaryAndInvertibility_CalculatesCorrectly()
    {
        double plotWidth = 648.0;
        double padLeft = 36.0;

        // Min frequency 20 Hz maps to padLeft
        double xMin = EqVisualizerCalculator.XFromFreq(20.0, plotWidth, padLeft);
        Assert.Equal(padLeft, xMin, 3);

        // Max frequency 20000 Hz maps to padLeft + plotWidth
        double xMax = EqVisualizerCalculator.XFromFreq(20000.0, plotWidth, padLeft);
        Assert.Equal(padLeft + plotWidth, xMax, 3);

        // Out-of-bounds lower frequency is clamped to 20 Hz
        double xUnder = EqVisualizerCalculator.XFromFreq(5.0, plotWidth, padLeft);
        Assert.Equal(padLeft, xUnder, 3);

        // Out-of-bounds upper frequency is clamped to 20000 Hz
        double xOver = EqVisualizerCalculator.XFromFreq(40000.0, plotWidth, padLeft);
        Assert.Equal(padLeft + plotWidth, xOver, 3);

        // Mid frequency 1000 Hz (log mid is sqrt(20 * 20000) ~ 632.45 Hz, so 1000 Hz is slightly right of center)
        double x1k = EqVisualizerCalculator.XFromFreq(1000.0, plotWidth, padLeft);
        Assert.True(x1k > padLeft && x1k < padLeft + plotWidth);

        // Invertibility check: FreqFromX(XFromFreq(f)) == f
        double[] testFreqs = { 20, 50, 100, 440, 1000, 4000, 10000, 20000 };
        foreach (var f in testFreqs)
        {
            double x = EqVisualizerCalculator.XFromFreq(f, plotWidth, padLeft);
            double restoredFreq = EqVisualizerCalculator.FreqFromX(x, plotWidth, padLeft);
            Assert.Equal(f, restoredFreq, 1);
        }
    }

    [Fact]
    public void YFromDb_BoundaryAndLinearity_CalculatesCorrectly()
    {
        double plotHeight = 154.0;
        double padTop = 14.0;

        // +18 dB (MaxDb) maps to top (padTop)
        double yMax = EqVisualizerCalculator.YFromDb(18.0, plotHeight, padTop);
        Assert.Equal(padTop, yMax, 3);

        // -18 dB (MinDb) maps to bottom (padTop + plotHeight)
        double yMin = EqVisualizerCalculator.YFromDb(-18.0, plotHeight, padTop);
        Assert.Equal(padTop + plotHeight, yMin, 3);

        // 0 dB maps to center
        double yZero = EqVisualizerCalculator.YFromDb(0.0, plotHeight, padTop);
        Assert.Equal(padTop + (plotHeight / 2.0), yZero, 3);

        // Clamping check for extreme dB values
        double yOver = EqVisualizerCalculator.YFromDb(30.0, plotHeight, padTop);
        Assert.Equal(padTop, yOver, 3);

        double yUnder = EqVisualizerCalculator.YFromDb(-30.0, plotHeight, padTop);
        Assert.Equal(padTop + plotHeight, yUnder, 3);
    }

    [Fact]
    public void GetBandColorHex_ReturnsValidAndDistinctColors()
    {
        Assert.Equal(20, EqVisualizerCalculator.BandColors.Length);

        // All 20 colors start with '#' and are valid 7-char HEX
        for (int i = 0; i < 20; i++)
        {
            string hex = EqVisualizerCalculator.GetBandColorHex(i);
            Assert.StartsWith("#", hex);
            Assert.Equal(7, hex.Length);
        }

        // Modulo wrapping for indices >= 20 and negative indices
        Assert.Equal(EqVisualizerCalculator.GetBandColorHex(0), EqVisualizerCalculator.GetBandColorHex(20));
        Assert.Equal(EqVisualizerCalculator.GetBandColorHex(5), EqVisualizerCalculator.GetBandColorHex(25));
        Assert.Equal(EqVisualizerCalculator.GetBandColorHex(3), EqVisualizerCalculator.GetBandColorHex(-3));
    }

    [Fact]
    public void Calculate_NullOrDisabledProfile_ReturnsBaselineFlatCurve()
    {
        var dataNull = EqVisualizerCalculator.Calculate(null, 700, 190);
        Assert.NotNull(dataNull);
        Assert.False(dataNull.IsEnabled);
        Assert.Empty(dataNull.BandPins);
        Assert.Equal(120, dataNull.CurvePoints.Count);
        Assert.Equal(122, dataNull.FillPoints.Count);

        // All curve points for null profile should lie on 0 dB line
        double zeroY = dataNull.ZeroY;
        foreach (var pt in dataNull.CurvePoints)
        {
            Assert.Equal(zeroY, pt.Y, 2);
        }

        // Disabled profile should also produce flat curve
        var disabledProfile = new EqProfile
        {
            Enabled = false,
            PreampDb = -6.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = 10 }
            }
        };

        var dataDisabled = EqVisualizerCalculator.Calculate(disabledProfile, 700, 190);
        Assert.False(dataDisabled.IsEnabled);
        Assert.Empty(dataDisabled.BandPins);
        foreach (var pt in dataDisabled.CurvePoints)
        {
            Assert.Equal(zeroY, pt.Y, 2);
        }
    }

    [Fact]
    public void Calculate_ActiveProfileWithBands_ComputesCurveAndPins()
    {
        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = -3.0,
            Bands = new()
            {
                new EqBandSettings { Type = EqFilterType.LowShelf, FrequencyHz = 100, GainDb = 6.0, Q = 1.0 },
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1000, GainDb = -4.0, Q = 2.0 },
                new EqBandSettings { Type = EqFilterType.HighShelf, FrequencyHz = 10000, GainDb = 5.0, Q = 1.0 }
            }
        };

        var data = EqVisualizerCalculator.Calculate(profile, 700, 190);
        Assert.NotNull(data);
        Assert.True(data.IsEnabled);
        Assert.Equal(3, data.BandPins.Count);
        Assert.Equal(120, data.CurvePoints.Count);

        // Check grid lines
        Assert.Equal(5, data.HorizontalDbLines.Count); // +12, +6, 0, -6, -12
        Assert.Equal(9, data.VerticalFreqLines.Count); // 50, 100, 200, 500, 1k, 2k, 5k, 10k, 20k

        // Check band pin 0 (100 Hz, gain 6.0 dB + preamp -3.0 dB = 3.0 dB)
        var pin0 = data.BandPins[0];
        Assert.Equal(0, pin0.Index);
        Assert.Equal("밴드 1", pin0.DisplayNumber);
        Assert.Equal(EqVisualizerCalculator.GetBandColorHex(0), pin0.ColorHex);
        Assert.Equal(EqVisualizerCalculator.XFromFreq(100, data.PlotWidth, data.PadLeft), pin0.X, 2);
        Assert.Equal(EqVisualizerCalculator.YFromDb(3.0, data.PlotHeight, data.PadTop), pin0.Y, 2);

        // Check band pin 1 (1000 Hz, gain -4.0 dB + preamp -3.0 dB = -7.0 dB)
        var pin1 = data.BandPins[1];
        Assert.Equal(1, pin1.Index);
        Assert.Equal("밴드 2", pin1.DisplayNumber);
        Assert.Equal(EqVisualizerCalculator.XFromFreq(1000, data.PlotWidth, data.PadLeft), pin1.X, 2);
        Assert.Equal(EqVisualizerCalculator.YFromDb(-7.0, data.PlotHeight, data.PadTop), pin1.Y, 2);

        // Fill points start and end at baseline zeroY
        Assert.Equal(data.PadLeft, data.FillPoints[0].X, 2);
        Assert.Equal(data.ZeroY, data.FillPoints[0].Y, 2);
        Assert.Equal(data.PadLeft + data.PlotWidth, data.FillPoints[^1].X, 2);
        Assert.Equal(data.ZeroY, data.FillPoints[^1].Y, 2);
    }

    [Fact]
    public void Calculate_Maximum20Bands_Generates20DistinctPins()
    {
        var bands = new List<EqBandSettings>();
        for (int i = 0; i < 20; i++)
        {
            bands.Add(new EqBandSettings
            {
                Type = EqFilterType.PeakEq,
                FrequencyHz = 50 * (i + 1),
                GainDb = (i % 2 == 0) ? 3.0 : -3.0,
                Q = 1.4
            });
        }

        var profile = new EqProfile
        {
            Enabled = true,
            PreampDb = 0.0,
            Bands = bands
        };

        var data = EqVisualizerCalculator.Calculate(profile, 800, 200);
        Assert.Equal(20, data.BandPins.Count);

        var distinctColors = data.BandPins.Select(p => p.ColorHex).Distinct().ToList();
        Assert.Equal(20, distinctColors.Count);
    }

    [Fact]
    public void Calculate_SmallCanvasFallback_DoesNotThrow()
    {
        var profile = new EqProfile { Enabled = true, PreampDb = 0.0 };

        // Test with 0 or negative dimensions
        var data0 = EqVisualizerCalculator.Calculate(profile, 0, 0);
        Assert.NotNull(data0);
        Assert.True(data0.PlotWidth > 0);
        Assert.True(data0.PlotHeight > 0);

        var dataSmall = EqVisualizerCalculator.Calculate(profile, 10, 10);
        Assert.NotNull(dataSmall);
    }
}
