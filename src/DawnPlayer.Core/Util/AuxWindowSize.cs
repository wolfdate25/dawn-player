namespace DawnPlayer.Core.Util;

/// <summary>
/// WinUI-free size math for auxiliary windows (lyrics search / editor) whose size the user
/// resized: validation, DIP/physical-pixel conversion, and clamping. The App-side placement
/// helper delegates to these so the rules stay unit-testable without a window.
/// Sizes are persisted in DIPs (scale-independent), matching the main-window convention.
/// </summary>
public static class AuxWindowSize
{
    /// <summary>Guard rails for restored sizes (DIPs). Only garbage protection, not UX policy.</summary>
    public const double MinWidth = 480;
    public const double MinHeight = 320;
    public const double MaxWidth = 3840;
    public const double MaxHeight = 2160;

    /// <summary>True when both halves form a restorable size (present, finite, positive).</summary>
    public static bool HasValidSize(double? widthDip, double? heightDip) =>
        widthDip.HasValue && heightDip.HasValue
        && !double.IsNaN(widthDip.Value) && !double.IsNaN(heightDip.Value)
        && !double.IsInfinity(widthDip.Value) && !double.IsInfinity(heightDip.Value)
        && widthDip.Value > 0 && heightDip.Value > 0;

    /// <summary>Clamps a DIP size into the guard rails.</summary>
    public static (double Width, double Height) Clamp(double widthDip, double heightDip) =>
        (Math.Clamp(widthDip, MinWidth, MaxWidth), Math.Clamp(heightDip, MinHeight, MaxHeight));

    /// <summary>DIPs to physical pixels at the given scale (for AppWindow.ResizeClient).</summary>
    public static (int Width, int Height) ToPhysical(double widthDip, double heightDip, double scale)
    {
        if (!(scale > 0) || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;
        var (w, h) = Clamp(widthDip, heightDip);
        return ((int)Math.Round(w * scale), (int)Math.Round(h * scale));
    }

    /// <summary>Physical pixels to DIPs at the given scale (for persisting ClientSize).</summary>
    public static (double Width, double Height) ToDip(int widthPx, int heightPx, double scale)
    {
        if (!(scale > 0) || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;
        return (widthPx / scale, heightPx / scale);
    }
}
