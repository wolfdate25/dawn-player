using DawnPlayer.App.Helpers;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.App.Views;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;

namespace DawnPlayer.App;

public sealed partial class MainWindow : Window
{
    private bool _closing;
    private readonly Services.NotificationPresenter _notifications = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _notifyCloseTimer = null!;

    public MainWindow()
    {
        InitializeComponent();
        App.Log("MainWindow: InitializeComponent done");

        try
        {
            AppServices.Initialize(this);
        }
        catch (Exception ex)
        {
            App.Log($"[FATAL services] {ex}");
            throw;
        }
        App.Log("MainWindow: services initialized");

        AppServices.CurrentTrackChanged += OnCurrentTrackChanged;
        AppServices.WarningRaised += ShowWarning;
        AppServices.OutputSessionChanged += OnOutputSession;
        AppServices.LanguageChanged += OnLanguageChanged;

        // U2: one policy for the single InfoBar slot — transient messages auto-close, warnings
        // and errors stay until dismissed (timer is the presenter's arm, UI is the hand).
        _notifications.Changed += RenderNotification;
        _notifyCloseTimer = DispatcherQueue.CreateTimer();
        _notifyCloseTimer.Interval = TimeSpan.FromMilliseconds(Services.NotificationPresenter.AutoCloseMs);
        _notifyCloseTimer.IsRepeating = false;
        _notifyCloseTimer.Tick += (_, _) => _notifications.Dismiss();

        // Without this the window reports the WinUI default ("WinUI Desktop") to the taskbar,
        // Alt+Tab and screen readers.
        Title = AppStrings.Get("MainWindow.Title", "Dawn Player");

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (System.IO.File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        // Title-bar overflow defense (2026-10-02): below this the nav tabs slide under the
        // system caption buttons no matter how the bar sheds content — enforce at the OS level.
        // WinUI's Window has no MinWidth property, and the preferred-minimum lives on the
        // OverlappedPresenter, not AppWindow. Mini mode relaxes this (it legally shrinks to
        // 500px with the title bar hidden) and restores it on exit.
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlapped)
        {
            overlapped.PreferredMinimumWidth = 620;
        }

        // 2026-10-03 (문서 정합 리팩터링): WinUI TitleBar 컨트롤(WASDK 1.7+)이 드래그 영역과
        // 인터랙티브 패스스루를 자체 계산하므로 SetTitleBar 호출은 없다. ExtendsContentIntoTitleBar는
        // 생성자에서 설정한다(문서 경고: 늦게 설정하면 시스템 타이틀바가 먼저 그려졌다가 사라질 수
        // 있다). 배경 재질은 ApplyTheme가 설정값(Mica/Acrylic/Solid/AlbumArtBlur)에 따라 유일하게
        // 결정하고, 시스템 캡션 색은 ThemeService.UpdateTitleBar 단일 구현이 담당한다.
        ExtendsContentIntoTitleBar = true;
        // 변형 A + Tall 48 (2026-10-03 사용자 승인): 인터랙티브 콘텐츠가 있는 타이틀바의 문서 권장
        // 높이 — 시스템 캡션도 48px로 늘어나 바를 꽉 채운다(40px 바 + 32px 캡션의 8px 괴리 해소,
        // 사용자 스크린샷 지적). 2026-10-04 바를 44로 완화했다가 롤백: 캡션 옵션은 32/48 둘뿐이라
        // Tall을 유지하는 한 캡션 호버가 바 아래로 4px 블리드됐고, 32는 글리프를 바 중앙보다 6px
        // 위로 밀었다(구 빨간 선 문제의 확대). 문서 경고: ExtendsContentIntoTitleBar=true 이후
        // 설정해야 한다.
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;

        // 단계 숨김(상태 840 → 브랜드 740)은 Window.SizeChanged의 창 논리 폭 기준 —
        // TitleBar 컨트롤 자체의 SizeChanged는 좁은 창에서 보고 폭이 예측적이지 않아
        // 720px에서 브랜드 숨김이 누락됐다(2026-10-03 4차 실측). 창 폭이 유일한 진실.
        SizeChanged += OnTitleBarSizeChanged;

        // 문서 "Do"(titlebar-design): 창이 비활성이면 타이틀바의 모든 요소가 반투명해야
        // 활성/비활성이 구분된다. 시스템 캡션은 스스로 디밍되므로 커스텀 Left/RightHeader
        // 콘텐츠만 여기서 함께 흐리게 한다.
        Activated += (_, args) =>
            AppTitleBar.Opacity = args.WindowActivationState == WindowActivationState.Deactivated
                ? InactiveTitleBarOpacity
                : 1.0;

        ApplyTheme();
        // ThemeMode.System follows the OS: re-apply the custom palette when Windows flips its
        // theme while the app is running. ActualThemeChanged only fires on a real change, and
        // ApplyTheme re-assigning the same RequestedTheme does not re-raise it — no loop.
        if (Content is FrameworkElement rootFe)
        {
            rootFe.ActualThemeChanged += (_, _) =>
            {
                if (AppServices.Settings.Ui.Theme == ThemeMode.System) ApplyTheme();
            };
        }
        WindowPlacementHelper.RestorePlacement(this, AppServices.Settings.Ui, AppServices.MainWindowHandle);
        App.Log("MainWindow: chrome configured");

        // Wire now-playing bar & lyrics to central events
        PlayerBar.InitializeState();

        AppServices.PlaybackStateChanged += PlayerBar.OnStateChanged;
        AppServices.AbRepeatChanged += PlayerBar.UpdateAbRepeatVisual;
        AppServices.AbRepeatRejected += PlayerBar.OnAbRepeatRejected;
        AppServices.RemoteArtResolved += PlayerBar.OnRemoteArtResolved;
        AppServices.CurrentTrackChanged += PlayerBar.OnTrackChanged;
        AppServices.QueueChanged += PlayerBar.OnQueueChanged;
        PlayerBar.LyricsToggleRequested += () => ToggleLyrics();

        if (AppServices.Settings.Ui.ShowLyricsPane) ShowLyrics(true);

        AppServices.Shortcuts.AttachTo(RootGrid);
        AppServices.Shortcuts.ShortcutsChanged += RefreshShortcutHints;
        RefreshShortcutHints();

        ContentFrame.Navigated += OnContentFrameNavigated;
        RestoreNavTab();
        RestoreLastSession();

        Closed += (_, _) =>
        {
            if (_closing) return;
            _closing = true;
            ShutdownForReal();
        };
        AppWindow.Closing += (sender, args) =>
        {
            if (_closing) return;

            // "Close to tray": hide instead of shutting down so playback keeps running. The tray
            // icon's Exit entry (and any real exit path) comes back through CloseFromTray, which
            // pre-sets _closing and lands here for the actual shutdown.
            if (AppServices.Settings.Ui.CloseToTray && Services.TrayIconService.IsRunning)
            {
                args.Cancel = true;
                Services.TrayIconService.HideToTray();
                return;
            }

            _closing = true;
            ShutdownForReal();
        };

        if (AppServices.Settings.Library is { ScanOnStartup: true, Folders.Count: > 0 })
            AppServices.StartLibraryScan();
    }

