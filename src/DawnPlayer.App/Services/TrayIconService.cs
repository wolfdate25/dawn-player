using System;
using System.Runtime.InteropServices;
using DawnPlayer.App.Localization;

namespace DawnPlayer.App.Services;

/// <summary>
/// Notification-area (tray) icon with a Win32 context menu and taskbar thumbnail toolbar
/// buttons. Pure interop: a message-only window owned by the UI thread receives the tray
/// callbacks, and the main window is subclassed to receive the thumbnail toolbar's
/// WM_COMMAND messages. Everything runs on the UI thread by construction — the message pump
/// of a WinUI dispatcher serves both windows.
/// </summary>
internal static class TrayIconService
{
    private const uint WM_APP_TRAY = 0x8000 + 0x10;             // WM_APP-based callback
    private const uint WM_COMMAND = 0x0111;
    private const int TrayIconId = 0x5100;
    // Thumbnail toolbar command ids (delivered as WM_COMMAND to the subclassed main window).
    private const int ThumbPrevCmd = 0x5201;
    private const int ThumbPlayCmd = 0x5202;
    private const int ThumbNextCmd = 0x5203;
    private const int MenuPlayPause = 1;
    private const int MenuStop = 2;
    private const int MenuPrev = 3;
    private const int MenuNext = 4;
    private const int MenuShow = 5;
    private const int MenuExit = 6;

    private static readonly WndProcDelegate TrayWndProc = OnWndProc;   // rooted: native keeps this pointer
    private static readonly SubclassProc MainWndSubclassProc = OnMainWndProc;
    private static IntPtr _trayHwnd = IntPtr.Zero;
    private static ushort _taskbarCreatedMsg;
    private static IntPtr _appIcon;         // tray icon
    private static IntPtr _iconPrev;
    private static IntPtr _iconPlay;
    private static IntPtr _iconPause;
    private static IntPtr _iconNext;
    private static ITaskbarList3? _taskbar;
    private static bool _thumbButtonsAdded;
    private static bool _lastKnownPlaying;

    public static bool IsRunning => _trayHwnd != IntPtr.Zero;

    /// <summary>True while the main window is hidden in the tray, so a settings disable can bring
    /// the window back before the icon (and the only way to reach it) goes away.</summary>
    public static bool IsWindowHidden { get; private set; }

    // ---------------- lifecycle ----------------

    /// <summary>Creates the tray icon (idempotent). Must be called on the UI thread after the
    /// main window exists.</summary>
    public static void EnsureCreated()
    {
        if (IsRunning) return;
        if (App.MainWin == null) return;

        _appIcon = LoadIconFromFile();
        if (_appIcon == IntPtr.Zero) return; // no icon, no tray

        _trayHwnd = CreateMessageWindow();
        if (_trayHwnd == IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_appIcon);
            _appIcon = IntPtr.Zero;
            return;
        }

        if (!AddTrayIcon()) RemoveAll();

        // Thumbnail toolbar on the main window: buttons appear in the taskbar preview.
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarListCoClass();
            _taskbar.HrInit();
            _iconPrev = DrawGlyphIcon("\uE76B");
            _iconPlay = DrawGlyphIcon("\uE768");
            _iconPause = DrawGlyphIcon("\uE769");
            _iconNext = DrawGlyphIcon("\uE76C");
            NativeMethods.SetWindowSubclass(AppServices.MainWindowHandle, MainWndSubclassProc, TrayIconId, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            App.Log($"[tray] thumbbar init failed: {ex.Message}");
            _taskbar = null;
        }

