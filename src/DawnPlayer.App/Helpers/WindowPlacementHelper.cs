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
            window.AppWindow.Move(new PointInt32(ui.WindowX.Value, ui.WindowY.Value));
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
