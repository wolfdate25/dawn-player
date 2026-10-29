using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Network.Dlna;
using DawnPlayer.Core.Network.YouTube;
using DawnPlayer.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DawnPlayer.App.Views.Network;

/// <summary>One card of the recent grid: page URL plus the -J metadata, with a thumbnail that
/// fills in asynchronously once its download from the shared art cache lands. <see cref="IsBusy"/>
/// lights the card's progress ring and status line while a double-click's resolve+open is in
/// flight — the yt-dlp delay is otherwise invisible and the click reads as dead.</summary>
public sealed class RecentRow : INotifyPropertyChanged
{
    public string PageUrl { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string DurationBadge { get; }
    public Visibility DurationBadgeVisibility { get; }
    public ImageSource? Art { get; private set; }

    private bool _isBusy;
    private string _rowStatus = "";

    /// <summary>True while this card's play request is resolving/opening.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BusyVisibility)));
        }
    }

    public Visibility BusyVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The subtitle hides while busy — the status line takes its slot.</summary>
    public Visibility SubtitleVisibility => _isBusy ? Visibility.Collapsed : Visibility.Visible;

    public string RowStatus
    {
        get => _rowStatus;
        private set
        {
            if (_rowStatus == value) return;
            _rowStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowStatus)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RecentRow(YouTubeRecentEntry entry)
    {
        PageUrl = entry.PageUrl;
        Title = string.IsNullOrWhiteSpace(entry.Title) ? entry.PageUrl : entry.Title;
        var uploader = string.IsNullOrWhiteSpace(entry.Uploader) ? "" : entry.Uploader;
        var duration = entry.DurationMs > 0 ? FormatDuration(entry.DurationMs) : "";
        Subtitle = string.Join(" · ", new[] { uploader, duration }.Where(part => part.Length > 0));
        DurationBadge = duration;
        DurationBadgeVisibility = duration.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Must run on the UI thread — creates the XAML image source.</summary>
    public void SetArt(string localPath)
    {
        Art = new BitmapImage(new Uri(localPath));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Art)));
    }

    /// <summary>Must run on the UI thread. Null clears the busy state.</summary>
    public void SetBusyState(string? status)
    {
        if (status == null)
        {
            IsBusy = false;
            RowStatus = "";
        }
        else
        {
            IsBusy = true;
            RowStatus = status;
        }
    }

    private static string FormatDuration(long durationMs)
    {
        var time = TimeSpan.FromMilliseconds(durationMs);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time:mm\\:ss}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }
}

/// <summary>
/// The YouTube section: paste a URL (or click a recent card) to play or queue. Every submit
/// resolves the -J metadata first — with live busy feedback on the buttons — so the recent grid
/// fills with real titles and the open itself reuses the cached resolve. Dependencies are probed
/// on activation and re-checked from the Configure flyout; playback failures surface through the
/// warning pipeline (InfoBar).
/// </summary>
public sealed partial class YouTubeSection : UserControl
{
    private readonly DlnaArtCache _artCache = new();
    private bool _probed;
    private int _pending; // re-entrancy guard: Play/Add while one is in flight is a no-op
    private (bool Play, string Url)? _lastFailed;

