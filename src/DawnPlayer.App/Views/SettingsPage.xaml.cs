using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.App.Calculators;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.App.Shortcuts;
using DawnPlayer.App.ViewModels.Settings;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Pickers;

namespace DawnPlayer.App.Views;

/// <summary>
/// Lean code-behind view for SettingsPage.
/// Delegating all state management, validation, clamping, and business logic to <see cref="SettingsViewModel"/>.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        _flashTimer.Tick += (_, _) => RestoreFlashedCard();
        ViewModel = new SettingsViewModel(
            AppServices.Settings,
            AppServices.AudioSettings,
            AppServices.EqSettings,
            AppServices.AppearanceSettings,
            scanStarter: AppServices.StartLibraryScan,
            lyricsChangedNotifier: AppServices.RaiseLyricsSettingsChanged,
            warningNotifier: AppServices.RaiseWarning,
            isExclusiveSessionGetter: () => AppServices.Playback.IsExclusiveSession ||
                (AppServices.Settings.Output.DriverType == AudioDriverType.Wasapi &&
                 AppServices.Settings.Output.UseExclusiveMode &&
                 AppServices.Playback.CurrentSessionInfo?.Exclusive == true),
            shortcutStore: AppServices.Shortcuts,
            logger: App.Log,
            lyricsOnlineService: AppServices.LyricsOnline,
            languageChangedNotifier: AppServices.ChangeLanguage);

        InitializeComponent();

        // The About version is read from the assembly so it tracks the build instead of a
        // hand-maintained literal that drifted to "0.1" while releases were at v1.0.x.
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null && AboutVersionText != null)
        {
            AboutVersionText.Text = AppStrings.Format("Settings_About_VersionFormat", "버전 {0} (x64) · foobar2000 & Eole 영감 네이티브 플레이어", version.ToString(3));
        }

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    // Called through x:Bind generated code (this.dataRoot.BooleanToVisibility), which
    // requires an instance member.
