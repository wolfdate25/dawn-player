using System.Runtime.InteropServices;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Persistence;
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
}