        AppServices.CurrentTrackChanged += OnTrackChanged;
        AppServices.PlaybackStateChanged += OnPlaybackStateChanged;
        UpdateThumbButtons();
        UpdateTooltip(null);
    }

    /// <summary>Removes the tray icon and subclassing. Safe to call when not running.</summary>
    public static void Destroy()
    {
        if (!IsRunning)
        {
            return;
        }

        AppServices.CurrentTrackChanged -= OnTrackChanged;
        AppServices.PlaybackStateChanged -= OnPlaybackStateChanged;

        var nid = new NOTIFYICONDATAW { cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(), uID = TrayIconId, hWnd = _trayHwnd };
        _ = NativeMethods.Shell_NotifyIcon(2 /* NIM_DELETE */, ref nid);

        if (AppServices.MainWindowHandle != IntPtr.Zero)
        {
            NativeMethods.RemoveWindowSubclass(AppServices.MainWindowHandle, MainWndSubclassProc, TrayIconId);
        }
        NativeMethods.DestroyWindow(_trayHwnd);
        _trayHwnd = IntPtr.Zero;
        _taskbar = null;
        _thumbButtonsAdded = false;

        foreach (var icon in new[] {_appIcon, _iconPrev, _iconPlay, _iconPause, _iconNext})
        {
            if (icon != IntPtr.Zero) NativeMethods.DestroyIcon(icon);
        }
        _appIcon = _iconPrev = _iconPlay = _iconPause = _iconNext = IntPtr.Zero;
    }

    // ---------------- window operations ----------------

    /// <summary>Hides the main window into the tray (close-to-tray path).</summary>
    public static void HideToTray()
    {
        if (App.MainWin == null) return;
        NativeMethods.ShowWindow(AppServices.MainWindowHandle, SW_HIDE);
        IsWindowHidden = true;
        UpdateTooltip(null);
    }

    /// <summary>Shows and activates the main window (tray double-click / "open").</summary>
    public static void RestoreFromTray()
    {
        if (App.MainWin == null) return;
        if (NativeMethods.IsIconic(AppServices.MainWindowHandle))
        {
            NativeMethods.ShowWindow(AppServices.MainWindowHandle, SW_RESTORE);
        }
        else
        {
            NativeMethods.ShowWindow(AppServices.MainWindowHandle, SW_SHOW);
        }
        _ = NativeMethods.SetForegroundWindow(AppServices.MainWindowHandle);
        App.MainWin.Activate();
        IsWindowHidden = false;
    }

    // ---------------- events (UI thread via AppServices marshaling) ----------------

    private static void OnTrackChanged(Core.Models.PlaylistItem? item) => UpdateTooltip(item?.Track);

    private static void OnPlaybackStateChanged()
    {
        bool playing = AppServices.Playback.State == Core.Audio.PlaybackState.Playing;
        if (playing == _lastKnownPlaying) return;
        _lastKnownPlaying = playing;
        UpdateThumbButtons();
    }

    /// <summary>Sets the tray tooltip; a track shows "Dawn Player — artist — title".</summary>
    public static void UpdateTooltip(Core.Models.Track? track)
    {
        if (!IsRunning) return;
        var text = track == null
            ? AppStrings.Get("Tray_TooltipIdle", "Dawn Player")
            : $"Dawn Player — {track.Artist} — {track.Title}";
        if (text.Length > 127) text = text[..127];

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _trayHwnd,
            uID = TrayIconId,
            hIcon = _appIcon,
            szTip = text,
        };
        _ = NativeMethods.Shell_NotifyIcon(1 /* NIM_MODIFY */, ref nid);
    }

    // ---------------- native plumbing ----------------

    private static IntPtr CreateMessageWindow()
    {
        var className = "DawnPlayer.TrayWnd";
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(TrayWndProc),
            lpszClassName = className,
            hInstance = NativeMethods.GetModuleHandle(null),
        };
        _ = NativeMethods.RegisterClassW(ref wc); // ERROR_CLASS_ALREADY_EXISTS on a second run is fine
        return NativeMethods.CreateWindowExW(
            0, className, string.Empty, 0, 0, 0, 0, 0,
            new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private static bool AddTrayIcon()
    {
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _trayHwnd,
            uID = TrayIconId,
            uFlags = 0x1 /* NIF_MESSAGE */ | 0x2 /* NIF_ICON */ | 0x4 /* NIF_TIP */,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _appIcon,
            szTip = AppStrings.Get("Tray_TooltipIdle", "Dawn Player"),
        };
        if (!NativeMethods.Shell_NotifyIcon(0 /* NIM_ADD */, ref nid))
        {
            return false;
        }

        // Re-add the icon if explorer restarts and recreates the taskbar.
        if (_taskbarCreatedMsg == 0)
        {
            _taskbarCreatedMsg = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        }
        return true;
    }

    private static void RemoveAll()
    {
        NativeMethods.DestroyWindow(_trayHwnd);
        _trayHwnd = IntPtr.Zero;
        NativeMethods.DestroyIcon(_appIcon);
        _appIcon = IntPtr.Zero;
    }

    private static IntPtr OnWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            // Explorer restarted: the old icon is gone, re-register it and the thumbbar.
            _thumbButtonsAdded = false;
            if (!AddTrayIcon()) RemoveAll();
            UpdateThumbButtons();
            return IntPtr.Zero;
        }

        if (msg != WM_APP_TRAY)
        {
            return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        switch ((uint)((long)lParam & 0xFFFF)) // low word of lParam
        {
            case 0x0203: // WM_LBUTTONDBLCLK
                RestoreFromTray();
                break;

            case 0x0205: // WM_RBUTTONUP
                ShowContextMenu();
                break;
        }
        return IntPtr.Zero;
    }

    private static void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        var playing = AppServices.Playback.State == Core.Audio.PlaybackState.Playing;
        string playPauseLabel = playing
            ? AppStrings.Get("Tray_Pause", "일시정지")
            : AppStrings.Get("Tray_Play", "재생");
        NativeMethods.AppendMenuW(menu, 0, MenuPlayPause, playPauseLabel);
        NativeMethods.AppendMenuW(menu, 0, MenuStop, AppStrings.Get("Tray_Stop", "정지"));
        NativeMethods.AppendMenuW(menu, 0, MenuPrev, AppStrings.Get("Tray_Previous", "이전 트랙"));
        NativeMethods.AppendMenuW(menu, 0, MenuNext, AppStrings.Get("Tray_Next", "다음 트랙"));
        NativeMethods.AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        NativeMethods.AppendMenuW(menu, 0, MenuShow, AppStrings.Get("Tray_Open", "창 열기"));
        NativeMethods.AppendMenuW(menu, 0, MenuExit, AppStrings.Get("Tray_Exit", "종료"));

        // SetForegroundWindow before TrackPopupMenu is the documented recipe so the menu also
        // dismisses when the user clicks elsewhere.
        _ = NativeMethods.SetForegroundWindow(_trayHwnd);
        NativeMethods.GetCursorPos(out var pt);
        int cmd = NativeMethods.TrackPopupMenu(menu, 0x100 /*TPM_RETURNCMD*/ | 0x2 /*TPM_RIGHTBUTTON*/,
            pt.X, pt.Y, 0, _trayHwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(menu);

        switch (cmd)
        {
            case MenuPlayPause: AppServices.Playback.PlayPause(); break;
            case MenuStop: AppServices.Playback.Stop(); break;
            case MenuPrev: _ = AppServices.Playback.PreviousAsync(); break;
            case MenuNext: _ = AppServices.Playback.NextAsync(); break;
            case MenuShow: RestoreFromTray(); break;
            case MenuExit: App.MainWin?.CloseFromTray(); break;
        }
    }

    private static IntPtr OnMainWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (msg == WM_COMMAND && _taskbar != null)
        {
            int cmd = wParam.ToInt32() & 0xFFFF;
            switch (cmd)
            {
                case ThumbPrevCmd: _ = AppServices.Playback.PreviousAsync(); return IntPtr.Zero;
                case ThumbPlayCmd: AppServices.Playback.PlayPause(); return IntPtr.Zero;
                case ThumbNextCmd: _ = AppServices.Playback.NextAsync(); return IntPtr.Zero;
            }
        }
        return NativeMethods.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private static void UpdateThumbButtons()
    {
        if (_taskbar == null || AppServices.MainWindowHandle == IntPtr.Zero) return;

        bool playing = AppServices.Playback.State == Core.Audio.PlaybackState.Playing;
        const uint mask = 0x1 /*THB_ICON*/ | 0x2 /*THB_TOOLTIP*/;
        var buttons = new[]
        {
            new THUMBBUTTON {iId = ThumbPrevCmd, hIcon = _iconPrev, szTip = AppStrings.Get("Tray_Previous", "이전 트랙"), dwMask = mask},
            new THUMBBUTTON
            {
                iId = ThumbPlayCmd,
                hIcon = playing ? _iconPause : _iconPlay,
                szTip = playing ? AppStrings.Get("Tray_Pause", "일시정지") : AppStrings.Get("Tray_Play", "재생"),
                dwMask = mask,
            },
            new THUMBBUTTON {iId = ThumbNextCmd, hIcon = _iconNext, szTip = AppStrings.Get("Tray_Next", "다음 트랙"), dwMask = mask},
        };

        if (_thumbButtonsAdded)
        {
            _taskbar.ThumbBarUpdateButtons(AppServices.MainWindowHandle, (uint)buttons.Length, buttons);
        }
        else if (_taskbar.ThumbBarAddButtons(AppServices.MainWindowHandle, (uint)buttons.Length, buttons) == 0)
        {
            _thumbButtonsAdded = true;
        }
    }

    // ---------------- icon helpers ----------------

    private static IntPtr LoadIconFromFile()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        var icon = NativeMethods.LoadImageW(IntPtr.Zero, path, 1 /*IMAGE_ICON*/, 32, 32, 0x10 /*LR_LOADFROMFILE*/);
        if (icon == IntPtr.Zero)
        {
            // Fall back to the application icon baked into the exe.
            icon = NativeMethods.LoadImageW(NativeMethods.GetModuleHandle(null), new IntPtr(1 /* IDI_APPLICATION via resource id is unreliable; use first icon group */),
                1, 32, 32, 0);
        }
        return icon;
    }

    /// <summary>Renders a Segoe MDL2 glyph into a 32×32 ARGB HICON for the thumbnail toolbar.</summary>
    private static IntPtr DrawGlyphIcon(string glyph)
    {
        const int size = 32;
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = size,
                    biHeight = -size, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };
            IntPtr bits;
            var color = NativeMethods.CreateDIBSection(hdc, ref bmi, 0, out bits, IntPtr.Zero, 0);
            var mask = NativeMethods.CreateBitmap(size, size, 1, 1, IntPtr.Zero);
            var memDc = NativeMethods.CreateCompatibleDC(hdc);
            var oldBmp = NativeMethods.SelectObject(memDc, color);
            try
            {
                // Clear to transparent, then draw the glyph in white with a soft dark shadow so it
                // stays visible on both light and dark taskbars.
                NativeMethods.PatBlt(memDc, 0, 0, size, size, 0x42 /*BLACKNESS*/);

                var uiSettings = new Windows.UI.ViewManagement.UISettings();
                var back = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                bool lightTaskbar = back.R * 0.299 + back.G * 0.587 + back.B * 0.114 > 128;
                uint colorRef = lightTaskbar ? 0x000000u : 0xFFFFFFu;

                var hFont = NativeMethods.CreateFontW(-22, 0, 0, 0, 400, 0, 0, 0, 1 /*DEFAULT_CHARSET*/,
                    0, 0, 0x5 /*CLEARTYPE_QUALITY*/, 0, "Segoe MDL2 Assets");
                var oldFont = NativeMethods.SelectObject(memDc, hFont);
                NativeMethods.SetBkMode(memDc, 1 /*TRANSPARENT*/);
                NativeMethods.SetTextColor(memDc, colorRef);
                var rect = new RECT {left = 0, top = 0, right = size, bottom = size};
                NativeMethods.DrawTextW(memDc, glyph, 1, ref rect, 0x25 /*DT_CENTER|DT_VCENTER|DT_SINGLELINE*/);
                NativeMethods.SelectObject(memDc, oldFont);
                NativeMethods.DeleteObject(hFont);
            }
            finally
            {
                NativeMethods.SelectObject(memDc, oldBmp);
                NativeMethods.DeleteDC(memDc);
            }

            var info = new ICONINFO {fIcon = true, hbmMask = mask, hbmColor = color};
            var icon = NativeMethods.CreateIconIndirect(ref info);
            NativeMethods.DeleteObject(color);
            NativeMethods.DeleteObject(mask);
            return icon;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    // ---------------- interop declarations ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)] public uint[] bmiColors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public int iId;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szTip;
        public uint dwMask;
        public uint dwFlags;
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        uint ThumbBarAddButtons(IntPtr hwnd, uint count, [In, MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        uint ThumbBarUpdateButtons(IntPtr hwnd, uint count, [In, MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarListCoClass
    {
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassW(ref WNDCLASSW wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern ushort RegisterWindowMessageW(string name);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AppendMenuW(IntPtr menu, uint flags, int id, string? text);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved,
            IntPtr hwnd, IntPtr rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT pt);

        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, int id, IntPtr refData);

        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, int id);

        [DllImport("comctl32.dll")]
        public static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadImageW(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint load);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint load);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? name);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern void ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage,
            out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bpp, IntPtr bits);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PatBlt(IntPtr hdc, int x, int y, int w, int h, uint rop);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation,
            int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
            uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);

        [DllImport("gdi32.dll")]
        public static extern void SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        public static extern void SetTextColor(IntPtr hdc, uint color);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern void DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);

        [DllImport("user32.dll")]
        public static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    }
}
