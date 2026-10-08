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
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private readonly DawnPlayer.App.ViewModels.Playlist.LibraryViewModel _libraryVm =
        new(AppServices.Settings ?? new AppSettings());
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
                AppServices.RatingsApplied += OnRatingsApplied;
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
            w => _libraryVm.SaveSidebarWidth(w, AppServices.Settings.Ui.LeftSidebarWidth, min: 140, max: 400));

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

        // Keyboard alternative to pointer-drag (WCAG 2.5.7 — audit PT1-09/PT5-07).
        _leftResizer.EnableKeyboardResizing(LeftSplitter, AppStrings.Get("Library_Splitter_Left", "왼쪽 패널 너비 조절"));
        _rightResizer.EnableKeyboardResizing(RightSplitter, AppStrings.Get("Library_Splitter_Right", "가사 패널/오른쪽 패널 너비 조절"));
        _lyricsResizer.EnableKeyboardResizing(LyricsSplitter, AppStrings.Get("Library_Splitter_Lyrics", "가사 패널 너비 조절"));
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
            AppServices.RatingsApplied += OnRatingsApplied;
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

    private readonly Controls.LoadingGate _scanGate = new();

    private void OnScanProgress(DawnPlayer.Core.Library.ScanProgress p)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (p.Finished)
            {
                _scanGate.End();
                ScanProgress.Visibility = Visibility.Collapsed;
                ScanProgress.Value = p.Total > 0 ? 100 : 0;
                RescanButton.IsEnabled = true;
            }
            else
            {
                // U2 loading gate: scans under the threshold (300ms) never flash the bar;
                // longer scans show it from the threshold moment on.
                _scanGate.Begin(Environment.TickCount64);
                ScanProgress.Visibility = _scanGate.ShouldShow(Environment.TickCount64)
                    ? Visibility.Visible : Visibility.Collapsed;
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

    // Interaction convention (audit PT5-10, documented as intentional): single click selects
    // or navigates; double-click or Enter plays. The YouTube recent grid is a deliberate
    // exception — its cards exist to replay on a single click ("click what you played").

    // ---------------- data flow ----------------

    private void RebuildAll()
    {
        RebuildTree();
        ApplyFilters();
    }

    private void RebuildTree()
    {
        var tracks = AppServices.Library.Tracks;

        // Clear the selection BEFORE the roots are rebuilt: with the old nodes still attached the
        // native TreeView processes the removal safely, while clearing after the rebuild walks a
        // selection vector of detached nodes — observed as 0xC0000374 heap corruption when
        // switching tabs during playback (WER: same bucket on 08-19, 11 hits).
        LibraryTree.SelectedNodes.Clear();

        var allTvNode = LibraryTreeBuilder.BuildTree(tracks, _treeMode, LibraryTree.RootNodes);

        TreeViewNode? matchingNode = null;
        var savedType = AppServices.Settings.Ui.LibrarySelectedFilterType;
        var savedVal = AppServices.Settings.Ui.LibrarySelectedFilterValue;
        var savedExtra = AppServices.Settings.Ui.LibrarySelectedFilterExtra;

        if (!string.IsNullOrEmpty(savedType))
        {
            matchingNode = LibraryTreeBuilder.FindNodeRecursiveMaterialized(LibraryTree.RootNodes, savedType, savedVal, savedExtra);
        }

        if (matchingNode != null)
        {
            _selectedNode = matchingNode.Content as LibraryTreeNode;
            LibraryTree.SelectedNodes.Add(matchingNode);
            LibraryTreeBuilder.ExpandAncestors(matchingNode);
        }
        else if (_selectedNode == null)
        {
            _selectedNode = allTvNode.Content as LibraryTreeNode;
            LibraryTree.SelectedNodes.Add(allTvNode);
        }
    }

    // PT2-08 (2026-09-30 audit): the whole-library filter+sort used to run synchronously on the
    // UI thread — large libraries froze the window on every search tick and tree pick. The pure
    // compute now runs off-thread; the visible list updates when the newest result lands.
    private readonly FilterRequestGate _filterGate = new();

    private void ApplyFilters()
    {
        // Snapshot on the UI thread: FilterAndSort enumerates the library, and a rescan landing
        // mid-iteration on another thread must never see a mutating collection.
        var snapshot = AppServices.Library.Tracks.ToList();
        var node = _selectedNode;
        var search = _search;
        var sort = _currentSort;
        var ascending = _sortAscending;

        int token = _filterGate.BeginRequest();
        _ = Task.Run(() =>
        {
            try
            {
                return LibraryFilterService.FilterAndSort(snapshot, node, search, sort, ascending);
            }
            catch (Exception ex)
            {
                // Keep the previously rendered list; an empty patch is worse than a stale one.
                App.Log($"[library-filter] compute failed: {ex.Message}");
                return null;
            }
        })
        .ContinueWith(t =>
        {
            var result = t.Result;
            if (result == null) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                // An out-of-order completion (retyped search, re-sort, node switch) must not
                // repaint the screen with stale data — only the newest token applies.
                if (!_filterGate.CanApply(token)) return;
                ApplyFilterResult(result);
            });
        });
    }

    /// <summary>Runs on the UI thread with a fresh, newest-token filter result.</summary>
    private void ApplyFilterResult(List<Track> visible)
    {
        _visible = visible;
        TracksList.ItemsSource = _visible;

        // Rebuild Album Cards & Rows for Grid View using batch ReplaceAll. Card construction
        // stays on the UI thread — AlbumCard holds a BitmapImage.
        _allBuiltCards = LibraryFilterService.BuildAlbumCards(_visible, AppServices.Settings.Ui.AlbumCoverSize);
        AlbumCards.ReplaceAll(_allBuiltCards);
        RechunkAlbumRows();

        var totalMs = _visible.Sum(t => t.DurationMs);
        // PT2-12: multi-selection used to be invisible until you opened a menu.
        int selectedCount = TracksList.SelectedItems.OfType<Track>().Count();
        string selectionPrefix = selectedCount > 1
            ? AppStrings.Format("Library_SelectedCount", "{0}곡 선택됨", selectedCount) + " • "
            : "";
        string nodeLabel = _selectedNode?.Title ?? AppStrings.Get("Library_MixedSelection", "혼합 선택");
        StatusText.Text = _visible.Count == 0
            ? AppStrings.Get("Msg_LibraryEmptyStatus", "트랙 없음 — 설정에서 음악 폴더를 추가하고 스캔하세요.")
            : AppStrings.Format("Msg_LibraryStatusBarFormat", "{0}{1} • {2}, {3:N0}곡, {4:N0}개 앨범", selectionPrefix, nodeLabel, TextFormat.LongDuration(TimeSpan.FromMilliseconds(totalMs)), _visible.Count, AlbumCards.Count);

        UpdateEmptyState();
    }

    /// <summary>PT2-07: an empty content area needs a message and an action. Empty library and
    /// search-no-hit are distinct states with distinct buttons.</summary>
    private void UpdateEmptyState()
    {
        bool libraryEmpty = AppServices.Library.Tracks.Count == 0;
        bool show = libraryEmpty || _visible.Count == 0;
        LibraryEmptyState.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        LibraryEmptyTitle.Text = libraryEmpty
            ? AppStrings.Get("Library_Empty_Title", "라이브러리가 비어 있습니다")
            : AppStrings.Get("Library_SearchNoHit_Title", "검색 결과가 없습니다");
        LibraryEmptyHint.Text = libraryEmpty
            ? AppStrings.Get("Library_Empty_Hint", "설정에서 음악 폴더를 추가하고 스캔하면 트랙이 표시됩니다.")
            : AppStrings.Get("Library_SearchNoHit_Hint", "다른 검색어를 사용하거나 검색 조건을 지워 보세요.");
        LibraryEmptySettingsButton.Visibility = libraryEmpty ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyClearSearchButton.Visibility = !libraryEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEmptyOpenSettingsClick(object sender, RoutedEventArgs e) =>
        App.MainWin?.NavigateToSettings();

    private void OnEmptyClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _search = "";
        ApplyFilters();
    }

    private void RechunkAlbumRows()
    {
        double width = CoverGridViewContainer?.ActualWidth ?? 0;
        if (width <= 0) width = 1000;

        double itemWidth = CurrentCoverCardWidth + 12;
        int cols = Math.Max(1, (int)((width - 28) / itemWidth));
        _lastColumnCount = cols;

        // Fast path: when the card sequence and column count are unchanged, the chunking is
        // identical by construction — keep the existing row objects instead of ReplaceAll,
        // which re-realized every visible card for a no-op change (filter ticks, drawer
        // refreshes). The open-drawer state lives on the kept rows and survives untouched.
        if (AlbumRows.Count > 0 && RowsMatchCurrentChunking(cols))
        {
            return;
        }

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

    /// <summary>Whether the built cards, chunked into <paramref name="cols"/> columns, would
    /// produce exactly the rows already held.</summary>
    private bool RowsMatchCurrentChunking(int cols)
    {
        int expectedRows = (_allBuiltCards.Count + cols - 1) / cols;
        if (AlbumRows.Count != expectedRows) return false;
        for (int r = 0; r < AlbumRows.Count; r++)
        {
            var row = AlbumRows[r];
            int start = r * cols;
            int end = Math.Min(start + cols, _allBuiltCards.Count);
            if (row.Cards.Count != end - start) return false;
            for (int j = start; j < end; j++)
            {
                if (!ReferenceEquals(row.Cards[j - start], _allBuiltCards[j])) return false;
            }
        }
        return true;
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

    /// <summary>P0 lazy expansion: materialize deferred folder children on first expand.</summary>
    // Wired from XAML (Expanding="OnTreeExpanding"), which requires an instance member.
#pragma warning disable CA1822
    private void OnTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node != null) LibraryTreeBuilder.EnsureChildrenLoaded(args.Node);
    }
