namespace DawnPlayer.App.Helpers;

/// <summary>
/// Pure placement math extracted from <see cref="WindowPlacementHelper"/> so the invariant is
/// unit-testable without WinUI (same discipline as NavigationStateCalculator). The window may
/// never come back unreachable just because the monitor layout changed between save and
/// restore (2026-09-30 audit PT1-06).
/// </summary>
public static class WindowPlacementMath
{
    /// <summary>Pure clamp: keeps at least a 160×60 px sliver of the window inside the virtual
    /// screen so it can be grabbed and dragged back. Idempotent — clamping an already-clamped
    /// point returns it unchanged.</summary>
    public static (int X, int Y) ClampToVirtualScreen(
        int x, int y, int w, int h, int vx, int vy, int vw, int vh)
    {
        const int MinVisibleX = 160;
        const int MinVisibleY = 60;
        int maxX = vx + vw - MinVisibleX;   // left edge must sit left of the right border minus a visible sliver
        int minX = vx - w + MinVisibleX;    // the window's right must reach at least a sliver past the left border
        int maxY = vy + vh - MinVisibleY;
        int minY = vy - h + MinVisibleY;
        return (Math.Clamp(x, minX, maxX), Math.Clamp(y, minY, maxY));
    }
}
