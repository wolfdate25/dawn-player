using DawnPlayer.App.Controls;
using DawnPlayer.App.Helpers;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;

namespace DawnPlayer.App.Views;

public sealed partial class PlaylistPage : Page
{
    private Playlist? _playlist;
    private bool _grouped = true;
    private readonly DispatcherTimer _rebuildDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private SplitterResizer? _leftResizer;
    private SplitterResizer? _lyricsResizer;

    private bool _pageInitialized;

    public PlaylistPage()
    {
        InitializeComponent();
        _rebuildDebounce.Tick += (_, _) => { _rebuildDebounce.Stop(); Rebuild(); };
        InitializeSplitters();

        Loaded += (_, _) =>
        {
            if (!_pageInitialized)
            {
                _pageInitialized = true;
                InitializePage();
            }
        };
    }

    public void InitializePage()
    {
        PlaylistsSidebarList.ItemsSource = AppServices.Playlists.Playlists;
        PlaylistsCountText.Text = AppServices.Playlists.Playlists.Count.ToString(CultureInfo.InvariantCulture);

        var activeName = AppServices.Settings.Playback.ActivePlaylistName;
        var targetPl = (!string.IsNullOrEmpty(activeName) ? AppServices.Playlists.Playlists.FirstOrDefault(p => p.Name == activeName) : null)
                       ?? AppServices.Playlists.Current;

        PlaylistsSidebarList.SelectedItem = targetPl;
        _grouped = AppServices.Settings.Ui.PlaylistGroupedView;
        if (GroupToggleMenuItem != null) GroupToggleMenuItem.IsChecked = _grouped;

        AppServices.LibraryChanged += OnLibraryChanged;
        AppServices.CurrentTrackChanged += OnCurrentTrackChangedForLyrics;
        AppServices.StopAfterCurrentChanged += SyncStopAfterCurrentMenuItem;
        SyncStopAfterCurrentMenuItem();

        SetLyricsVisibility(AppServices.Settings.Ui.ShowLyricsPane);
        PlaylistLyricsPane.OnTrackChanged(AppServices.Playback.CurrentItem);

        RestoreLayoutSettings();
    }