#pragma warning disable CA1822
    public Visibility BooleanToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BooleanToVisibilityNot(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
#pragma warning restore CA1822

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // CR2: a scan settling while the page is unloaded drops its completion event — sync
        // the button state from the source of truth instead of trusting the last event.
        _settingsSearchIndex = null; // rebuild per visit: category labels are localized
        var scanRunning = AppServices.IsReplayGainScanRunning;
        RgScanStartButton.IsEnabled = !scanRunning;
        RgRescanAllButton.IsEnabled = !scanRunning;
        // Seed the masked secret box from the saved credential (PasswordBox has no bindable
        // two-way Password; see OnLastfmSecretPasswordChanged).
        LastfmApiSecretBox.Password = ViewModel.Lastfm.ApiSecret ?? "";
        AppServices.OutputSessionChanged += OnOutputSessionChanged;
        AppServices.RgScanProgressChanged += OnRgScanProgress;
        AppServices.RgScanCompleted += OnRgScanCompleted;
        ViewModel.Equalizer.PropertyChanged += OnEqualizerPropertyChanged;
        ViewModel.Lyrics.PropertyChanged += OnLyricsPropertyChanged;
        ViewModel.Shortcuts.AttachToStore();

        ViewModel.RefreshAll();
        UpdateLyricsPreview();
        RenderVisualizer();
        ViewModel.Lastfm.Refresh();

        if (AppServices.Settings != null)
        {
            DspPluginsToggle.IsOn = AppServices.Settings.Plugins.DspEnabled;
        }
    }

    private void OnDspPluginsToggled(object sender, RoutedEventArgs e)
    {
        if (AppServices.Settings == null) return;
        AppServices.Settings.Plugins.DspEnabled = DspPluginsToggle.IsOn;
        SettingsWriter.Schedule(AppServices.Settings);
        AppServices.Playback?.ApplyPluginDsp();
    }

    // ---------------- last.fm scrobbling (state lives in ViewModel.Lastfm) ----------------

    private void OnLastfmToggleToggled(object sender, RoutedEventArgs e) =>
        ViewModel.Lastfm.ApplyEnabled(LastfmToggle.IsOn);

    private void OnLastfmCredentialsLostFocus(object sender, RoutedEventArgs e) =>
        ViewModel.Lastfm.ApplyCredentials();

    /// <summary>PasswordBox.Password is not a DependencyProperty, so the secret syncs through
    /// this handler instead of a TwoWay binding (PT4-06).</summary>
    private void OnLastfmSecretPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Lastfm == null || LastfmApiSecretBox == null) return;
        ViewModel.Lastfm.ApiSecret = LastfmApiSecretBox.Password;
    }

    /// <summary>Commits the typed HEX on blur and reports malformed input beside the field —
    /// the old TwoWay binding silently dropped invalid text (PT4-07).</summary>
    private void OnCustomAccentHexLostFocus(object sender, RoutedEventArgs e)
    {
        var ok = ViewModel.Appearance.TrySetCustomAccentHex(CustomAccentHexBox.Text);
        CustomAccentHexError.Text = ok
            ? ""
            : AppStrings.Get("Settings_Appearance_CustomAccent_InvalidHex",
                "올바른 HEX 형식이 아닙니다. #RRGGBB 형식으로 입력하세요.");
        CustomAccentHexError.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        if (ok && ThemeService.TryColorFromHex(ViewModel.Appearance.CustomAccentHex, out var color))
        {
            CustomColorPreview.Background = new SolidColorBrush(color);
        }
    }

    private void OnLastfmAuthClick(object sender, RoutedEventArgs e) =>
        _ = ViewModel.Lastfm.StartAuthAsync();

    private void OnLastfmConfirmClick(object sender, RoutedEventArgs e) =>
        _ = ViewModel.Lastfm.ConfirmAuthAsync();

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        RestoreFlashedCard();
        AppServices.OutputSessionChanged -= OnOutputSessionChanged;
        AppServices.RgScanProgressChanged -= OnRgScanProgress;
        AppServices.RgScanCompleted -= OnRgScanCompleted;
        ViewModel.Equalizer.PropertyChanged -= OnEqualizerPropertyChanged;
        ViewModel.Lyrics.PropertyChanged -= OnLyricsPropertyChanged;
        ViewModel.Shortcuts.DetachFromStore();
    }

    private void OnRgScanProgress(string message)
    {
        if (RgScanStatusText == null) return;
        RgScanStatusText.Text = message;
        RgScanStatusText.Visibility = Visibility.Visible;
    }

    private void OnOutputSessionChanged(SessionInfo info)
    {
        AppServices.RunOnUi(() =>
        {
            ViewModel.HandleSessionChanged(info);
        });
    }

    private void OnEqualizerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EqualizerSettingsViewModel.VisualizerData))
        {
            RenderVisualizer();
        }
    }

    private void OnLyricsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateLyricsPreview();
    }

    // ---------------- settings search (PT4-14) ----------------

    private sealed class SettingSearchEntry
    {
        public required string Display;
        public required string SearchText;
        public int SectionIndex;
        public FrameworkElement? Target;

        public override string ToString() => Display;
    }

    private List<SettingSearchEntry>? _settingsSearchIndex;

    /// <summary>Indexes every settings card title (plus its sibling description text and the
    /// category name) once, on first use. All sections stay instantiated behind Visibility
    /// toggles, so the whole page is searchable without switching categories.</summary>
    private void BuildSettingsSearchIndex()
    {
        if (_settingsSearchIndex != null) return;
        var index = new List<SettingSearchEntry>();
        var titleStyle = Resources["SettingTitle"] as Style;
        var descStyle = Resources["SettingDesc"] as Style;
        int sectionIndex = 0;
        foreach (var item in CategoryList.Items.OfType<ListViewItem>())
        {
            var tag = item.Tag as string ?? "";
            var section = FindName("Section_" + tag) as StackPanel;
            var categoryName = (item.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? tag;

            // The category itself is always searchable.
            index.Add(new SettingSearchEntry
            {
                Display = categoryName,
                SearchText = categoryName,
                SectionIndex = sectionIndex,
                Target = section,
            });

            if (section != null)
            {
                foreach (var title in EnumerateDescendants<TextBlock>(section)
                             .Where(t => titleStyle != null && ReferenceEquals(t.Style, titleStyle)))
                {
                    // Sibling SettingDesc texts (same label stack) join the search text so a
                    // word from the description finds the row too.
                    var siblings = title.Parent is StackPanel sp
                        ? sp.Children.OfType<TextBlock>()
                            .Where(t => descStyle != null && ReferenceEquals(t.Style, descStyle))
                            .Select(t => t.Text)
                        : Enumerable.Empty<string>();
                    var searchText = string.Join(" ", new[] { title.Text }.Concat(siblings).Where(x => x.Length > 0));
                    if (searchText.Length == 0) continue;
                    index.Add(new SettingSearchEntry
                    {
                        Display = title.Text,
                        SearchText = searchText,
                        SectionIndex = sectionIndex,
                        Target = title,
                    });
                }
            }
            sectionIndex++;
        }
        _settingsSearchIndex = index;
    }

    private static IEnumerable<T> EnumerateDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            int count;
            try { count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(current); }
            catch { continue; }
            for (int i = 0; i < count; i++)
            {
                DependencyObject? child;
                try { child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(current, i); }
                catch { continue; }
                if (child is T match) yield return match;
                if (child != null) queue.Enqueue(child);
            }
        }
    }

    private void OnSettingsSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        BuildSettingsSearchIndex();
        var query = sender.Text.Trim();
        if (query.Length == 0)
        {
            sender.ItemsSource = null;
            return;
        }
        var matches = _settingsSearchIndex!
            .Where(e => e.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();
        sender.ItemsSource = matches;
    }

