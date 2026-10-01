using DawnPlayer.App.Calculators;
using DawnPlayer.App.Controls;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Audio.Dsp;
using WinRT.Interop;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DawnPlayer.App.Views;

/// <summary>
/// U4 fullscreen Now Playing: cover hero, waveform seek canvas (preserved WaveformPeaks
/// scanner), and the 28-band spectrum fed by the chain's analysis tap through
/// <see cref="SpectrumSmoother"/>. Esc exits; SMTC and playback are untouched — this window is
/// a pure view over the running session. Peak scans run off the UI thread; an undecodable or
/// unknown-length source (radio streams) renders no envelope instead of a fake one.
/// </summary>
public sealed partial class FullscreenNowPlayingWindow : Window
{
    private const int WaveHeight = 72;
    private const int SpectrumBars = SpectrumCalculator.BinCount;
    private const double FrameSeconds = 0.1; // timer cadence; used as the smoother's dt

    private readonly SpectrumSmoother _smoother = new(SpectrumCalculator.BinCount);
    private readonly float[] _spectrumWindow = new float[SpectrumTapDspEffect.WindowSamples];
    private readonly Rectangle[] _spectrumBars = new Rectangle[SpectrumCalculator.BinCount];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private bool _dragging;
    private double _dragFraction;
    private float[]? _peaks;   // scan buckets for the current track
    private int _generation;

    public FullscreenNowPlayingWindow()
    {
        InitializeComponent();

        TryEnterFullScreen();
        // Opt-in (default off — battery/GPU): acrylic behind the dim overlay keeps text contrast.
        if (AppServices.Settings.Ui.FullscreenAcrylic)
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        }

        WaveCanvas.SizeChanged += (_, _) => UpdateWaveGeometry();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(FrameSeconds);
        _timer.Tick += (_, _) => OnFrame();