    public void ActivatePage()
    {
        if (!_pageInitialized)
        {
            _pageInitialized = true;
            InitializePage();
        }

        PlaylistsCountText.Text = AppServices.Playlists.Playlists.Count.ToString(CultureInfo.InvariantCulture);
        SetLyricsVisibility(AppServices.Settings.Ui.ShowLyricsPane);
        PlaylistLyricsPane.OnTrackChanged(AppServices.Playback.CurrentItem);
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

    private void OnLibraryChanged() =>
        DispatcherQueue.TryEnqueue(ScheduleRebuild);

    private void OnCurrentTrackChangedForLyrics(PlaylistItem? item)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            PlaylistLyricsPane.OnTrackChanged(item);
            ScrollPlaylistToItem(item);
        });
    }

    private void ScrollPlaylistToItem(PlaylistItem? item)
    {
        if (item == null || PlaylistList == null) return;
        var target = PlaybackUiHelper.FindItemToScroll(Current?.Items, item);
        if (target != null)
        {
            try
            {
                PlaylistList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
            }
            catch (Exception ex)
            {
                App.Log($"[ScrollPlaylist Error] {ex}");
            }
        }
    }

    public void SetLyricsVisibility(bool show)
    {
        LyricsPanelContainer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PlaylistLyricsPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            PlaylistLyricsPane.OnTrackChanged(AppServices.Playback.CurrentItem);
        }
    }

    // ---------------- splitters ----------------

    private void InitializeSplitters()
    {
        _leftResizer = new SplitterResizer(
            MainLayoutGrid, LeftSplitterLine, 140, 400, invertDelta: false,
            () => ColLeft.ActualWidth,
            w => ColLeft.Width = new GridLength(w),
            cursor => ProtectedCursor = cursor,
            w => { if (AppServices.Settings != null) { AppServices.Settings.Ui.LeftSidebarWidth = w; SettingsWriter.Schedule(AppServices.Settings); } });

        _lyricsResizer = new SplitterResizer(
            MainLayoutGrid, LyricsSplitterLine, 200, 450, invertDelta: true,
            () => PlaylistLyricsPane.ActualWidth,
            w => PlaylistLyricsPane.Width = w,
            cursor => ProtectedCursor = cursor,
            w => { if (AppServices.Settings != null) { AppServices.Settings.Ui.LyricsSidebarWidth = w; SettingsWriter.Schedule(AppServices.Settings); } });
    }

    private void RestoreLayoutSettings()
    {
        var ui = AppServices.Settings.Ui;
        if (ui.LeftSidebarWidth >= 140) ColLeft.Width = new GridLength(ui.LeftSidebarWidth);
        if (ui.LyricsSidebarWidth >= 200) PlaylistLyricsPane.Width = ui.LyricsSidebarWidth;
    }

    private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        if (sender is Border b && b.Child is Rectangle line)
            line.Fill = Helpers.ThemeResourceHelper.GetBrush("DawnAccentBrush");
    }

    private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = null;
        if (sender is Border b && b.Child is Rectangle line)
            line.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void OnLeftSplitterPressed(object sender, PointerRoutedEventArgs e) => _leftResizer?.OnPointerPressed(sender, e);
    private void OnLeftSplitterMoved(object sender, PointerRoutedEventArgs e) => _leftResizer?.OnPointerMoved(sender, e);
    private void OnLeftSplitterReleased(object sender, PointerRoutedEventArgs e) => _leftResizer?.OnPointerReleased(sender, e);

    private void OnLyricsSplitterPressed(object sender, PointerRoutedEventArgs e) => _lyricsResizer?.OnPointerPressed(sender, e);
    private void OnLyricsSplitterMoved(object sender, PointerRoutedEventArgs e) => _lyricsResizer?.OnPointerMoved(sender, e);
    private void OnLyricsSplitterReleased(object sender, PointerRoutedEventArgs e) => _lyricsResizer?.OnPointerReleased(sender, e);

    // ---------------- sidebar selection & management ----------------

    private void OnSidebarSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistsSidebarList.SelectedItem is Playlist pl)
        {
            if (_playlist != null) _playlist.Items.CollectionChanged -= OnItemsChanged;
            _playlist = pl;
            AppServices.Playlists.SelectPlaylist(pl);
            // Smart playlist names are localized and rebuilt every launch; persisting one would
            // try to restore it by a name that may not exist (or mean something else) next time.
            if (!pl.IsSmart)
            {
                AppServices.Settings.Playback.ActivePlaylistName = pl.Name;
                SettingsWriter.Schedule(AppServices.Settings);
            }
            pl.Items.CollectionChanged += OnItemsChanged;
            Rebuild();
        }
    }

    // ScheduleRebuild touches a DispatcherTimer, which is thread-affine, so never trust the
    // raising thread even though the manager now marshals its own writes.
    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(ScheduleRebuild);

    private void ScheduleRebuild()
    {
        _rebuildDebounce.Stop();
        _rebuildDebounce.Start();
    }

    private void OnCreatePlaylistClick(object sender, RoutedEventArgs e)
    {
        var pl = AppServices.Playlists.CreatePlaylist();
        PlaylistsCountText.Text = AppServices.Playlists.Playlists.Count.ToString(CultureInfo.InvariantCulture);
        PlaylistsSidebarList.SelectedItem = pl;
        _ = PlaylistDialogs.ShowRenameDialogAsync(pl, XamlRoot, AppServices.Playlists);
    }

    private void OnSidebarListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && VisualTreeHelperExtensions.FindAncestorDataContext<Playlist>(fe) is { } pl)
        {
            PlaylistsSidebarList.SelectedItem = pl;
        }
    }

    private void OnSidebarListContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && VisualTreeHelperExtensions.FindAncestorDataContext<Playlist>(fe) is { } pl)
        {
            PlaylistsSidebarList.SelectedItem = pl;
        }
    }

    private void OnSidebarListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            DeleteSelectedSidebarPlaylist();
            e.Handled = true;
        }
    }

    private void OnRenameSidebarPlaylist(object sender, RoutedEventArgs e)
    {
        var pl = ResolveSidebarPlaylist(sender);
        if (pl != null)
        {
            if (pl.IsSystem)
            {
                AppServices.RaiseWarning(AppStrings.Get("Msg_SystemPlaylistRenameNotAllowed", "시스템 재생목록(Now Playing)의 이름은 변경할 수 없습니다."));
                return;
            }
            _ = PlaylistDialogs.ShowRenameDialogAsync(pl, XamlRoot, AppServices.Playlists);
        }
    }

    private async void OnExportSidebarPlaylist(object sender, RoutedEventArgs e)
    {
        try
        {
            var pl = ResolveSidebarPlaylist(sender);
            if (pl == null) return;
            await PlaylistDialogs.ExportPlaylistAsync(pl, AppServices.MainWindowHandle);
        }
        catch (Exception ex)
        {
            App.Log($"[OnExportSidebarPlaylist Error] {ex}");
        }
    }

    private void OnSidebarContextMenuOpening(object sender, object e)
    {
        var pl = ResolveSidebarPlaylist(null);
        // System (Now Playing) and smart (generated) playlists cannot be renamed, exported as a
        // user file, or deleted; smart ones cannot be cleared either — the next refresh would
        // just regenerate the contents.
        bool locked = pl is { IsSystem: true } or { IsSmart: true };

        if (SidebarRenameMenuItem != null)
            SidebarRenameMenuItem.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

        if (SidebarDeleteSeparator != null)
            SidebarDeleteSeparator.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

        if (SidebarDeleteMenuItem != null)
            SidebarDeleteMenuItem.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

        if (SidebarClearMenuItem != null)
        {
            bool isSystem = pl?.IsSystem ?? false;
            bool isSmart = pl?.IsSmart ?? false;
            SidebarClearMenuItem.Visibility = isSmart ? Visibility.Collapsed : Visibility.Visible;
            SidebarClearMenuItem.Text = isSystem
                ? AppStrings.Get("Msg_PlaylistClearQueue", "대기열 비우기")
                : AppStrings.Get("Msg_PlaylistClearPlaylist", "재생목록 비우기");
        }
    }

    private void OnHeaderToolsMenuOpening(object sender, object e)
    {
        var current = Current;
        bool locked = current is { IsSystem: true } or { IsSmart: true };

        if (HeaderRenameMenuItem != null)
            HeaderRenameMenuItem.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

        if (HeaderDeleteSeparator != null)
            HeaderDeleteSeparator.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

        if (HeaderDeleteMenuItem != null)
            HeaderDeleteMenuItem.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

        if (HeaderClearMenuItem != null)
        {
            // Clearing a smart playlist is pointless — it regenerates on the next refresh.
            bool isSystem = current?.IsSystem ?? false;
            bool isSmart = current?.IsSmart ?? false;
            HeaderClearMenuItem.Visibility = isSmart ? Visibility.Collapsed : Visibility.Visible;
            HeaderClearMenuItem.Text = isSystem
                ? AppStrings.Get("Msg_PlaylistClearQueue", "대기열 비우기")
                : AppStrings.Get("Msg_PlaylistClearPlaylist", "재생목록 비우기");
        }
    }

    private void OnClearSidebarPlaylist(object sender, RoutedEventArgs e)
    {
        var pl = ResolveSidebarPlaylist(sender);
        if (pl == null || pl.IsSmart) return;
        AppServices.Playlists.RemoveAll(pl);
        Rebuild();
    }

    private void OnDeleteSidebarPlaylist(object sender, RoutedEventArgs e) =>
        DeleteSelectedSidebarPlaylist(ResolveSidebarPlaylist(sender));

    private void OnDeleteActivePlaylist(object sender, RoutedEventArgs e) =>
        DeleteSelectedSidebarPlaylist(Current);

    private void DeleteSelectedSidebarPlaylist(Playlist? target = null)
    {
        var pl = target ?? ResolveSidebarPlaylist(null);
        if (pl == null) return;

        if (pl.IsSystem)
        {
            AppServices.Playlists.RemoveAll(pl);
            AppServices.RaiseWarning(AppStrings.Get("Msg_ClearedNowPlayingQueue", "현재 재생 대기열(Now Playing)의 모든 곡을 비웠습니다."));
            Rebuild();
            return;
        }

        AppServices.Playlists.RemovePlaylist(pl);
        var next = AppServices.Playlists.Current;
        PlaylistsSidebarList.SelectedItem = next;
        PlaylistsCountText.Text = AppServices.Playlists.Playlists.Count.ToString(CultureInfo.InvariantCulture);
        Rebuild();
    }

    private Playlist? ResolveSidebarPlaylist(object? sender)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Playlist pl)
            return pl;
        if (sender is MenuFlyoutItem mfi && mfi.DataContext is Playlist mfiPl)
            return mfiPl;
        return PlaylistsSidebarList.SelectedItem as Playlist ?? Current;
    }

    private void OnRenameActivePlaylist(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        if (Current.IsSystem)
        {
            AppServices.RaiseWarning(AppStrings.Get("Msg_SystemPlaylistRenameNotAllowed", "시스템 재생목록(Now Playing)의 이름은 변경할 수 없습니다."));
            return;
        }
        _ = PlaylistDialogs.ShowRenameDialogAsync(Current, XamlRoot, AppServices.Playlists);
    }

    private async void OnExportActivePlaylist(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Current == null) return;
            await PlaylistDialogs.ExportPlaylistAsync(Current, AppServices.MainWindowHandle);
        }
        catch (Exception ex)
        {
            App.Log($"[OnExportActivePlaylist Error] {ex}");
        }
    }

    // ---------------- adding content ----------------

    private async void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = await PlaylistDialogs.PickAudioFilesAsync(AppServices.MainWindowHandle);
            if (files.Count > 0)
                await AppServices.Playlists.AddPathsAsync(Current, files);
        }
        catch (Exception ex)
        {
            App.Log($"[OnAddFilesClick Error] {ex}");
        }
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await PlaylistDialogs.PickMusicFolderAsync(AppServices.MainWindowHandle);
            if (folder != null)
                await AppServices.Playlists.AddPathsAsync(Current, new[] { folder });
        }
        catch (Exception ex)
        {
            App.Log($"[OnAddFolderClick Error] {ex}");
        }
    }

    private async void OnImportPlaylistClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = await PlaylistDialogs.PickPlaylistFileAsync(AppServices.MainWindowHandle);
            if (!string.IsNullOrEmpty(file))
            {
                var pl = await AppServices.Playlists.ImportPlaylistAsync(file);
                if (pl != null)
                {
                    PlaylistsCountText.Text = AppServices.Playlists.Playlists.Count.ToString(CultureInfo.InvariantCulture);
                    PlaylistsSidebarList.SelectedItem = pl;
                    AppServices.RaiseWarning(AppStrings.Format("Msg_PlaylistImported", pl.Name, pl.Items.Count));
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[OnImportPlaylistClick Error] {ex}");
            AppServices.RaiseWarning(AppStrings.Format("Msg_ImportFailed", ex.Message));
        }
    }

    private Playlist Current => _playlist ?? AppServices.Playlists.Current;

    // ---------------- list rendering ----------------

    private void Rebuild()
    {
        try
        {
            var pl = Current;
            if (pl == null) return;

            PlaylistTitleText.Text = pl.Name;
            PlaylistStatsText.Text = PlaybackUiHelper.FormatEolePlaylistStats(pl);
            PlaylistsCountText.Text = AppServices.Playlists.Playlists.Count.ToString(CultureInfo.InvariantCulture);

            bool isEmpty = pl.Items.Count == 0;
            EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            PlaylistList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

            if (isEmpty) return;

            if (_grouped)
            {
                PlaylistList.CanReorderItems = false;
                var groups = PlaylistGroupBuilder.BuildGroups(pl);
                PlaylistCvs.Source = groups;
                if (PlaylistList.ItemsSource != PlaylistCvs.View)
                    PlaylistList.ItemsSource = PlaylistCvs.View;
            }
            else
            {
                PlaylistList.CanReorderItems = true;
                if (PlaylistList.ItemsSource != pl.Items)
                    PlaylistList.ItemsSource = pl.Items;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[PlaylistPage.Rebuild Error] {ex}");
        }
    }

    // ---------------- toolbar ----------------

    private void OnGroupToggle(object sender, RoutedEventArgs e)
    {
        _grouped = GroupToggleMenuItem?.IsChecked == true;
        AppServices.Settings.Ui.PlaylistGroupedView = _grouped;
        SettingsWriter.Schedule(AppServices.Settings);
        Rebuild();
    }

    private void OnSortTitle(object sender, RoutedEventArgs e) => Sort(PlaylistSort.Title);
    private void OnSortArtist(object sender, RoutedEventArgs e) => Sort(PlaylistSort.Artist);
    private void OnSortAlbum(object sender, RoutedEventArgs e) => Sort(PlaylistSort.Album);
    private void OnSortTrackNo(object sender, RoutedEventArgs e) => Sort(PlaylistSort.TrackNo);
    private void OnSortPath(object sender, RoutedEventArgs e) => Sort(PlaylistSort.Path);
    private void OnSortRandom(object sender, RoutedEventArgs e) => Sort(PlaylistSort.Random);
    private void OnSortReverse(object sender, RoutedEventArgs e) => Sort(PlaylistSort.Reverse);

    private void Sort(PlaylistSort mode)
    {
        if (Current.Items.Count > 1)
            AppServices.Playlists.Sort(Current, mode);
    }

    private void OnRemoveDuplicates(object sender, RoutedEventArgs e) =>
        AppServices.Playlists.RemoveDuplicates(Current);

    private async void OnRemoveDeadItemsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            int removed = await AppServices.Playlists.RemoveDeadItemsAsync(Current);
            if (removed > 0)
            {
                AppServices.RaiseWarning(AppStrings.Format("Msg_RemovedMissingFiles", removed));
            }
            else
            {
                AppServices.RaiseWarning(AppStrings.Get("Msg_NoMissingFiles", "제거할 누락된 파일이 없습니다."));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[RemoveDeadItems Error] {ex}");
        }
    }

    private void OnStopAfterCurrentClick(object sender, RoutedEventArgs e)
    {
        if (AppServices.Playback == null) return;
        AppServices.Playback.StopAfterCurrent = StopAfterCurrentMenuItem?.IsChecked == true;
    }

    /// <summary>
    /// Mirrors the controller back into the menu item. The flag is also flipped by the keyboard
    /// shortcut and cleared by the controller once the track ends, and without this the check mark
    /// kept showing whatever the menu was last clicked to.
    /// </summary>
    private void SyncStopAfterCurrentMenuItem()
    {
        if (StopAfterCurrentMenuItem == null || AppServices.Playback == null) return;
        var flag = AppServices.Playback.StopAfterCurrent;
        if (StopAfterCurrentMenuItem.IsChecked != flag) StopAfterCurrentMenuItem.IsChecked = flag;
    }

    private void OnClearPlaylist(object sender, RoutedEventArgs e) =>
        AppServices.Playlists.RemoveAll(Current);

    // ---------------- row interactions ----------------

    private List<PlaylistItem> SelectedItems() =>
        PlaylistList.SelectedItems.OfType<PlaylistItem>().ToList();

    private void OnMoveUpClick(object sender, RoutedEventArgs e)
    {
        var sel = SelectedItems();
        if (sel.Count > 0)
        {
            AppServices.Playlists.MoveSelection(Current, sel, up: true);
            PlaylistList.SelectedItems.Clear();
            foreach (var itm in sel) PlaylistList.SelectedItems.Add(itm);
        }
    }

    private void OnMoveDownClick(object sender, RoutedEventArgs e)
    {
        var sel = SelectedItems();
        if (sel.Count > 0)
        {
            AppServices.Playlists.MoveSelection(Current, sel, up: false);
            PlaylistList.SelectedItems.Clear();
            foreach (var itm in sel) PlaylistList.SelectedItems.Add(itm);
        }
    }

    private async void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = VisualTreeHelperExtensions.ResolveItem(e, PlaylistList.SelectedItem as PlaylistItem);
        if (item != null)
        {
            await PlaybackUiHelper.PlayItemAsync(AppServices.Playback, Current, item);
        }
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var isAlt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            var sel = SelectedItems();
            if (sel.Count > 0)
            {
                AppServices.Playlists.RemoveItems(Current, sel);
                e.Handled = true;
            }
        }
        else if (isAlt && e.Key == Windows.System.VirtualKey.Up)
        {
            OnMoveUpClick(sender, e);
            e.Handled = true;
        }
        else if (isAlt && e.Key == Windows.System.VirtualKey.Down)
        {
            OnMoveDownClick(sender, e);
            e.Handled = true;
        }
    }

    private void OnListContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && VisualTreeHelperExtensions.FindAncestorDataContext<PlaylistItem>(fe) is { } item)
        {
            if (!PlaylistList.SelectedItems.Contains(item))
            {
                PlaylistList.SelectedItems.Clear();
                PlaylistList.SelectedItem = item;
            }
        }
    }

    private async void OnPlayItems(object sender, RoutedEventArgs e)
    {
        var sel = SelectedItems();
        if (sel.Count > 0)
        {
            await PlaybackUiHelper.PlayItemAsync(AppServices.Playback, Current, sel[0]);
        }
    }

    private void OnQueueItems(object sender, RoutedEventArgs e) =>
        PlaybackUiHelper.EnqueueItems(AppServices.Playback, Current, SelectedItems(), playNext: false);

    private void OnQueueNextItems(object sender, RoutedEventArgs e) =>
        PlaybackUiHelper.EnqueueItems(AppServices.Playback, Current, SelectedItems(), playNext: true);

    private void OnRemoveItems(object sender, RoutedEventArgs e) =>
        PlaybackUiHelper.RemoveItems(AppServices.Playlists, Current, SelectedItems());
}