    // ---------------- title-bar overflow defense (2026-10-02) ----------------

    // Stepwise shedding thresholds in effective px, pinned by MainWindowTitleBarLayoutTests.
    // Below 840 the flexible status column can only show meaningless fragments ("No s…"), so
    // the track hides; below 740 even the brand no longer fits beside nav (≈352px ko) + gear +
    // the 140px caption reserve, leaving only ☰ + tabs. PreferredMinimumWidth (620) is the
    // last line of defense. AdaptiveTrigger was tried first and did not re-evaluate on live
    // resizes in this window, so the states are applied directly from SizeChanged.
    private const double TrackVisibleMinWidth = 840;
    private const double BrandVisibleMinWidth = 740;

    /// <summary>Semi-transparent title bar while the window is inactive — the titlebar-design
    /// "Do" contract (active/inactive must be distinguishable at a glance).</summary>
    private const double InactiveTitleBarOpacity = 0.5;

    /// <summary>타이틀바 행 높이 — Tall 캡션(48px)과의 일치 계약. 2026-10-04 44 완화를 시도했다가
    /// 캡션 호버 4px 블리드로 롤백(경위는 MainWindow.xaml 상단 주석). 미니 모드 복원이 리터럴 40으로
    /// 어긋나던 것(PT1-12 교훈)의 상수화. XAML 행·MinHeight 48과 함께 높이 게이트가 고정한다.</summary>
    private const double TitleBarRowHeight = 48;

