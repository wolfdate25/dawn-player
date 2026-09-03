using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DawnPlayer.App.Controls;
using DawnPlayer.App.Helpers;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using DawnPlayer.Core.Util;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

namespace DawnPlayer.App.Views;

public sealed partial class LibraryPage : Page
{
    public FastObservableCollection<AlbumRowVm> AlbumRows { get; } = new();
    public FastObservableCollection<AlbumCard> AlbumCards { get; } = new();
    private List<AlbumCard> _allBuiltCards = new();
    private int _lastColumnCount = -1;
    private Playlist? _observedPlaylist;

    public static readonly DependencyProperty CurrentCoverCardWidthProperty =
        DependencyProperty.Register(nameof(CurrentCoverCardWidth), typeof(double), typeof(LibraryPage), new PropertyMetadata(144.0));

    public static readonly DependencyProperty CurrentCoverImageHeightProperty =
        DependencyProperty.Register(nameof(CurrentCoverImageHeight), typeof(double), typeof(LibraryPage), new PropertyMetadata(140.0));

    public double CurrentCoverCardWidth
    {
        get => (double)GetValue(CurrentCoverCardWidthProperty);
        set => SetValue(CurrentCoverCardWidthProperty, value);
    }

    public double CurrentCoverImageHeight
    {
        get => (double)GetValue(CurrentCoverImageHeightProperty);
        set => SetValue(CurrentCoverImageHeightProperty, value);
    }

    private TreeGroupMode _treeMode = TreeGroupMode.ArtistAlbum;
    private LibraryTreeNode? _selectedNode;
    private string _search = "";
    private List<Track> _visible = new();
    private readonly DispatcherTimer _rebuildDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _resizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private bool _viewReady;
    private bool _libraryDirty;

    private SortColumn _currentSort = SortColumn.None;
    private bool _sortAscending = true;
    private bool _isSettingCoverSize;
    private bool _restoringLayout;

    // Splitter resizers
    private SplitterResizer _leftResizer = null!;
    private SplitterResizer _rightResizer = null!;
    private SplitterResizer _lyricsResizer = null!;

