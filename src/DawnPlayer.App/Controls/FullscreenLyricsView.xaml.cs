using System;
using System.Collections.Generic;
using DawnPlayer.App.Helpers;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.App.Views;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DawnPlayer.App.Controls;

/// <summary>
/// The fullscreen Now Playing lyrics surface (plan §11): the same VM list, templates and tick
/// mirror as the side pane but sized for the hero area — settings-derived scale (base ×1.6,
/// active ×1.5, so the pane's A-/A+ carries over proportionally), no source badge, no
/// editor/search chrome. All decisions come from <see cref="LyricsFeed"/>; this control owns
/// only threading, scrolling and its clock. The clock runs only while the surface is visible
/// and the owning window is active (I10 — the window relays activation).
/// </summary>
public sealed partial class FullscreenLyricsView : UserControl
{
    private const double FontScale = 1.6;
    private const double ActiveScale = 1.5;
    private const double CenterRatio = 0.42;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly LyricsFeed _feed;
    private readonly List<LrcLineVm> _lines = new();
    private int _currentIndex = -1;
    private bool _hasTimedLines;
    private PlaylistItem? _currentItem;
    private int _loadGeneration;
    private LyricsDocument? _currentDoc;
    private string? _currentSource;
    private bool _pointerOver;
    private bool _clockPaused;

    public FullscreenLyricsView()
    {
        InitializeComponent();
        _feed = new LyricsFeed(AppServices.LyricsOnline, () => AppServices.Settings);
        _timer.Tick += (_, _) => OnTimer();

        // Same reader-hold rule as the side pane (PT3-04): hovering pauses auto-scroll.
        LinesList.PointerEntered += (_, _) => _pointerOver = true;
        LinesList.PointerExited += (_, _) => _pointerOver = false;
        LinesList.PointerCanceled += (_, _) => _pointerOver = false;

        AppServices.LyricsSettingsChanged += OnLyricsSettingsChanged;
        AppServices.LyricsChanged += OnExternalLyricsChanged;
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => SyncTimer());

        // Static events would otherwise keep this view (and its window's visual tree) alive.
        AppServices.LyricsSettingsChanged -= OnLyricsSettingsChanged;
        AppServices.LyricsChanged -= OnExternalLyricsChanged;
        Loaded += (_, _) =>
        {
            AppServices.LyricsSettingsChanged += OnLyricsSettingsChanged;
            AppServices.LyricsChanged += OnExternalLyricsChanged;
            SyncTimer();
        };
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Deterministic teardown for the owning window's Closed handler (I10).</summary>
    public void Detach()
    {
        AppServices.LyricsSettingsChanged -= OnLyricsSettingsChanged;
        AppServices.LyricsChanged -= OnExternalLyricsChanged;
        _timer.Stop();
    }

    /// <summary>The owning window lost activation — the frame loop stops and so must this clock.</summary>
    public void PauseClock()
    {
        _clockPaused = true;
        _timer.Stop();
    }

    public void ResumeClock()
    {
        _clockPaused = false;
        SyncTimer();
    }

