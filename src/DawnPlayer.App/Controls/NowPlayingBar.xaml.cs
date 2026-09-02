using DawnPlayer.App.Helpers;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DawnPlayer.App.Controls;

public sealed partial class NowPlayingBar : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly QueuePopupController _queueController = new();
    private readonly SeekbarScrubbingCalculator _seekCalculator = new();

    private bool _updatingSliderFromTimer;
    private double _lastVolume = 0.8;
    private int _smtcTick;
    private string _formatBadgeText = "";
    private string _outputBadgeText = "";
    private bool _volumeAvailableInSession = true;
    private int _artGeneration;

    public event Action? LyricsToggleRequested;

    public NowPlayingBar()
    {
        InitializeComponent();
        // drag-to-seek via pointer events (Thumb routed-event fields are unavailable in WinUI 3)
        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _seekCalculator.BeginDrag()), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => CompleteSeek()), true);
        SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => CompleteSeek()), true);
        SeekSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => CompleteSeek()), true);
        SeekSlider.ValueChanged += OnSeekChanged;
        _timer.Tick += (_, _) => OnTimer();
        QueueList.ItemsSource = _queueController.Entries;

        AppServices.OutputSessionChanged += OnOutputSession;
    }

    private void CompleteSeek()
    {
        if (!_seekCalculator.IsDragging) return;
        var target = _seekCalculator.CompleteDrag(SeekSlider.Value, AppServices.Playback?.Duration ?? TimeSpan.Zero);
        if (target.HasValue)
        {
            AppServices.Playback?.Seek(target.Value);
        }
    }

    /// <summary>Called by MainWindow after AppServices.Initialize.</summary>
    public void InitializeState()
    {
        VolumeSlider.Value = AppServices.Settings.Playback.Volume * 100;
        ShuffleButton.IsChecked = AppServices.Settings.Playback.Shuffle;
        UpdateRepeatVisual();
        UpdateShuffleVisual();
        UpdateVolumeIcon(VolumeSlider.Value);
        UpdateAbRepeatVisual();
        OnQueueChanged();
        OnStateChanged();
        _timer.Start();
    }

    public void RestoreLastPosition(double seconds, double maxSeconds)
    {
        _updatingSliderFromTimer = true;
        var state = SeekbarScrubbingCalculator.CalculateRestoreState(seconds, maxSeconds);
        SeekSlider.Maximum = state.ClampedMax;
        SeekSlider.Value = state.ClampedValue;
        ElapsedText.Text = state.Elapsed;
        RemainingText.Text = state.Remaining;
        _updatingSliderFromTimer = false;
    }

    // ---------- central events (already on UI thread) ----------

    public void OnTrackChanged(Core.Models.PlaylistItem? item)
    {
        if (item == null)
        {
            TrackTitle.Text = AppStrings.Get("NowPlaying_TrackTitle_Empty.Text", "재생 중인 트랙 없음");
            TrackArtist.Text = "";
            _formatBadgeText = "";
            FormatBadge.Visibility = Visibility.Collapsed;
            ArtImage.Source = null;
            ArtFlyoutImage.Source = null;
            ArtImage.Visibility = Visibility.Collapsed;
            ArtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        var t = item.Track;
        TrackTitle.Text = t.Title;
        TrackArtist.Text = string.IsNullOrEmpty(t.Artist) ? t.Album : t.Artist;

        // format badge
        _formatBadgeText = AudioFormatBadgeFormatter.FormatTrackBadgeText(t);
        UpdateFormatBadge();

        UpdateArt(t);
    }

    /// <summary>
    /// Resolves and shows the artwork for <paramref name="track"/>. Tracks whose tags carried no
    /// art still usually have a cover next to the file, so fall back to that — but off the UI
    /// thread, because both the folder probe and the tag extraction touch the disk.
    /// </summary>
    private void UpdateArt(Track track)
    {
        int generation = ++_artGeneration;

        if (!string.IsNullOrEmpty(track.ArtPath) && System.IO.File.Exists(track.ArtPath))
        {
            ApplyArt(track.ArtPath);
            return;
        }

        ShowArtPlaceholder();

        Task.Run(() =>
        {
            string? resolved = null;
            try
            {
                resolved = AlbumArtService.FindFolderArt(track.Path);
                if (string.IsNullOrEmpty(resolved))
                    resolved = AlbumArtService.TryExtractArt(track, AlbumArtService.ComputeAlbumKey(track));
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(resolved) || !System.IO.File.Exists(resolved)) return;

            var found = resolved;
            DispatcherQueue.TryEnqueue(() =>
            {
                // Drop the result if the user moved on to another track while we were looking.
                if (generation == _artGeneration) ApplyArt(found);
            });
        });
    }

    private void ApplyArt(string path)
    {
        try
        {
            var uri = new Uri(path, UriKind.Absolute);

            // Cover art is routinely 1000px or larger. DecodePixelWidth must be set before
            // UriSource — assigning the URI starts the decode, so setting it afterwards (as the
            // previous code did) has no effect and decodes the image at full resolution.
            var thumb = new BitmapImage { DecodePixelWidth = 112 };  // 56px slot at 2x
            thumb.UriSource = uri;
            var large = new BitmapImage { DecodePixelWidth = 560 };  // 280px flyout at 2x
            large.UriSource = uri;

            ArtImage.Source = thumb;
            ArtFlyoutImage.Source = large;
            ArtImage.Visibility = Visibility.Visible;
            ArtPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            ShowArtPlaceholder();
        }
    }

    private void ShowArtPlaceholder()
    {
        ArtImage.Source = null;
        ArtFlyoutImage.Source = null;
        ArtImage.Visibility = Visibility.Collapsed;
        ArtPlaceholder.Visibility = Visibility.Visible;
    }

    public void OnOutputSession(SessionInfo info)
    {
        _outputBadgeText = AudioFormatBadgeFormatter.FormatOutputBadgeText(info);
        UpdateOutputBadge();

        // Exclusive sessions without the "allow volume" option run bit-perfect:
        // digital volume has no effect, so disable the controls instead.
        bool allowVolume = !info.Exclusive || AppServices.Settings.Output.AllowVolumeInExclusive;
        if (allowVolume != _volumeAvailableInSession)
        {
            _volumeAvailableInSession = allowVolume;
            UpdateVolumeControlAvailability();
        }
    }

    private void UpdateVolumeControlAvailability()
    {
        VolumeSlider.IsEnabled = _volumeAvailableInSession;
        MuteButton.IsEnabled = _volumeAvailableInSession;
        ToolTipService.SetToolTip(VolumeSlider, _volumeAvailableInSession
            ? AppStrings.Get("Settings_Shortcuts_Cat_Volume", "볼륨")
            : AppStrings.Get("Msg_VolumeDisabledInExclusive", "WASAPI 배타 모드에서는 볼륨 조절이 적용되지 않습니다"));
        ToolTipService.SetToolTip(MuteButton, _volumeAvailableInSession
            ? AppStrings.Get("Shortcut_Command_MuteToggle", "음소거")
            : AppStrings.Get("Msg_VolumeDisabledInExclusive", "WASAPI 배타 모드에서는 볼륨 조절이 적용되지 않습니다"));
    }

    /// <summary>Hides the volume slider on narrow windows so the bar compresses
    /// gracefully instead of pushing the right controls out of the window.</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 730;
        var wanted = compact ? Visibility.Collapsed : Visibility.Visible;
        if (VolumeSlider.Visibility != wanted) VolumeSlider.Visibility = wanted;

        if (OutputBadge != null)
        {
            var outputWanted = compact || !AudioFormatBadgeFormatter.IsBadgeVisible(_outputBadgeText)
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (OutputBadge.Visibility != outputWanted) OutputBadge.Visibility = outputWanted;
        }
    }

    private void UpdateFormatBadge()
    {
        if (!AudioFormatBadgeFormatter.IsBadgeVisible(_formatBadgeText))
        {
            FormatBadge.Visibility = Visibility.Collapsed;
            return;
        }

        FormatBadgeText.Text = _formatBadgeText;
        FormatBadge.Visibility = Visibility.Visible;
    }

    private void UpdateOutputBadge()
    {
        if (OutputBadge == null) return;
        if (!AudioFormatBadgeFormatter.IsBadgeVisible(_outputBadgeText))
        {
            OutputBadge.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(OutputBadge, null);
            return;
        }

        OutputBadgeText.Text = _outputBadgeText;
        ToolTipService.SetToolTip(OutputBadge, _outputBadgeText);
        bool compact = ActualWidth > 0 && ActualWidth < 730;
        OutputBadge.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    public void OnStateChanged()
    {
        bool playing = AppServices.Playback?.State == PlaybackState.Playing;
        PlayIcon.Glyph = playing ? "\uE769" : "\uE768";
        // The glyph is the only visual cue, so the automation name has to track it or a screen
        // reader always announces "\uC7AC\uC0DD" no matter what the button will actually do.
        AutomationProperties.SetName(PlayButton, playing ? "\uC77C\uC2DC\uC815\uC9C0" : "\uC7AC\uC0DD");
    }

    public void OnQueueChanged()
    {
        var playback = AppServices.Playback;
        if (playback == null) return;
        var entries = playback.Queue.Entries;
        _queueController.SyncFromQueue(entries);
        var count = entries.Count;
        QueueBadgeText.Text = QueuePopupController.FormatBadgeText(count);
        QueueBadge.Visibility = QueuePopupController.ShouldShowBadge(count) ? Visibility.Visible : Visibility.Collapsed;
        QueueEmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTimer()
    {
        var playback = AppServices.Playback;
        if (playback == null) return;
        var duration = playback.Duration;
        var position = playback.Position;

        if (!_seekCalculator.IsDragging)
        {
            _updatingSliderFromTimer = true;
            try
            {
                var progress = SeekbarScrubbingCalculator.CalculateSliderProgress(
                    position, duration, SeekSlider.Maximum, _seekCalculator.IsDragging);
                if (progress.UpdateMax) SeekSlider.Maximum = progress.NewMax;
                SeekSlider.Value = progress.NewValue;
            }
            finally
            {
                _updatingSliderFromTimer = false;
            }
        }

        ElapsedText.Text = SeekbarScrubbingCalculator.FormatTime(position);
        RemainingText.Text = SeekbarScrubbingCalculator.FormatRemaining(position, duration);

        if (playback.CurrentItem != null)
        {
            var rem = duration - position;
            if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
            playback.CurrentItem.RemainingTimeText = "-" + SeekbarScrubbingCalculator.FormatTime(rem);
        }

        var playing = playback.State == PlaybackState.Playing;
        var wanted = playing ? "\uE769" : "\uE768";
        if (PlayIcon.Glyph != wanted)
        {
            PlayIcon.Glyph = wanted;
            AutomationProperties.SetName(PlayButton, playing ? "\uC77C\uC2DC\uC815\uC9C0" : "\uC7AC\uC0DD");
        }

        if (++_smtcTick % 5 == 0)
            AppServices.Smtc.UpdateTimeline(position, duration);
    }

    // ---------- seek ----------

    private void OnSeekChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_seekCalculator.IsDragging || _updatingSliderFromTimer || AppServices.Playback == null) return;
        // tap-to-seek (no drag)
        AppServices.Playback.Seek(TimeSpan.FromSeconds(e.NewValue));
    }

    // ---------- transport ----------

    private void OnPlayClick(object sender, RoutedEventArgs e) =>
        _ = PlaybackUiHelper.TriggerPlayOrResumeAsync(AppServices.Playback, AppServices.Playlists, AppServices.Library);

    private void OnStopClick(object sender, RoutedEventArgs e) => AppServices.Playback?.Stop();

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (AppServices.Playback != null) _ = AppServices.Playback.NextAsync();
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        if (AppServices.Playback != null) _ = AppServices.Playback.PreviousAsync();
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e) => CycleShuffle();

    private void OnRepeatClick(object sender, RoutedEventArgs e) => CycleRepeat();

    private void OnABRepeatClick(object sender, RoutedEventArgs e) => AppServices.Playback?.CycleAbRepeat();

    /// <summary>Refreshes the A-B affordance from the controller stage. Called by MainWindow on
    /// AppServices.AbRepeatChanged (already on the UI thread).</summary>
    public void UpdateAbRepeatVisual()
    {
        var stage = AppServices.Playback?.AbRepeat ?? AbRepeatStage.Off;
        ABRepeatButton.IsChecked = stage == AbRepeatStage.Looping;
        ABRepeatLabel.Foreground = stage == AbRepeatStage.Off
            ? ThemeResourceHelper.GetBrush("TextSecondaryBrush")
            : ThemeResourceHelper.GetBrush("DawnAccentBrush");
        string tooltip = stage switch
        {
            AbRepeatStage.WaitingForB => AppStrings.Get("NowPlaying_ABRepeat_Tooltip_Marking",
                "A-B 반복: A 지점 설정됨 — 다시 눌러 B 지점 설정"),
            AbRepeatStage.Looping => AppStrings.Get("NowPlaying_ABRepeat_Tooltip_Looping",
                "A-B 반복 반복 중 — 눌러서 해제"),
            _ => AppStrings.Get("NowPlaying_ABRepeat_Tooltip_Off",
                "A-B 반복: 눌러서 현재 위치를 A 지점으로 설정")
        };
        ToolTipService.SetToolTip(ABRepeatButton, tooltip);
    }

    /// <summary>
    /// Advances the shuffle mode and refreshes the button. Public because the keyboard shortcut
    /// drives this same path — dispatching the mode change anywhere else would leave the button icon
    /// and tooltip showing the previous mode.
    /// </summary>
    public void CycleShuffle()
    {
        if (AppServices.Settings == null) return;
        AppServices.Settings.Playback.ShuffleMode =
            TransportToggleCalculator.NextShuffleMode(AppServices.Settings.Playback.ShuffleMode);
        SettingsWriter.Schedule(AppServices.Settings);
        UpdateShuffleVisual();
    }

    /// <summary>Advances the repeat mode and refreshes the button. Shared with the keyboard shortcut.</summary>
    public void CycleRepeat()
    {
        if (AppServices.Settings == null) return;
        AppServices.Settings.Playback.Repeat =
            TransportToggleCalculator.NextRepeatMode(AppServices.Settings.Playback.Repeat);
        SettingsWriter.Schedule(AppServices.Settings);
        UpdateRepeatVisual();
    }

    private void UpdateShuffleVisual()
    {
        if (ShuffleIcon == null || AppServices.Settings == null) return;
        var mode = AppServices.Settings.Playback.ShuffleMode;
        ShuffleButton.IsChecked = mode != ShuffleMode.Off;
        ShuffleIcon.Foreground = mode != ShuffleMode.Off
            ? ThemeResourceHelper.GetBrush("DawnAccentBrush")
            : ThemeResourceHelper.GetBrush("TextSecondaryBrush");

        ShuffleIcon.Glyph = mode == ShuffleMode.Albums ? "\uE93C" : "\uE8B1";

        string tip = mode switch
        {
            ShuffleMode.Tracks => AppStrings.Get("NowPlaying_ShuffleTip_Tracks", "셔플: 트랙 (무작위 곡 재생)"),
            ShuffleMode.Albums => AppStrings.Get("NowPlaying_ShuffleTip_Albums", "셔플: 앨범 (앨범 순차 재생 후 다음 앨범 셔플)"),
            _ => AppStrings.Get("NowPlaying_ShuffleTip_Off", "셔플 끄기 (순차 재생)")
        };
        ToolTipService.SetToolTip(ShuffleButton, tip);
    }

    private void UpdateRepeatVisual()
    {
        if (AppServices.Settings == null || RepeatIcon == null) return;
        var mode = AppServices.Settings.Playback.Repeat;
        RepeatIcon.Glyph = mode == RepeatMode.One ? "\uE8ED" : "\uE8EE";
        RepeatIcon.Foreground = mode != RepeatMode.Off
            ? ThemeResourceHelper.GetBrush("DawnAccentBrush")
            : ThemeResourceHelper.GetBrush("TextSecondaryBrush");
        RepeatButton.IsChecked = mode != RepeatMode.Off;
    }

    // ---------- volume ----------

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (AppServices.Playback == null) return;
        AppServices.Playback.Volume = e.NewValue / 100.0;
        if (e.NewValue > 0) _lastVolume = e.NewValue / 100.0;
        DawnPlayer.Core.Persistence.SettingsWriter.Schedule(AppServices.Settings);
        UpdateVolumeIcon(e.NewValue);
    }

    private void OnMuteClick(object sender, RoutedEventArgs e) => ToggleMute();

    /// <summary>
    /// Mutes to zero remembering the level, or restores it. Shared with the keyboard shortcut.
    /// Writing to the slider is what applies the change — <see cref="OnVolumeChanged"/> does the rest.
    /// </summary>
    public void ToggleMute()
    {
        if (AppServices.Playback == null || !_volumeAvailableInSession) return;

        var (volumePercent, lastNonZeroPercent) = TransportToggleCalculator.ComputeMuteToggle(
            AppServices.Playback.Volume * 100, _lastVolume * 100);
        _lastVolume = lastNonZeroPercent / 100.0;
        VolumeSlider.Value = volumePercent;
    }

    /// <summary>Nudges the volume by <paramref name="deltaPercent"/> slider points (shortcut only).</summary>
    public void StepVolume(double deltaPercent)
    {
        if (AppServices.Playback == null || !_volumeAvailableInSession) return;
        VolumeSlider.Value = TransportToggleCalculator.StepVolumePercent(VolumeSlider.Value, deltaPercent);
    }

    private void UpdateVolumeIcon(double v)
    {
        if (VolumeIcon == null) return;
        bool muted = v <= 0;
        VolumeIcon.Glyph = muted ? "\uE74F" : "\uE767";
        if (MuteButton != null)
            AutomationProperties.SetName(MuteButton, muted ? "\uC74C\uC18C\uAC70 \uD574\uC81C" : "\uC74C\uC18C\uAC70");
    }

    // ---------- queue ----------

    private void OnQueueClick(object sender, RoutedEventArgs e)
    {
        OnQueueChanged();
        QueueButton.Flyout.ShowAt(QueueButton);
    }

    private void OnQueueClearClick(object sender, RoutedEventArgs e) =>
        QueuePopupController.RequestClear(AppServices.Playback?.Queue);

    private void OnQueueSaveClick(object sender, RoutedEventArgs e)
    {
        var playback = AppServices.Playback;
        if (playback == null || playback.Queue.Count == 0) return;
        var tracks = playback.Queue.Entries.Select(entry => entry.Item?.Track).Where(t => t != null).Cast<Track>().ToList();
        if (tracks.Count > 0)
        {
            var pl = AppServices.Playlists.CreatePlaylistFromTracks(AppStrings.Get("Msg_DefaultSavedQueueName", "대기열 저장"), tracks);
            AppServices.RaiseWarning(AppStrings.Format("Msg_SavedQueueToPlaylist", tracks.Count, pl.Name));
        }
    }

    private void OnQueueRemoveClick(object sender, RoutedEventArgs e)
    {
        int index = -1;
        if (sender is FrameworkElement fe)
        {
            if (fe.Tag is int intTag) index = intTag;
            else if (fe.Tag != null && int.TryParse(fe.Tag.ToString(), out var parsed)) index = parsed;
            else if (fe.DataContext is QueueUiEntry qe) index = qe.Index;
        }

        if (index > 0)
        {
            QueuePopupController.RequestRemoveAt(AppServices.Playback?.Queue, index);
        }
    }

    // ---------- lyrics & settings ----------

    private void OnLyricsClick(object sender, RoutedEventArgs e) => LyricsToggleRequested?.Invoke();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => App.MainWin?.NavigateToSettings();

    public void SetLyricsToggle(bool show)
    {
        if (LyricsButton != null) LyricsButton.IsChecked = show;
    }

    // ---------- shortcut hints ----------

    /// <summary>
    /// Rebuilds the transport-bar tooltips from the live shortcut map so a rebound key never
    /// leaves a tooltip advertising the shipped default. Called by MainWindow.RefreshShortcutHints.
    /// </summary>
    public void RefreshShortcutHints()
    {
        var map = AppServices.Shortcuts?.Map;
        if (map == null) return;

        // Labels come from the catalog's localized names so the tooltip, the settings list and
        // conflict dialogs all show the same string for a command.
        SetHint(PreviousButton, CommandName(Shortcuts.ShortcutCommand.Previous, "이전 트랙"), map.GetChord(Shortcuts.ShortcutCommand.Previous));
        SetHint(PlayButton, CommandName(Shortcuts.ShortcutCommand.PlayPause, "재생/일시정지"), map.GetChord(Shortcuts.ShortcutCommand.PlayPause));
        SetHint(NextButton, CommandName(Shortcuts.ShortcutCommand.Next, "다음 트랙"), map.GetChord(Shortcuts.ShortcutCommand.Next));
        SetHint(StopButton, CommandName(Shortcuts.ShortcutCommand.Stop, "정지"), map.GetChord(Shortcuts.ShortcutCommand.Stop));
        SetHint(ShuffleButton, CommandName(Shortcuts.ShortcutCommand.ShuffleCycle, "무작위 재생"), map.GetChord(Shortcuts.ShortcutCommand.ShuffleCycle));
        SetHint(RepeatButton, CommandName(Shortcuts.ShortcutCommand.RepeatCycle, "반복 (끔 / 전체 / 한 곡)"), map.GetChord(Shortcuts.ShortcutCommand.RepeatCycle));
        SetHint(MuteButton, CommandName(Shortcuts.ShortcutCommand.MuteToggle, "음소거"), map.GetChord(Shortcuts.ShortcutCommand.MuteToggle));
        SetHint(LyricsButton, CommandName(Shortcuts.ShortcutCommand.ToggleLyrics, "가사 패널"), map.GetChord(Shortcuts.ShortcutCommand.ToggleLyrics));
        SetHint(SettingsButton, CommandName(Shortcuts.ShortcutCommand.OpenPreferences, "환경설정"), map.GetChord(Shortcuts.ShortcutCommand.OpenPreferences));
    }

    private static string CommandName(Shortcuts.ShortcutCommand command, string fallback) =>
        AppStrings.Get($"Shortcut_Command_{command}", fallback);

    private static void SetHint(DependencyObject? target, string label, Shortcuts.KeyChord? chord)
    {
        if (target == null) return;
        var text = chord?.ToDisplayString();
        ToolTipService.SetToolTip(target, string.IsNullOrEmpty(text) ? label : $"{label} ({text})");
    }
}