    public LibraryPage()
    {
        InitializeComponent();
        InitializeSplitters();

        // See the comment on the TreeView in LibraryPage.xaml: the item handles Enter first, so the
        // handler has to opt into already-handled events to see it at all.
        LibraryTree.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnTreeKeyDown), true);

        Loaded += (_, _) =>
        {
            if (!_viewReady)
            {
                _viewReady = true;
                AppServices.LibraryChanged += OnLibraryChanged;
                AppServices.ScanProgressChanged += OnScanProgress;
                AppServices.CurrentTrackChanged += OnCurrentTrackChanged;
                AppServices.QueueChanged += OnPlaylistOrQueueChanged;
                SubscribeCurrentPlaylistItems();

                RestoreLayoutSettings();
                RebuildAll();
                _libraryDirty = false;
            }
        };

        _rebuildDebounce.Tick += (_, _) => { _rebuildDebounce.Stop(); RebuildAll(); };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilters(); };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            if (CoverGridViewContainer == null || _allBuiltCards.Count == 0) return;
            double width = CoverGridViewContainer.ActualWidth;
            if (width <= 0) return;
            double itemWidth = CurrentCoverCardWidth + 12;
            int cols = Math.Max(1, (int)((width - 28) / itemWidth));
            if (cols != _lastColumnCount)
            {
                RechunkAlbumRows();
            }
        };

        KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                var openRow = AlbumRows.FirstOrDefault(r => r.IsDrawerOpen);
                if (openRow != null)
                {
                    openRow.CloseDrawer();
                    e.Handled = true;
                }
            }
        };
    }

    private void InitializeSplitters()
    {
        _leftResizer = new SplitterResizer(
            MainLayoutGrid, LeftSplitterLine, 140, 550, invertDelta: false,
            () => ColLeft.ActualWidth,
            w => ColLeft.Width = new GridLength(w),
            cursor => ProtectedCursor = cursor,
            w => { if (AppServices.Settings != null) { AppServices.Settings.Ui.LeftSidebarWidth = w; SettingsWriter.Schedule(AppServices.Settings); } });

        _rightResizer = new SplitterResizer(
            MainLayoutGrid, RightSplitterLine, 180, 500, invertDelta: true,
            () => ColRight.ActualWidth,
            w => ColRight.Width = new GridLength(w),
            cursor => ProtectedCursor = cursor,
            w => { if (AppServices.Settings != null) { AppServices.Settings.Ui.RightSidebarWidth = w; SettingsWriter.Schedule(AppServices.Settings); } });

        _lyricsResizer = new SplitterResizer(
            MainLayoutGrid, LyricsSplitterLine, 200, 450, invertDelta: true,
            () => LibraryLyricsPane.ActualWidth,
            w => LibraryLyricsPane.Width = w,
            cursor => ProtectedCursor = cursor,
            w => { if (AppServices.Settings != null) { AppServices.Settings.Ui.LyricsSidebarWidth = w; SettingsWriter.Schedule(AppServices.Settings); } });
    }

    private void RestoreLayoutSettings()
    {
        // Assigning TreeGroupModeBox.SelectedIndex below raises SelectionChanged, which used to
        // clear the persisted tree filter — so the saved library selection was wiped on every
        // startup for any group mode other than the first.
        _restoringLayout = true;
        try
        {
            RestoreLayoutSettingsCore();
        }
        finally
        {
            _restoringLayout = false;
        }
    }

    private void RestoreLayoutSettingsCore()
    {
        var ui = AppServices.Settings.Ui;
        if (ui.LeftSidebarWidth >= 150) ColLeft.Width = new GridLength(ui.LeftSidebarWidth);
        if (ui.RightSidebarWidth >= 180) ColRight.Width = new GridLength(ui.RightSidebarWidth);
        if (ui.LyricsSidebarWidth >= 200) LibraryLyricsPane.Width = ui.LyricsSidebarWidth;
        SetCoverSize(ui.AlbumCoverSize > 0 ? ui.AlbumCoverSize : 144);

        if (TreeGroupModeBox != null && ui.LibraryTreeGroupMode >= 0 && ui.LibraryTreeGroupMode <= 6)
        {
            // Set the backing field first so state stays consistent regardless of handler order.
            _treeMode = (TreeGroupMode)ui.LibraryTreeGroupMode;
            TreeGroupModeBox.SelectedIndex = ui.LibraryTreeGroupMode;
        }

        bool isList = ui.LibraryViewMode == 1;
        if (ViewListBtn != null && ViewGridBtn != null)
        {
            ViewListBtn.IsChecked = isList;
            ViewGridBtn.IsChecked = !isList;
        }
        if (TrackListViewContainer != null) TrackListViewContainer.Visibility = isList ? Visibility.Visible : Visibility.Collapsed;
        if (CoverGridViewContainer != null) CoverGridViewContainer.Visibility = isList ? Visibility.Collapsed : Visibility.Visible;

        _currentSort = (SortColumn)Math.Clamp(ui.LibrarySortColumn, 0, 5);
        _sortAscending = ui.LibrarySortAscending;
        UpdateHeaderIndicators();
    }

    public void ActivatePage()
    {
        if (!_viewReady)
        {
            _viewReady = true;
            AppServices.LibraryChanged += OnLibraryChanged;
            AppServices.ScanProgressChanged += OnScanProgress;
            AppServices.CurrentTrackChanged += OnCurrentTrackChanged;
            AppServices.QueueChanged += OnPlaylistOrQueueChanged;
            SubscribeCurrentPlaylistItems();

            RestoreLayoutSettings();
            RebuildAll();
            _libraryDirty = false;
        }
        else if (_libraryDirty)
        {
            RebuildAll();
            _libraryDirty = false;
        }
        else
        {
            // Zero-cost instant navigation: update live playing highlight state on right queue & open drawers
            UpdateRightQueuePlayingState(AppServices.Playback.CurrentItem);
            UpdateDrawerPlayingState(AppServices.Playback.CurrentItem?.Track?.Path);
        }

        SetLyricsVisibility(AppServices.Settings.Ui.ShowLyricsPane);
        LibraryLyricsPane.OnTrackChanged(AppServices.Playback.CurrentItem);
        RefreshRightQueuePanel();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ActivatePage();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
    }

    private void SubscribeCurrentPlaylistItems()
    {
        if (_observedPlaylist != null)
        {
            _observedPlaylist.Items.CollectionChanged -= OnObservedPlaylistItemsChanged;
        }
        _observedPlaylist = AppServices.Playlists.NowPlaying;
        if (_observedPlaylist != null)
        {
            _observedPlaylist.Items.CollectionChanged += OnObservedPlaylistItemsChanged;
        }
    }

    private void UnsubscribeCurrentPlaylistItems()
    {
        if (_observedPlaylist != null)
        {
            _observedPlaylist.Items.CollectionChanged -= OnObservedPlaylistItemsChanged;
            _observedPlaylist = null;
        }
    }

    private void OnObservedPlaylistItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(RefreshRightQueuePanel);
    }

    public void SetLyricsVisibility(bool show)
    {
        LyricsPanelContainer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LibraryLyricsPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            LibraryLyricsPane.OnTrackChanged(AppServices.Playback.CurrentItem);
        }
    }

    private void OnPlaylistOrQueueChanged()
    {
        DispatcherQueue.TryEnqueue(RefreshRightQueuePanel);
    }

    private void OnCurrentTrackChanged(PlaylistItem? item)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            LibraryLyricsPane.OnTrackChanged(item);
            UpdateRightQueuePlayingState(item);
            UpdateDrawerPlayingState(item?.Track?.Path);
            ScrollRightQueueToItem(item);
        });
    }

    private void UpdateDrawerPlayingState(string? playingTrackPath)
    {
        foreach (var row in AlbumRows)
        {
            row.UpdatePlayingState(playingTrackPath);
        }
    }

    public void RefreshRightQueuePanel()
    {
        var pl = AppServices.Playlists.NowPlaying;
        if (pl != _observedPlaylist)
        {
            SubscribeCurrentPlaylistItems();
        }
        var groups = PlaylistGroupBuilder.BuildGroups(pl);
        RightQueueCvs.Source = groups;
        RightQueueStatsText.Text = PlaybackUiHelper.FormatEolePlaylistStats(pl);
        UpdateRightQueuePlayingState(AppServices.Playback.CurrentItem);
        ScrollRightQueueToItem(AppServices.Playback.CurrentItem);
    }

    private static void UpdateRightQueuePlayingState(PlaylistItem? currentItem)
    {
        PlaybackUiHelper.UpdatePlayingState(AppServices.Playlists.NowPlaying?.Items, currentItem);
    }

    private void OnLibraryChanged()
    {
        _libraryDirty = true;
        if (IsLoaded)
        {
            DispatcherQueue.TryEnqueue(ScheduleRebuild);
        }
    }

    private void OnScanProgress(DawnPlayer.Core.Library.ScanProgress p)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (p.Finished)
            {
                ScanProgress.Visibility = Visibility.Collapsed;
                ScanProgress.Value = p.Total > 0 ? 100 : 0;
                RescanButton.IsEnabled = true;
            }
            else
            {
                ScanProgress.Visibility = Visibility.Visible;
                ScanProgress.Maximum = Math.Max(1, p.Total);
                ScanProgress.Value = p.Done;
                RescanButton.IsEnabled = false;
            }
        });
    }

    private void ScheduleRebuild()
    {
        if (!_rebuildDebounce.IsEnabled) _rebuildDebounce.Start();
        else { _rebuildDebounce.Stop(); _rebuildDebounce.Start(); }
    }

    public void FocusSearch()
    {
        SearchBox.Focus(FocusState.Keyboard);
    }

    // ---------------- data flow ----------------

    private void RebuildAll()
    {
        RebuildTree();
        ApplyFilters();
    }

    private void RebuildTree()
    {
        var tracks = AppServices.Library.Tracks;
        var allTvNode = LibraryTreeBuilder.BuildTree(tracks, _treeMode, LibraryTree.RootNodes);

        TreeViewNode? matchingNode = null;
        var savedType = AppServices.Settings.Ui.LibrarySelectedFilterType;
        var savedVal = AppServices.Settings.Ui.LibrarySelectedFilterValue;
        var savedExtra = AppServices.Settings.Ui.LibrarySelectedFilterExtra;

        if (!string.IsNullOrEmpty(savedType))
        {
            matchingNode = LibraryTreeBuilder.FindNodeRecursive(LibraryTree.RootNodes, savedType, savedVal, savedExtra);
        }

        if (matchingNode != null)
        {
            _selectedNode = matchingNode.Content as LibraryTreeNode;
            LibraryTree.SelectedNodes.Clear();
            LibraryTree.SelectedNodes.Add(matchingNode);
            LibraryTreeBuilder.ExpandAncestors(matchingNode);
        }
        else if (_selectedNode == null)
        {
            _selectedNode = allTvNode.Content as LibraryTreeNode;
            LibraryTree.SelectedNodes.Clear();
            LibraryTree.SelectedNodes.Add(allTvNode);
        }
    }

    private void ApplyFilters()
    {
        _visible = LibraryFilterService.FilterAndSort(
            AppServices.Library.Tracks,
            _selectedNode,
            _search,
            _currentSort,
            _sortAscending);

        TracksList.ItemsSource = _visible;

        // Rebuild Album Cards & Rows for Grid View using batch ReplaceAll
        _allBuiltCards = LibraryFilterService.BuildAlbumCards(_visible, AppServices.Settings.Ui.AlbumCoverSize);
        AlbumCards.ReplaceAll(_allBuiltCards);
        RechunkAlbumRows();

        var totalMs = _visible.Sum(t => t.DurationMs);
        string nodeLabel = _selectedNode?.Title ?? "Mixed selection";
        StatusText.Text = _visible.Count == 0
            ? AppStrings.Get("Msg_LibraryEmptyStatus", "트랙 없음 — 설정에서 음악 폴더를 추가하고 스캔하세요.")
            : AppStrings.Format("Msg_LibraryStatusBarFormat", nodeLabel, TextFormat.LongDuration(TimeSpan.FromMilliseconds(totalMs)), _visible.Count, AlbumCards.Count);
    }

    private void RechunkAlbumRows()
    {
        double width = CoverGridViewContainer?.ActualWidth ?? 0;
        if (width <= 0) width = 1000;

        double itemWidth = CurrentCoverCardWidth + 12;
        int cols = Math.Max(1, (int)((width - 28) / itemWidth));
        _lastColumnCount = cols;

        var openAlbum = AlbumRows.FirstOrDefault(r => r.IsDrawerOpen)?.SelectedAlbum;
        var currentPath = AppServices.Playback?.CurrentItem?.Track?.Path;

        var newRows = new List<AlbumRowVm>((_allBuiltCards.Count / cols) + 1);
        int rowIndex = 0;
        for (int i = 0; i < _allBuiltCards.Count; i += cols)
        {
            var rowVm = new AlbumRowVm { RowIndex = rowIndex++ };
            // Index directly instead of Skip(i).Take(cols): Skip on a List walks i elements, so
            // chunking n cards this way costs O(n^2/cols) enumeration steps for no reason.
            int end = Math.Min(i + cols, _allBuiltCards.Count);
            for (int j = i; j < end; j++)
            {
                rowVm.Cards.Add(_allBuiltCards[j]);
            }

            if (openAlbum != null && rowVm.Cards.Any(c => ReferenceEquals(c, openAlbum) || (!string.IsNullOrEmpty(openAlbum.Key) && c.Key == openAlbum.Key)))
            {
                var match = rowVm.Cards.First(c => ReferenceEquals(c, openAlbum) || (!string.IsNullOrEmpty(openAlbum.Key) && c.Key == openAlbum.Key));
                rowVm.OpenDrawer(match, currentPath);
                openAlbum = null;
            }

            newRows.Add(rowVm);
        }

        AlbumRows.ReplaceAll(newRows);
    }

    private void OnCoverGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || _allBuiltCards.Count == 0) return;
        double itemWidth = CurrentCoverCardWidth + 12;
        int cols = Math.Max(1, (int)((e.NewSize.Width - 28) / itemWidth));
        if (cols != _lastColumnCount)
        {
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        }
    }

    // ---------------- tree events ----------------

    private void OnTreeGroupModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady || _restoringLayout) return;
        _treeMode = (TreeGroupMode)TreeGroupModeBox.SelectedIndex;
        _selectedNode = null;
        AppServices.Settings.Ui.LibraryTreeGroupMode = TreeGroupModeBox.SelectedIndex;
        AppServices.Settings.Ui.LibrarySelectedFilterType = null;
        AppServices.Settings.Ui.LibrarySelectedFilterValue = null;
        AppServices.Settings.Ui.LibrarySelectedFilterExtra = null;
        SettingsWriter.Schedule(AppServices.Settings);
        RebuildTree();
        ApplyFilters();
    }

    private void SelectTreeNode(LibraryTreeNode? node)
    {
        if (node == null) return;

        // Prevent redundant filter recalculation and destructive UIElement layout churn on same node
        if (ReferenceEquals(_selectedNode, node) ||
            (_selectedNode != null &&
             _selectedNode.FilterType == node.FilterType &&
             _selectedNode.FilterValue == node.FilterValue &&
             _selectedNode.FilterExtra == node.FilterExtra &&
             _selectedNode.FilterExtra2 == node.FilterExtra2))
        {
            return;
        }

        _selectedNode = node;
        SaveTreeSelection(node);
        ApplyFilters();
    }

    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        TreeViewNode? tvn = null;
        if (args.InvokedItem is TreeViewNode directNode) tvn = directNode;
        else if (args.InvokedItem is LibraryTreeNode tn) tvn = sender.SelectedNodes.FirstOrDefault(n => n.Content == tn);

        if (tvn?.Content is LibraryTreeNode ctn)
        {
            SelectTreeNode(ctn);
        }
    }

    private void OnTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNodes.Count > 0 && sender.SelectedNodes[0].Content is LibraryTreeNode node)
        {
            SelectTreeNode(node);
        }
    }

    private async void OnTreeItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (sender is FrameworkElement fe && fe.DataContext is TreeViewNode directNode)
            {
                if (directNode.Content is LibraryTreeNode ctn)
                {
                    _selectedNode = ctn;
                    SaveTreeSelection(ctn);
                    ApplyFilters();
                }
            }

            await PlayCurrentTreeSelectionAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[OnTreeItemDoubleTapped Error] {ex}");
        }
    }

    private async void OnTreeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            try
            {
                await PlayCurrentTreeSelectionAsync();
            }
            catch (Exception ex)
            {
                App.Log($"[OnTreeKeyDown Error] {ex}");
            }
        }
    }

    private async Task PlayCurrentTreeSelectionAsync()
    {
        if (_visible.Count == 0) return;
        await PlaybackUiHelper.PlayAlbumNowPlayingAsync(
            AppServices.Playlists, AppServices.Playback, _visible, 0);
    }

    private async void OnTreeContextMenuPlay(object sender, RoutedEventArgs e)
    {
        try
        {
            await PlayCurrentTreeSelectionAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[OnTreeContextMenuPlay Error] {ex}");
        }
    }

    private void OnTreeContextMenuAddToPlaylist(object sender, RoutedEventArgs e)
    {
        if (_visible.Count == 0) return;
        var items = PlaybackUiHelper.AddTracksToNowPlaying(AppServices.Playlists, _visible);
        if (items.Count > 0)
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToCurrentPlaylist", items.Count));
    }

    private void OnTreeContextMenuEnqueue(object sender, RoutedEventArgs e)
    {
        if (_visible.Count == 0) return;
        var items = PlaybackUiHelper.EnqueueAlbumNowPlaying(
            AppServices.Playlists, AppServices.Playback, _visible);
        if (items.Count > 0)
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToQueue", items.Count));
    }

    private void OnTreeContextMenuOpening(object? sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            var subMenu = flyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(i => (i.Tag as string) == SendToPlaylistTag);
            PopulatePlaylistSubMenu(subMenu, () => _visible.ToList());
        }
    }

    private static void SaveTreeSelection(LibraryTreeNode node)
    {
        AppServices.Settings.Ui.LibrarySelectedFilterType = node.FilterType;
        AppServices.Settings.Ui.LibrarySelectedFilterValue = node.FilterValue;
        AppServices.Settings.Ui.LibrarySelectedFilterExtra = node.FilterExtra;
        SettingsWriter.Schedule(AppServices.Settings);
    }

    // ---------------- column sorting ----------------

    private void SortBy(SortColumn col)
    {
        if (_currentSort == col)
            _sortAscending = !_sortAscending;
        else
        {
            _currentSort = col;
            _sortAscending = true;
        }

        AppServices.Settings.Ui.LibrarySortColumn = (int)_currentSort;
        AppServices.Settings.Ui.LibrarySortAscending = _sortAscending;
        SettingsWriter.Schedule(AppServices.Settings);

        UpdateHeaderIndicators();
        ApplyFilters();
    }

    private void UpdateHeaderIndicators()
    {
        string arrow = _sortAscending ? " ▲" : " ▼";
        HeaderTrackNo.Text = "#" + (_currentSort == SortColumn.TrackNo ? arrow : "");
        HeaderTitle.Text = AppStrings.Get("Library_Header_Title.Text", "제목") + (_currentSort == SortColumn.Title ? arrow : "");
        HeaderArtist.Text = AppStrings.Get("Library_Header_Artist.Text", "아티스트") + (_currentSort == SortColumn.Artist ? arrow : "");
        HeaderAlbum.Text = AppStrings.Get("Library_Header_Album.Text", "앨범") + (_currentSort == SortColumn.Album ? arrow : "");
        HeaderDuration.Text = AppStrings.Get("Library_Header_Duration.Text", "길이") + (_currentSort == SortColumn.Duration ? arrow : "");
    }
    private void OnSortByTrackNo(object sender, RoutedEventArgs e) => SortBy(SortColumn.TrackNo);
    private void OnSortByTitle(object sender, RoutedEventArgs e) => SortBy(SortColumn.Title);
    private void OnSortByArtist(object sender, RoutedEventArgs e) => SortBy(SortColumn.Artist);
    private void OnSortByAlbum(object sender, RoutedEventArgs e) => SortBy(SortColumn.Album);
    private void OnSortByDuration(object sender, RoutedEventArgs e) => SortBy(SortColumn.Duration);

    private void ScrollRightQueueToItem(PlaylistItem? item)
    {
        if (item == null || RightQueueList == null) return;
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var target = PlaybackUiHelper.FindItemToScroll(AppServices.Playlists.NowPlaying?.Items, item);
            if (target != null)
            {
                try
                {
                    RightQueueList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
                }
                catch (Exception ex)
                {
                    App.Log($"[ScrollRightQueue Error] {ex}");
                }
            }
        });
    }

    // ---------------- view mode ----------------

    private void SetViewMode(bool grid)
    {
        if (TrackListViewContainer != null) TrackListViewContainer.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        if (CoverGridViewContainer != null) CoverGridViewContainer.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        AppServices.Settings.Ui.LibraryViewMode = grid ? 0 : 1;
        SettingsWriter.Schedule(AppServices.Settings);
    }

    private void OnViewGridClick(object sender, RoutedEventArgs e) => SetViewMode(true);
    private void OnViewListClick(object sender, RoutedEventArgs e) => SetViewMode(false);

    // ---------------- Eole In-line Album Tracklist Drawer (Showlist) ----------------

    private void OnAlbumCardTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        var card = VisualTreeHelperExtensions.ResolveItem<AlbumCard>(e)
            ?? (sender as FrameworkElement)?.DataContext as AlbumCard;
        if (card == null) return;

        var targetRow = AlbumRows.FirstOrDefault(r => r.Cards.Contains(card));
        if (targetRow == null) return;

        if (targetRow.IsDrawerOpen && targetRow.SelectedAlbum == card)
        {
            targetRow.CloseDrawer();
            return;
        }

        foreach (var r in AlbumRows)
        {
            if (r != targetRow && r.IsDrawerOpen)
            {
                r.CloseDrawer();
            }
        }

        var currentPath = AppServices.Playback?.CurrentItem?.Track?.Path;
        targetRow.OpenDrawer(card, currentPath);
    }

    private void OnAlbumCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var card = VisualTreeHelperExtensions.ResolveItem<AlbumCard>(e)
            ?? (sender as FrameworkElement)?.DataContext as AlbumCard;
        if (card != null && card.Tracks.Count > 0)
        {
            _ = PlaybackUiHelper.PlayAlbumNowPlayingAsync(AppServices.Playlists, AppServices.Playback, card.Tracks, 0);
        }
    }

    private void OnCloseRowDrawerClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AlbumRowVm row)
        {
            row.CloseDrawer();
        }
    }

    private void OnDrawerPlayAlbumClick(object sender, RoutedEventArgs e)
    {
        var row = AlbumRows.FirstOrDefault(r => r.IsDrawerOpen && r.SelectedAlbum != null);
        if (row?.SelectedAlbum != null && row.SelectedAlbum.Tracks.Count > 0)
        {
            _ = PlaybackUiHelper.PlayAlbumNowPlayingAsync(AppServices.Playlists, AppServices.Playback, row.SelectedAlbum.Tracks, 0);
        }
    }

    private void OnDrawerEnqueueAlbumClick(object sender, RoutedEventArgs e)
    {
        var row = AlbumRows.FirstOrDefault(r => r.IsDrawerOpen && r.SelectedAlbum != null);
        if (row?.SelectedAlbum != null && row.SelectedAlbum.Tracks.Count > 0)
        {
            PlaybackUiHelper.EnqueueAlbumNowPlaying(AppServices.Playlists, AppServices.Playback, row.SelectedAlbum.Tracks);
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToQueue", row.SelectedAlbum.Tracks.Count));
        }
    }

    private void OnDrawerAddToPlaylistClick(object sender, RoutedEventArgs e)
    {
        var row = AlbumRows.FirstOrDefault(r => r.IsDrawerOpen && r.SelectedAlbum != null);
        if (row?.SelectedAlbum != null && row.SelectedAlbum.Tracks.Count > 0)
        {
            PlaybackUiHelper.AddTracksToNowPlaying(AppServices.Playlists, row.SelectedAlbum.Tracks);
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToCurrentPlaylist", row.SelectedAlbum.Tracks.Count));
        }
    }

    private async void OnDrawerTrackDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var vm = VisualTreeHelperExtensions.ResolveItem<AlbumTrackItemVm>(e)
            ?? (sender as FrameworkElement)?.DataContext as AlbumTrackItemVm;
        var row = AlbumRows.FirstOrDefault(r => r.IsDrawerOpen && r.SelectedAlbum != null);
        if (vm?.Track != null && row?.SelectedAlbum != null)
        {
            var tracks = row.SelectedAlbum.Tracks
                .OrderBy(t => t.DiscNo > 0 ? t.DiscNo : 1)
                .ThenBy(t => t.TrackNo > 0 ? t.TrackNo : 1)
                .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            int startIndex = tracks.FindIndex(t => string.Equals(t.Path, vm.Track.Path, StringComparison.OrdinalIgnoreCase));
            if (startIndex < 0) startIndex = 0;
            await PlaybackUiHelper.PlayAlbumNowPlayingAsync(
                AppServices.Playlists, AppServices.Playback, tracks, startIndex);
        }
    }

    private void OnDrawerTrackRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
    }

    private List<Track> GetSelectedDrawerTracks(object? sender)
    {
        if (sender is AlbumTrackItemVm directVm && directVm.Track != null)
        {
            return new List<Track> { directVm.Track };
        }

        if (sender is FrameworkElement fe)
        {
            if (fe.DataContext is AlbumTrackItemVm vm && vm.Track != null)
                return new List<Track> { vm.Track };
        }

        if (sender is MenuFlyoutItem mfi && mfi.DataContext is AlbumTrackItemVm mfiVm && mfiVm.Track != null)
        {
            return new List<Track> { mfiVm.Track };
        }

        var row = AlbumRows.FirstOrDefault(r => r.IsDrawerOpen && r.SelectedAlbum != null);
        if (row != null)
        {
            var playing = row.LeftTracks.Concat(row.RightTracks).FirstOrDefault(t => t.IsPlaying)?.Track;
            if (playing != null) return new List<Track> { playing };
            var first = row.LeftTracks.Concat(row.RightTracks).FirstOrDefault()?.Track;
            if (first != null) return new List<Track> { first };
        }
        return new List<Track>();
    }

    private void OnDrawerTrackPlaySelected(object sender, RoutedEventArgs e) =>
        _ = PlaybackUiHelper.PlayAlbumNowPlayingAsync(AppServices.Playlists, AppServices.Playback, GetSelectedDrawerTracks(sender), 0);

    private void OnDrawerTrackAddToPlaylist(object sender, RoutedEventArgs e)
    {
        var items = PlaybackUiHelper.AddTracksToNowPlaying(AppServices.Playlists, GetSelectedDrawerTracks(sender));
        if (items.Count > 0)
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToCurrentPlaylist", items.Count));
    }

    private void OnDrawerTrackEnqueue(object sender, RoutedEventArgs e) =>
        PlaybackUiHelper.EnqueueAlbumNowPlaying(AppServices.Playlists, AppServices.Playback, GetSelectedDrawerTracks(sender));

    private void OnDrawerTrackShowInExplorer(object sender, RoutedEventArgs e)
    {
        foreach (var t in GetSelectedDrawerTracks(sender))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{t.Path}\""));
            }
            catch { }
            break;
        }
    }

    private void OnDrawerTrackMenuOpening(object? sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            var subMenu = flyout.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault(i => (i.Tag as string) == SendToPlaylistTag);
            PopulatePlaylistSubMenu(subMenu, () => GetSelectedDrawerTracks(flyout.Target));
        }
    }

    private List<Track> GetSelectedAlbumTracks(object? sender)
    {
        if ((sender as FrameworkElement)?.DataContext is AlbumCard card && card.Tracks.Count > 0)
        {
            return card.Tracks.ToList();
        }
        var openCard = AlbumRows.FirstOrDefault(r => r.IsDrawerOpen)?.SelectedAlbum;
        if (openCard != null && openCard.Tracks.Count > 0)
        {
            return openCard.Tracks.ToList();
        }
        return new List<Track>();
    }

    private void OnAlbumPlaySelected(object sender, RoutedEventArgs e)
    {
        var tracks = GetSelectedAlbumTracks(sender);
        if (tracks.Count > 0)
            _ = PlaybackUiHelper.PlayAlbumNowPlayingAsync(AppServices.Playlists, AppServices.Playback, tracks, 0);
    }

    private void OnAlbumAddToPlaylist(object sender, RoutedEventArgs e)
    {
        var tracks = GetSelectedAlbumTracks(sender);
        if (tracks.Count > 0)
        {
            PlaybackUiHelper.AddTracksToNowPlaying(AppServices.Playlists, tracks);
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToCurrentPlaylist", tracks.Count));
        }
    }

    private void OnAlbumEnqueue(object sender, RoutedEventArgs e)
    {
        var tracks = GetSelectedAlbumTracks(sender);
        if (tracks.Count > 0)
        {
            PlaybackUiHelper.EnqueueAlbumNowPlaying(AppServices.Playlists, AppServices.Playback, tracks);
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToQueue", tracks.Count));
        }
    }

    private async void OnRightQueueTrackDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = VisualTreeHelperExtensions.ResolveItem(e, RightQueueList.SelectedItem as PlaylistItem);
        if (item != null)
        {
            var nowPlaying = AppServices.Playlists.NowPlaying;
            await PlaybackUiHelper.PlayItemAsync(AppServices.Playback, nowPlaying, item);
        }
    }

    private void OnTrackMenuOpening(object? sender, object e) =>
        PopulatePlaylistSubMenu(TrackSendToPlaylistSubMenu, GetSelectedTracks);

    private void OnAlbumMenuOpening(object? sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            var subMenu = flyout.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault(i => (i.Tag as string) == SendToPlaylistTag);
            PopulatePlaylistSubMenu(subMenu, () => GetSelectedAlbumTracks(flyout.Target));
        }
    }

    /// <summary>Locates the "send to playlist" submenu inside template flyouts without matching on display text.</summary>
    private const string SendToPlaylistTag = "SendToPlaylist";

    private static void PopulatePlaylistSubMenu(MenuFlyoutSubItem? subMenu, Func<List<Track>> getTracks)
    {
        if (subMenu == null) return;
        subMenu.Items.Clear();

        var createNewItem = new MenuFlyoutItem { Text = AppStrings.Get("Msg_CreateNewPlaylistAndAdd", "새 재생목록 생성 후 추가...") };
        createNewItem.Icon = new FontIcon { Glyph = "\uE710" };
        createNewItem.Click += (s, args) =>
        {
            var tracks = getTracks();
            if (tracks.Count > 0)
            {
                var pl = AppServices.Playlists.CreatePlaylistFromTracks(null, tracks);
                AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToNamedPlaylist", pl.Name, tracks.Count));
            }
        };
        subMenu.Items.Add(createNewItem);

        var userPlaylists = AppServices.Playlists.Playlists
            .Where(p => p != null && !p.IsSystem && !string.Equals(p.Name, PlaylistManager.NowPlayingPlaylistName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (userPlaylists.Count > 0)
        {
            subMenu.Items.Add(new MenuFlyoutSeparator());
            foreach (var pl in userPlaylists)
            {
                var targetPl = pl;
                var plItem = new MenuFlyoutItem { Text = pl.Name };
                plItem.Icon = new FontIcon { Glyph = "\uE8B9" };
                plItem.Click += (s, args) =>
                {
                    var tracks = getTracks();
                    if (tracks.Count > 0)
                    {
                        AppServices.Playlists.AddTracks(targetPl, tracks);
                        AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToNamedPlaylist", targetPl.Name, tracks.Count));
                    }
                };
                subMenu.Items.Add(plItem);
            }
        }
    }

    // ---------------- toolbar events ----------------

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var text = sender.Text.Trim();
        if (string.Equals(text, _search, StringComparison.Ordinal)) return;
        _search = text;

        // Filtering re-sorts the whole library and rebuilds every album card, so running it on
        // each keystroke made typing feel like the window had hung. Coalesce the burst instead.
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnRescanClick(object sender, RoutedEventArgs e) => AppServices.StartLibraryScan();

    // ---------------- playback actions ----------------

    private List<Track> GetSelectedTracks()
    {
        var sel = TracksList.SelectedItems.OfType<Track>().ToList();
        return sel.Count > 0 ? sel : _visible.Take(1).ToList();
    }

    private void OnTrackDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var track = VisualTreeHelperExtensions.ResolveItem<Track>(e);
        if (track == null) return;

        // Hand playback the whole context the user is looking at and start at the row they clicked,
        // the way every other double-click path here does (tree, album card, album drawer). Passing
        // only the clicked track left Now Playing exactly one track long, so Next and Previous had
        // nowhere to go ("다음 트랙이 없습니다") and playback stopped at the end of that one track.
        var selection = TracksList.SelectedItems.OfType<Track>().ToList();
        var tracks = selection.Count > 1 && selection.Contains(track) ? selection : _visible;

        int startIndex = tracks.IndexOf(track);
        if (startIndex < 0)
        {
            tracks = new List<Track> { track };
            startIndex = 0;
        }

        _ = PlaybackUiHelper.PlayAlbumNowPlayingAsync(AppServices.Playlists, AppServices.Playback, tracks, startIndex);
    }

    private void OnPlaySelected(object sender, RoutedEventArgs e) =>
        _ = PlaybackUiHelper.PlayAlbumNowPlayingAsync(AppServices.Playlists, AppServices.Playback, GetSelectedTracks(), 0);

    private void OnAddSelectedToPlaylist(object sender, RoutedEventArgs e)
    {
        var items = PlaybackUiHelper.AddTracksToNowPlaying(AppServices.Playlists, GetSelectedTracks());
        if (items.Count > 0)
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToCurrentPlaylist", items.Count));
    }

    private void OnQueueSelected(object sender, RoutedEventArgs e) =>
        PlaybackUiHelper.EnqueueAlbumNowPlaying(AppServices.Playlists, AppServices.Playback, GetSelectedTracks());

    private void OnShowInExplorer(object sender, RoutedEventArgs e)
    {
        foreach (var t in GetSelectedTracks())
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{t.Path}\""));
            }
            catch { }
            break;
        }
    }

    private async void OnEditTrackTags(object sender, RoutedEventArgs e)
    {
        var track = GetSelectedTracks().FirstOrDefault();
        if (track == null || XamlRoot == null) return;
        await TagEditorDialogs.ShowForTrackAsync(track, XamlRoot);
    }

    private async void OnAlbumEditTags(object sender, RoutedEventArgs e)
    {
        var tracks = GetSelectedAlbumTracks(sender);
        if (tracks.Count == 0 || XamlRoot == null) return;
        await TagEditorDialogs.ShowForAlbumAsync(tracks, XamlRoot);
    }

    // ---------------- Cover Zoom (Slider / Ctrl+Wheel / Presets) ----------------

    private void SetCoverSize(double size)
    {
        size = Math.Clamp(size, 80, 260);
        _isSettingCoverSize = true;
        try
        {
            CurrentCoverCardWidth = size;
            CurrentCoverImageHeight = Math.Max(20, size - 4);
            foreach (var card in _allBuiltCards)
            {
                card.CardWidth = size;
            }
            foreach (var card in AlbumCards)
            {
                card.CardWidth = size;
            }
            if (CoverZoomSlider != null && Math.Abs(CoverZoomSlider.Value - size) > 0.5)
            {
                CoverZoomSlider.Value = size;
            }
            if (ZoomLabel != null) ZoomLabel.Text = $"{(int)size}px";
            RechunkAlbumRows();
            AppServices.Settings.Ui.AlbumCoverSize = size;
        }
        finally
        {
            _isSettingCoverSize = false;
        }
    }

    private void SetCoverSizeAndSave(double size)
    {
        SetCoverSize(size);
        SettingsWriter.Schedule(AppServices.Settings);
    }

    private void OnCoverZoomFlyoutOpened(object? sender, object? e) => SyncZoomFlyoutUi();
    private void OnCoverZoomSliderLoaded(object sender, RoutedEventArgs e) => SyncZoomFlyoutUi();

    private void SyncZoomFlyoutUi()
    {
        var size = AppServices.Settings.Ui.AlbumCoverSize > 0 ? AppServices.Settings.Ui.AlbumCoverSize : 144;
        _isSettingCoverSize = true;
        try
        {
            if (CoverZoomSlider != null) CoverZoomSlider.Value = size;
            if (ZoomLabel != null) ZoomLabel.Text = $"{(int)size}px";
        }
        finally
        {
            _isSettingCoverSize = false;
        }
    }

    private void OnCoverZoomSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_viewReady || _isSettingCoverSize) return;
        SetCoverSize(e.NewValue);
        SettingsWriter.Schedule(AppServices.Settings);
    }

    private void OnZoomSmall(object sender, RoutedEventArgs e) => SetCoverSizeAndSave(100);
    private void OnZoomMedium(object sender, RoutedEventArgs e) => SetCoverSizeAndSave(144);
    private void OnZoomLarge(object sender, RoutedEventArgs e) => SetCoverSizeAndSave(184);
    private void OnZoomExtraLarge(object sender, RoutedEventArgs e) => SetCoverSizeAndSave(230);

    private void OnCoverGridPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            int delta = e.GetCurrentPoint(CoverScrollViewer).Properties.MouseWheelDelta;
            if (delta != 0)
            {
                SetCoverSizeAndSave(CurrentCoverCardWidth + (delta > 0 ? 12 : -12));
                e.Handled = true;
            }
        }
    }

    // ---------------- Interactive Splitters ----------------

    private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        if (sender is Border border && border.Child is Rectangle rect)
        {
            rect.Fill = ThemeResourceHelper.GetBrush("DawnAccentBrush");
        }
    }

    private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_leftResizer.IsDragging && !_rightResizer.IsDragging && !_lyricsResizer.IsDragging)
        {
            ProtectedCursor = null;
            if (sender is Border border && border.Child is Rectangle rect)
            {
                rect.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }
    }

    // Left Splitter
    private void OnLeftSplitterPressed(object sender, PointerRoutedEventArgs e) => _leftResizer.OnPointerPressed(sender, e);
    private void OnLeftSplitterMoved(object sender, PointerRoutedEventArgs e) => _leftResizer.OnPointerMoved(sender, e);
    private void OnLeftSplitterReleased(object sender, PointerRoutedEventArgs e) => _leftResizer.OnPointerReleased(sender, e);

    // Right Splitter
    private void OnRightSplitterPressed(object sender, PointerRoutedEventArgs e) => _rightResizer.OnPointerPressed(sender, e);
    private void OnRightSplitterMoved(object sender, PointerRoutedEventArgs e) => _rightResizer.OnPointerMoved(sender, e);
    private void OnRightSplitterReleased(object sender, PointerRoutedEventArgs e) => _rightResizer.OnPointerReleased(sender, e);

    // Lyrics Splitter
    private void OnLyricsSplitterPressed(object sender, PointerRoutedEventArgs e) => _lyricsResizer.OnPointerPressed(sender, e);
    private void OnLyricsSplitterMoved(object sender, PointerRoutedEventArgs e) => _lyricsResizer.OnPointerMoved(sender, e);
    private void OnLyricsSplitterReleased(object sender, PointerRoutedEventArgs e) => _lyricsResizer.OnPointerReleased(sender, e);
}
