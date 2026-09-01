using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Helpers;
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
            ? ThemeResourceHelper.GetBrush("DawnAccentBrush")
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
        DispatcherQueue?.TryEnqueue(ApplyLyricsStyle);
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
        _lines.Clear();
        LinesList.ItemsSource = null;

        if (item == null || AppServices.Settings == null)
        {
            ShowEmpty();
            SetSourceBadge(null);
            return;
        }

        var doc = LyricsFinder.LoadLyrics(item.Track, AppServices.Settings);
        string? onlineSource = null;

        // Online fallback: lyrics fetched by a plugin earlier this session (auto lookup or the
        // search window) display until an offline file shows up for the track.
        if ((doc == null || !doc.HasLines) && AppServices.LyricsOnline != null)
        {
            var online = AppServices.LyricsOnline.GetSessionLyrics(item.Track.Path);
            if (online is { Document.HasLines: true })
            {
                doc = online.Document;
                onlineSource = online.IsSynced
                    ? $"온라인 · {online.PluginName}"
                    : $"온라인 · {online.PluginName} · 비동기";
            }
        }
        SetSourceBadge(onlineSource);

        if (doc != null && doc.HasLines)
        {
            var s = AppServices.Settings.Lyrics;
            foreach (var line in doc.Lines)
            {
                _lines.Add(new LrcLineVm
                {
                    Time = line.Time,
                    Text = line.Text,
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

    private void OnTimer()
    {
        if (AppServices.Playback == null || _lines.Count == 0 || !_hasTimedLines) return;
        if (AppServices.Playback.State == PlaybackState.Stopped) return;

        int targetIdx = LyricsScrollSynchronizer.FindActiveLineIndex(_lines, AppServices.Playback.Position, 0);
        if (LyricsScrollSynchronizer.UpdateActiveLineState(_lines, ref _currentIndex, targetIdx))
        {
            if (_currentIndex >= 0 && _currentIndex < _lines.Count)
            {
                ScrollActiveLineToViewportRatio(_currentIndex, 0.33);
            }
        }
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
            AppServices.RaiseWarning("현재 재생 중인 트랙이 없습니다.");
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
            AppServices.RaiseWarning("현재 재생 중인 트랙이 없습니다.");
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
