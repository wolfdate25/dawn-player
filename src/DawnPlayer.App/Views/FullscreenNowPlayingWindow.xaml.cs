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
        };

        BuildSpectrumBars();
        AppServices.CurrentTrackChanged += OnTrackChanged;
        AppServices.LiveStreamTitleChanged += OnLiveStreamTitle;

        _timer.Start();
        OnTrackChanged(AppServices.Playback.CurrentItem);
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
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
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
        try
        {
            string? artPath = !string.IsNullOrEmpty(track.ArtPath) && File.Exists(track.ArtPath)
                ? track.ArtPath
                : AlbumArtService.FindFolderArt(track.Path ?? "");
            if (string.IsNullOrEmpty(artPath))
                artPath = AlbumArtService.TryExtractArt(track, TagReader.ComputeAlbumKey(track));

            if (string.IsNullOrEmpty(artPath) || !File.Exists(artPath)) return;
            CoverImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(artPath));
        }
        catch (Exception ex)
        {
            App.Log($"[fullscreen] cover: {ex.Message}");
        }
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
        if (_dragging) UpdateSeekPreview(e);
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
