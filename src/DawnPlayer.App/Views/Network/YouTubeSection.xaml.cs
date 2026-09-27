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
/// fills in asynchronously once its download from the shared art cache lands.</summary>
public sealed class RecentRow : INotifyPropertyChanged
{
    public string PageUrl { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string DurationBadge { get; }
    public Visibility DurationBadgeVisibility { get; }
    public ImageSource? Art { get; private set; }

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
                    await Controls.PlaybackUiHelper.PlayItemAsync(AppServices.Playback, playlist, item);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // The resolve/chain failure reason is the useful part — surface it here and in
                // the InfoBar, and leave a retry in place.
                StatusText.Text = AppStrings.Format("Network_YouTube_ResolveFailed",
                    "항목을 가져오지 못했습니다: {0}", TrimDetail(ex.Message));
                RetryButton.Visibility = Visibility.Visible;
                _lastFailed = (play, normalized);
                AppServices.RaiseWarning(StatusText.Text);
                return;
            }
            finally
            {
                SetBusy(false);
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
        RetryButton.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = busy;
        if (busy)
        {
            StatusRow.Visibility = Visibility.Visible;
            StatusText.Text = AppStrings.Get("Network_YouTube_Status_Resolving",
                "해석 중… — 첫 재생까지 몇 초 걸립니다");
        }
        else if (BusyRing.IsActive == false && StatusText.Text.Length == 0)
        {
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
        RefreshPathRows();          // row-level probe: what each configured binary actually does
        _ = RefreshDependencyAsync(); // badge-level probe: replaces the old dedicated Re-check button
    }

    private void RefreshPathRows()
    {
        var config = AppServices.Settings.YouTube;
        YtDlpPathBox.Text = config.YtDlpPath.Length == 0 ? DefaultPathDisplay : config.YtDlpPath;
        FfmpegPathBox.Text = config.FfmpegPath.Length == 0 ? DefaultPathDisplay : config.FfmpegPath;
        var ytTarget = config.YtDlpPath.Length == 0 ? "yt-dlp" : config.YtDlpPath;
        var ffTarget = config.FfmpegPath.Length == 0 ? "ffmpeg" : config.FfmpegPath;
        var ytOk = YouTubeProcess.Runner.TryProbe(ytTarget, out var ytVersion);
        SetRowStatus(YtDlpRowStatus, ytOk, ytVersion);
        var ffOk = YouTubeProcess.Runner.TryProbe(ffTarget, out var ffVersion);
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

        if (path.Length > 0 && !YouTubeProcess.Runner.TryProbe(probeName, out version))
        {
            rowStatus.Text = AppStrings.Get("Network_YouTube_PathFail", "실행할 수 없습니다");
            return;
        }
        SetRowStatus(rowStatus, ok: true, version);

        var config = AppServices.Settings.YouTube;
        if (isYtDlp) config.YtDlpPath = path; else config.FfmpegPath = path;
        SettingsWriter.Schedule(AppServices.Settings);

        SwapRunnerFromSettings();
        var status = await Task.Run(YouTubeDependency.Probe);
        UpdateStatus(status, probing: false);
        RefreshPathRows();
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
            var path = await _artCache.GetOrDownloadAsync(new Uri(thumbnailUrl));
            if (path == null) return;
            DispatcherQueue.TryEnqueue(() => row.SetArt(path));
        }
        catch { /* thumbnails are decorative — never surface a download failure */ }
    }

    private static RecentRow? RowFromItem(object item) => item as RecentRow;

    private async void OnRecentItemClick(object sender, ItemClickEventArgs e)
    {
        if (RowFromItem(e.ClickedItem) is not { } row) return;
        await PlayRecentAsync(row, play: true);
    }

    private async void OnRecentPlayClick(object sender, RoutedEventArgs e)
    {
        if (RowFromSender(sender) is { } row) await PlayRecentAsync(row, play: true);
    }

    private async void OnRecentAddClick(object sender, RoutedEventArgs e)
    {
        if (RowFromSender(sender) is { } row) await PlayRecentAsync(row, play: false);
    }

    private void OnRecentRemoveClick(object sender, RoutedEventArgs e)
    {
        if (RowFromSender(sender) is not { } row) return;
        AppServices.YouTubeRecent.Remove(row.PageUrl);
        AppServices.YouTubeRecent.Save();
        RefreshRecentRows();
    }

    /// <summary>Replays a recent entry: adds the (already metadata-carrying) track and plays it.
    /// The dependency gate still runs at open — a stale configured path fails loudly there.</summary>
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
                await Controls.PlaybackUiHelper.PlayItemAsync(AppServices.Playback, playlist, item);
            }
        }
        finally
        {
            Volatile.Write(ref _pending, 0);
        }
    }

    private static RecentRow? RowFromSender(object sender) =>
        (sender as FrameworkElement)?.DataContext as RecentRow;
}