    public YouTubeSection()
    {
        InitializeComponent();
        // WinUI 3 has no Button.IsDefault — Enter in the URL box submits the primary action.
        UrlBox.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                args.Handled = true;
                _ = PlayOrAddAsync(play: true);
            }
        };
        // Keyboard parity with double-click on the recent grid.
        RecentGrid.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter && RowFromItem(RecentGrid.SelectedItem) is { } row)
            {
                args.Handled = true;
                _ = PlayRecentAsync(row, play: true);
            }
        };
        // A play started anywhere in this section clears a card's busy ring when the track is
        // actually current (OpenCompletedPath) or the section is left for good.
        Unloaded += (_, _) => ClearBusyRow();
    }

    /// <summary>First activation kicks off the dependency probe; later activations keep the state.</summary>
    public void Activate()
    {
        UpdateStatus(YouTubeDependency.GetStatus(), probing: false);
        RefreshRecentRows();
        if (!_probed)
        {
            _probed = true;
            _ = RefreshDependencyAsync();
        }
    }

    private async Task RefreshDependencyAsync()
    {
        UpdateStatus(YouTubeDependency.GetStatus(), probing: true);
        var status = await Task.Run(YouTubeDependency.Probe);
        UpdateStatus(status, probing: false);
    }

    private void OnReprobeClick(object sender, RoutedEventArgs e) => _ = RefreshDependencyAsync();

    private void OnPlayClick(object sender, RoutedEventArgs e) => _ = PlayOrAddAsync(play: true);

    private void OnAddClick(object sender, RoutedEventArgs e) => _ = PlayOrAddAsync(play: false);

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        RetryButton.Visibility = Visibility.Collapsed;
        if (_lastFailed is { } last) _ = PlayOrAddAsync(last.Play);
    }

    /// <summary>Inline validation on blur — a malformed URL is reported when the field is left,
    /// not after a submit that was doomed anyway. A valid URL is normalized in place.</summary>
    private void OnUrlBoxLostFocus(object sender, RoutedEventArgs e)
    {
        var text = UrlBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        var normalized = YouTubePageUrl.TryNormalize(text);
        if (normalized == null)
        {
            GuidanceText.Text = AppStrings.Get("Network_YouTube_InvalidUrl",
                "YouTube 동영상 주소가 아닙니다. youtube.com/watch, youtu.be, music.youtube.com 주소를 입력하세요.");
            return;
        }
        GuidanceText.Text = "";
        UrlBox.Text = normalized;
    }

    private async Task PlayOrAddAsync(bool play)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        try
        {
            var normalized = YouTubePageUrl.TryNormalize(UrlBox.Text);
            if (normalized == null)
            {
                GuidanceText.Text = AppStrings.Get("Network_YouTube_InvalidUrl",
                    "YouTube 동영상 주소가 아닙니다. youtube.com/watch, youtu.be, music.youtube.com 주소를 입력하세요.");
                return;
            }
            UrlBox.Text = normalized;
            GuidanceText.Text = "";

            SetBusy(true);
            try
            {
                // Resolve first: the recent grid gets real metadata, and the open below reuses
                // the cached resolve instead of spawning a second yt-dlp run.
                var meta = await Task.Run(() => YouTubeResolve.ResolveMeta(YouTubeProcess.Runner, normalized));

                var track = new Track
                {
                    Path = normalized,
                    SourceKind = TrackSourceKind.YouTube,
                    Title = string.IsNullOrWhiteSpace(meta.Title) ? normalized : meta.Title,
                    Artist = meta.Uploader,
                    DurationMs = meta.DurationMs,
                    ArtUrl = meta.ThumbnailUrl,
                };

                var playlists = AppServices.Playlists;
                var playlist = playlists.NowPlaying;
                var item = playlists.AddTracks(playlist, new[] { track }).FirstOrDefault();

                AppServices.YouTubeRecent.Add(new YouTubeRecentEntry(
                    normalized,
                    track.Title,
                    meta.Uploader,
                    meta.DurationMs,
                    meta.ThumbnailUrl,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                AppServices.YouTubeRecent.Save();
                RefreshRecentRows();

                if (item != null && play)
                {
                    SetBusy(true);
                    // The recent card for this URL shows the same card-level feedback (ring +
                    // status) the double-click path uses — one behavior for both entry points.
                    var row = RowForUrl(normalized);
                    ClearBusyRow(keep: row);
                    row?.SetBusyState(AppStrings.Get("Network_YouTube_RowProcessing", "불러오는 중…"));
                    try
                    {
                        await Controls.PlaybackUiHelper.PlayItemAsync(AppServices.Playback, playlist, item);
                    }
                    finally
                    {
                        SetBusy(false);
                        ClearBusyRow();
                    }
                }
                else
                {
                    SetBusy(false);
                }
            }
            catch (OperationCanceledException)
            {
                SetBusy(false);
            }
            catch (Exception ex)
            {
                // The resolve/chain failure reason is the useful part — surface it here and in
                // the InfoBar. SetBusy(false) resets first, then the failure state goes on top,
                // so the retry button and the reason both survive the busy reset.
                SetBusy(false);
                StatusRow.Visibility = Visibility.Visible;
                // PT1-14: failure text carries the semantic danger token.
                StatusText.Foreground = Helpers.ThemeResourceHelper.GetBrush("StatusDangerBrush");
                StatusText.Text = AppStrings.Format("Network_YouTube_ResolveFailed",
                    "항목을 가져오지 못했습니다: {0}", TrimDetail(ex.Message));
                RetryButton.Visibility = Visibility.Visible;
                _lastFailed = (play, normalized);
                AppServices.RaiseWarning(StatusText.Text);
            }
        }
        finally
        {
            Volatile.Write(ref _pending, 0);
        }
    }

    private static string TrimDetail(string detail)
    {
        detail = detail.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return detail.Length <= 160 ? detail : detail[..160] + "…";
    }

    private void SetBusy(bool busy)
    {
        PlayButton.IsEnabled = !busy;
        AddButton.IsEnabled = !busy;
        BusyRing.IsActive = busy;
        if (busy)
        {
            RetryButton.Visibility = Visibility.Collapsed;
            StatusRow.Visibility = Visibility.Visible;
            StatusText.Foreground = Helpers.ThemeResourceHelper.GetBrush("TextSecondaryBrush");
            StatusText.Text = AppStrings.Get("Network_YouTube_Status_Resolving",
                "해석 중… — 첫 재생까지 몇 초 걸립니다");
        }
        else
        {
            // Done (or cancelled): hide the status line entirely so no stale "resolving…" text
            // survives a completed resolve. The retry button is deliberately left alone here —
            // a failure sets it visible after this reset runs.
            StatusText.Text = "";
            StatusRow.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateStatus(YouTubeDependencyStatus status, bool probing)
    {
        // Never show a verdict on an unprobed status — a user with both tools installed would see
        // a false "not installed" flash before the first probe lands.
        if (status == YouTubeDependencyStatus.Unknown || probing)
        {
            DependencyBadge.Text = AppStrings.Get("Network_YouTube_Status_Checking", "확인 중…");
            return;
        }

        if (!status.IsUsable)
        {
            // User-configured paths get their own message — "install the tools" is wrong advice
            // when a picked path went missing.
            if (status.HasOverrides)
            {
                DependencyBadge.Text = AppStrings.Get("Network_YouTube_Status_OverrideInvalid", "도구 경로 무효");
                GuidanceText.Text = AppStrings.Get("Network_YouTube_Guidance_OverrideInvalid",
                    "구성된 yt-dlp/ffmpeg 경로가 유효하지 않습니다. '구성…'에서 다시 선택하거나 기본값을 사용하세요.");
            }
            else
            {
                DependencyBadge.Text = AppStrings.Get("Network_YouTube_Status_Missing", "yt-dlp/ffmpeg 없음");
                GuidanceText.Text = AppStrings.Get("Network_YouTube_Guidance_Install",
                    "재생하려면 yt-dlp와 ffmpeg가 PATH에 있어야 합니다. https://github.com/yt-dlp/yt-dlp 에서 설치한 뒤 '구성…'에서 다시 검사하세요.");
            }
            return;
        }

        if (status.HasOverrides)
        {
            DependencyBadge.Text = AppStrings.Get("Network_YouTube_Status_CustomOk", "준비됨 (사용자 지정)");
            return;
        }

        var parts = "yt-dlp " + (status.YtDlpVersion.Length == 0 ? "?" : status.YtDlpVersion)
            + " / ffmpeg " + (status.FfmpegVersion.Length == 0 ? "?" : status.FfmpegVersion)
            + (status.JsRuntimeAvailable ? "" : " · " + AppStrings.Get("Network_YouTube_NoJsRuntime", "JS 런타임 없음(일부 영상 제한)"));
        DependencyBadge.Text = AppStrings.Format("Network_YouTube_Status_Ok", "준비됨 ({0})", parts);
    }

    // ---------------- tool-path flyout ----------------

    private static string DefaultPathDisplay => AppStrings.Get("Network_YouTube_DefaultPathDisplay", "(PATH 자동 감지)");

    private void OnPathsFlyoutOpened(object sender, object e)
    {
        RefreshPathTextBoxes();
        _ = RefreshPathRowsAsync();   // row-level probe: what each configured binary actually does
        _ = RefreshDependencyAsync(); // badge-level probe: replaces the old dedicated Re-check button
    }

    private void RefreshPathTextBoxes()
    {
        var config = AppServices.Settings.YouTube;
        YtDlpPathBox.Text = config.YtDlpPath.Length == 0 ? DefaultPathDisplay : config.YtDlpPath;
        FfmpegPathBox.Text = config.FfmpegPath.Length == 0 ? DefaultPathDisplay : config.FfmpegPath;
    }

    /// <summary>The row probes spawn each configured binary and wait up to 5 s apiece — run on a
    /// worker, since opening this flyout with a misbehaving exe froze the UI for ~10 s.</summary>
    private async Task RefreshPathRowsAsync()
    {
        RefreshPathTextBoxes();
        var config = AppServices.Settings.YouTube;
        var ytTarget = config.YtDlpPath.Length == 0 ? "yt-dlp" : config.YtDlpPath;
        var ffTarget = config.FfmpegPath.Length == 0 ? "ffmpeg" : config.FfmpegPath;

        SetRowStatus(YtDlpRowStatus, ok: true, "?");
        SetRowStatus(FfmpegRowStatus, ok: true, "?");
        var (ytOk, ytVersion, ffOk, ffVersion) = await Task.Run(() =>
        {
            var y = YouTubeProcess.Runner.TryProbe(ytTarget, out var yv);
            var f = YouTubeProcess.Runner.TryProbe(ffTarget, out var fv);
            return (y, yv, f, fv);
        });
        SetRowStatus(YtDlpRowStatus, ytOk, ytVersion);
        SetRowStatus(FfmpegRowStatus, ffOk, ffVersion);
    }

    private static void SetRowStatus(TextBlock target, bool ok, string version)
    {
        target.Text = ok
            ? AppStrings.Format("Network_YouTube_PathOk", "버전 {0} 확인됨", version.Length == 0 ? "?" : version)
            : AppStrings.Get("Network_YouTube_PathFail", "실행할 수 없습니다");
    }

    private void OnBrowseYtDlpClick(object sender, RoutedEventArgs e) => _ = BrowseAndApplyAsync(isYtDlp: true);

    private void OnBrowseFfmpegClick(object sender, RoutedEventArgs e) => _ = BrowseAndApplyAsync(isYtDlp: false);

    private void OnDefaultYtDlpClick(object sender, RoutedEventArgs e) => _ = ApplyOverrideAsync(isYtDlp: true, path: "");

    private void OnDefaultFfmpegClick(object sender, RoutedEventArgs e) => _ = ApplyOverrideAsync(isYtDlp: false, path: "");

    private async Task BrowseAndApplyAsync(bool isYtDlp)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, AppServices.MainWindowHandle);
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".cmd");
        picker.FileTypeFilter.Add(".bat");
        picker.FileTypeFilter.Add(".com");

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        await ApplyOverrideAsync(isYtDlp, file.Path);
    }

    /// <summary>Applies one path override. A picked path is probed <em>before</em> persisting — a
    /// binary that cannot run never reaches the settings. Clearing to the PATH default always
    /// applies (the row status then shows what PATH actually provides). Persisting also swaps the
    /// process runner and re-probes so the badge reflects reality without a manual re-check.</summary>
    private async Task ApplyOverrideAsync(bool isYtDlp, string path)
    {
        var rowStatus = isYtDlp ? YtDlpRowStatus : FfmpegRowStatus;
        var probeName = path.Length == 0 ? (isYtDlp ? "yt-dlp" : "ffmpeg") : path;
        var version = "";

        if (path.Length > 0)
        {
            // The probe spawns the binary and waits up to 5 s — off the UI thread, or a picked
            // console exe that ignores --version froze the window.
            SetRowStatus(rowStatus, ok: true, "?");
            var (ok, probedVersion) = await Task.Run(() =>
            {
                bool probed = YouTubeProcess.Runner.TryProbe(probeName, out var v);
                return (probed, v);
            });
            if (!ok)
            {
                rowStatus.Text = AppStrings.Get("Network_YouTube_PathFail", "실행할 수 없습니다");
                return;
            }
            version = probedVersion;
        }
        SetRowStatus(rowStatus, ok: true, version);

        var config = AppServices.Settings.YouTube;
        if (isYtDlp) config.YtDlpPath = path; else config.FfmpegPath = path;
        SettingsWriter.Schedule(AppServices.Settings);

        SwapRunnerFromSettings();
        var status = await Task.Run(YouTubeDependency.Probe);
        UpdateStatus(status, probing: false);
        await RefreshPathRowsAsync();
    }

    private static void SwapRunnerFromSettings()
    {
        var config = AppServices.Settings.YouTube;
        YouTubeProcess.Runner = new YouTubeProcessRunner(config.YtDlpPath, config.FfmpegPath);
    }

    // ---------------- recent grid ----------------

    private void RefreshRecentRows()
    {
        var entries = AppServices.YouTubeRecent.Entries;
        var rows = entries.Select(entry => new RecentRow(entry)).ToArray();
        RecentGrid.ItemsSource = rows;
        RecentEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentGrid.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var pair in rows.Zip(entries, (row, entry) => (row, entry)))
        {
            if (pair.entry.ThumbnailUrl == null) continue;
            _ = LoadThumbnailAsync(pair.row, pair.entry.ThumbnailUrl);
        }
    }

    private async Task LoadThumbnailAsync(RecentRow row, string thumbnailUrl)
    {
        try
        {
            // yt-dlp hands out WebP variants for some videos; the cache rejects (and BitmapImage
            // cannot decode) WebP, so request the JPEG variant of the same image instead. This
            // also repairs entries whose store already carries a .webp URL.
            var normalized = YouTubePageUrl.NormalizeThumbnailUrl(thumbnailUrl);
            if (normalized == null) return;
            var path = await _artCache.GetOrDownloadAsync(new Uri(normalized));
            if (path == null) return;
            DispatcherQueue.TryEnqueue(() => row.SetArt(path));
        }
        catch { /* thumbnails are decorative — never surface a download failure */ }
    }

    private static RecentRow? RowFromItem(object item) => item as RecentRow;

    private RecentRow? RowForUrl(string pageUrl) =>
        (RecentGrid.ItemsSource as System.Collections.IEnumerable)?
        .OfType<RecentRow>().FirstOrDefault(r =>
            string.Equals(r.PageUrl, pageUrl, StringComparison.OrdinalIgnoreCase));

    private async void OnRecentGridDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        // The first tap selects (selection mode), the second plays — matching the playlist and
        // library tables. Enter on the selected card plays too (keyboard parity, wired in ctor).
        if (RowFromItem(RecentGrid.SelectedItem) is { } row) await PlayRecentAsync(row, play: true);
        e.Handled = true;
    }

    private async void OnRecentPlayClick(object sender, RoutedEventArgs e)
    {
        if (RowFromSender(sender) is { } row) await PlayRecentAsync(row, play: true);
    }

    private async void OnRecentAddClick(object sender, RoutedEventArgs e)
    {
        if (RowFromSender(sender) is { } row) await PlayRecentAsync(row, play: false);
    }

    private async void OnRecentRemoveClick(object sender, RoutedEventArgs e)
    {
        if (RowFromSender(sender) is not { } row) return;

        // Same destructive-action rule as the radio favorites (PT4-05): confirm before the
        // entry is gone for good.
        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("Network_YouTube_DeleteTitle", "최근 항목 삭제"),
            Content = AppStrings.Format("Network_YouTube_DeleteMessage", "'{0}'을(를) 최근 항목에서 삭제할까요?", row.Title),
            PrimaryButtonText = AppStrings.Get("Msg_DeletePlaylistConfirm", "삭제"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        AppServices.YouTubeRecent.Remove(row.PageUrl);
        AppServices.YouTubeRecent.Save();
        RefreshRecentRows();
    }

    /// <summary>Replays a recent entry: adds the (already metadata-carrying) track and plays it.
    /// The dependency gate still runs at open — a stale configured path fails loudly there. The
    /// card shows its progress ring + "불러오는 중…" for the whole resolve+open window (seconds on
    /// the first play), because a silent click read as dead; the URL-box path shares the same
    /// section-level busy line via <see cref="SetBusy"/>.</summary>
    private async Task PlayRecentAsync(RecentRow row, bool play)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        try
        {
            var track = new Track
            {
                Path = row.PageUrl,
                SourceKind = TrackSourceKind.YouTube,
                Title = row.Title,
            };
            var playlists = AppServices.Playlists;
            var playlist = playlists.NowPlaying;
            var item = playlists.AddTracks(playlist, new[] { track }).FirstOrDefault();
            if (item != null && play)
            {
                // Same busy contract the URL submit path uses: the section status line AND the
                // card itself announce the work until the open settles.
                SetBusy(true);
                ClearBusyRow(keep: row);
                row.SetBusyState(AppStrings.Get("Network_YouTube_RowProcessing",
                    "불러오는 중…"));
                try
                {
                    await Controls.PlaybackUiHelper.PlayItemAsync(AppServices.Playback, playlist, item);
                }
                finally
                {
                    SetBusy(false);
                    // Clear the card regardless of outcome: success moves the Now Playing area,
                    // failure is explained by the controller's warning → InfoBar.
                    ClearBusyRow();
                }
            }
        }
        finally
        {
            Volatile.Write(ref _pending, 0);
        }
    }

    /// <summary>The card currently showing its progress ring, or null.</summary>
    private RecentRow? _busyRow;

    private void ClearBusyRow(RecentRow? keep = null)
    {
        if (_busyRow != null && !ReferenceEquals(_busyRow, keep))
        {
            _busyRow.SetBusyState(null);
        }
        _busyRow = keep;
    }

    private static RecentRow? RowFromSender(object sender) =>
        (sender as FrameworkElement)?.DataContext as RecentRow;
}
