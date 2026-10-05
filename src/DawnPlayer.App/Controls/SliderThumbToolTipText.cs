using System;

namespace DawnPlayer.App.Controls;

/// <summary>
/// Pure contracts for the content of the Slider built-in drag tooltip (the floating box above
/// the thumb while dragging). WinUI shows that tooltip by default but leaves its content empty
/// unless a <c>ThumbToolTipValueConverter</c> supplies text — without one both the seek and the
/// volume sliders dragged a blank box (2026-10-05 user report). The WinUI converter shells in
/// Services/Converters.cs delegate here; this file is headless so the formats stay test-gated.
/// </summary>
public static class SliderThumbToolTipText
{
    /// <summary>Slider value in seconds → "m:ss" / "h:mm:ss" — the seek thumb readout. The
    /// slider carries seconds while a track is loaded (Maximum = duration); garbage input
    /// clamps to 0:00 so the tooltip can never render a nonsense or blank time.</summary>
    public static string Time(double seconds) =>
        SeekbarScrubbingCalculator.FormatTime(TimeSpan.FromSeconds(
            double.IsFinite(seconds) ? Math.Max(0.0, seconds) : 0.0));

    /// <summary>Slider value (0-100) → "42%" — the volume thumb readout.</summary>
    public static string Percent(double value) =>
        $"{Math.Round(double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : 0.0):0}%";
}
