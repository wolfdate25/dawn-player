using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Localization;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Calculators;

/// <summary>
/// Representation of a horizontal dB or vertical frequency grid line with coordinate and display label.
/// </summary>
public sealed record GridLineData(double Position, double Value, string? Label);

/// <summary>
/// 2D geometric coordinate point for rendering paths and polylines.
/// </summary>
public sealed record PointData(double X, double Y);

/// <summary>
/// Coordinate, palette color, and metadata for an equalizer band pin node.
/// </summary>
public sealed record BandPinData(int Index, double X, double Y, string ColorHex, string DisplayNumber);

/// <summary>
/// Complete calculated layout snapshot for the parametric equalizer visualizer graph.
/// Pure C# model containing zero dependencies on WinUI or XAML elements.
/// </summary>
public sealed record EqVisualizerData(
    IReadOnlyList<GridLineData> HorizontalDbLines,
    IReadOnlyList<GridLineData> VerticalFreqLines,
    IReadOnlyList<PointData> CurvePoints,
    IReadOnlyList<PointData> FillPoints,
    IReadOnlyList<BandPinData> BandPins,
    bool IsEnabled,
    double ZeroY,
    double PlotWidth,
    double PlotHeight,
    double PadLeft,
    double PadTop);

/// <summary>
/// Pure mathematical calculator for parametric equalizer visualizer coordinates,
/// logarithmic frequency mapping, decibel scales, response sampling, and band node pin placement.
/// </summary>
public static class EqVisualizerCalculator
{
    public const double DefaultPadLeft = 36.0;
    public const double DefaultPadRight = 16.0;
    public const double DefaultPadTop = 14.0;
    public const double DefaultPadBottom = 22.0;

    public const double MinFreqHz = 20.0;
    public const double MaxFreqHz = 20000.0;
    public const double MinDb = -18.0;
    public const double MaxDb = 18.0;

    private static readonly double LogMin = Math.Log10(MinFreqHz);
    private static readonly double LogMax = Math.Log10(MaxFreqHz);
    private static readonly double LogRange = LogMax - LogMin;
    private static readonly double DbRange = MaxDb - MinDb;

    public static readonly string[] BandColors = new string[]
    {
        "#FF5E5E", // 1. Coral Red
        "#FF9500", // 2. Orange
        "#FFCC00", // 3. Amber Yellow
        "#A3E635", // 4. Lime Green
        "#10B981", // 5. Emerald Green
        "#14B8A6", // 6. Teal
        "#06B6D4", // 7. Cyan
        "#0EA5E9", // 8. Sky Blue
        "#3B82F6", // 9. Azure Blue
        "#6366F1", // 10. Indigo
        "#8B5CF6", // 11. Violet
        "#A855F7", // 12. Purple
        "#D946EF", // 13. Fuchsia
        "#EC4899", // 14. Pink
        "#F43F5E", // 15. Rose
        "#34D399", // 16. Mint
        "#38BDF8", // 17. Ice Blue
        "#EAB308", // 18. Golden
        "#E11D48", // 19. Crimson
        "#2DD4BF"  // 20. Turquoise
    };

    public static string GetBandColorHex(int index)
    {
        return BandColors[Math.Abs(index) % BandColors.Length];
    }

    /// <summary>
    /// Computes X canvas coordinate from frequency in Hz using logarithmic scale.
    /// </summary>
    public static double XFromFreq(double freq, double plotWidth, double padLeft = DefaultPadLeft)
    {
        double f = double.IsFinite(freq) ? Math.Clamp(freq, MinFreqHz, MaxFreqHz) : MinFreqHz;
        double pw = double.IsFinite(plotWidth) && plotWidth > 0 ? plotWidth : 10.0;
        double pl = double.IsFinite(padLeft) ? padLeft : DefaultPadLeft;
        return pl + pw * (Math.Log10(f) - LogMin) / LogRange;
    }

    /// <summary>
    /// Computes frequency in Hz from X canvas coordinate using logarithmic scale.
    /// </summary>
    public static double FreqFromX(double x, double plotWidth, double padLeft = DefaultPadLeft)
    {
        if (!double.IsFinite(x) || !double.IsFinite(plotWidth) || plotWidth <= 0) return MinFreqHz;
        double pl = double.IsFinite(padLeft) ? padLeft : DefaultPadLeft;
        double ratio = Math.Clamp((x - pl) / plotWidth, 0.0, 1.0);
        return Math.Pow(10.0, LogMin + ratio * LogRange);
    }

    /// <summary>
    /// Computes Y canvas coordinate from decibels (inverted Y-axis: higher dB = smaller Y).
    /// </summary>
    public static double YFromDb(double db, double plotHeight, double padTop = DefaultPadTop)
    {
        double clamped = double.IsFinite(db) ? Math.Clamp(db, MinDb, MaxDb) : 0.0;
        double ph = double.IsFinite(plotHeight) && plotHeight > 0 ? plotHeight : 10.0;
        double pt = double.IsFinite(padTop) ? padTop : DefaultPadTop;
        return pt + ph * (MaxDb - clamped) / DbRange;
    }

