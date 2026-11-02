using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Helpers;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.App.Views;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace DawnPlayer.App.Controls;

public sealed class IsCurrentToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true
            ? ThemeResourceHelper.GetBrush("DawnAccentTextBrush")
            : ThemeResourceHelper.GetBrush("TextPrimaryBrush");

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class IsCurrentToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is true)
        {
            var bold = AppServices.Settings?.Lyrics.BoldActiveLine ?? true;
            return bold ? FontWeights.SemiBold : FontWeights.Normal;
        }
        return FontWeights.Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class LyricsPane : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly List<LrcLineVm> _lines = new();
    private int _currentIndex = -1;
    private PlaylistItem? _currentItem;
    private bool _hasTimedLines;

    public LyricsPane()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => OnTimer();

        // PT3-04: auto-scroll must not fight the reader — while the pointer rests over the
        // lyrics the active-line highlight keeps updating, but the view is left where the user
        // put it. Leaving the pane resumes following on the next line change.
        LinesList.PointerEntered += (_, _) => _pointerOverLines = true;
        LinesList.PointerExited += (_, _) => _pointerOverLines = false;
        LinesList.PointerCanceled += (_, _) => _pointerOverLines = false;

        AppServices.LyricsSettingsChanged += OnLyricsSettingsChanged;
        AppServices.LyricsChanged += OnExternalLyricsChanged;

        // The app hosts a lyrics pane per page, and each one used to poll playback position
        // roughly eight times a second whether or not it was on screen. Tick only while this
        // pane is actually visible and actually has timestamped lines to follow.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => SyncTimer());
        Loaded += (_, _) =>
        {
            AppServices.LyricsSettingsChanged -= OnLyricsSettingsChanged;
            AppServices.LyricsChanged -= OnExternalLyricsChanged;
            AppServices.LyricsSettingsChanged += OnLyricsSettingsChanged;
            AppServices.LyricsChanged += OnExternalLyricsChanged;
            SyncTimer();
        };
        Unloaded += (_, _) =>
        {
            // Static events would otherwise keep this pane (and its whole visual tree) alive and
            // still doing per-track work after it has left the tree.
            AppServices.LyricsSettingsChanged -= OnLyricsSettingsChanged;
            AppServices.LyricsChanged -= OnExternalLyricsChanged;
            _timer.Stop();
        };
    }

    private void SyncTimer()
    {
        bool shouldRun = Visibility == Visibility.Visible && _hasTimedLines;
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
            var s = AppServices.Settings.Lyrics;

            // Template-affecting switches need a rebuild from the retained document; a realized
            // container's template is only picked at realization, so a style pass can't swap it.
            if (_currentDoc is { HasLines: true } &&
                (s.KaraokeHighlight != _lastKaraokeHighlight || s.ShowTranslation != _lastShowTranslation))
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

    /// <summary>UI-thread event from AppServices.</summary>
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
            SetSourceBadge(null);
            return;
        }

        int generation = ++_loadGeneration;
        var track = item.Track;
        var settings = AppServices.Settings;

        // An explicit search-window pick outranks everything (see ApplyResult): without this,
        // Apply re-showed the stale offline document the user opened search to replace. The
        // in-memory caches are instant, so they stay synchronous.
        var applied = AppServices.LyricsOnline?.GetAppliedResult(track.Path);
        if (applied is { Document.HasLines: true })
        {
            DisplayLyrics(applied.Document,
                applied.IsSynced
                    ? AppStrings.Format("LyricsPane_SourceOnlineFormat", "온라인 · {0}", applied.PluginName)
                    : AppStrings.Format("LyricsPane_SourceOnlineUnsyncedFormat", "온라인 · {0} · 비동기", applied.PluginName));
            return;
        }

        // The disk lookup walks pattern×folder File.Exists probes plus a TagLib open — with a
        // network search folder that froze the UI for hundreds of ms on EVERY track change.
        // Load on a worker; a completion for a superseded track is dropped.
        _ = Task.Run(() =>
        {
            try
            {
                var doc = LyricsFinder.LoadLyrics(track, settings);
                string? source = null;
                // Online fallback keeps the original precedence (offline file > session cache);
                // the badge is decided here on the worker so the UI thread never re-probes.
                if (doc is not { HasLines: true })
                {
                    var online = AppServices.LyricsOnline?.GetSessionLyrics(track.Path);
                    if (online is { Document.HasLines: true })
                    {
                        doc = online.Document;
                        source = online.IsSynced
                            ? AppStrings.Format("LyricsPane_SourceOnlineFormat", "온라인 · {0}", online.PluginName)
                            : AppStrings.Format("LyricsPane_SourceOnlineUnsyncedFormat", "온라인 · {0} · 비동기", online.PluginName);
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
                App.Log($"[lyrics-pane] async load failed for '{track.Path}': {ex.Message}");
            }
        });
    }

    private int _loadGeneration;
    private LyricsDocument? _currentDoc;
    private string? _currentSource;
    private bool _lastKaraokeHighlight;
    private bool _lastShowTranslation;

    private void DisplayLyrics(LyricsDocument? doc, string? onlineSource)
    {
        _currentDoc = doc is { HasLines: true } ? doc : null;
        _currentSource = onlineSource;
        SetSourceBadge(onlineSource);

        _lines.Clear();
        _currentIndex = -1;

        if (doc != null && doc.HasLines)
        {
            var s = AppServices.Settings.Lyrics;
            _lastKaraokeHighlight = s.KaraokeHighlight;
            _lastShowTranslation = s.ShowTranslation;
            foreach (var line in doc.Lines)
            {
                _lines.Add(new LrcLineVm
                {
                    Time = line.Time,
                    Text = line.Text,
                    Translation = line.Translation,
                    Words = line.Words is { Count: > 0 } ? line.Words : null,
                    ShowTranslationSubLine = s.ShowTranslation,
                    UseKaraoke = s.KaraokeHighlight && line.Words is { Count: > 0 },
                    BaseFontSize = s.FontSize,
                    ActiveFontSize = s.ActiveFontSize,
                    EnableFocusEffect = s.EnableFocusEffect,
                    FontFamily = s.FontFamily,
                    CharacterSpacing = s.CharacterSpacing,
                    LineHeight = s.LineHeight,
                    TextAlignment = s.Alignment
                });
            }
        }

        if (_lines.Count == 0)
        {
            ShowEmpty();
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        LinesList.Visibility = Visibility.Visible;
        LinesList.ItemsSource = null;
        LinesList.ItemsSource = _lines;

        // Cached once per track instead of scanning every line on every timer tick.
        _hasTimedLines = _lines.Exists(l => l.Time > TimeSpan.Zero);
        SyncTimer();

        ApplyLyricsStyle();
        if (_lines.Count > 0) LinesList.ScrollIntoView(_lines[0]);
    }

    private void ApplyLyricsStyle()
    {
        if (AppServices.Settings == null) return;
        var s = AppServices.Settings.Lyrics;

        foreach (var l in _lines)
        {
            l.BaseFontSize = s.FontSize;
            l.ActiveFontSize = s.ActiveFontSize;
            l.EnableFocusEffect = s.EnableFocusEffect;
            l.FontFamily = s.FontFamily;
            l.CharacterSpacing = s.CharacterSpacing;
            l.LineHeight = s.LineHeight;
            l.TextAlignment = s.Alignment;
            l.ShowTranslationSubLine = s.ShowTranslation;
            l.NotifyTypographyChanged();
        }

        if (_currentIndex >= 0 && _currentIndex < _lines.Count)
        {
            ScrollActiveLineToViewportRatio(_currentIndex, 0.33);
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

    private void ScrollActiveLineToViewportRatio(int index, double ratioFromTop = 0.33)
    {
        if (index < 0 || index >= _lines.Count) return;

        var sv = GetScrollViewer();
        if (sv != null)
        {
            if (LinesList.ContainerFromIndex(index) is UIElement container)
            {
                var transform = container.TransformToVisual(sv);
                var pos = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                double currentOffset = sv.VerticalOffset;
                double itemAbsoluteY = currentOffset + pos.Y;
                double targetOffset = itemAbsoluteY - (sv.ViewportHeight * ratioFromTop);
                if (targetOffset < 0) targetOffset = 0;
                sv.ChangeView(null, targetOffset, null, false);
                return;
            }
        }

        int approxLinesAbove = 3;
        if (sv != null && sv.ViewportHeight > 0)
        {
            approxLinesAbove = Math.Max(1, (int)Math.Round((sv.ViewportHeight * ratioFromTop) / 36.0));
        }

        int scrollTargetIndex = Math.Max(0, index - approxLinesAbove);
        LinesList.ScrollIntoView(_lines[scrollTargetIndex], ScrollIntoViewAlignment.Leading);
    }

    private bool _pointerOverLines;

    private void OnTimer()
    {
        if (AppServices.Playback == null || _lines.Count == 0 || !_hasTimedLines) return;
        if (AppServices.Playback.State == PlaybackState.Stopped) return;

        var position = AppServices.Playback.Position;
        var previousIndex = _currentIndex;
        int targetIdx = LyricsScrollSynchronizer.FindActiveLineIndex(_lines, position, 0);
        if (LyricsScrollSynchronizer.UpdateActiveLineState(_lines, ref _currentIndex, targetIdx))
        {
            // Deactivate the previous line's karaoke residue: a recycled/dimmed line must not
            // keep half-lit "sung" runs from its moment in the spotlight.
            if (previousIndex >= 0 && previousIndex < _lines.Count)
            {
                _lines[previousIndex].ActiveWordIndex = -1;
            }
            SyncTimerInterval();

            // Highlight always follows; the scroll only follows when the user isn't reading.
            if (!_pointerOverLines && _currentIndex >= 0 && _currentIndex < _lines.Count)
            {
                ScrollActiveLineToViewportRatio(_currentIndex, 0.33);
            }
        }

        UpdateActiveWordIndex(position);
    }

    /// <summary>Word highlighting polls faster only while the active line actually has word timings.</summary>
    private void SyncTimerInterval()
    {
        bool fast = _currentIndex >= 0 && _currentIndex < _lines.Count && _lines[_currentIndex].UseKaraoke;
        var interval = TimeSpan.FromMilliseconds(fast ? 40 : 120);
        if (_timer.Interval != interval) _timer.Interval = interval;
    }

    /// <summary>
    /// The VM mirrors KaraokeTimeline's answer for the current position (invariant I7: the
    /// highlighted word is the computed word, never a stale or hand-waved index).
    /// </summary>
    private void UpdateActiveWordIndex(TimeSpan position)
    {
        if (_currentIndex < 0 || _currentIndex >= _lines.Count) return;
        var vm = _lines[_currentIndex];
        if (!vm.UseKaraoke) return;
        vm.ActiveWordIndex = KaraokeTimeline.WordIndexAt(vm.Words, position);
    }

    private void OnLineClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LrcLineVm vm && vm.Time > TimeSpan.Zero && AppServices.Playback != null)
        {
            AppServices.Playback.Seek(vm.Time);
        }
    }

    private void OnFontSmallerClick(object sender, RoutedEventArgs e)
    {
        if (AppServices.Settings == null) return;
        var s = AppServices.Settings.Lyrics;
        s.FontSize = Math.Clamp(s.FontSize - 1.0, 10.0, 24.0);
        s.ActiveFontSize = Math.Clamp(s.ActiveFontSize - 1.0, 12.0, 32.0);
        SettingsWriter.Schedule(AppServices.Settings);
        AppServices.RaiseLyricsSettingsChanged();
    }

    private void OnFontBiggerClick(object sender, RoutedEventArgs e)
    {
        if (AppServices.Settings == null) return;
        var s = AppServices.Settings.Lyrics;
        s.FontSize = Math.Clamp(s.FontSize + 1.0, 10.0, 24.0);
        s.ActiveFontSize = Math.Clamp(s.ActiveFontSize + 1.0, 12.0, 32.0);
        SettingsWriter.Schedule(AppServices.Settings);
        AppServices.RaiseLyricsSettingsChanged();
    }

    private void OnOpenEditorClick(object sender, RoutedEventArgs e)
    {
        var track = _currentItem?.Track ?? AppServices.Playback?.CurrentItem?.Track;
        if (track != null)
        {
            LyricsEditorWindow.OpenForTrack(track);
        }
        else
        {
            AppServices.RaiseWarning(AppStrings.Get("Msg_NoTrackPlayingWarning", "현재 재생 중인 트랙이 없습니다."));
        }
    }

    private void OnOpenSearchClick(object sender, RoutedEventArgs e)
    {
        var track = _currentItem?.Track ?? AppServices.Playback?.CurrentItem?.Track;
        if (track != null)
        {
            LyricsSearchWindow.OpenForTrack(track);
        }
        else
        {
            AppServices.RaiseWarning(AppStrings.Get("Msg_NoTrackPlayingWarning", "현재 재생 중인 트랙이 없습니다."));
        }
    }

    private void SetSourceBadge(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            SourceBadge.Visibility = Visibility.Collapsed;
            return;
        }
        SourceBadge.Text = text;
        SourceBadge.Visibility = Visibility.Visible;
    }
}
