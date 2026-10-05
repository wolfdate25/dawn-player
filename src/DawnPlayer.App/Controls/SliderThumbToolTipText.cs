using System;

namespace DawnPlayer.App.Controls;

/// <summary>
/// Pure contracts for the app-owned drag readout that floats above a slider thumb. WinUI's
/// built-in thumb tooltip is unusable for us: its content is a DataContext binding and the
/// ToolTip never receives a DataContext (Slider_Partial.cpp creates it without one and the
/// popup tree does not inherit the target's — with a null DataContext the converter is never
/// invoked, which is the empty box the 2026-10-05 report captured). The bubble is drawn by
/// NowPlayingBar instead and formats through this file; headless so the formats stay
/// test-gated.
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

    /// <summary>Bubble left so its <em>center</em> sits on the thumb center, clamped to the track
    /// bounds so the bubble can never hang off the slider edge. Reuses the A-B overlay's
    /// thumb-center mapping (thumb travels from ThumbWidth/2 to trackWidth − ThumbWidth/2).</summary>
    public static double BubbleLeft(double fraction, double trackWidth, double bubbleWidth)
    {
        var thumbX = AbRepeatOverlayCalculator.FractionToX(fraction, trackWidth);
        var maxLeft = Math.Max(0.0, trackWidth - bubbleWidth);
        return Math.Clamp(thumbX - bubbleWidth / 2.0, 0.0, maxLeft);
    }
}
