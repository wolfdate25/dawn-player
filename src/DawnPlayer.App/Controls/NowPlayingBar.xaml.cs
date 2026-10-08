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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DawnPlayer.App.Controls;

public sealed partial class NowPlayingBar : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _abRejectionTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };
    private readonly QueuePopupController _queueController = new();
    private readonly SeekbarScrubbingCalculator _seekCalculator = new();

    private bool _updatingSliderFromTimer;
    private bool _volumeDragging;
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
        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => StartSeekBubble()), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => CompleteSeek()), true);
        SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => CompleteSeek()), true);
        SeekSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => CompleteSeek()), true);
        SeekSlider.ValueChanged += OnSeekChanged;
        SeekSlider.SizeChanged += (_, _) => { UpdateAbRepeatOverlay(); if (_seekCalculator.IsDragging) UpdateSeekBubble(); };
        SeekBubble.SizeChanged += (_, _) => { if (_seekCalculator.IsDragging) UpdateSeekBubble(); };
        // volume drag readout — same pointer-event lifecycle as the seek bubble
        VolumeSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => StartVolumeBubble()), true);
        VolumeSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => HideVolumeBubble()), true);
        VolumeSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => HideVolumeBubble()), true);
        VolumeSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => HideVolumeBubble()), true);
        VolumeBubble.SizeChanged += (_, _) => { if (_volumeDragging) UpdateVolumeBubble(); };
        // L15(변형 A): 제목 열이 Auto라 별-제목 인라인 배치에서 트리밍이 작동하지 않는다 —
        // 행 폭이 바뀔 때마다 제목 MaxWidth를 다시 계산한다.
        TitleRow.SizeChanged += (_, _) => UpdateTitleMaxWidth();
        SeekSlider.PointerMoved += OnSeekPointerMovedPreview;
        _timer.Tick += (_, _) => OnTimer();
        _abRejectionTimer.Tick += (_, _) =>
        {
            _abRejectionTimer.Stop();
            UpdateAbRepeatVisual();
        };
        QueueList.ItemsSource = _queueController.Entries;

        AppServices.OutputSessionChanged += OnOutputSession;
        AppServices.LiveStreamTitleChanged += OnLiveStreamTitle;
        AppServices.RemoteArtResolved += OnRemoteArtResolved;
        AppServices.RatingsApplied += OnRatingsApplied;

        // PT5-09: surface the live chord on every static transport tooltip. Dynamic tooltips
        // (shuffle, A-B) get their suffix at their own update sites.
        ShortcutTooltipBinder.BindShortcutTooltip(PreviousButton, Shortcuts.ShortcutCommand.Previous);
        ShortcutTooltipBinder.BindShortcutTooltip(PlayButton, Shortcuts.ShortcutCommand.PlayPause);
        ShortcutTooltipBinder.BindShortcutTooltip(NextButton, Shortcuts.ShortcutCommand.Next);
        ShortcutTooltipBinder.BindShortcutTooltip(StopButton, Shortcuts.ShortcutCommand.Stop);
        ShortcutTooltipBinder.BindShortcutTooltip(RepeatButton, Shortcuts.ShortcutCommand.RepeatCycle);
        ShortcutTooltipBinder.BindShortcutTooltip(MuteButton, Shortcuts.ShortcutCommand.MuteToggle);
        MiniRestoreButton.Click += (_, _) => MiniRestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised by the mini-only restore button (visible only in the MiniVolume state).
    /// MainWindow exits mini mode on it — the mini surface's pointer input is system-owned
    /// (InputNonClientPointerSource caption region), so the visible button is the primary
    /// pointer escape hatch alongside Escape.</summary>
    public event EventHandler? MiniRestoreRequested;

    /// <summary>Root of the bar's visual tree — MainWindow walks it to compute the mini
    /// mode's passthrough rectangles (interactive controls) over the caption region.</summary>
    internal Microsoft.UI.Xaml.FrameworkElement BarVisualRoot => RootLayout;

    /// <summary>Runs on the UI thread (AppServices relay): a remote track's art finished
    /// downloading after playback started (M3U8-restored tracks). Re-runs the art pipeline only
    /// while this is still the displayed track; UpdateArt's generation guard absorbs any race.
    /// Public — MainWindow wires AppServices.RemoteArtResolved to it, like the other relays.</summary>
    public void OnRemoteArtResolved(Core.Models.Track track)
    {
        if (ReferenceEquals(AppServices.Playback?.CurrentItem?.Track, track))
            UpdateArt(track);
    }

    /// <summary>Runs on the UI thread (AppServices relay): refreshes the artist line while the
    /// metadata's item is still the one being shown — a title racing a track change must not
    /// overwrite the new track's text.</summary>
    private void OnLiveStreamTitle(Core.Audio.LiveStreamMetadata m)
    {
        if (ReferenceEquals(AppServices.Playback?.CurrentItem, m.Item))
        {
            TrackArtist.Text = m.Item.NowPlayingSubtitle;
        }
    }

    private void CompleteSeek()
    {
        if (!_seekCalculator.IsDragging) return;
        var target = _seekCalculator.CompleteDrag(SeekSlider.Value, AppServices.Playback?.Duration ?? TimeSpan.Zero);
        if (target.HasValue)
        {
            AppServices.Playback?.Seek(target.Value);
        }
        SeekBubbleOverlay.Visibility = Visibility.Collapsed;
    }

    // ---------- drag readout bubbles ----------

    // WinUI's built-in thumb tooltip is unusable here: its content is a DataContext binding the
    // ToolTip never gets a source for (platform Slider_Partial.cpp creates it without a
    // DataContext and the popup tree does not inherit the target's), so the converter never
    // runs and the box floats empty. These bubbles are app-owned overlay elements; text and
    // geometry both come from the headless SliderThumbToolTipText contract.

    private void StartSeekBubble()
    {
        _seekCalculator.BeginDrag();
        UpdateSeekBubble();
        SeekBubbleOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateSeekBubble()
    {
        var max = SeekSlider.Maximum;
        if (max <= 0 || SeekSlider.ActualWidth <= 0) return;
        SeekBubbleText.Text = SliderThumbToolTipText.Time(SeekSlider.Value);
        PositionBubble(SeekBubble, SeekSlider.Value / max, SeekSlider.ActualWidth);
    }

    private void StartVolumeBubble()
    {
        _volumeDragging = true;
        UpdateVolumeBubble();
        VolumeBubbleOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateVolumeBubble()
    {
        var max = VolumeSlider.Maximum;
        if (max <= 0 || VolumeSlider.ActualWidth <= 0) return;
        VolumeBubbleText.Text = SliderThumbToolTipText.Percent(VolumeSlider.Value);
        PositionBubble(VolumeBubble, VolumeSlider.Value / max, VolumeSlider.ActualWidth);
    }

    private void HideVolumeBubble()
    {
        _volumeDragging = false;
        VolumeBubbleOverlay.Visibility = Visibility.Collapsed;
    }

    private static void PositionBubble(Border bubble, double fraction, double trackWidth)
    {
        // ActualWidth is 0 on the first show (not yet measured); a nominal width keeps the
        // initial position sane until the SizeChanged hook re-positions precisely.
        var width = bubble.ActualWidth > 0 ? bubble.ActualWidth : 36;
        bubble.SetValue(Canvas.LeftProperty, SliderThumbToolTipText.BubbleLeft(fraction, trackWidth, width));
    }

    /// <summary>Called by MainWindow after AppServices.Initialize.</summary>
    public void InitializeState()
    {
        VolumeSlider.Value = AppServices.Settings.Playback.Volume * 100;
        ShuffleButton.IsChecked = AppServices.Settings.Playback.Shuffle;
        // 설정(하단바 평점 표시 토글)·테마/액센트 변경 → 평점 아이콘 상태 재계산.
        // 구독은 생성자가 아니라 여기(InitializeState — AppServices 초기화 후 호출 보장)에 둔다:
        // 생성자는 MainWindow InitializeComponent 도중에 돌기 때문에 AppearanceSettings가 아직
        // null이라 XAML instance-creation 크래시가 났었다 (2026-10-03).
        AppServices.AppearanceSettings.AppearanceChanged += OnAppearanceChanged;
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
        // L15: 별점 대상 기준점 — 바가 "표시 중인" 트랙. 세션 복원 후 재생 전에는
        // Playback.CurrentItem이 null이지만 바에는 복원된 트랙이 표시되므로, 별점 대상도
        // 표시 중인 트랙을 따른다(아래 별점 핸들러들의 ?? _displayedTrack 폴백).
        _displayedTrack = item?.Track;
        if (item == null)
        {
            TrackTitle.Text = AppStrings.Get("NowPlaying_TrackTitle_Empty.Text", "재생 중인 트랙 없음");
            TrackArtist.Text = "";
            _formatBadgeText = "";
            FormatBadge.Visibility = Visibility.Collapsed;
            UpdateTrackRatingCell(null);
            ArtImage.Source = null;
            ArtFlyoutImage.Source = null;
            ArtImage.Visibility = Visibility.Collapsed;
            ArtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        var t = item.Track;
        TrackTitle.Text = t.Title;
        // A live source's subtitle (radio "station · song") outranks the track's own empty artist
        // fields; it keeps updating through OnLiveStreamTitle while the item plays.
        var liveSubtitle = item.NowPlayingSubtitle;
        TrackArtist.Text = liveSubtitle.Length > 0 ? liveSubtitle
            : (string.IsNullOrEmpty(t.Artist) ? t.Album : t.Artist);

        // format badge
        _formatBadgeText = AudioFormatBadgeFormatter.FormatTrackBadgeText(t);
        UpdateFormatBadge();

        UpdateTrackRatingCell(t);
        UpdateArt(t);
    }

    // ---------- rating cell (L11) ----------

    /// <summary>바가 현재 표시 중인 트랙(OnTrackChanged에서 갱신). 별점 대상은 Playback.CurrentItem
    /// 이 아니라 이 트랙을 기준으로 한다 — 세션 복원 후 재생 전에는 CurrentItem이 null이지만 바에는
    /// 마지막 트랙이 표시되고, 그 별도 즉시 매기고 적용할 수 있어야 한다(L15 사용자 보고).</summary>
    private Core.Models.Track? _displayedTrack;

    /// <summary>Shows the playing track's star on the title row. 2026-10-03 (아이콘 이질감 수정):
    /// 텍스트 별(★☆) 대신 Segoe Fluent 아이콘 — 미평점 E735(회색, 하단바 아이콘과 같은 무채색),
    /// 평점 있음 E734(앰버). 설정에서 하단바 평점 버튼을 숨기면 항상 Collapsed.</summary>
    private void UpdateTrackRatingCell(Track? track)
    {
        if (track == null || !RatingCommands.IsRateable(track) || !AppServices.Settings.Ui.ShowNowPlayingRating)
        {
            TrackRatingButton.Visibility = Visibility.Collapsed;
            UpdateTitleMaxWidth();
            return;
        }

        var rating = Math.Clamp(track.Rating, 0, 5);
        // 2026-10-04: 평점 부여 곡은 평점 수만큼 채운 별(E735×N) — 재생목록·라이브러리 표의
        // DisplayText 계약과 동일 표현. 미평점은 외곽 별(E734) 1개(발견 어포던스).
        // FontFamily를 Segoe MDL2 Assets로 고정 — 주의: MDL2와 Segoe Fluent Icons는 이 두
        // 코드포인트의 채움 스타일이 서로 반대다(MDL2: E734=외곽, E735=채움 / Fluent: 반대).
        // 사용자 육안 확인 기준으로 MDL2 매핑을 따른다(2026-10-04 스왑 — 뒤집혀 보인던 보고).
        TrackRatingStars.Children.Clear();
        var starGlyph = rating > 0 ? "\uE735" : "\uE734";
        var starForeground = rating > 0
            ? ThemeResourceHelper.GetBrush("DawnAccentTextBrush")
            : ThemeResourceHelper.GetBrush("TextSecondaryBrush");
        var starCount = rating > 0 ? rating : 1;
        for (var i = 0; i < starCount; i++)
        {
            TrackRatingStars.Children.Add(new Microsoft.UI.Xaml.Controls.FontIcon
            {
                Glyph = starGlyph,
                FontSize = 12,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                // +2px 광학 정렬: 별 글리프가 em 박스 상단에 치우쳐 옆 13px 제목 텍스트보다
                // 위로 떠 보인다(2026-10-04 사용자 지적, 확대 캡처로 측정) — 아래로 2px 내린다.
                Foreground = starForeground,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 2, 0, 0)
            });
        }
        AutomationProperties.SetName(TrackRatingButton, RatingAccessibilityConverter.AccessibilityText(track.Rating));
        TrackRatingButton.Visibility = Visibility.Visible;
        UpdateTitleMaxWidth();
    }

    /// <summary>L15(변형 A): 제목 열이 Auto여서 트리밍이 작동하지 않는다 — 제목 MaxWidth를
    /// "행 폭 - 별(가시일 때 +6px 마진)"로 갱신해 긴 제목이 별을 행 밖으로 밀어내지 않게 한다.
    /// TitleRow.SizeChanged(창 크기·폰트 변경)와 UpdateTrackRatingCell(별 가시성 변경 — Collapsed
    /// 요소의 ActualWidth는 마지막 값이 남으므로 가시성으로 판단)에서 호출한다.</summary>
    private void UpdateTitleMaxWidth()
    {
        var starWidth = TrackRatingButton.Visibility == Visibility.Visible
            ? TrackRatingButton.ActualWidth + 6
            : 0;
        TrackTitle.MaxWidth = Math.Max(24, TitleRow.ActualWidth - starWidth);
    }

    /// <summary>외관 설정 변경(표시 토글·테마·액센트) 시 평점 아이콘을 다시 계산 — 설정 즉시 반영과
    /// 브러시 새로 고침을 한 번에 처리한다.</summary>
    private void OnAppearanceChanged()
    {
        UpdateTrackRatingCell(AppServices.Playback?.CurrentItem?.Track);
    }

    private void OnRatingsApplied(IReadOnlyList<Track> tracks)
    {
        var current = AppServices.Playback?.CurrentItem?.Track ?? _displayedTrack;
        if (current == null) return;
        if (!tracks.Any(t => ReferenceEquals(t, current))) return;
        UpdateTrackRatingCell(current);
    }

    private void OnTrackRatingFlyoutOpening(object? sender, object e)
    {
        var t = AppServices.Playback?.CurrentItem?.Track ?? _displayedTrack;
        UpdateFlyoutStarRow(Math.Clamp(t?.Rating ?? 0, 0, 5));
    }

    /// <summary>L15 후속: 플라이아웃 별 행(5개 버튼)의 채움 상태를 평점에 맞춰 갱신한다 —
    /// 미평점(0)은 5개 모두 외곽 별(사용자 요구: RatingControl은 미평점 렌더링이 불가). 각 별의
    /// 접근 이름도 여기서 코드로 설정한다(XAML 리터럴 금지 게이트 준수). 글리프 매핑은
    /// MDL2 기준: 채움=E735, 외곽=E734(Fluent와 반대 — 사용자 육안 확인).</summary>
    private void UpdateFlyoutStarRow(int rating)
    {
        var nameComposite = System.Text.CompositeFormat.Parse(
            Localization.AppStrings.Get("Rating_Flyout_Star_Name.Text", "별 {0}점"));
        var stars = new[] { FlyoutStar1, FlyoutStar2, FlyoutStar3, FlyoutStar4, FlyoutStar5 };
        for (var i = 0; i < stars.Length; i++)
        {
            var starValue = i + 1;
            var filled = starValue <= rating;
            if (stars[i].Content is not Microsoft.UI.Xaml.Controls.FontIcon icon) continue;
            icon.Glyph = filled ? "\uE735" : "\uE734";
            icon.Foreground = filled
                ? ThemeResourceHelper.GetBrush("DawnAccentTextBrush")
                : ThemeResourceHelper.GetBrush("TextSecondaryBrush");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stars[i],
                string.Format(System.Globalization.CultureInfo.CurrentCulture, nameComposite, starValue));
        }
    }

    private void OnFlyoutStarClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Button button || button.Tag is not string tag) return;
        if (!int.TryParse(tag, out var stars)) return;
        var t = AppServices.Playback?.CurrentItem?.Track ?? _displayedTrack;
        if (t == null) return;

        // 같은 별 개수 재클릭 → 평점 지우기(RatingControl IsClearEnabled 계약 유지)
        var current = Math.Clamp(t.Rating, 0, 5);
        var next = stars == current ? 0 : stars;
        AppServices.RateTracks([t], next);
        UpdateFlyoutStarRow(next);
        UpdateTrackRatingCell(t);
        TrackRatingFlyout.Hide();
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
    /// gracefully instead of pushing the right controls out of the window. The mini context
    /// wins over the compact shed (the mini window is always narrow) and re-asserts it —
    /// this handler is the re-assertion hook that runs after every AdaptiveTrigger pass,
    /// which would otherwise undo <see cref="ApplyMiniContext"/>.</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        GoToWidthState();

        // 미니 문맥에서는 슬라이더를 되살린다(VisualState MiniVolume이 호스트를 담당, 여기선
        // 코드 측 토글). 트리거 재평가마다 이 메서드가 불리므로 재단언이 자동이다.
        bool compact = !_miniContext && e.NewSize.Width < MiniWideBreakpoint;
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

    // ---------------- mini player context ----------------

    // XAML AdaptiveTrigger(Wide 730)와 동일한 값 — 어긋나면 혼합 상태(PT3-15 교훈). 게이트가 잠금.
    private const double MiniWideBreakpoint = 730;

    /// <summary>True while MainWindow is collapsed to the mini player — the code-driven
    /// companion to the width VisualStates. The mini window (logical 600px) always lands in
    /// the Compact trigger, which hides the volume slider entirely (mute-only mini) and
    /// keeps a lyrics toggle whose pane is unreachable in mini. ApplyMiniContext applies the
    /// triggerless MiniVolume state over it; <see cref="OnRootSizeChanged"/> re-asserts both
    /// layers because every AdaptiveTrigger re-evaluation wins until we re-run.</summary>
    private bool _miniContext;

    /// <summary>Called by MainWindow on mini enter/exit. Applies the MiniVolume state
    /// immediately and once more after layout settles, so the trigger pass that the mini
    /// resize provokes cannot have the last word.</summary>
    public void ApplyMiniContext(bool isMini)
    {
        _miniContext = isMini;
        GoToWidthState();
        DispatcherQueue.TryEnqueue(GoToWidthState);
    }

    /// <summary>Picks the visual state: MiniVolume while the mini context is active,
    /// otherwise the width trigger's verdict (Wide/Compact) so normal resize behavior is
    /// untouched. GoToState from code overrides the trigger-selected state until the next
    /// trigger evaluation.</summary>
    private void GoToWidthState()
    {
        var state = _miniContext
            ? "MiniVolume"
            : ActualWidth >= MiniWideBreakpoint ? "Wide" : "Compact";
        VisualStateManager.GoToState(this, state, useTransitions: false);
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
        UpdateBufferingBadge();

        // Poll the playback position only while it actually moves: a permanent 200 ms
        // dispatcher timer against a paused player is pure battery drain. One final tick
        // settles the seekbar at the paused position.
        if (playing)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            OnTimer();
        }
    }

    /// <summary>PT3-11: dead-air feedback. Driven from both OnStateChanged (open-window
    /// Buffering) and OnTimer (a mid-play stream stall raises no state change — only the 200 ms
    /// poll sees the reader's stall flag flip).</summary>
    private void UpdateBufferingBadge()
    {
        var playback = AppServices.Playback;
        if (playback == null) return;
        // The stall flag is only meaningful while sound is expected: a paused stream keeps
        // refilling and the flag clears with no event, so any state but Playing/Buffering
        // hides the badge instead of pinning a stale "버퍼링…" over healthy audio.
        var state = playback.State;
        var buffering = state == PlaybackState.Buffering
            || (state == PlaybackState.Playing && playback.IsBuffering);
        BufferingBadge.Visibility = buffering ? Visibility.Visible : Visibility.Collapsed;
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
        UpdateBufferingBadge();
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

        if (_seekCalculator.IsDragging)
        {
            // Dragging: the labels preview the thumb, not the playhead — a live playhead readout
            // under a thumb parked elsewhere reads as reported position ≠ visible position.
            var (dragElapsed, dragRemaining) = SeekbarScrubbingCalculator.CalculateDraggingLabels(
                SeekSlider.Value, duration);
            ElapsedText.Text = dragElapsed;
            RemainingText.Text = dragRemaining;
            UpdateSeekBubble();
        }
        else
        {
            ElapsedText.Text = SeekbarScrubbingCalculator.FormatTime(position);
            RemainingText.Text = SeekbarScrubbingCalculator.FormatRemaining(position, duration);
        }

        // WaitingForB's preview band tracks the live playhead; the Looping band is static but
        // redrawing it is a couple of double writes, so one path serves both.
        if (AbRepeatOverlay.Visibility == Visibility.Visible)
            UpdateAbRepeatOverlay();

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
        if (_seekCalculator.IsDragging)
        {
            // Paused drags get no timer ticks, so the thumb preview labels must update here.
            var (elapsed, remaining) = SeekbarScrubbingCalculator.CalculateDraggingLabels(
                SeekSlider.Value, AppServices.Playback?.Duration ?? TimeSpan.Zero);
            ElapsedText.Text = elapsed;
            RemainingText.Text = remaining;
            UpdateSeekBubble();
            return;
        }
        if (_updatingSliderFromTimer || AppServices.Playback == null) return;
        // tap-to-seek (no drag)
        AppServices.Playback.Seek(TimeSpan.FromSeconds(e.NewValue));
    }

    /// <summary>Hover preview: a tooltip at the pointer shows the time that spot would seek to,
    /// so scrubbing decisions happen before the click, not after the jump.</summary>
    private void OnSeekPointerMovedPreview(object sender, PointerRoutedEventArgs e)
    {
        if (AppServices.Playback is not { } playback || playback.Duration <= TimeSpan.Zero) return;
        if (SeekSlider.ActualWidth <= 0) return;
        var x = e.GetCurrentPoint(SeekSlider).Position.X;
        var fraction = Math.Clamp(x / SeekSlider.ActualWidth, 0.0, 1.0);
        ToolTipService.SetToolTip(SeekSlider,
            SeekbarScrubbingCalculator.FormatTime(TimeSpan.FromSeconds(fraction * playback.Duration.TotalSeconds)));
    }

    // ---------- transport ----------

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        Helpers.MotionHelper.PressPop(PlayButton, AppServices.Motion?.MotionEnabled ?? false);
        _ = PlaybackUiHelper.TriggerPlayOrResumeAsync(AppServices.Playback, AppServices.Playlists, AppServices.Library);
    }

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

    /// <summary>Right-click clears the A-B window outright. The three-stage cycle has no quick
    /// exit from WaitingForB (a click there would either re-mark A or start the loop), and Escape
    /// is deliberately not bindable in this app, so the button's own right-click is the cancel.</summary>
    private void OnABRepeatRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        AppServices.Playback?.CancelAbRepeat();
        e.Handled = true;
    }

    /// <summary>Runs on the UI thread (AppServices relay): a press was refused — say why on the
    /// label instead of failing silently, then restore the stage label via the one-shot timer.</summary>
    public void OnAbRepeatRejected(Core.Audio.AbRepeatRejectionReason reason)
    {
        ABRepeatLabel.Text = reason switch
        {
            AbRepeatRejectionReason.BBeforeA => "B<A",
            AbRepeatRejectionReason.UnsupportedSource => "YT",
            _ => "LIVE"
        };
        ABRepeatLabel.Foreground = ThemeResourceHelper.GetBrush("TextSecondaryBrush");
        string reasonText = reason switch
        {
            AbRepeatRejectionReason.BBeforeA => AppStrings.Format("NowPlaying_ABRepeat_Reject_BBeforeA",
                "B 지점은 A 지점보다 뒤에 있어야 합니다"),
            AbRepeatRejectionReason.UnsupportedSource => AppStrings.Format("NowPlaying_ABRepeat_Reject_YouTube",
                "YouTube 스트림에서는 A-B 반복을 사용할 수 없습니다"),
            _ => AppStrings.Format("NowPlaying_ABRepeat_Reject_Live",
                "라이브 스트림에서는 A-B 반복을 사용할 수 없습니다")
        };
        ToolTipService.SetToolTip(ABRepeatButton, reasonText);
        // The label snaps back after 1.4 s (PT3-07): the reason must survive for screen readers
        // and non-hovering users via the persistent tooltip + automation name.
        AutomationProperties.SetName(ABRepeatButton, reasonText);
        _abRejectionTimer.Stop();
        _abRejectionTimer.Start();
    }

    /// <summary>Refreshes the A-B affordance from the controller stage. Called by MainWindow on
    /// AppServices.AbRepeatChanged (already on the UI thread).</summary>
    public void UpdateAbRepeatVisual()
    {
        var window = AppServices.Playback?.AbRepeatWindow ?? Core.Audio.AbRepeatWindow.Off;
        var stage = window.Stage;
        ABRepeatButton.IsChecked = stage == AbRepeatStage.Looping;
        ABRepeatLabel.Foreground = stage == AbRepeatStage.Off
            ? ThemeResourceHelper.GetBrush("TextSecondaryBrush")
            : ThemeResourceHelper.GetBrush("DawnAccentTextBrush");
        // The ✕ is the visible cancel affordance (PT3-08): right-click clears the window and
        // that used to be tooltip-only knowledge.
        ABRepeatLabel.Text = stage switch
        {
            AbRepeatStage.WaitingForB => "A ✕",
            AbRepeatStage.Looping => "A→B",
            _ => "A–B"
        };
        string tooltip = stage switch
        {
            AbRepeatStage.WaitingForB => AppStrings.Format("NowPlaying_ABRepeat_Tooltip_Marking",
                "A-B 반복: A 지점({0}) 설정됨 — 다시 눌러 B 지점 설정, 우클릭으로 해제",
                SeekbarScrubbingCalculator.FormatTime(window.Start)),
            AbRepeatStage.Looping => AppStrings.Format("NowPlaying_ABRepeat_Tooltip_Looping",
                "A-B 반복 중 {0}–{1} ({2}) — 눌러서 해제",
                SeekbarScrubbingCalculator.FormatTime(window.Start),
                SeekbarScrubbingCalculator.FormatTime(window.End),
                AbRepeatOverlayCalculator.FormatLoopLength(window.End - window.Start)),
            _ => AppStrings.Get("NowPlaying_ABRepeat_Tooltip_Off",
                "A-B 반복: 눌러서 현재 위치를 A 지점으로 설정")
        };
        ToolTipService.SetToolTip(ABRepeatButton, ShortcutTooltipBinder.WithShortcutSuffix(tooltip, Shortcuts.ShortcutCommand.ABRepeatCycle));
        UpdateAbRepeatOverlay();
    }

    /// <summary>Recomputes the seekbar overlay from the current A-B window. Cheap enough for the
    /// 200 ms timer (the WaitingForB preview follows the playhead); also wired to slider
    /// SizeChanged so a window resize can never leave the band lying about where the loop is.</summary>
    private void UpdateAbRepeatOverlay()
    {
        var playback = AppServices.Playback;
        var window = playback?.AbRepeatWindow ?? Core.Audio.AbRepeatWindow.Off;
        var geo = AbRepeatOverlayCalculator.Compute(window.Stage, window.Start, window.End,
            playback?.Position ?? TimeSpan.Zero, playback?.Duration ?? TimeSpan.Zero, SeekSlider.ActualWidth);

        AbRepeatOverlay.Visibility = geo.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (!geo.Visible) return;

        var band = geo.BandIsPreview ? (FrameworkElement)AbLoopBandPreview : AbLoopBand;
        var other = geo.BandIsPreview ? (FrameworkElement)AbLoopBand : AbLoopBandPreview;
        other.Visibility = Visibility.Collapsed;
        band.Visibility = geo.ShowBand ? Visibility.Visible : Visibility.Collapsed;
        if (geo.ShowBand)
        {
            band.Visibility = Visibility.Visible;
            Canvas.SetLeft(band, geo.BandLeft);
            band.Width = geo.BandWidth;
        }

        AbMarkerA.Visibility = Visibility.Visible;
        Canvas.SetLeft(AbMarkerA, geo.MarkerALeft);
        AbMarkerB.Visibility = window.HasEnd ? Visibility.Visible : Visibility.Collapsed;
        if (window.HasEnd) Canvas.SetLeft(AbMarkerB, geo.MarkerBLeft);
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
            ? ThemeResourceHelper.GetBrush("DawnAccentTextBrush")
            : ThemeResourceHelper.GetBrush("TextSecondaryBrush");

        ShuffleIcon.Glyph = mode == ShuffleMode.Albums ? "\uE93C" : "\uE8B1";

        string tip = mode switch
        {
            ShuffleMode.Tracks => AppStrings.Get("NowPlaying_ShuffleTip_Tracks", "셔플: 트랙 (무작위 곡 재생)"),
            ShuffleMode.Albums => AppStrings.Get("NowPlaying_ShuffleTip_Albums", "셔플: 앨범 (앨범 순차 재생 후 다음 앨범 셔플)"),
            _ => AppStrings.Get("NowPlaying_ShuffleTip_Off", "셔플 끄기 (순차 재생)")
        };
        ToolTipService.SetToolTip(ShuffleButton, ShortcutTooltipBinder.WithShortcutSuffix(tip, Shortcuts.ShortcutCommand.ShuffleCycle));
    }

    private void UpdateRepeatVisual()
    {
        if (AppServices.Settings == null || RepeatIcon == null) return;
        var mode = AppServices.Settings.Playback.Repeat;
        RepeatIcon.Glyph = mode == RepeatMode.One ? "\uE8ED" : "\uE8EE";
        RepeatIcon.Foreground = mode != RepeatMode.Off
            ? ThemeResourceHelper.GetBrush("DawnAccentTextBrush")
            : ThemeResourceHelper.GetBrush("TextSecondaryBrush");
        RepeatButton.IsChecked = mode != RepeatMode.Off;
    }

    // ---------- volume ----------

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_volumeDragging) UpdateVolumeBubble();
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

    private async void OnQueueClearClick(object sender, RoutedEventArgs e)
    {
        var playback = AppServices.Playback;
        if (playback == null || playback.Queue.Count == 0) return;

        // PT3-16: "clear" wiped the whole queue instantly with no undo — confirm with the
        // item count so the click can't be a one-shot accident.
        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("NowPlaying_QueueClearConfirmTitle", "대기열 비우기"),
            Content = AppStrings.Format("NowPlaying_QueueClearConfirmMessage",
                "대기열의 {0}곡을 모두 비울까요?", playback.Queue.Count),
            PrimaryButtonText = AppStrings.Get("Common_OK", "확인"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        QueuePopupController.RequestClear(playback.Queue);
    }

    private void OnQueueSaveClick(object sender, RoutedEventArgs e)
    {
        var playback = AppServices.Playback;
        if (playback == null || playback.Queue.Count == 0) return;
        var tracks = playback.Queue.Entries.Select(entry => entry.Item?.Track).Where(t => t != null).Cast<Track>().ToList();
        if (tracks.Count > 0)
        {
            var pl = AppServices.Playlists.CreatePlaylistFromTracks(AppStrings.Get("Msg_DefaultSavedQueueName", "대기열 저장"), tracks);
            AppServices.RaiseWarning(AppStrings.Format("Msg_SavedQueueToPlaylist", "대기열 {0}곡을 '{1}'에 저장했습니다.", tracks.Count, pl.Name));
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

    // ---------- lyrics ----------

    private void OnLyricsClick(object sender, RoutedEventArgs e) => LyricsToggleRequested?.Invoke();

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
