using System;
using DawnPlayer.Core.Audio;

namespace DawnPlayer.App.Controls;

/// <summary>Pixel geometry for the A-B overlay drawn over the seek slider. All values are
/// canvas coordinates relative to the slider's own box; consumers assign them straight to
/// Canvas.Left / Canvas.Width.</summary>
public sealed class AbRepeatOverlayGeometry
{
    /// <summary>False whenever nothing should be drawn (stage off, no track, slider too narrow).</summary>
    public bool Visible { get; init; }

    /// <summary>False when only the A marker has a meaningful place (no band to draw yet).</summary>
    public bool ShowBand { get; init; }

    /// <summary>True when the band is the WaitingForB preview (A → live position) rather than the loop band.</summary>
    public bool BandIsPreview { get; init; }

    public double BandLeft { get; init; }
    public double BandWidth { get; init; }
    public double MarkerALeft { get; init; }
    public double MarkerBLeft { get; init; }

    public static AbRepeatOverlayGeometry Hidden { get; } = new();
}

/// <summary>
/// Pure geometry for the A-B seekbar overlay, kept free of WinUI types so the mapping invariant
/// is unit testable: a band edge must land on the x where the seekbar thumb's <em>center</em>
/// sits at that time, or the drawn window lies about the enforced one. WinUI sliders reserve the
/// thumb width when mapping value → pixels, so the thumb center travels from
/// <see cref="ThumbWidth"/>/2 to trackWidth − <see cref="ThumbWidth"/>/2; this mirrors that.
/// </summary>
public static class AbRepeatOverlayCalculator
{
    /// <summary>The seekbar thumb's width (EoleSlimSliderStyle's HorizontalThumb).</summary>
    public const double ThumbWidth = 12.0;

    public static double FractionToX(double fraction, double trackWidth)
    {
        var usable = trackWidth - ThumbWidth;
        if (usable <= 0) return trackWidth / 2;
        return ThumbWidth / 2 + Math.Clamp(fraction, 0.0, 1.0) * usable;
    }

    public static AbRepeatOverlayGeometry Compute(
        AbRepeatStage stage, TimeSpan start, TimeSpan end, TimeSpan position, TimeSpan duration, double trackWidth)
    {
        if (stage == AbRepeatStage.Off || trackWidth <= ThumbWidth || duration <= TimeSpan.Zero)
            return AbRepeatOverlayGeometry.Hidden;

        var startFraction = Clamp01(start.TotalSeconds / duration.TotalSeconds);
        var markerA = FractionToX(startFraction, trackWidth);

        if (stage == AbRepeatStage.WaitingForB)
        {
            // Preview band from A to the live position; when playback has not passed A yet there
            // is nothing between A and the playhead to shade, so draw the marker alone.
            var positionFraction = Clamp01(position.TotalSeconds / duration.TotalSeconds);
            if (positionFraction <= startFraction)
                return new AbRepeatOverlayGeometry { Visible = true, ShowBand = false, MarkerALeft = markerA };
            return Band(markerA, FractionToX(positionFraction, trackWidth), markerA, markerA, preview: true);
        }

        var endFraction = Clamp01(end.TotalSeconds / duration.TotalSeconds);
        if (endFraction <= startFraction)
        {
            // Degenerate window (defensive: the controller refuses B <= A): markers only, never a
            // zero-width or inverted band.
            return new AbRepeatOverlayGeometry
            {
                Visible = true,
                ShowBand = false,
                MarkerALeft = markerA,
                MarkerBLeft = FractionToX(endFraction, trackWidth)
            };
        }

        return Band(markerA, FractionToX(endFraction, trackWidth), markerA, FractionToX(endFraction, trackWidth), preview: false);
    }

    /// <summary>Compact loop length for tooltips: one-decimal seconds under a minute, m:ss.d at
    /// and above. Language-neutral digits; the surrounding label carries the localization.</summary>
    public static string FormatLoopLength(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return "0";
        return span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 100}"
            : $"{span.TotalSeconds:0.#}";
    }

    private static AbRepeatOverlayGeometry Band(double left, double right, double markerA, double markerB, bool preview) =>
        new()
        {
            Visible = true,
            ShowBand = true,
            BandIsPreview = preview,
            BandLeft = left,
            BandWidth = Math.Max(0, right - left),
            MarkerALeft = markerA,
            MarkerBLeft = markerB
        };

    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);
}
