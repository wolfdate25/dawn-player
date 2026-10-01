using System.Runtime.InteropServices;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DawnPlayer.App.Helpers;

/// <summary>
/// Handles DPI scale calculation and window placement (size, position, maximized state) restoration and persistence.
/// </summary>
public static class WindowPlacementHelper
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>
    /// Computes the DPI scale factor for a given window handle. Falls back to XamlRoot rasterization scale or 1.0.
    /// </summary>
    public static double GetDpiScale(IntPtr hwnd, XamlRoot? xamlRoot = null)
    {
        try
        {
            if (hwnd != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) return dpi / 96.0;
            }
        }
        catch { }

        return xamlRoot?.RasterizationScale ?? 1.0;
    }

    /// <summary>
    /// Restores window placement (position, size, maximized state) from UiSettings.
    /// </summary>
    public static void RestorePlacement(Window window, UiSettings ui, IntPtr hwnd)
    {
        if (ui.WindowX.HasValue && ui.WindowY.HasValue)
        {
            // Clamp against the virtual desktop (PT1-06): restoring a saved position after a
            // monitor was unplugged used to move the window fully off-screen, which read as
            // "the app no longer starts".
            var (vx, vy, vw, vh) = GetVirtualScreenBounds();
            var size = EstimateRestoredSize(ui, hwnd);
            var (x, y) = ClampToVirtualScreen(ui.WindowX.Value, ui.WindowY.Value, size.W, size.H, vx, vy, vw, vh);
            window.AppWindow.Move(new PointInt32(x, y));
        }

        if (ui.WindowMaximized && window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
            return;
        }

        double scale = GetDpiScale(hwnd, window.Content?.XamlRoot);
        int w = Math.Clamp((int)Math.Round(ui.WindowWidth * scale), 760, 3840);
        int h = Math.Clamp((int)Math.Round(ui.WindowHeight * scale), 520, 2160);
        window.AppWindow.ResizeClient(new SizeInt32(w, h));
    }

    private static (int X, int Y, int W, int H) GetVirtualScreenBounds()
    {
        try
        {
            var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (w > 0 && h > 0) return (x, y, w, h);
        }
        catch { }
        return (0, 0, 1920, 1080);
    }

    private static (int W, int H) EstimateRestoredSize(UiSettings ui, IntPtr hwnd)
    {
        // The clamp runs before ResizeClient, so the eventual size is estimated from the saved
        // DIP size at the current DPI — close enough to keep the window reachable.
        double scale = GetDpiScale(hwnd, null);
        int w = Math.Clamp((int)Math.Round(ui.WindowWidth * scale), 760, 3840);
        int h = Math.Clamp((int)Math.Round(ui.WindowHeight * scale), 520, 2160);
        return (w, h);
    }

    /// <summary>Pure clamp lives in <see cref="WindowPlacementMath"/> (unit-tested); this shim
    /// keeps the restore call site readable.</summary>
    internal static (int X, int Y) ClampToVirtualScreen(
        int x, int y, int w, int h, int vx, int vy, int vw, int vh) =>
        WindowPlacementMath.ClampToVirtualScreen(x, y, w, h, vx, vy, vw, vh);

    /// <summary>
    /// Saves current window placement (position, size, maximized state) into UiSettings.
    /// </summary>
    public static void SavePlacement(Window window, UiSettings ui, IntPtr hwnd)
    {
        try
        {
            ui.WindowMaximized = window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            if (!ui.WindowMaximized)
            {
                double scale = GetDpiScale(hwnd, window.Content?.XamlRoot);
                var client = window.AppWindow.ClientSize;
                ui.WindowWidth = client.Width / scale;
                ui.WindowHeight = client.Height / scale;
                var pos = window.AppWindow.Position;
                ui.WindowX = pos.X;
                ui.WindowY = pos.Y;
            }
            SettingsWriter.Schedule(AppServices.Settings);
        }
        catch { }
    }

    /// <summary>
    /// Restores a user-resized auxiliary window size (DIPs). No-op when never resized.
    /// Position and maximized state are intentionally untouched (size-only request).
    /// </summary>
    public static bool TryRestoreWindowSize(Window window, double? widthDip, double? heightDip, IntPtr hwnd)
    {
        try
        {
            if (!AuxWindowSize.HasValidSize(widthDip, heightDip)) return false;
            double scale = GetDpiScale(hwnd, window.Content?.XamlRoot);
            var (w, h) = AuxWindowSize.ToPhysical(widthDip!.Value, heightDip!.Value, scale);
            window.AppWindow.ResizeClient(new SizeInt32(w, h));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Captures the current auxiliary window size in DIPs. Skipped (false) when maximized —
    /// persisting the fullscreen size as the restored normal size would be wrong.
    /// </summary>
    public static bool TrySaveWindowSize(Window window, IntPtr hwnd, out double widthDip, out double heightDip)
    {
        widthDip = 0;
        heightDip = 0;
        try
        {
            if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
                return false;
            double scale = GetDpiScale(hwnd, window.Content?.XamlRoot);
            var client = window.AppWindow.ClientSize;
            (widthDip, heightDip) = AuxWindowSize.ToDip(client.Width, client.Height, scale);
            return AuxWindowSize.HasValidSize(widthDip, heightDip);
        }
        catch { return false; }
    }
}