#pragma warning disable CA1822
    private void OnSettingsSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        // Highlight-only here; the jump happens on submit (click/Enter) so arrowing through the
        // dropdown doesn't yank the page around.
    }
#pragma warning restore CA1822

    private void OnSettingsSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        BuildSettingsSearchIndex();
        var entry = args.ChosenSuggestion as SettingSearchEntry;
        if (entry == null)
        {
            var query = args.QueryText.Trim();
            if (query.Length == 0) return;
            entry = _settingsSearchIndex!.FirstOrDefault(
                e => e.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        if (entry == null) return;
        JumpToSetting(entry);
    }

    /// <summary>Switches to the entry's category, scrolls the matched card into view and
    /// flashes it. CategoryList's SelectionChanged resets the scroll position, so the jump
    /// scroll runs one dispatcher tick later — after that reset.</summary>
    private void JumpToSetting(SettingSearchEntry entry)
    {
        SettingsSearchBox.IsSuggestionListOpen = false;
        SettingsSearchBox.Text = entry.Display;
        if (CategoryList.SelectedIndex != entry.SectionIndex)
        {
            CategoryList.SelectedIndex = entry.SectionIndex;
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var target = entry.Target;
            if (target == null || ContentScrollViewer?.Content is not UIElement content) return;
            target.UpdateLayout();
            var pos = target.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point(0, 0));
            ContentScrollViewer.ChangeView(null, Math.Max(0, pos.Y - 40), null, false);
            FlashSectionCard(target);
        });
    }

    private Border? _flashingCard;
    private Brush? _flashOriginalBrush;
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

    private void FlashSectionCard(FrameworkElement target)
    {
        var card = Helpers.VisualTreeHelperExtensions.FindAncestor<Border>(target);
        while (card != null && !ReferenceEquals(card.Style, Resources["SectionCard"]))
        {
            card = Helpers.VisualTreeHelperExtensions.FindAncestor<Border>(card.Parent);
        }
        if (card == null) return;

        if (_flashingCard != card)
        {
            RestoreFlashedCard();
            _flashingCard = card;
            _flashOriginalBrush = card.Background;
        }
        card.Background = Helpers.ThemeResourceHelper.GetBrush("DawnAccentMutedBrush");
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private void RestoreFlashedCard()
    {
        if (_flashingCard != null && _flashOriginalBrush != null)
        {
            _flashingCard.Background = _flashOriginalBrush;
        }
        _flashingCard = null;
        _flashOriginalBrush = null;
        _flashTimer.Stop();
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ContentScrollViewer?.ChangeView(0, 0, 1.0f, true);

        if (ViewModel.IsEqualizerCategorySelected)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RenderVisualizer);
        }
        else if (ViewModel.IsLyricsCategorySelected)
        {
            UpdateLyricsPreview();
        }
        else if (ViewModel.IsLastfmCategorySelected)
        {
            // The queued-scrobble count in the status line changes while the page is open.
            ViewModel.Lastfm.Refresh();
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        // Navigating the frame to a LibraryPage created a *second* instance alongside the one that
        // is a permanent XAML child of MainWindow. Each copy stayed subscribed to the static
        // AppServices events forever, so every Settings round trip added another full set of
        // per-track lyrics loads and per-queue-change regroup passes. Ask the shell to show the
        // real page instead.
        App.MainWin?.NavigateToTab(AppServices.Settings.Ui.LastNavTab ?? "Library");
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e) =>
        ViewModel.Audio.RefreshDevices();

    // ---------------- online lyrics plugins ----------------

    private void OnRescanPluginsClick(object sender, RoutedEventArgs e) =>
        ViewModel.OnlineLyrics.Rescan();

    private async void OnOpenPluginsFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ViewModel.OnlineLyrics.PluginsFolder);
            await Windows.System.Launcher.LaunchFolderPathAsync(ViewModel.OnlineLyrics.PluginsFolder);
        }
        catch (Exception ex)
        {
            App.Log($"[OnlineLyrics] 플러그인 폴더 열기 실패: {ex.Message}");
        }
    }

    private async void OnPickLyricsSaveFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, AppServices.MainWindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            ViewModel.OnlineLyrics.SetCustomSaveFolder(folder.Path);
        }
    }

    private void OnPluginMoveUpClick(object sender, RoutedEventArgs e) =>
        MovePlugin(sender, -1);

    private void OnPluginMoveDownClick(object sender, RoutedEventArgs e) =>
        MovePlugin(sender, +1);

    private void MovePlugin(object sender, int delta)
    {
        if (sender is FrameworkElement { Tag: ViewModels.Settings.LyricsPluginItemVm item })
        {
            if (delta < 0) ViewModel.OnlineLyrics.MovePluginUp(item);
            else ViewModel.OnlineLyrics.MovePluginDown(item);
        }
    }

    private void OnPluginToggled(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ViewModels.Settings.LyricsPluginItemVm item })
        {
            ViewModel.OnlineLyrics.SetPluginEnabled(item);
        }
    }

    // ---------------- keyboard shortcuts ----------------

    private async void OnShortcutRebindClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShortcutBindingViewModel row }) return;

        var capture = new ShortcutCaptureDialog(row.DisplayName) { XamlRoot = XamlRoot };
        await capture.ShowAsync();
        if (capture.CapturedChord is not { } chord) return;

        switch (ViewModel.Shortcuts.TryAssign(row.Command, chord, out var conflicting))
        {
            case ShortcutAssignResult.Assigned:
                return;

            case ShortcutAssignResult.InvalidChord:
                await ShowShortcutNoticeAsync(
                    AppStrings.Get("Msg_ShortcutInvalidTitle", "사용할 수 없는 키"),
                    AppStrings.Get("Msg_ShortcutInvalidMessage", "이 키 조합은 단축키로 지정할 수 없습니다."));
                return;

            case ShortcutAssignResult.Conflict:
                var overwrite = new ContentDialog
                {
                    Title = AppStrings.Get("Msg_ShortcutConflictTitle", "단축키 충돌"),
                    Content = AppStrings.Format("Msg_ShortcutConflictMessage", "'{0}' 은(는) 이미 '{1}'에 할당되어 있습니다. 덮어쓰면 해당 명령의 단축키가 해제됩니다.",
                        chord.ToDisplayString(),
                        ShortcutSettingsViewModel.GetCommandDisplayName(conflicting)),
                    PrimaryButtonText = AppStrings.Get("Common_Overwrite", "덮어쓰기"),
                    CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };

                if (await overwrite.ShowAsync() == ContentDialogResult.Primary)
                {
                    ViewModel.Shortcuts.ForceAssign(row.Command, chord);
                }
                return;
        }
    }

    private void OnShortcutResetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutBindingViewModel row })
        {
            ViewModel.Shortcuts.ResetToDefault(row.Command);
        }
    }

    private void OnShortcutClearClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutBindingViewModel row })
        {
            ViewModel.Shortcuts.Clear(row.Command);
        }
    }

    private async void OnShortcutResetAllClick(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = AppStrings.Get("Msg_ShortcutResetAllTitle", "모든 단축키 초기화"),
            Content = AppStrings.Get("Msg_ShortcutResetAllMessage", "모든 단축키를 기본값으로 되돌립니다. 직접 지정한 조합은 사라집니다."),
            PrimaryButtonText = AppStrings.Get("Common_Reset", "되돌리기"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Shortcuts.ResetAll();
        }
    }

    private Task<ContentDialogResult> ShowShortcutNoticeAsync(string title, string body) =>
        new ContentDialog
        {
            Title = title,
            Content = body,
            CloseButtonText = AppStrings.Get("Common_OK", "확인"),
            XamlRoot = XamlRoot
        }.ShowAsync().AsTask();

    private void OnOpenSoundControlPanel(object sender, RoutedEventArgs e) =>
        ViewModel.Audio.OpenSoundControlPanel();

    private async void OnEqNewProfileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("Msg_EqNewProfileTitle", "새 EQ 프로필 생성"),
            PrimaryButtonText = AppStrings.Get("Common_Create", "생성"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var input = new TextBox
        {
            PlaceholderText = AppStrings.Get("Msg_EqProfilePlaceholder", "프로필 이름 입력 (예: 보컬 부스트, 헤드폰)"),
            Text = AppStrings.Format("Msg_EqProfileDefaultName", "프로필 {0}", ViewModel.Equalizer.Profiles.Count + 1)
        };
        dialog.Content = input;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            ViewModel.Equalizer.CreateProfile(input.Text.Trim());
        }
    }

    private void OnEqDuplicateProfileClick(object sender, RoutedEventArgs e) =>
        ViewModel.Equalizer.DuplicateProfile();

    private async void OnEqRenameProfileClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Equalizer.SelectedProfile == null) return;

        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("Msg_EqRenameProfileTitle", "프로필 이름 변경"),
            PrimaryButtonText = AppStrings.Get("Common_Change", "변경"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var input = new TextBox { Text = ViewModel.Equalizer.ProfileName };
        dialog.Content = input;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            ViewModel.Equalizer.RenameProfile(input.Text.Trim());
        }
    }

    private async void OnEqDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Equalizer.SelectedProfile == null || !ViewModel.Equalizer.CanDeleteProfile) return;

        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("Msg_EqDeleteProfileTitle", "프로필 삭제"),
            Content = AppStrings.Format("Msg_EqDeleteProfileMessage", "'{0}' 프로필을 삭제하시겠습니까?\n이 프로필에 연결된 장치는 기본 프로필로 전환됩니다.", ViewModel.Equalizer.ProfileName),
            PrimaryButtonText = AppStrings.Get("Common_Delete", "삭제"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.Equalizer.DeleteCurrentProfile();
        }
    }

    private void OnEqAddBandClick(object sender, RoutedEventArgs e) =>
        ViewModel.Equalizer.AddBand();

    private void OnDeleteBandClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is EqBandViewModel band)
        {
            ViewModel.Equalizer.RemoveBand(band);
        }
    }

    private void OnRefreshEqDevices(object sender, RoutedEventArgs e) =>
        ViewModel.Equalizer.RefreshDevicesAndBindings();

    private void OnEqVisualizerContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 20 && e.NewSize.Height > 20)
        {
            ViewModel.Equalizer.RecalculateVisualizer(e.NewSize.Width, e.NewSize.Height);
        }
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, AppServices.MainWindowHandle);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                ViewModel.Library.AddFolder(folder.Path);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[OnAddFolder Error] {ex}");
        }
    }

    private async void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        var target = ViewModel.Library.SelectedFolder;
        if (string.IsNullOrWhiteSpace(target)) return;

        // Removing a watch folder stops updates for everything under it (PT4-09) — confirm.
        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("Settings_Library_RemoveFolderTitle", "감시 폴더 제거"),
            Content = AppStrings.Format("Settings_Library_RemoveFolderMessage",
                "'{0}'을(를) 감시 폴더에서 제거할까요? 폴더와 파일은 삭제되지 않습니다.", target),
            PrimaryButtonText = AppStrings.Get("Msg_DeletePlaylistConfirm", "삭제"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        ViewModel.Library.RemoveFolder();
    }

    private void OnScanNow(object sender, RoutedEventArgs e) =>
        ViewModel.Library.TriggerScanNow();

    private void OnRgScanStart(object sender, RoutedEventArgs e) => RunRgScan(rescanAll: false);

    private async void OnPickImpulseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, AppServices.MainWindowHandle);
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".flac");
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".ogg");
            picker.FileTypeFilter.Add(".m4a");

            var file = await picker.PickSingleFileAsync();
            if (file != null) ViewModel.Playback.SetImpulsePath(file.Path);
        }
        catch (Exception ex)
        {
            App.Log($"[impulse pick] {ex}");
        }
    }

    private void OnClearImpulseClick(object sender, RoutedEventArgs e) =>
        ViewModel.Playback.SetImpulsePath("");

    private async void OnRgScanRescanAll(object sender, RoutedEventArgs e)
    {
        // A rescan overwrites existing REPLAYGAIN_* tags on every file — confirm (PT4-10).
        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("Settings_Library_Rg_RescanConfirmTitle", "전체 재분석"),
            Content = AppStrings.Get("Settings_Library_Rg_RescanConfirmMessage",
                "모든 트랙의 ReplayGain 태그를 다시 계산하고 기존 값을 덮어씁니다. 계속할까요?"),
            PrimaryButtonText = AppStrings.Get("Common_OK", "확인"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        RunRgScan(rescanAll: true);
    }

    /// <summary>Starts an RG scan with immediate feedback: the buttons lock for the whole run
    /// and the status line wakes up before the first per-track event lands (PT4-10).</summary>
    private void RunRgScan(bool rescanAll)
    {
        if (AppServices.IsReplayGainScanRunning)
        {
            AppServices.RaiseWarning(AppStrings.Get("Settings_Library_Rg_AlreadyRunning", "ReplayGain 분석이 이미 실행 중입니다."));
            return;
        }
        RgScanStartButton.IsEnabled = false;
        RgRescanAllButton.IsEnabled = false;
        if (RgScanStatusText != null)
        {
            RgScanStatusText.Text = AppStrings.Get("Settings_Library_Rg_Starting", "분석 시작…");
            RgScanStatusText.Visibility = Visibility.Visible;
        }
        AppServices.StartReplayGainScan(rescanAll);
    }

    private void OnRgScanCompleted() => AppServices.RunOnUi(() =>
    {
        // Raised on a worker thread — the button unlock must hop back to the UI thread.
        RgScanStartButton.IsEnabled = true;
        RgRescanAllButton.IsEnabled = true;
    });

    private void OnLrcPatternsLostFocus(object sender, RoutedEventArgs e) =>
        ViewModel.Lyrics.SaveLrcPatterns(LrcPatternsBox.Text);

    private void OnResetLrcPatterns(object sender, RoutedEventArgs e) =>
        ViewModel.Lyrics.ResetLrcPatternsToDefault();

    private void OnColorPickerColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        var col = args.NewColor;
        var hex = $"#FF{col.R:X2}{col.G:X2}{col.B:X2}";
        ViewModel.Appearance.TrySetCustomAccentHex(hex);
        CustomColorPreview.Background = new SolidColorBrush(col);
    }

    private void OnResetLayoutClick(object sender, RoutedEventArgs e) =>
        ViewModel.Layout.ResetLayoutToDefaults();

    private void RenderVisualizer()
    {
        if (EqVisualizerCanvas == null || EqVisualizerBorder == null) return;

        var data = ViewModel.Equalizer.VisualizerData;
        if (data == null)
        {
            double w = EqVisualizerBorder.ActualWidth > 50 ? EqVisualizerBorder.ActualWidth : 700;
            double h = EqVisualizerBorder.ActualHeight > 50 ? EqVisualizerBorder.ActualHeight : 190;
            ViewModel.Equalizer.RecalculateVisualizer(w, h);
            data = ViewModel.Equalizer.VisualizerData;
            if (data == null) return;
        }

        double width = EqVisualizerBorder.ActualWidth > 50 ? EqVisualizerBorder.ActualWidth : data.PlotWidth + data.PadLeft + 16;
        double height = EqVisualizerBorder.ActualHeight > 50 ? EqVisualizerBorder.ActualHeight : data.PlotHeight + data.PadTop + 22;

        EqVisualizerCanvas.Width = width;
        EqVisualizerCanvas.Height = height;
        EqVisualizerCanvas.Children.Clear();

        var lineBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255));
        var zeroLineBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 255, 255, 255));
        var textBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255));

        // 1. Horizontal dB lines
        foreach (var h in data.HorizontalDbLines)
        {
            var line = new Border
            {
                Width = data.PlotWidth,
                Height = h.Value == 0 ? 1.0 : 0.8,
                Background = h.Value == 0 ? zeroLineBrush : lineBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            Canvas.SetLeft(line, data.PadLeft);
            Canvas.SetTop(line, h.Position);
            EqVisualizerCanvas.Children.Add(line);

            if (!string.IsNullOrEmpty(h.Label))
            {
                var label = new TextBlock
                {
                    Text = h.Label,
                    FontSize = 9.5,
                    Foreground = textBrush,
                    HorizontalTextAlignment = TextAlignment.Right,
                    Width = 28
                };
                Canvas.SetLeft(label, data.PadLeft - 32);
                Canvas.SetTop(label, h.Position - 7);
                EqVisualizerCanvas.Children.Add(label);
            }
        }

        // 2. Vertical frequency lines
        foreach (var v in data.VerticalFreqLines)
        {
            var line = new Border
            {
                Width = 0.8,
                Height = data.PlotHeight,
                Background = lineBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            Canvas.SetLeft(line, v.Position);
            Canvas.SetTop(line, data.PadTop);
            EqVisualizerCanvas.Children.Add(line);

            if (!string.IsNullOrEmpty(v.Label))
            {
                var label = new TextBlock
                {
                    Text = v.Label,
                    FontSize = 9.5,
                    Foreground = textBrush,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Width = 30
                };
                Canvas.SetLeft(label, v.Position - 15);
                Canvas.SetTop(label, data.PadTop + data.PlotHeight + 3);
                EqVisualizerCanvas.Children.Add(label);
            }
        }

        if (data.CurvePoints.Count == 0) return;

        // 3. Fill Polygon
        var fillGeometry = new PathGeometry();
        var fillFigure = new PathFigure
        {
            StartPoint = new Point(data.FillPoints[0].X, data.FillPoints[0].Y),
            IsClosed = true,
            IsFilled = true
        };
        var fillSegment = new PolyLineSegment();
        for (int i = 1; i < data.FillPoints.Count; i++)
        {
            fillSegment.Points.Add(new Point(data.FillPoints[i].X, data.FillPoints[i].Y));
        }
        fillFigure.Segments.Add(fillSegment);
        fillGeometry.Figures.Add(fillFigure);

        var fillPath = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = fillGeometry,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 235, 140, 50)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        EqVisualizerCanvas.Children.Add(fillPath);

        // 4. Response Curve Line
        var curveGeometry = new PathGeometry();
        var curveFigure = new PathFigure
        {
            StartPoint = new Point(data.CurvePoints[0].X, data.CurvePoints[0].Y),
            IsClosed = false,
            IsFilled = false
        };
        var curveSegment = new PolyLineSegment();
        for (int i = 1; i < data.CurvePoints.Count; i++)
        {
            curveSegment.Points.Add(new Point(data.CurvePoints[i].X, data.CurvePoints[i].Y));
        }
        curveFigure.Segments.Add(curveSegment);
        curveGeometry.Figures.Add(curveFigure);

        var accentBrush = (Application.Current.Resources.TryGetValue("DawnAccentBrush", out var ab) && ab is Brush accBrush)
            ? accBrush
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 235, 140, 50));

        var curvePath = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = curveGeometry,
            Stroke = data.IsEnabled ? accentBrush : new SolidColorBrush(Windows.UI.Color.FromArgb(120, 160, 160, 160)),
            StrokeThickness = 2.2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        EqVisualizerCanvas.Children.Add(curvePath);

        // 5. Band Pins
        if (data.IsEnabled)
        {
            foreach (var pin in data.BandPins)
            {
                var pinColor = ThemeService.ColorFromHex(pin.ColorHex);
                var pinBrush = new SolidColorBrush(pinColor);

                var guideLine = new Border
                {
                    Width = 1,
                    Height = data.PlotHeight,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, pinColor.R, pinColor.G, pinColor.B)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Canvas.SetLeft(guideLine, pin.X);
                Canvas.SetTop(guideLine, data.PadTop);
                EqVisualizerCanvas.Children.Add(guideLine);

                var glowRing = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(45, pinColor.R, pinColor.G, pinColor.B)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Canvas.SetLeft(glowRing, pin.X - 8);
                Canvas.SetTop(glowRing, pin.Y - 8);
                EqVisualizerCanvas.Children.Add(glowRing);

                var nodeDot = new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(5),
                    Background = pinBrush,
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 20, 20, 20)),
                    BorderThickness = new Thickness(1.5),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Canvas.SetLeft(nodeDot, pin.X - 5);
                Canvas.SetTop(nodeDot, pin.Y - 5);
                EqVisualizerCanvas.Children.Add(nodeDot);
            }
        }
    }

    private void UpdateLyricsPreview()
    {
        if (PreviewLine1 == null || PreviewLine2 == null || PreviewLine3 == null) return;

        var lyr = ViewModel.Lyrics;
        var fontFam = new FontFamily(lyr.EffectiveFontFamily);
        var align = lyr.Alignment switch
        {
            "Left" => TextAlignment.Left,
            "Right" => TextAlignment.Right,
            _ => TextAlignment.Center
        };

        PreviewLine1.FontFamily = fontFam;
        PreviewLine1.FontSize = lyr.FontSize;
        PreviewLine1.CharacterSpacing = lyr.CharacterSpacing;
        PreviewLine1.LineHeight = lyr.LineHeight;
        PreviewLine1.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        PreviewLine1.TextAlignment = align;
        PreviewLine1.Opacity = lyr.EnableFocusEffect ? 0.40 : 0.85;

        PreviewLine2.FontFamily = fontFam;
        PreviewLine2.FontSize = lyr.ActiveFontSize;
        PreviewLine2.CharacterSpacing = lyr.CharacterSpacing;
        PreviewLine2.LineHeight = lyr.LineHeight;
        PreviewLine2.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        PreviewLine2.TextAlignment = align;
        PreviewLine2.Opacity = 1.0;

        PreviewLine3.FontFamily = fontFam;
        PreviewLine3.FontSize = lyr.FontSize;
        PreviewLine3.CharacterSpacing = lyr.CharacterSpacing;
        PreviewLine3.LineHeight = lyr.LineHeight;
        PreviewLine3.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        PreviewLine3.TextAlignment = align;
        PreviewLine3.Opacity = lyr.EnableFocusEffect ? 0.40 : 0.85;
    }
}