    // Window.SizeChanged는 WindowSizeChangedEventArgs(컨트롤의 SizeChanged가 쓰는
    // SizeChangedEventArgs와 다른 타입 — CS0123 교훈)를 전달하고 창 논리 폭은 e.Size다.
    private void OnTitleBarSizeChanged(object sender, WindowSizeChangedEventArgs e)
    {
        var width = e.Size.Width;
        TitleBarTrack.Visibility = width >= TrackVisibleMinWidth ? Visibility.Visible : Visibility.Collapsed;
        AppBrandText.Visibility = width >= BrandVisibleMinWidth ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Real exit from the tray: pre-set the closing latch so AppWindow.Closing runs the
    /// shutdown path instead of hiding to the tray again.</summary>
    public void CloseFromTray()
    {
        _closing = true;
        Close();
    }

    private void ShutdownForReal()
    {
        SessionManager.Shutdown(AppServices.Settings, AppServices.Playback, this, AppServices.MainWindowHandle);
    }

    private void RestoreLastSession()
    {
        SessionManager.RestoreSession(
            AppServices.Settings,
            AppServices.Playlists,
            AppServices.Library,
            AppServices.Playback,
            onTrackRestored: item => PlayerBar.OnTrackChanged(item),
            onPositionRestored: (pos, dur) => PlayerBar.RestoreLastPosition(pos, dur));
    }

    // ---------------- theme ----------------

    // ---------------- theme & wallpaper ----------------

    public void ApplyTheme()
    {
        var ui = AppServices.Settings.Ui;
        // 시스템 캡션 색을 포함한 테마 파이프라인 전체는 ThemeService 단일 구현(고대비 분기
        // 포함)이 처리한다 — MainWindow 전용 중복 구현은 2026-10-03 리팩터링에서 제거됐다.
        ThemeService.ApplyTheme(this, ui, RootGrid);

        if (ui.Backdrop == BackdropMode.AlbumArtBlur)
        {
            var isLight = ThemeService.IsEffectiveLight(this, ui);
            var isOled = ui.Theme == ThemeMode.OledBlack;
            WallpaperDimOverlay.Fill = isOled
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(ThemeService.ColorFromHex("#C0000000"))
                : isLight
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(ThemeService.ColorFromHex("#B3F7F6F3"))
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(ThemeService.ColorFromHex("#9918181D"));

            UpdateThemeAndWallpaperForTrack(AppServices.Playback.CurrentItem?.Track);
        }
        else
        {
            WallpaperImage.Opacity = 0;
            WallpaperDimOverlay.Opacity = 0;
            UpdateThemeAndWallpaperForTrack(AppServices.Playback.CurrentItem?.Track);
        }
    }

    private int _artworkGeneration;

    private void UpdateThemeAndWallpaperForTrack(Track? track)
    {
        var ui = AppServices.Settings.Ui;
        var isLight = ThemeService.IsEffectiveLight(this, ui);
        bool wantAccent = ui.AutoAlbumArtAccent;
        bool wantWallpaper = ui.Backdrop == BackdropMode.AlbumArtBlur;

        if (!wantAccent && !wantWallpaper)
        {
            ThemeService.ApplyAccentPreset(ui, isLight);
            HideWallpaper();
            return;
        }

        // Only the newest track's artwork may touch the UI: these tasks complete out of order, so
        // without a generation stamp skipping quickly through tracks left whichever palette and
        // wallpaper happened to finish last, not the one for the track now playing.
        int generation = ++_artworkGeneration;

        Task.Run(() =>
        {
            string? artPath = null;
            string? blurPath = null;
            ExtractedAlbumPalette? palette = null;

            try
            {
                // ResolveArtPath probes the track's folder, opens the file with TagLib and can
                // write an extracted cover to the art cache. Running that on the UI thread stalled
                // every track change by however long the disk took.
                artPath = ResolveArtPath(track);

                if (!string.IsNullOrEmpty(artPath))
                {
                    var albumKey = track != null ? TagReader.ComputeAlbumKey(track) : artPath;
                    if (wantAccent) palette = AlbumArtColorExtractor.ExtractFromFile(artPath, albumKey, !isLight);
                    if (wantWallpaper) blurPath = AlbumArtBlurHelper.GetOrCreateBlurredArtPath(artPath, albumKey, 28);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[Artwork Error] {ex}");
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _artworkGeneration) return;

                if (wantAccent && palette != null) ThemeService.ApplyDynamicAlbumPalette(palette);
                else ThemeService.ApplyAccentPreset(ui, isLight);

                if (wantWallpaper && !string.IsNullOrEmpty(blurPath) && File.Exists(blurPath))
                {
                    var bmp = new BitmapImage { DecodePixelWidth = 1280 };
                    bmp.UriSource = new Uri(blurPath, UriKind.Absolute);
                    WallpaperImage.Source = bmp;
                    Helpers.MotionHelper.FadeIn(WallpaperImage, AppServices.Motion?.MotionEnabled ?? false);
                    WallpaperDimOverlay.Opacity = 1;
                }
                else
                {
                    HideWallpaper();
                }
            });
        });
    }

