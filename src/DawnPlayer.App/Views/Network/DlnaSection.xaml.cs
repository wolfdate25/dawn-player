using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Network.Dlna;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DawnPlayer.App.Views.Network;

/// <summary>One row of the browser list: either a folder or a playable track. Track rows carry a
/// thumbnail that fills in asynchronously from the shared art cache (folder rows keep the icon).</summary>
public sealed class DlnaRow : INotifyPropertyChanged
{
    public required bool IsTrack { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string IconGlyph { get; init; }
    public string ContainerId { get; init; } = "";
    public DidlItemEntry? Item { get; init; }

    private ImageSource? _art;
    public ImageSource? Art
    {
        get => _art;
        private set
        {
            if (ReferenceEquals(_art, value)) return;
            _art = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Art)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Must run on the UI thread — creates the XAML image source.</summary>
    public void SetArt(string localPath)
    {
        if (IsTrack) Art = new BitmapImage(new Uri(localPath));
    }

    public static DlnaRow FromContainer(DidlContainerEntry entry, string childrenText) =>
        new()
        {
            IsTrack = false,
            Title = entry.Title,
            Subtitle = childrenText,
            IconGlyph = "\uE8B7", // folder
            ContainerId = entry.Id,
        };

    public static DlnaRow FromItem(DidlItemEntry entry)
    {
        var duration = entry.Duration is { } d ? FormatDuration(d) : "";
        var subtitle = string.Join(" · ", new[] { entry.Artist ?? "", duration }.Where(s => s.Length > 0));
        return new DlnaRow
        {
            IsTrack = true,
            Title = entry.Title,
            Subtitle = subtitle,
            IconGlyph = "\uE8D6", // audio
            Item = entry,
        };
    }

    private static string FormatDuration(TimeSpan d) => d.TotalHours >= 1
        ? ((int)d.TotalHours) + d.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
        : d.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}

/// <summary>One breadcrumb segment; <see cref="Index"/> is the depth to truncate to when clicked.
/// Plain settable properties (not a positional record) because the XAML type-info generator emits
/// setters for every x:Bind target — the same constraint <c>Track</c> documents.</summary>
public sealed class DlnaCrumb
{
    public string Title { get; set; } = "";
    public int Index { get; set; }
}

/// <summary>
/// DLNA section of the Network tab: discovers servers over SSDP, browses their ContentDirectory,
/// and plays tracks through the same pipeline as everything else (N1 spool reader). Asynchronous
/// results are discarded unless their browse generation is still current, so a response racing a
/// server/folder switch can never repaint the wrong folder.
/// </summary>
public sealed partial class DlnaSection : UserControl
{
    private static readonly HttpClient DescriptionHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>RequestedCount sent for every browse page — owned here so the pagination UI's
    /// arithmetic (Loaded vs TotalMatches) rests on a value this layer controls.</summary>
    private const int BrowsePageSize = 500;

    private readonly ContentDirectoryClient _cds = new();
    private readonly DlnaArtCache _artCache = new();    private readonly ObservableCollection<DlnaCrumb> _crumbs = [];
    private readonly List<(string Id, string Title)> _path = [];
    private readonly List<DlnaRow> _rows = [];

    private List<DlnaServer> _servers = [];
    private DlnaServer? _server;
    private string _containerId = "0";
    private int _loaded;
    private int _totalMatches;
    private int _browseGeneration;
    private (bool Reset, int StartIndex) _lastBrowse = (true, 0);
    private bool _everSearched;

    public ObservableCollection<DlnaCrumb> Crumbs => _crumbs;

    public DlnaSection()
    {
        InitializeComponent();
    }

    /// <summary>First activation kicks off discovery; later activations keep current results.</summary>
    public void Activate()
    {
        if (!_everSearched)
        {
            _ = RefreshServersAsync();
        }
    }

    private async Task RefreshServersAsync()
    {
        _everSearched = true;
        SetBusy(true);
        try
        {
            var hits = await SsdpDiscovery.SearchAsync(TimeSpan.FromSeconds(3));
            var servers = new List<DlnaServer>();
            var seenUdn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hit in hits)
            {
                try
                {
                    var xml = await DescriptionHttp.GetStringAsync(hit.Location);
                    var server = DlnaDeviceDescriptionParser.TryParse(xml, hit.Location);
                    if (server == null || !seenUdn.Add(server.Udn)) continue;
                    servers.Add(server);
                }
                catch (Exception ex)
                {
                    App.Log($"[dlna] device description fetch failed for '{hit.Location}': {ex.Message}");
                }
            }

            _servers = servers;
            ServerList.ItemsSource = servers;
            ServerList.SelectedIndex = servers.Count > 0 ? 0 : -1;
            EmptyState.Visibility = servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EntryList.Visibility = servers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnRefreshServersClick(object sender, RoutedEventArgs e) => _ = RefreshServersAsync();

    private void OnServerSelected(object sender, SelectionChangedEventArgs e)
    {
        _server = ServerList.SelectedItem as DlnaServer;
        _path.Clear();
        _containerId = "0";
        if (_server != null) _ = BrowseAsync(reset: true);
    }

    private async Task BrowseAsync(bool reset, int startIndex = -1)
    {
        if (_server == null) return;

        var generation = ++_browseGeneration;
        var serverSnapshot = _server;
        var containerSnapshot = _containerId;
        if (reset)
        {
            startIndex = 0;
        }
        else if (startIndex < 0)
        {
            startIndex = _loaded;
        }
        _lastBrowse = (reset, startIndex);
        BrowseRetryButton.Visibility = Visibility.Collapsed;

        SetBusy(true);
        try
        {
            var page = await _cds.BrowseAsync(serverSnapshot, containerSnapshot, startIndex, BrowsePageSize);
            // Anything that changed the browse target since the request went out invalidates this.
            if (generation != _browseGeneration) return;

            if (reset)
            {
                _rows.Clear();
                _loaded = 0;
            }
            _totalMatches = page.TotalMatches;
            var artLoads = 0;
            foreach (var entry in page.Entries)
            {
                switch (entry)
                {
                    case DidlContainerEntry container:
                        _rows.Add(DlnaRow.FromContainer(container,
                            container.ChildCount is { } n
                                ? AppStrings.Format("Network_Dlna_ChildCountFormat", "{0}개", n)
                                : ""));
                        break;
                    case DidlItemEntry item when DlnaTrackFactory.TryCreate(item, serverSnapshot.DescriptionUrl) != null:
                        var row = DlnaRow.FromItem(item);
                        _rows.Add(row);
                        // Album art per row, capped per page — the cache dedupes repeats and a
                        // failed download just leaves the audio icon in place.
                        if (row.Item?.AlbumArtUri != null && artLoads < 40)
                        {
                            artLoads++;
                            _ = LoadRowArtAsync(row, row.Item.AlbumArtUri);
                        }
                        break;
                    // Non-audio items are invisible: this browser is a music browser.
                }
            }
            _loaded += page.NumberReturned;

            EntryList.ItemsSource = _rows.ToArray();
            LoadMoreButton.Visibility = _loaded < _totalMatches ? Visibility.Visible : Visibility.Collapsed;
            RebuildCrumbs();
        }
        catch (Exception ex)
        {
            App.Log($"[dlna] browse failed on {serverSnapshot.FriendlyName}: {ex.Message}");
            AppServices.RaiseWarning(AppStrings.Format("Network_Dlna_BrowseFailed", "항목을 불러오지 못했습니다: {0}", ex.Message));
            // The failed request is retryable as-is (same container, same page offset).
            if (generation == _browseGeneration) BrowseRetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnBrowseRetryClick(object sender, RoutedEventArgs e)
    {
        BrowseRetryButton.Visibility = Visibility.Collapsed;
        var (reset, startIndex) = _lastBrowse;
        _ = BrowseAsync(reset, startIndex);
    }

    private async Task LoadRowArtAsync(DlnaRow row, Uri albumArtUri)
    {
        try
        {
            var path = await _artCache.GetOrDownloadAsync(albumArtUri);
            if (path == null) return;
            DispatcherQueue.TryEnqueue(() => row.SetArt(path));
        }
        catch { /* thumbnails are decorative — never surface a download failure */ }
    }

    private void RebuildCrumbs()
    {
        _crumbs.Clear();
        _crumbs.Add(new DlnaCrumb { Title = AppStrings.Get("Network_Dlna_Root", "루트"), Index = 0 });
        for (int i = 0; i < _path.Count; i++)
        {
            _crumbs.Add(new DlnaCrumb { Title = _path[i].Title, Index = i + 1 });
        }
    }

    private void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not int depth) return;
        if (depth >= _path.Count) return;

        var target = depth == 0 ? ("0", "") : _path[depth - 1];
        _path.RemoveRange(depth, _path.Count - depth);
        _containerId = target.Item1;
        _ = BrowseAsync(reset: true);
    }

    private void OnEntryItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DlnaRow row || row.IsTrack) return;
        _path.Add((row.ContainerId, row.Title));
        _containerId = row.ContainerId;
        _ = BrowseAsync(reset: true);
    }

    private async void OnEntryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (EntryList.SelectedItem is DlnaRow { IsTrack: true } row && row.Item != null)
        {
            await PlayRowAsync(row, play: true);
        }
    }