    private void SyncTimer()
    {
        bool shouldRun = !_clockPaused && Visibility == Visibility.Visible && _hasTimedLines;
        if (shouldRun)
        {
            if (!_timer.IsEnabled) _timer.Start();
        }
        else if (_timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    private void OnLyricsSettingsChanged()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (AppServices.Settings == null) return;

            if (_currentDoc is { HasLines: true } && _feed.ConsumeToggleChange(AppServices.Settings))
            {
                DisplayLyrics(_currentDoc, _currentSource);
                return;
            }

            ApplyLyricsStyle();
        });
    }

    private void OnExternalLyricsChanged(Track? track)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_currentItem?.Track != null && (track == null || string.Equals(_currentItem.Track.Path, track.Path, StringComparison.OrdinalIgnoreCase)))
            {
                OnTrackChanged(_currentItem);
            }
        });
    }

    /// <summary>UI-thread event from the owning window's track relay.</summary>
    public void OnTrackChanged(PlaylistItem? item)
    {
        _currentItem = item;
        _currentIndex = -1;
        _currentDoc = null;
        _currentSource = null;
        _lines.Clear();
        LinesList.ItemsSource = null;

        if (item == null || AppServices.Settings == null)
        {
            ShowEmpty();
            return;
        }

        int generation = ++_loadGeneration;
        var track = item.Track;
        var settings = AppServices.Settings;

        var applied = _feed.PeekAppliedOverride(track.Path);
        if (applied != null)
        {
            DisplayLyrics(applied.Document, applied.Source);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var loaded = settings == null ? null : _feed.LoadOffline(track, settings);
                LyricsDocument? doc = null;
                string? source = null;
                if (loaded != null)
                {
                    doc = loaded.Document;
                    source = loaded.Source;
                }
                else
                {
                    var session = _feed.PeekSessionLyrics(track.Path);
                    if (session != null)
                    {
                        doc = session.Document;
                        source = session.Source;
                    }
                }

                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (generation != _loadGeneration ||
                        !string.Equals(_currentItem?.Track.Path, track.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    DisplayLyrics(doc, source);
                });
            }
            catch (Exception ex)
            {
                App.Log($"[fullscreen-lyrics] async load failed for '{track.Path}': {ex.Message}");
            }
        });
    }

    private void DisplayLyrics(LyricsDocument? doc, string? onlineSource)
    {
        _currentDoc = doc is { HasLines: true } ? doc : null;
        _currentSource = onlineSource;

        _lines.Clear();
        _currentIndex = -1;
        _lines.AddRange(_feed.BuildLines(doc, FontScale, ActiveScale));

        if (_lines.Count == 0)
        {
            ShowEmpty();
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        LinesList.Visibility = Visibility.Visible;
        LinesList.ItemsSource = null;
        LinesList.ItemsSource = _lines;

        _hasTimedLines = _lines.Exists(l => l.Time > TimeSpan.Zero);
        SyncTimer();

        if (_lines.Count > 0) LinesList.ScrollIntoView(_lines[0]);
    }

    private void ApplyLyricsStyle()
    {
        if (AppServices.Settings == null) return;
        _feed.ApplyStyle(_lines, FontScale, ActiveScale);

        if (_currentIndex >= 0 && _currentIndex < _lines.Count)
        {
            ScrollActiveLineToCenter(_currentIndex);
        }
    }

    private void ShowEmpty()
    {
        EmptyState.Visibility = Visibility.Visible;
        LinesList.Visibility = Visibility.Collapsed;
        _hasTimedLines = false;
        SyncTimer();
    }

    private ScrollViewer? _scrollViewer;
    private ScrollViewer? GetScrollViewer() => _scrollViewer ??= VisualTreeHelperExtensions.FindDescendant<ScrollViewer>(LinesList);

    private void ScrollActiveLineToCenter(int index)
    {
        if (index < 0 || index >= _lines.Count) return;

        var sv = GetScrollViewer();
        if (sv != null)
        {
            if (LinesList.ContainerFromIndex(index) is UIElement container)
            {
                var transform = container.TransformToVisual(sv);
                var pos = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                double itemAbsoluteY = sv.VerticalOffset + pos.Y;
                double targetOffset = itemAbsoluteY - (sv.ViewportHeight * CenterRatio);
                sv.ChangeView(null, Math.Max(0, targetOffset), null, false);
                return;
            }
        }

        int approxLinesAbove = 3;
        if (sv != null && sv.ViewportHeight > 0)
        {
            approxLinesAbove = Math.Max(1, (int)Math.Round((sv.ViewportHeight * CenterRatio) / 48.0));
        }
        LinesList.ScrollIntoView(_lines[Math.Max(0, index - approxLinesAbove)], ScrollIntoViewAlignment.Leading);
    }

    private void OnTimer()
    {
        if (AppServices.Playback == null || _lines.Count == 0 || !_hasTimedLines) return;
        if (AppServices.Playback.State == PlaybackState.Stopped) return;

        var tick = _feed.Tick(_lines, _currentIndex, AppServices.Playback.Position);
        _currentIndex = tick.Index;

        if (tick.Changed)
        {
            SyncTimerInterval(tick.Fast);
            if (!_pointerOver && _currentIndex >= 0 && _currentIndex < _lines.Count)
            {
                ScrollActiveLineToCenter(_currentIndex);
            }
        }
    }

    private void SyncTimerInterval(bool fast)
    {
        var interval = TimeSpan.FromMilliseconds(fast ? 40 : 120);
        if (_timer.Interval != interval) _timer.Interval = interval;
    }

    private void OnLineClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LrcLineVm vm && vm.Time > TimeSpan.Zero && AppServices.Playback != null)
        {
            AppServices.Playback.Seek(vm.Time);
        }
    }
}