    private void HideWallpaper()
    {
        WallpaperImage.Opacity = 0;
        WallpaperDimOverlay.Opacity = 0;
    }

    private static string? ResolveArtPath(Track? track)
    {
        if (track == null) return null;
        if (!string.IsNullOrEmpty(track.ArtPath) && File.Exists(track.ArtPath))
            return track.ArtPath;

        if (!string.IsNullOrEmpty(track.Path))
        {
            var folderArt = AlbumArtService.FindFolderArt(track.Path);
            if (!string.IsNullOrEmpty(folderArt) && File.Exists(folderArt))
                return folderArt;

            var extracted = AlbumArtService.TryExtractArt(track, TagReader.ComputeAlbumKey(track));
            if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted))
                return extracted;
        }
        return null;
    }

    // ---------------- navigation ----------------

    private void RestoreNavTab()
    {
        NavigateToTab(NavigationStateCalculator.NormalizeTab(AppServices.Settings.Ui.LastNavTab));
    }

    private void OnTabLibraryClick(object sender, RoutedEventArgs e)
    {
        NavigateToTab("Library");
    }

    private void OnTabPlaylistsClick(object sender, RoutedEventArgs e)
    {
        NavigateToTab("Playlists");
    }

    private void OnTabNetworkClick(object sender, RoutedEventArgs e)
    {
        NavigateToTab("Network");
    }

    public void NavigateToTab(string tabName)
    {
        var normalized = NavigationStateCalculator.NormalizeTab(tabName);
        AppServices.Settings.Ui.LastNavTab = normalized;
        SettingsWriter.Schedule(AppServices.Settings);

        var state = NavigationStateCalculator.ForTab(normalized, AppServices.Settings.Ui.ShowLyricsPane);
        ApplyNavigationState(state);

        // Activation is a side effect the calculator has no business knowing about.
        if (state.PlaylistsVisible) PlaylistPageView?.ActivatePage();
        else if (state.NetworkVisible) NetworkPageView?.ActivatePage();
        else if (state.LibraryVisible) LibraryPageView?.ActivatePage();
    }

    /// <summary>Applies a computed navigation state to the shell's surfaces.</summary>
    private void ApplyNavigationState(NavigationViewState state)
    {
        if (TabLibrary != null) TabLibrary.IsChecked = state.TabLibraryChecked;
        if (TabPlaylists != null) TabPlaylists.IsChecked = state.TabPlaylistsChecked;
        if (TabNetwork != null) TabNetwork.IsChecked = state.TabNetworkChecked;

        // Incoming surfaces fade in (U1); MotionHelper sets the final state instantly when the
        // motion gate is off, so reduced-motion users get a plain visibility flip.
        bool motion = AppServices.Motion?.MotionEnabled ?? false;

        if (LibraryPageView != null)
        {
            var wasHidden = LibraryPageView.Visibility != Visibility.Visible;
            LibraryPageView.Visibility = state.LibraryVisible ? Visibility.Visible : Visibility.Collapsed;
            if (wasHidden && state.LibraryVisible) Helpers.MotionHelper.FadeIn(LibraryPageView, motion);
        }
        if (PlaylistPageView != null)
        {
            var wasHidden = PlaylistPageView.Visibility != Visibility.Visible;
            PlaylistPageView.Visibility = state.PlaylistsVisible ? Visibility.Visible : Visibility.Collapsed;
            if (wasHidden && state.PlaylistsVisible) Helpers.MotionHelper.FadeIn(PlaylistPageView, motion);
        }
        if (NetworkPageView != null)
        {
            var wasHidden = NetworkPageView.Visibility != Visibility.Visible;
            NetworkPageView.Visibility = state.NetworkVisible ? Visibility.Visible : Visibility.Collapsed;
            if (wasHidden && state.NetworkVisible) Helpers.MotionHelper.FadeIn(NetworkPageView, motion);
        }
        if (ContentFrame != null)
        {
            var wasHidden = ContentFrame.Visibility != Visibility.Visible;
            ContentFrame.Visibility = state.SettingsVisible ? Visibility.Visible : Visibility.Collapsed;
            if (wasHidden && state.SettingsVisible) Helpers.MotionHelper.FadeIn(ContentFrame, motion);
        }

        // 설정 표면에서 나오는 경로(탭 클릭)는 ContentFrame 탐색이 없어 Navigated가 불지 않는다 —
        // 톱니 위치 마커는 여기서 직접 회복해야 남아있지 않다.
        UpdateSettingsGearMarker();

        if (state.LibraryVisible) LibraryPageView?.SetLyricsVisibility(state.LibraryLyricsVisible);
        if (state.PlaylistsVisible) PlaylistPageView?.SetLyricsVisibility(state.PlaylistLyricsVisible);
    }

    private void OnTabDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnMenuRescan(object sender, RoutedEventArgs e)
    {
        AppServices.StartLibraryScan();
    }

    // ---------------- sleep timer menu ----------------

    // The flyout is rebuilt from the service every time it opens, so the checkmarks and the
    // remaining-time label never go stale while the menu is closed.
    private void OnTitleMenuOpening(object sender, object e)
    {
        SyncSleepTimerMenu();
    }

    private void SyncSleepTimerMenu()
    {
        var active = AppServices.SleepTimer.Active;
        SleepOffItem.IsChecked = active == SleepTimerOption.Off;
        Sleep15Item.IsChecked = active == SleepTimerOption.Minutes15;
        Sleep30Item.IsChecked = active == SleepTimerOption.Minutes30;
        Sleep60Item.IsChecked = active == SleepTimerOption.Minutes60;
        SleepTrackItem.IsChecked = active == SleepTimerOption.AfterCurrentTrack;

        // Header carries the live state ("수면 타이머 · 30분 (28:41)") so the countdown is visible
        // before committing to an option.
        var header = AppStrings.Get("MainWindow_Menu_SleepTimer", "수면 타이머");
        SleepTimerMenu.Text = active == SleepTimerOption.Off ? header : $"{header} · {AppServices.SleepTimer.DescribeActive()}";
    }

    private void OnSleepTimerOff(object sender, RoutedEventArgs e) => AppServices.SleepTimer.Set(SleepTimerOption.Off);
    private void OnSleepTimer15(object sender, RoutedEventArgs e) => AppServices.SleepTimer.Set(SleepTimerOption.Minutes15);
    private void OnSleepTimer30(object sender, RoutedEventArgs e) => AppServices.SleepTimer.Set(SleepTimerOption.Minutes30);
    private void OnSleepTimer60(object sender, RoutedEventArgs e) => AppServices.SleepTimer.Set(SleepTimerOption.Minutes60);
    private void OnSleepTimerAfterTrack(object sender, RoutedEventArgs e) => AppServices.SleepTimer.Set(SleepTimerOption.AfterCurrentTrack);

    public void NavigateToSettings()
    {
        ApplyNavigationState(NavigationStateCalculator.ForSettings());

        if (ContentFrame != null && ContentFrame.Content is not SettingsPage)
            ContentFrame.Navigate(typeof(SettingsPage));
    }

    private void OnMenuSettings(object sender, RoutedEventArgs e)
    {
        NavigateToSettings();
    }

    private async void OnMenuReport(object sender, RoutedEventArgs e)
    {
        try
        {
            await Views.ListeningReportDialog.ShowAsync(Content.XamlRoot);
        }
        catch (Exception ex)
        {
            App.Log($"[ListeningReport] {ex}");
        }
    }

    private void OnMenuExit(object sender, RoutedEventArgs e)
    {
        ShutdownForReal();
    }

    private async void OnMenuOpenUrl(object sender, RoutedEventArgs e)
    {
        try
        {
            // The play flow lives in the Network tab's radio section now; the menu only routes
            // there (N0: one implementation, no duplicated open logic).
            NavigateToTab("Network");
            NetworkPageView?.ActivatePage();
            if (NetworkPageView != null)
                await NetworkPageView.OpenRadioAddDialogAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[open-url] {ex}");
            ShowWarning(ex.Message);
        }
    }

    // ---------------- mini player mode ----------------

    private bool _isMiniMode;
    private Windows.Graphics.SizeInt32 _preMiniSize;
    private bool _preMiniAlwaysOnTop;
    private bool _preMiniMaximized;

    /// <summary>True while the window is collapsed to the compact player bar.</summary>
    public bool IsMiniMode => _isMiniMode;

    /// <summary>Toggles the compact always-on-top player: content and title bar hide, the bar
    /// remains, and dragging the bar background moves the window. Escape exits as well.</summary>
    public void ToggleMiniMode()
    {
        var appWindow = GetAppWindow();
        if (appWindow == null) return;
        var presenter = appWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;

        if (!_isMiniMode)
        {
            _preMiniSize = appWindow.Size;
            _preMiniAlwaysOnTop = presenter?.IsAlwaysOnTop == true;
            // PT1-15: exiting mini mode used to un-maximize the window — the maximized state
            // is part of the pre-mini placement.
            _preMiniMaximized = presenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;

            AppTitleBar.Visibility = Visibility.Collapsed;
            ContentHost.Visibility = Visibility.Collapsed;
            RootGrid.RowDefinitions[0].Height = new GridLength(0);
            RootGrid.RowDefinitions[1].Height = new GridLength(0);

            appWindow.Resize(new Windows.Graphics.SizeInt32(500, 104));
            // Mini legally shrinks below the title bar's minimum — lift it, restore on exit.
            if (presenter != null) presenter.PreferredMinimumWidth = 0;
            if (presenter != null) presenter.IsAlwaysOnTop = true;
            // 미니(104px 창)에서 Tall 캡션이 절반을 잠식하지 않게 Standard로 — 복귀 시 Tall 복원.
            appWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;
            _isMiniMode = true;
        }
        else
        {
            RootGrid.RowDefinitions[0].Height = new GridLength(TitleBarRowHeight);
            RootGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            AppTitleBar.Visibility = Visibility.Visible;
            ContentHost.Visibility = Visibility.Visible;

            if (_preMiniMaximized && presenter != null)
            {
                presenter.Maximize();
            }
            else
            {
                appWindow.Resize(_preMiniSize);
            }
            if (presenter != null) presenter.PreferredMinimumWidth = 620;
            if (presenter != null) presenter.IsAlwaysOnTop = _preMiniAlwaysOnTop;
            appWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
            _isMiniMode = false;
        }
    }

    private Microsoft.UI.Windowing.AppWindow? GetAppWindow()
    {
        try
        {
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
            return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
        }
        catch
        {
            return null;
        }
    }

    private void OnMenuMiniToggle(object sender, RoutedEventArgs e) => ToggleMiniMode();

    /// <summary>U4: opens the fullscreen Now Playing surface. Mini mode and fullscreen are both
    /// window-state overlays — mini exits first so the two never fight over the shell.</summary>
    private void OnMenuFullscreen(object sender, RoutedEventArgs e) => OpenFullscreenNowPlaying();

    public void OpenFullscreenNowPlaying()
    {
        if (_isMiniMode) ToggleMiniMode();
        try
        {
            new Views.FullscreenNowPlayingWindow().Activate();
        }
        catch (Exception ex)
        {
            App.Log($"[fullscreen] open failed: {ex}");
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isMiniMode && e.Key == Windows.System.VirtualKey.Escape)
        {
            ToggleMiniMode();
            e.Handled = true;
        }
    }

    /// <summary>
    /// In mini mode the whole bar becomes the drag surface: presses on background Grid/Canvas
    /// areas start a caption drag, presses on interactive controls pass through untouched.
    /// </summary>
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMiniMode) return;
        if (e.OriginalSource is not DependencyObject source) return;
        for (var node = source; node != null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is Microsoft.UI.Xaml.Controls.Control || node is Microsoft.UI.Xaml.Controls.UserControl)
            {
                return; // a real control owns this press
            }
        }

        try
        {
            _ = NativeMethods.SendMessageForDrag(WindowNative.GetWindowHandle(this));
        }
        catch { }
        e.Handled = true;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static bool SendMessageForDrag(IntPtr hwnd) =>
            SendMessage(hwnd, 0xA1 /* WM_NCLBUTTONDOWN */, new IntPtr(2 /* HTCAPTION */), IntPtr.Zero);
    }

    // ---------------- central event handlers ----------------

    private void OnCurrentTrackChanged(PlaylistItem? item)
    {
        TitleBarTrack.Text = item == null
            ? AppStrings.Get("MainWindow_TitleBarTrack.Text", "No sound — Nothing played")
            : $"{item.Track.Artist} — {item.Track.Title}";
        if (item != null) AppServices.Smtc.UpdateTimeline(TimeSpan.Zero, item.Track.Duration);

        UpdateThemeAndWallpaperForTrack(item?.Track);

        // While the window lives in the tray the only surface left for "what changed" is the
        // balloon. Visible-window track changes stay quiet on purpose.
        if (item != null && Services.TrayIconService.IsWindowHidden)
        {
            Services.TrayIconService.ShowBalloon(
                AppStrings.Get("Toast_NowPlaying", "재생 중"),
                $"{item.Track.Title} — {item.Track.Artist}");
        }
    }

    private void OnOutputSession(DawnPlayer.Core.Audio.SessionInfo info)
    {
    }

    private void ShowWarning(string message)
    {
        _notifications.Show(message, Services.UiSeverity.Warning);
    }

    /// <summary>Transient (informational/success) notification through the shared presenter —
    /// auto-closes so confirmations do not linger over the content.</summary>
    private void ShowTransient(string message)
    {
        _notifications.Show(message, Services.UiSeverity.Informational);
    }

    private void RenderNotification()
    {
        if (NotifyBar == null) return;
        var n = _notifications;
        if (!n.IsOpen)
        {
            _notifyCloseTimer.Stop();
            NotifyBar.IsOpen = false;
            return;
        }

        NotifyBar.Message = n.Message;
        NotifyBar.Severity = n.Severity switch
        {
            Services.UiSeverity.Success => InfoBarSeverity.Success,
            Services.UiSeverity.Warning => InfoBarSeverity.Warning,
            Services.UiSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        NotifyBar.IsOpen = true;

        _notifyCloseTimer.Stop();
        if (n.WillAutoClose) _notifyCloseTimer.Start();
    }

    private void OnNotifyBarClosed(InfoBar sender, object args)
    {
        // Manual close (the InfoBar's ✕) must not be overridden by a pending auto-close tick.
        _notifyCloseTimer.Stop();
        _notifications.Dismiss();
    }

    // ---------------- language switch ----------------

    // x:Uid bindings only resolve at XAML load time, so a live language switch would leave the
    // visible tree half old, half new. Offering a restart keeps every surface consistent; the
    // new language is already persisted and applied for strings fetched from here on.
    private async void OnLanguageChanged(Core.Persistence.UiLanguage language)
    {
        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("Msg_LanguageChangedTitle", "언어 변경"),
            Content = AppStrings.Get("Msg_LanguageChangedMessage", "언어 변경을 적용하려면 앱을 다시 시작해야 합니다. 지금 다시 시작할까요?"),
            PrimaryButtonText = AppStrings.Get("Msg_LanguageChangedRestart", "지금 다시 시작"),
            CloseButtonText = AppStrings.Get("Msg_LanguageChangedLater", "나중에"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Services.AppServices.RestartApp();
        }
    }

    // ---------------- lyrics toggle ----------------

    public void ToggleLyrics() => ShowLyrics(!AppServices.Settings.Ui.ShowLyricsPane);

    private void ShowLyrics(bool show)
    {
        AppServices.Settings.Ui.ShowLyricsPane = show;
        PlayerBar.SetLyricsToggle(show);

        // The visible page owns its lyrics pane. On the settings page there is nothing to toggle,
        // so the preference is simply recorded and applied when a content page comes back.
        if (LibraryPageView?.Visibility == Visibility.Visible)
            LibraryPageView.SetLyricsVisibility(show);
        else if (PlaylistPageView?.Visibility == Visibility.Visible)
            PlaylistPageView.SetLyricsVisibility(show);
    }

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        if (ContentFrame.Content is SettingsPage)
        {
            if (TabLibrary != null) TabLibrary.IsChecked = false;
            if (TabPlaylists != null) TabPlaylists.IsChecked = false;
            if (TabNetwork != null) TabNetwork.IsChecked = false;
        }

        // PT1-03: the settings entry must carry the active-location marker while the tabs are
        // all unchecked, or nothing on the shell indicates where you are.
        UpdateSettingsGearMarker();
    }

    /// <summary>PT1-03: the settings entry carries the active-location marker (accent gear)
    /// while the tabs are all unchecked. Runs on BOTH surface changes — ContentFrame.Navigated
    /// (entering settings) and ApplyNavigationState (leaving via a tab click, which fires no
    /// navigation and used to leave the gear amber until restart).</summary>
    private void UpdateSettingsGearMarker()
    {
        if (SettingsGearIcon == null) return;
        var onSettings = ContentFrame.Visibility == Visibility.Visible && ContentFrame.Content is SettingsPage;
        SettingsGearIcon.Foreground = onSettings
            ? Helpers.ThemeResourceHelper.GetBrush("DawnAccentTextBrush")
            : Helpers.ThemeResourceHelper.GetBrush("TextSecondaryBrush");
    }

    // ---------------- keyboard shortcuts ----------------

    /// <summary>
    /// The now-playing bar, exposed so <c>ShortcutCommandExecutor</c> can drive the same transport
    /// methods the buttons use instead of duplicating the state changes and desyncing the icons.
    /// </summary>
    public DawnPlayer.App.Controls.NowPlayingBar Player => PlayerBar;

    /// <summary>Moves focus to the library search box, switching to the Library tab if needed.</summary>
    public void FocusLibrarySearch()
    {
        if (LibraryPageView?.Visibility != Visibility.Visible)
        {
            NavigateToTab("Library");
        }
        LibraryPageView?.FocusSearch();
    }

    /// <summary>
    /// Pushes the current chord into the title-bar gear tooltip, so rebinding Ctrl+P does not
    /// leave the gear advertising the old key.
    /// </summary>
    private void RefreshShortcutHints()
    {
        var preferences = AppServices.Shortcuts.Map.GetChord(DawnPlayer.App.Shortcuts.ShortcutCommand.OpenPreferences);
        var text = preferences?.ToDisplayString();

        if (SettingsGearButton != null)
        {
            var baseText = AppStrings.Get("MainWindow_SettingsGearButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip", "환경설정");
            ToolTipService.SetToolTip(SettingsGearButton,
                text == null ? baseText : AppStrings.Format("Msg_PreferencesWithChord", "환경설정 ({0})", text));
        }

        PlayerBar?.RefreshShortcutHints();
    }

    // ---------------- drag & drop ----------------

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = AppStrings.Get("Msg_DragDrop_AddToPlaylist", "재생목록에 추가");
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = false;
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList()!;
            if (paths.Count == 0) return;

            var playlistFiles = paths.Where(p => p.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)).ToList();
            var audioPaths = paths.Except(playlistFiles).ToList();

            if (playlistFiles.Count > 0)
            {
                foreach (var plFile in playlistFiles)
                {
                    var imported = await AppServices.Playlists.ImportPlaylistAsync(plFile);
                    if (imported != null)
                    {
                        ShowTransient(AppStrings.Format("Msg_PlaylistImported", "'{0}' 재생목록을 가져왔습니다 ({1}곡).", imported.Name, imported.Items.Count));
                    }
                }
            }

            if (audioPaths.Count > 0)
            {
                var added = await AppServices.Playlists.AddPathsAsync(AppServices.Playlists.Current, audioPaths);
                ShowTransient(AppStrings.Format("Msg_TracksAddedToPlaylist", "{0}개 트랙을 '{1}'에 추가했습니다.", added.Count, AppServices.Playlists.Current.Name));
            }
        }
        catch (Exception ex)
        {
            ShowWarning(AppStrings.Format("Msg_DropFailed", "드롭 처리 실패: {0}", ex.Message));
        }
    }
}