    /// <summary>
    /// Calculates complete visualizer geometry and grid data for the given profile and canvas dimensions.
    /// </summary>
    public static EqVisualizerData Calculate(
        EqProfile? profile,
        double width,
        double height,
        double padLeft = DefaultPadLeft,
        double padRight = DefaultPadRight,
        double padTop = DefaultPadTop,
        double padBottom = DefaultPadBottom,
        int sampleCount = 120)
    {
        double effectiveWidth = double.IsFinite(width) && width > 50 ? width : 700;
        double effectiveHeight = double.IsFinite(height) && height > 50 ? height : 190;

        double pLeft = double.IsFinite(padLeft) && padLeft >= 0 ? padLeft : DefaultPadLeft;
        double pRight = double.IsFinite(padRight) && padRight >= 0 ? padRight : DefaultPadRight;
        double pTop = double.IsFinite(padTop) && padTop >= 0 ? padTop : DefaultPadTop;
        double pBottom = double.IsFinite(padBottom) && padBottom >= 0 ? padBottom : DefaultPadBottom;

        double plotW = Math.Max(10.0, effectiveWidth - pLeft - pRight);
        double plotH = Math.Max(10.0, effectiveHeight - pTop - pBottom);

        // 1. Horizontal dB Grid Lines
        var dbLevels = new double[] { 12, 6, 0, -6, -12 };
        var hLines = new List<GridLineData>(dbLevels.Length);
        foreach (var db in dbLevels)
        {
            double y = YFromDb(db, plotH, pTop);
            string label = db > 0 ? $"+{db}" : $"{db}";
            hLines.Add(new GridLineData(y, db, label));
        }

        // 2. Vertical Frequency Grid Lines
        var freqDefs = new (double Freq, string? Label)[]
        {
            (50, null),
            (100, "100"),
            (200, null),
            (500, "500"),
            (1000, "1k"),
            (2000, null),
            (5000, "5k"),
            (10000, "10k"),
            (20000, "20k")
        };

        var vLines = new List<GridLineData>(freqDefs.Length);
        foreach (var (f, lbl) in freqDefs)
        {
            double x = XFromFreq(f, plotW, pLeft);
            vLines.Add(new GridLineData(x, f, lbl));
        }

        double zeroY = YFromDb(0.0, plotH, pTop);

        int count = Math.Max(2, sampleCount);
        var sampleFreqs = new double[count];
        var xCoords = new double[count];
        for (int i = 0; i < count; i++)
        {
            double x = pLeft + (plotW * i / (count - 1));
            xCoords[i] = x;
            sampleFreqs[i] = FreqFromX(x, plotW, pLeft);
        }

        bool isEnabled = profile?.Enabled == true;
        var dbResponse = EqFrequencyResponseCalculator.CalculateResponse(profile, sampleFreqs);

        // 3. Curve points & Fill points
        var curvePoints = new List<PointData>(count);
        var fillPoints = new List<PointData>(count + 2)
        {
            new(pLeft, zeroY)
        };

        for (int i = 0; i < count; i++)
        {
            double respDb = i < dbResponse.Length && double.IsFinite(dbResponse[i]) ? dbResponse[i] : 0.0;
            double y = YFromDb(respDb, plotH, pTop);
            var pt = new PointData(xCoords[i], y);
            curvePoints.Add(pt);
            fillPoints.Add(pt);
        }

        fillPoints.Add(new PointData(pLeft + plotW, zeroY));

        // 4. Band node pins
        var bandPins = new List<BandPinData>();
        if (profile?.Enabled == true && profile.Bands != null)
        {
            for (int i = 0; i < profile.Bands.Count; i++)
            {
                var b = profile.Bands[i];
                double bx = XFromFreq(b.FrequencyHz, plotW, pLeft);
                double preamp = double.IsFinite(profile.PreampDb) ? profile.PreampDb : 0.0;
                double gain = double.IsFinite(b.GainDb) ? b.GainDb : 0.0;
                double by = YFromDb(gain + preamp, plotH, pTop);
                string colorHex = GetBandColorHex(i);
                bandPins.Add(new BandPinData(i, bx, by, colorHex, AppStrings.Format("Settings_Eq_BandFormat", "밴드 {0}", i + 1)));
            }
        }

        return new EqVisualizerData(
            HorizontalDbLines: hLines,
            VerticalFreqLines: vLines,
            CurvePoints: curvePoints,
            FillPoints: fillPoints,
            BandPins: bandPins,
            IsEnabled: isEnabled,
            ZeroY: zeroY,
            PlotWidth: plotW,
            PlotHeight: plotH,
            PadLeft: pLeft,
            PadTop: pTop);
    }
}