#pragma warning restore CA1822

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
        // A tree activation is a direct user action on the just-selected node, so it computes
        // its context synchronously instead of playing the previous node's still-on-screen
        // list — the async pipeline (PT2-08) may not have landed for the new node yet.
        if (_selectedNode == null) return;
        var visible = LibraryFilterService.FilterAndSort(
            AppServices.Library.Tracks, _selectedNode, _search, _currentSort, _sortAscending);
        if (visible.Count == 0) return;
        await PlaybackUiHelper.PlayAlbumNowPlayingAsync(
            AppServices.Playlists, AppServices.Playback, visible, 0);
    }

    private async void OnTreeContextMenuPlay(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_treeMenuTracks.Count == 0) return;
            await PlaybackUiHelper.PlayAlbumNowPlayingAsync(
                AppServices.Playlists, AppServices.Playback, _treeMenuTracks, 0);
        }
        catch (Exception ex)
        {
            App.Log($"[OnTreeContextMenuPlay Error] {ex}");
        }
    }

    private void OnTreeContextMenuAddToPlaylist(object sender, RoutedEventArgs e)
    {
        if (_treeMenuTracks.Count == 0) return;
        var items = PlaybackUiHelper.AddTracksToNowPlaying(AppServices.Playlists, _treeMenuTracks);
        if (items.Count > 0)
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToCurrentPlaylist", "현재 재생목록에 {0}곡을 추가했습니다.", items.Count));
    }

    private void OnTreeContextMenuEnqueue(object sender, RoutedEventArgs e)
    {
        if (_treeMenuTracks.Count == 0) return;
        var items = PlaybackUiHelper.EnqueueAlbumNowPlaying(
            AppServices.Playlists, AppServices.Playback, _treeMenuTracks);
        if (items.Count > 0)
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToQueue", "대기열에 {0}곡을 추가했습니다.", items.Count));
    }

    /// <summary>Tracks the tree menu acts on, fixed at Opening time from the right-clicked node.
    /// TreeView does not move its selection on right-press, so the old <c>_visible</c> (the
    /// selected node's filter result) made the menu act on the wrong node; the popup-hosted menu
    /// items also never see the row's DataContext (measured on the playlist page), so the target
    /// must be resolved from flyout.Target — the node row Grid in the visual tree.</summary>
    private List<Track> _treeMenuTracks = [];

    private void OnTreeContextMenuOpening(object? sender, object e)
    {
        _treeMenuTracks = [];
        if (sender is MenuFlyout flyout)
        {
            var node = VisualTreeHelperExtensions.FindAncestorDataContext<TreeViewNode>(flyout.Target)
                ?.Content as LibraryTreeNode;
            if (node != null)
            {
                _treeMenuTracks = LibraryFilterService.FilterAndSort(
                    AppServices.Library.Tracks, node, "", _currentSort, _sortAscending);
            }
            var subMenu = flyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(i => (i.Tag as string) == SendToPlaylistTag);
            PopulatePlaylistSubMenu(subMenu, () => _treeMenuTracks.ToList());
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
        HeaderRating.Text = AppStrings.Get("Library_Header_Rating.Text", "평점") + (_currentSort == SortColumn.Rating ? arrow : "");
        HeaderDuration.Text = AppStrings.Get("Library_Header_Duration.Text", "길이") + (_currentSort == SortColumn.Duration ? arrow : "");
    }
    private void OnSortByTrackNo(object sender, RoutedEventArgs e) => SortBy(SortColumn.TrackNo);
    private void OnSortByTitle(object sender, RoutedEventArgs e) => SortBy(SortColumn.Title);
    private void OnSortByArtist(object sender, RoutedEventArgs e) => SortBy(SortColumn.Artist);
    private void OnSortByAlbum(object sender, RoutedEventArgs e) => SortBy(SortColumn.Album);
    private void OnSortByDuration(object sender, RoutedEventArgs e) => SortBy(SortColumn.Duration);
    private void OnSortByRating(object sender, RoutedEventArgs e) => SortBy(SortColumn.Rating);

    // ---------------- rating (L11) ----------------

    private Track? _ratingFlyoutTarget;
    private bool _suppressRatingValueChanged;

    private void RateSelected(int stars)
    {
        var tracks = GetSelectedTracks();
        if (tracks.Count == 0) return;
        AppServices.RateTracks(tracks, stars);
    }

    private void OnRateSelected1(object sender, RoutedEventArgs e) => RateSelected(1);
    private void OnRateSelected2(object sender, RoutedEventArgs e) => RateSelected(2);
    private void OnRateSelected3(object sender, RoutedEventArgs e) => RateSelected(3);
    private void OnRateSelected4(object sender, RoutedEventArgs e) => RateSelected(4);
    private void OnRateSelected5(object sender, RoutedEventArgs e) => RateSelected(5);
    private void OnUnrateSelected(object sender, RoutedEventArgs e) => RateSelected(0);

    private void OnLibraryRatingCellClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button cell || cell.Tag is not Track track) return;
        _ratingFlyoutTarget = track;
        // Programmatic Value assignment raises ValueChanged; the guard keeps the initial value
        // from being applied as a user rating.
        _suppressRatingValueChanged = true;
        try { LibraryRatingSelector.Value = Math.Clamp(track.Rating, 0, 5); }
        finally { _suppressRatingValueChanged = false; }
        LibraryRatingFlyout.ShowAt(cell);
    }

    private void OnLibraryRatingSelectorValueChanged(RatingControl sender, object args)
    {
        if (_suppressRatingValueChanged || _ratingFlyoutTarget == null) return;
        var track = _ratingFlyoutTarget;
        _ratingFlyoutTarget = null;
        AppServices.RateTracks([track], (int)Math.Round(sender.Value));
        LibraryRatingFlyout.Hide();
    }

    private void OnLibraryRatingFlyoutClosed(object sender, object args)
    {
        _ratingFlyoutTarget = null;
    }

    /// <summary>Rating-cell refresh after a rating command. Track has no INPC, so when the sort
    /// column is rating the table must re-sort (ApplyFilters); otherwise only the realized
    /// containers are patched in place — virtualized rows re-bind the mutated Track.Rating the
    /// moment they scroll back into view.</summary>
    private void OnRatingsApplied(IReadOnlyList<Track> tracks)
    {
        if (_currentSort == SortColumn.Rating)
        {
            ApplyFilters();
            return;
        }

        foreach (var track in tracks)
        {
            if (TracksList.ContainerFromItem(track) is not ListViewItem container) continue;
            if (VisualTreeHelperExtensions.FindDescendant<Button>(container) is not { } cell) continue;
            if (cell.Content is TextBlock stars) stars.Text = RatingToStarsConverter.DisplayText(track.Rating);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, RatingAccessibilityConverter.AccessibilityText(track.Rating));
        }
    }

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
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToQueue", "대기열에 {0}곡을 추가했습니다.", row.SelectedAlbum.Tracks.Count));
        }
    }

    private void OnDrawerAddToPlaylistClick(object sender, RoutedEventArgs e)
    {
        var row = AlbumRows.FirstOrDefault(r => r.IsDrawerOpen && r.SelectedAlbum != null);
        if (row?.SelectedAlbum != null && row.SelectedAlbum.Tracks.Count > 0)
        {
            PlaybackUiHelper.AddTracksToNowPlaying(AppServices.Playlists, row.SelectedAlbum.Tracks);
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToCurrentPlaylist", "현재 재생목록에 {0}곡을 추가했습니다.", row.SelectedAlbum.Tracks.Count));
        }
    }

    private async void OnDrawerTrackDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var vm = VisualTreeHelperExtensions.ResolveItem<AlbumTrackItemVm>(e)
            ?? (sender as FrameworkElement)?.DataContext as AlbumTrackItemVm;
        await PlayDrawerTrackAsync(vm);
    }

    /// <summary>Keyboard activation parity (PT5-01): the drawer row is a focusable Button whose
    /// Click was never wired, so Space/Enter did nothing. KeyDown on the focused row plays it;
    /// marking the key handled keeps single-pointer semantics unchanged (still double-click).</summary>
    private void OnDrawerTrackKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)) return;
        e.Handled = true;
        var vm = VisualTreeHelperExtensions.FindAncestorDataContext<AlbumTrackItemVm>(sender as DependencyObject);
        _ = PlayDrawerTrackAsync(vm);
    }

    private async Task PlayDrawerTrackAsync(AlbumTrackItemVm? vm)
    {
        var row = AlbumRows.FirstOrDefault(r => r.IsDrawerOpen && r.SelectedAlbum != null);
        if (vm?.Track == null || row?.SelectedAlbum == null) return;
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

    /// <summary>Tracks the drawer-row menu acts on, fixed at Opening time from the right-clicked
    /// row. The old resolver read MenuFlyoutItem.DataContext, which the popup-hosted flyout never
    /// receives (measured: "x:Bind item templates leave the template elements without a
    /// DataContext"), so the menu fell back to the playing/first drawer track and acted on the
    /// wrong row — or nothing. flyout.Target is the row Button; walk its visual tree instead.</summary>
    private List<Track> _drawerMenuTracks = [];

    private void OnDrawerTrackMenuOpening(object? sender, object e)
    {
        _drawerMenuTracks = [];
        if (sender is MenuFlyout flyout)
        {
            var vm = VisualTreeHelperExtensions.FindAncestorDataContext<AlbumTrackItemVm>(flyout.Target);
            if (vm?.Track != null)
            {
                _drawerMenuTracks = [vm.Track];
            }
            var subMenu = flyout.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault(i => (i.Tag as string) == SendToPlaylistTag);
            PopulatePlaylistSubMenu(subMenu, () => _drawerMenuTracks.ToList());
        }
    }

    private void OnDrawerTrackPlaySelected(object sender, RoutedEventArgs e)
    {
        if (_drawerMenuTracks.Count == 0) return;
        _ = PlaybackUiHelper.PlayAlbumNowPlayingAsync(AppServices.Playlists, AppServices.Playback, _drawerMenuTracks, 0);
    }

    private void OnDrawerTrackAddToPlaylist(object sender, RoutedEventArgs e)
    {
        if (_drawerMenuTracks.Count == 0) return;
        var items = PlaybackUiHelper.AddTracksToNowPlaying(AppServices.Playlists, _drawerMenuTracks);
        if (items.Count > 0)
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToCurrentPlaylist", "현재 재생목록에 {0}곡을 추가했습니다.", items.Count));
    }

    private void OnDrawerTrackEnqueue(object sender, RoutedEventArgs e)
    {
        if (_drawerMenuTracks.Count == 0) return;
        PlaybackUiHelper.EnqueueAlbumNowPlaying(AppServices.Playlists, AppServices.Playback, _drawerMenuTracks);
    }

    private void OnDrawerTrackShowInExplorer(object sender, RoutedEventArgs e)
    {
        foreach (var t in _drawerMenuTracks)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{t.Path}\""));
            }
            catch { }
            break;
        }
    }

    /// <summary>Tracks the album-card menu acts on, fixed at Opening time from the right-clicked
    /// card. MenuFlyoutItem.DataContext is empty inside the popup (see OnDrawerTrackMenuOpening),
    /// so the old resolver fell through to "the open drawer's album" — with a drawer open the
    /// menu edited a different album, without one it silently no-op'd. flyout.Target is the card
    /// Button; walk its visual tree for the AlbumCard.</summary>
    private List<Track> _albumMenuTracks = [];

    private void OnAlbumMenuOpening(object? sender, object e)
    {
        _albumMenuTracks = [];
        if (sender is MenuFlyout flyout)
        {
            var card = VisualTreeHelperExtensions.FindAncestorDataContext<AlbumCard>(flyout.Target);
            if (card != null && card.Tracks.Count > 0)
            {
                _albumMenuTracks = card.Tracks.ToList();
            }
            var subMenu = flyout.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault(i => (i.Tag as string) == SendToPlaylistTag);
            PopulatePlaylistSubMenu(subMenu, () => _albumMenuTracks.ToList());
        }
    }

    private void OnAlbumPlaySelected(object sender, RoutedEventArgs e)
    {
        if (_albumMenuTracks.Count == 0) return;
        _ = PlaybackUiHelper.PlayAlbumNowPlayingAsync(AppServices.Playlists, AppServices.Playback, _albumMenuTracks, 0);
    }

    private void OnAlbumAddToPlaylist(object sender, RoutedEventArgs e)
    {
        if (_albumMenuTracks.Count == 0) return;
        PlaybackUiHelper.AddTracksToNowPlaying(AppServices.Playlists, _albumMenuTracks);
        AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToCurrentPlaylist", "현재 재생목록에 {0}곡을 추가했습니다.", _albumMenuTracks.Count));
    }

    private void OnAlbumEnqueue(object sender, RoutedEventArgs e)
    {
        if (_albumMenuTracks.Count == 0) return;
        PlaybackUiHelper.EnqueueAlbumNowPlaying(AppServices.Playlists, AppServices.Playback, _albumMenuTracks);
        AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToQueue", "대기열에 {0}곡을 추가했습니다.", _albumMenuTracks.Count));
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

    // Right-tap must drive the selection refresh (same platform trap the tracks list and the
    // playlist page fixed): a right-press never moves the selection, so without it the queue
    // menu would act on the previously selected row — or nobody — instead of the clicked row.
    private void OnRightQueueListRightTapped(object sender, RightTappedRoutedEventArgs e) =>
        SelectQueueRowForContext(e.OriginalSource as DependencyObject);

    private void OnRightQueueListContextRequested(UIElement sender, ContextRequestedEventArgs e) =>
        SelectQueueRowForContext(e.OriginalSource as DependencyObject);

    /// <summary>Selects the queue row under the pointer so the context menu acts on it. The
    /// single-selection sidebar collapses the selection to the clicked row; an unresolvable
    /// target (album group header, blank area) leaves the selection untouched.</summary>
    private void SelectQueueRowForContext(DependencyObject? source)
    {
        var container = VisualTreeHelperExtensions.FindAncestor<ListViewItem>(source);
        if (container == null) return;
        if (RightQueueList.ItemFromContainer(container) is PlaylistItem item)
        {
            RightQueueList.SelectedItem = item;
        }
    }

    private async void OnRightQueueMenuPlay(object sender, RoutedEventArgs e)
    {
        if (RightQueueList.SelectedItem is not PlaylistItem item) return;
        await PlaybackUiHelper.PlayItemAsync(AppServices.Playback, AppServices.Playlists.NowPlaying, item);
    }

    private void OnRightQueueMenuRemove(object sender, RoutedEventArgs e)
    {
        if (RightQueueList.SelectedItem is not PlaylistItem item) return;
        PlaybackUiHelper.RemoveItems(AppServices.Playlists, AppServices.Playlists.NowPlaying, new[] { item });
    }

    private void OnTrackMenuOpening(object? sender, object e) =>
        PopulatePlaylistSubMenu(TrackSendToPlaylistSubMenu, GetSelectedTracks);

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
                AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToNamedPlaylist", "'{0}'에 {1}곡을 추가했습니다.", pl.Name, tracks.Count));
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
                        AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToNamedPlaylist", "'{0}'에 {1}곡을 추가했습니다.", targetPl.Name, tracks.Count));
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

    // Right-tap must drive the selection refresh (same platform trap the playlist page fixed):
    // ListViewBase swallows ContextRequested for pointer gestures before the ListView-level
    // hook runs, and a right-press never moves the selection — so the menu acted on whatever
    // row was already selected, or on nobody.
    private void OnTracksListRightTapped(object sender, RightTappedRoutedEventArgs e) =>
        SelectTrackRowForContext(e.OriginalSource as DependencyObject);

    private void OnTracksListContextRequested(UIElement sender, ContextRequestedEventArgs e) =>
        SelectTrackRowForContext(e.OriginalSource as DependencyObject);

    /// <summary>Selects the track row under the pointer so the context menu acts on it. Windows
    /// list convention: a click on a row outside the current selection collapses the selection
    /// to that row; a click inside the multi-selection keeps it; a target that resolves to no
    /// row (header, blank area) leaves the selection untouched (PT2-11, Extended mode).</summary>
    private void SelectTrackRowForContext(DependencyObject? source)
    {
        var container = VisualTreeHelperExtensions.FindAncestor<ListViewItem>(source);
        if (container == null) return;
        if (TracksList.ItemFromContainer(container) is not Track track) return;
        if (TracksList.SelectedItems.Contains(track)) return;
        TracksList.SelectedItems.Clear();
        TracksList.SelectedItems.Add(track);
    }

    private List<Track> GetSelectedTracks()
    {
        // No first-track fallback: a menu invoked over a blank area with nothing selected must
        // no-op, not surprise-play the top of the list.
        return TracksList.SelectedItems.OfType<Track>().ToList();
    }

    private void OnTrackDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var track = VisualTreeHelperExtensions.ResolveItem<Track>(e);
        if (track == null) return;
        _ = PlayTrackFromListAsync(track);
    }

    private void OnTracksListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Keyboard parity with double-click (PT5-02): the track table had no Enter path at all,
        // so the core workflow was pointer-only.
        if (e.Key == Windows.System.VirtualKey.Enter && TracksList.SelectedItem is Track selected)
        {
            e.Handled = true;
            _ = PlayTrackFromListAsync(selected);
        }
    }

    /// <summary>Shared by double-click and Enter: hand playback the whole context the user is
    /// looking at and start at the activated row, the way every other activation path here does
    /// (tree, album card, album drawer). Passing only the clicked track left Now Playing exactly
    /// one track long, so Next and Previous had nowhere to go ("다음 트랙이 없습니다").</summary>
    private async Task PlayTrackFromListAsync(Track track)
    {
        var selection = TracksList.SelectedItems.OfType<Track>().ToList();
        // _visible is the list currently RENDERED (the async pipeline updates it together with
        // ItemsSource), so it is the correct playback context for the row the user just acted on.
        var tracks = selection.Count > 1 && selection.Contains(track) ? selection : _visible;

        int startIndex = tracks.IndexOf(track);
        if (startIndex < 0)
        {
            tracks = new List<Track> { track };
            startIndex = 0;
        }

        await PlaybackUiHelper.PlayAlbumNowPlayingAsync(AppServices.Playlists, AppServices.Playback, tracks, startIndex);
    }

    private void OnPlaySelected(object sender, RoutedEventArgs e) =>
        _ = PlaybackUiHelper.PlayAlbumNowPlayingAsync(AppServices.Playlists, AppServices.Playback, GetSelectedTracks(), 0);

    private void OnAddSelectedToPlaylist(object sender, RoutedEventArgs e)
    {
        var items = PlaybackUiHelper.AddTracksToNowPlaying(AppServices.Playlists, GetSelectedTracks());
        if (items.Count > 0)
            AppServices.RaiseWarning(AppStrings.Format("Msg_AddedTracksToCurrentPlaylist", "현재 재생목록에 {0}곡을 추가했습니다.", items.Count));
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
        if (_albumMenuTracks.Count == 0 || XamlRoot == null) return;
        await TagEditorDialogs.ShowForAlbumAsync(_albumMenuTracks, XamlRoot);
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

    private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e) =>
        SplitterChrome.SetHover(this, c => ProtectedCursor = c, sender as Border, hovered: true, _leftResizer, _rightResizer, _lyricsResizer);

    private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e) =>
        SplitterChrome.ClearHover(this, c => ProtectedCursor = c, sender as Border, _leftResizer, _rightResizer, _lyricsResizer);

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
