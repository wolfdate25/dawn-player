namespace DawnPlayer.App.Helpers;

/// <summary>
/// Mini-mode window geometry — the single source of the mini constants, pure so the DPI
/// scaling invariant is unit-testable without WinUI (same discipline as
/// <see cref="WindowPlacementMath"/>). History: ToggleMiniMode resized the window to a
/// hard-coded physical 500×104, which at 150% scale laid the bar out at 333×69 logical and
/// clipped the controls off the window (2026-10-08 defect), and left row 0 at zero height so
/// the system caption buttons (Standard = 32px, still drawn under
/// ExtendsContentIntoTitleBar with the TitleBar control collapsed) floated on top of the
/// seek row's remaining-time text. The design is logical now: a 32px caption gutter hosts
/// the caption buttons, the 96px bar sits below it, and the client size is scaled by the
/// live DPI at entry time.
/// </summary>
public static class MiniPlayerPlacement
{
    /// <summary>Compact-bar natural minimum with the volume slider shown ≈528 logical px
    /// (padding 24 + art column 52 + transport 276 + tools 176) plus title slack.</summary>
    public const double LogicalWidth = 600;

    /// <summary>Caption gutter + bar, exact fit — the caption strip may never overlap the
    /// bar (the gate MiniModeBehaviorTests.MiniGeometry_CaptionNeverOverlapsBar pins it).</summary>
    public const double LogicalHeight = CaptionGutterHeight + BarLogicalHeight;

    /// <summary>System caption height at TitleBarHeightOption.Standard — the only caption
    /// heights are 32/48, and Tall would eat half the mini window.</summary>
    public const double CaptionGutterHeight = 32;

    /// <summary>NowPlayingBar MinHeight contract (UserControl MinHeight=96, pinned by
    /// MiniModeBehaviorTests and the bar XAML).</summary>
    public const double BarLogicalHeight = 96;

    /// <summary>Physical-px width floor while mini — below it the Compact bar starts
    /// clipping controls. Strictly below the normal 620 or "mini" stops being mini.</summary>
    public const int MiniPreferredMinWidth = 420;

    /// <summary>Scales the logical mini design to physical client pixels for AppWindow.
    /// AppWindow sizes are physical; round-trip error stays within 0.5 physical px.</summary>
    public static (int Width, int Height) PhysicalSize(double scale) =>
        ((int)Math.Round(LogicalWidth * scale), (int)Math.Round(LogicalHeight * scale));
}