    private async void OnPlayItemClick(object sender, RoutedEventArgs e)
    {
        if (RowFromSender(sender) is { IsTrack: true } row && row.Item != null)
        {
            await PlayRowAsync(row, play: true);
        }
    }

    private async void OnAddItemClick(object sender, RoutedEventArgs e)
    {
        if (RowFromSender(sender) is { IsTrack: true } row && row.Item != null)
        {
            await PlayRowAsync(row, play: false);
        }
    }

    private static DlnaRow? RowFromSender(object sender) =>
        (sender as FrameworkElement)?.DataContext as DlnaRow;

    private void OnLoadMoreClick(object sender, RoutedEventArgs e) => _ = BrowseAsync(reset: false);

    /// <summary>Re-entrancy guard for the play/add flow: DLNA opens download the whole file, so a
    /// double-click or impatient second click must not queue duplicate rows.</summary>
    private int _playPending;

    private async Task PlayRowAsync(DlnaRow row, bool play)
    {
        if (_server == null || row.Item == null) return;
        if (Interlocked.Exchange(ref _playPending, 1) == 1) return;

        // Opening a DLNA track downloads the whole file before audio starts — tell the user
        // instead of leaving a silent multi-second gap.
        PlayStatusRow.Visibility = Visibility.Visible;
        try
        {
            var track = DlnaTrackFactory.TryCreate(row.Item, _server.DescriptionUrl);
            if (track == null)
            {
                AppServices.RaiseWarning(AppStrings.Get("Network_Dlna_Unplayable", "재생할 수 있는 오디오 형식이 없습니다."));
                return;
            }

            try
            {
                if (row.Item.AlbumArtUri != null)
                {
                    var artPath = await _artCache.GetOrDownloadAsync(row.Item.AlbumArtUri);
                    if (artPath != null) track.ArtPath = artPath;
                }

                var playlists = AppServices.Playlists;
                var playlist = playlists.NowPlaying;
                var item = playlists.AddTracks(playlist, new[] { track }).FirstOrDefault();
                if (item != null && play)
                {
                    await Controls.PlaybackUiHelper.PlayItemAsync(AppServices.Playback, playlist, item);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[dlna] play failed for '{track.Path}': {ex.Message}");
                AppServices.RaiseWarning(ex.Message);
            }
        }
        finally
        {
            Volatile.Write(ref _playPending, 0);
            PlayStatusRow.Visibility = Visibility.Collapsed;
        }
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
    }
}