        // Deactivation pauses the frame pump; nothing else needs to pause.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) _timer.Stop();
            else _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            AppServices.CurrentTrackChanged -= OnTrackChanged;
            AppServices.LiveStreamTitleChanged -= OnLiveStreamTitle;
            AppServices.PlaybackStateChanged -= OnPlaybackStateChanged;
        };

        BuildSpectrumBars();
        AppServices.CurrentTrackChanged += OnTrackChanged;
        AppServices.LiveStreamTitleChanged += OnLiveStreamTitle;
        AppServices.PlaybackStateChanged += OnPlaybackStateChanged;

        _timer.Start();
        OnTrackChanged(AppServices.Playback.CurrentItem);
        OnPlaybackStateChanged();
        OnFrame();
    }

    private void TryEnterFullScreen()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
        }
        catch (Exception ex)
        {
            App.Log($"[fullscreen] presenter fallback: {ex.Message}");
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Escape was the only key this window took — immersive mode read as a trap (PT3-02/
        // PT5-17). Space and the arrows mirror the main-window transport shortcuts.
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Space:
                AppServices.Playback?.PlayPause();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Left:
                _ = AppServices.Playback?.PreviousAsync();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right:
                _ = AppServices.Playback?.NextAsync();
                e.Handled = true;
                break;
        }
    }

    // ---------- transport (PT3-02) ----------

    private void OnPlaybackStateChanged()
    {
        var playing = AppServices.Playback?.State == PlaybackState.Playing;
        FsPlayIcon.Glyph = playing ? "\uE769" : "\uE768";
        // The custom template wraps the icon in a ContentPresenter, so Parent is not the
        // Button — walk to it or the screen reader keeps announcing the static name.
        var button = Helpers.VisualTreeHelperExtensions.FindAncestor<Microsoft.UI.Xaml.Controls.Button>(FsPlayIcon);
        if (button != null)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, playing ? "\uC77C\uC2DC\uC815\uC9C0" : "\uC7AC\uC0DD");
    }

    private void OnFsPreviousClick(object sender, RoutedEventArgs e) => _ = AppServices.Playback?.PreviousAsync();
    private void OnFsNextClick(object sender, RoutedEventArgs e) => _ = AppServices.Playback?.NextAsync();
    private void OnFsPlayPauseClick(object sender, RoutedEventArgs e) => AppServices.Playback?.PlayPause();

    /// <summary>Mirrors shuffle/repeat/A-B state every frame — cheap reads, and WinUI no-ops
    /// when a value is unchanged (PT3-10).</summary>
    private void UpdateTransportState()
    {
        var settings = AppServices.Settings;
        var playback = AppServices.Playback;
        bool shuffle = (settings?.Playback.ShuffleMode ?? Core.Persistence.ShuffleMode.Off) != Core.Persistence.ShuffleMode.Off;
        var repeat = settings?.Playback.Repeat ?? Core.Persistence.RepeatMode.Off;
        var stage = (playback?.AbRepeatWindow ?? Core.Audio.AbRepeatWindow.Off).Stage;

        FsShuffleIcon.Visibility = shuffle ? Visibility.Visible : Visibility.Collapsed;
        FsRepeatIcon.Visibility = repeat != Core.Persistence.RepeatMode.Off ? Visibility.Visible : Visibility.Collapsed;
        FsRepeatIcon.Glyph = repeat == Core.Persistence.RepeatMode.One ? "\uE8ED" : "\uE8EE";
        FsAbLabel.Visibility = stage != Core.Audio.AbRepeatStage.Off ? Visibility.Visible : Visibility.Collapsed;
        FsAbLabel.Text = stage switch
        {
            Core.Audio.AbRepeatStage.WaitingForB => "A \u2715",
            Core.Audio.AbRepeatStage.Looping => "A\u2192B",
            _ => "A\u2013B"
        };
        FsStateRow.Visibility = shuffle || repeat != Core.Persistence.RepeatMode.Off || stage != Core.Audio.AbRepeatStage.Off
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>A-B window mirrored on the waveform (PT3-10). The wave canvas has no thumb, so
    /// band edges map linearly — the slider's thumb-inset mapping would lie here.</summary>
    private void UpdateAbBand()
    {
        var playback = AppServices.Playback;
        var window = playback?.AbRepeatWindow ?? Core.Audio.AbRepeatWindow.Off;
        double width = WaveCanvas.ActualWidth;
        if (window.Stage == Core.Audio.AbRepeatStage.Off || width <= 0 || playback == null || playback.Duration <= TimeSpan.Zero)
        {
            AbBand.Visibility = Visibility.Collapsed;
            AbMarkerA.Visibility = Visibility.Collapsed;
            AbMarkerB.Visibility = Visibility.Collapsed;
            return;
        }

        double durationSec = playback.Duration.TotalSeconds;
        double X(TimeSpan t) => WaveformLayout.FractionToX(Math.Clamp(t.TotalSeconds / durationSec, 0, 1), width);

        double aX = X(window.Start);
        AbMarkerA.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(AbMarkerA, aX - 1);

        double? bX = window.HasEnd ? X(window.End) : null;
        double? positionX = window.Stage == Core.Audio.AbRepeatStage.WaitingForB ? X(playback.Position) : null;

        double? bandLeft = window.Stage == Core.Audio.AbRepeatStage.Looping ? aX
            : positionX.HasValue && positionX.Value > aX ? aX : null;
        double? bandRight = window.Stage == Core.Audio.AbRepeatStage.Looping ? bX : positionX;

        if (bandLeft.HasValue && bandRight.HasValue && bandRight.Value > bandLeft.Value)
        {
            AbBand.Visibility = Visibility.Visible;
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(AbBand, bandLeft.Value);
            AbBand.Width = bandRight.Value - bandLeft.Value;
        }
        else
        {
            AbBand.Visibility = Visibility.Collapsed;
        }

        AbMarkerB.Visibility = bX.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (bX.HasValue) Microsoft.UI.Xaml.Controls.Canvas.SetLeft(AbMarkerB, bX.Value - 1);
    }

    // ---------- track + waveform ----------

    private void OnTrackChanged(PlaylistItem? item) => DispatcherQueue.TryEnqueue(() => LoadTrack(item));

    /// <summary>Already on the UI thread via the AppServices relay; guards against a metadata
    /// racing a track change the same way the bar does.</summary>
    private void OnLiveStreamTitle(Core.Audio.LiveStreamMetadata m)
    {
        if (!ReferenceEquals(AppServices.Playback?.CurrentItem, m.Item)) return;
        var subtitle = m.Item.NowPlayingSubtitle;
        TrackArtist.Text = subtitle.Length > 0 ? subtitle : TrackArtist.Text;
        Title = subtitle.Length > 0 ? $"{m.Item.Track.Title} — {subtitle}" : Title;
    }

    private void LoadTrack(PlaylistItem? item)
    {
        _peaks = null;
        _generation++;
        TrackTitle.Text = item?.Track.Title ?? AppStrings.Get("NowPlaying_TrackTitle_Empty.Text", "재생 중인 트랙 없음");
        TrackArtist.Text = item?.Track.Artist ?? "";
        Title = item is null ? "Dawn Player" : $"{item.Track.Title} — {item.Track.Artist}";
        CoverImage.Source = null;
        CoverPlaceholder.Visibility = Visibility.Visible;
        WavePlayed.Points.Clear();
        WaveUnplayed.Points.Clear();
        WaveClip.Rect = new Rect(0, 0, 0, WaveHeight);

        var track = item?.Track;
        if (track == null) return;

        int generation = _generation;
        var path = track.Path;
        Task.Run(() =>
        {
            try
            {
                // Guard against a superseded track whose scan already started.
                if (string.IsNullOrEmpty(path) || AppServices.Playback.CurrentItem?.Track.Path != path)
                    return (generation, null);
                return (generation, WaveformPeaks.GetOrScan(path, WaveformLayout.DefaultScanBuckets));
            }
            catch
            {
                // Undecodable source: empty envelope; playback surfaces the real error.
                return (generation, null);
            }
        }).ContinueWith(t =>
        {
            if (t.Result.Item1 != _generation || t.Result.Item2 == null) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (t.Result.Item1 != _generation) return;
                _peaks = t.Result.Item2;
                UpdateWaveGeometry();
            });
        });

        LoadCover(track);
    }

    private void LoadCover(Track track)
    {
        // PT3-12: folder probing and tag extraction touch the disk — same off-thread rule the
        // bar follows, so a track change can't stall the fullscreen frame loop.
        int generation = _generation;
        Task.Run(() =>
        {
            try
            {
                string? artPath = !string.IsNullOrEmpty(track.ArtPath) && File.Exists(track.ArtPath)
                    ? track.ArtPath
                    : AlbumArtService.FindFolderArt(track.Path ?? "");
                if (string.IsNullOrEmpty(artPath))
                    artPath = AlbumArtService.TryExtractArt(track, TagReader.ComputeAlbumKey(track));
                return (generation, string.IsNullOrEmpty(artPath) || !File.Exists(artPath) ? null : artPath);
            }
            catch (Exception ex)
            {
                App.Log($"[fullscreen] cover: {ex.Message}");
                return (generation, null);
            }
        }).ContinueWith(t =>
        {
            if (t.Result.generation != _generation) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (t.Result.generation != _generation) return;
                CoverImage.Source = t.Result.Item2 is { } artPath
                    ? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(artPath))
                    : null;
                // PT3-17: cover-less tracks show the placeholder glyph, not a black void.
                CoverPlaceholder.Visibility = CoverImage.Source == null ? Visibility.Visible : Visibility.Collapsed;
            });
        });
    }

    /// <summary>Rebuilds the mirrored envelope polygons from the decimated peaks. Call after
    /// size changes and scan completion; the played overlay shares the geometry via clipping.</summary>
    private void UpdateWaveGeometry()
    {
        double width = WaveCanvas.ActualWidth;
        if (width <= 0 || double.IsNaN(width)) return;

        if (_peaks == null || _peaks.Length == 0)
        {
            WavePlayed.Points.Clear();
            WaveUnplayed.Points.Clear();
            return;
        }

        int bars = Math.Clamp((int)(width / 3), 24, WaveformLayout.DefaultScanBuckets);
        float[] display = WaveformLayout.Decimate(_peaks, bars);
        double mid = WaveHeight / 2.0;
        double halfMax = WaveHeight / 2.0 - 2;

        WavePlayed.Points = BuildEnvelope(display, bars, width, mid, halfMax);
        WaveUnplayed.Points = BuildEnvelope(display, bars, width, mid, halfMax);
        ApplyWaveClip(CurrentFraction());
    }

    private static Microsoft.UI.Xaml.Media.PointCollection BuildEnvelope(
        float[] display, int bars, double width, double mid, double halfMax)
    {
        var points = new Microsoft.UI.Xaml.Media.PointCollection();
        // Top edge left→right …
        for (int b = 0; b < bars; b++)
        {
            double h = Math.Max(1.5, display[b] * halfMax);
            double x0 = WaveformLayout.FractionToX((double)b / bars, width);
            double x1 = WaveformLayout.FractionToX((double)(b + 1) / bars, width);
            points.Add(new Point(x0, mid - h));
            points.Add(new Point(x1, mid - h));
        }
        // … then bottom edge right→left closes the mirrored band.
        for (int b = bars - 1; b >= 0; b--)
        {
            double h = Math.Max(1.5, display[b] * halfMax);
            double x0 = WaveformLayout.FractionToX((double)b / bars, width);
            double x1 = WaveformLayout.FractionToX((double)(b + 1) / bars, width);
            points.Add(new Point(x1, mid + h));
            points.Add(new Point(x0, mid + h));
        }
        return points;
    }

    // ---------- seek interaction ----------

    private void OnWavePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        WaveCanvas.CapturePointer(e.Pointer);
        UpdateSeekPreview(e);
    }

    private void OnWavePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            UpdateSeekPreview(e);
            return;
        }

        // Hover affordance (PT3-13): preview the time under the pointer before committing a
        // seek; the crosshair-style cursor marks the canvas as scrubbable.
        var playback = AppServices.Playback;
        if (playback == null || playback.Duration <= TimeSpan.Zero || WaveCanvas.ActualWidth <= 0) return;
        var x = e.GetCurrentPoint(WaveCanvas).Position.X;
        var fraction = WaveformLayout.XToFraction(x, WaveCanvas.ActualWidth);
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(WaveCanvas,
            TextFormat.LongDuration(TimeSpan.FromSeconds(fraction * playback.Duration.TotalSeconds)));
        WaveHoverLine.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(WaveHoverLine, x);
    }

    private void OnWavePointerEntered(object sender, PointerRoutedEventArgs e)
    {
    }

    private void OnWavePointerExited(object sender, PointerRoutedEventArgs e)
    {
        WaveHoverLine.Visibility = Visibility.Collapsed;
    }

    private void OnWavePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        WaveCanvas.ReleasePointerCapture(e.Pointer);
        UpdateSeekPreview(e);

        var playback = AppServices.Playback;
        var duration = playback?.Duration ?? TimeSpan.Zero;
        if (duration > TimeSpan.Zero)
            playback!.Seek(TimeSpan.FromSeconds(_dragFraction * duration.TotalSeconds));
    }

    private void UpdateSeekPreview(PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(WaveCanvas).Position;
        _dragFraction = WaveformLayout.XToFraction(position.X, WaveCanvas.ActualWidth);
        ApplyWaveClip(_dragFraction);
    }

    private static double CurrentFraction()
    {
        var playback = AppServices.Playback;
        if (playback == null) return 0;
        return WaveformLayout.FractionOfTrack(playback.Position, playback.Duration);
    }

    private void ApplyWaveClip(double fraction)
    {
        WaveClip.Rect = new Rect(0, 0, WaveformLayout.FractionToX(fraction, WaveCanvas.ActualWidth), WaveHeight);
    }

    // ---------- 10 Hz frame: progress + spectrum ----------

    private void OnFrame()
    {
        var playback = AppServices.Playback;
        if (playback == null) return;

        if (!_dragging)
        {
            var duration = playback.Duration;
            var position = playback.Position;
            ElapsedText.Text = TextFormat.LongDuration(position);
            RemainingText.Text = duration > TimeSpan.Zero
                ? "-" + TextFormat.LongDuration(duration - position)
                : "";
            ApplyWaveClip(CurrentFraction());
        }

        UpdateTransportState();
        UpdateAbBand();
        RenderSpectrum();
    }

    private void RenderSpectrum()
    {
        double width = SpectrumCanvas.ActualWidth;
        double height = SpectrumCanvas.Height;
        if (width <= 0 || double.IsNaN(width)) return;

        var tap = AppServices.Playback?.SpectrumTap;
        if (tap == null) return;

        _ = tap.CopyTo(_spectrumWindow);
        var sampleRate = AppServices.Playback?.CurrentSampleRate ?? 44100;
        var levels = SpectrumCalculator.ComputeLevels(_spectrumWindow, sampleRate);

        if (AppServices.Motion?.MotionEnabled ?? false)
        {
            // Release tuned for a ~0.5s fall at the 100ms frame cadence.
            _smoother.Apply(levels, releasePerSecond: 2.0, dtSeconds: FrameSeconds);
            _smoother.CopyTo(levels);
        }
        else
        {
            _smoother.Reset(levels); // reduced motion: bars show current truth, no decay
        }

        double barWidth = width / SpectrumBars * 0.62;
        for (int i = 0; i < SpectrumBars; i++)
        {
            var bar = _spectrumBars[i];
            double h = Math.Max(2, levels[i] * (height - 2));
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(bar, WaveformLayout.BarCenterX(i, SpectrumBars, width) - barWidth / 2);
            bar.Width = barWidth;
            bar.Height = h;
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(bar, height - h);
        }
    }

    private void BuildSpectrumBars()
    {
        for (int i = 0; i < SpectrumBars; i++)
        {
            var bar = new Rectangle
            {
                Fill = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["DawnAccentBrush"],
                RadiusX = 1,
                RadiusY = 1,
            };
            _spectrumBars[i] = bar;
            SpectrumCanvas.Children.Add(bar);
        }
    }
}
