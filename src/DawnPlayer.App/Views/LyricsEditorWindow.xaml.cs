using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using DawnPlayer.App.Helpers;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace DawnPlayer.App.Views;

public sealed class LrcEditLineVm : INotifyPropertyChanged
{
    private int _lineNo;
    private TimeSpan _time;
    private string _timeString = "00:00.000";
    private string _text = "";

    public int LineNo
    {
        get => _lineNo;
        set { if (_lineNo != value) { _lineNo = value; OnPropertyChanged(); } }
    }

    public TimeSpan Time
    {
        get => _time;
        set
        {
            if (_time != value)
            {
                _time = value;
                int min = (int)value.TotalMinutes;
                int sec = value.Seconds;
                int ms = value.Milliseconds;
                _timeString = $"{min:D2}:{sec:D2}.{ms:D3}";
                OnPropertyChanged();
                OnPropertyChanged(nameof(TimeString));
            }
        }
    }

    public string TimeString
    {
        get => _timeString;
        set
        {
            if (_timeString != value)
            {
                _timeString = value;
                OnPropertyChanged();
                if (TryParseTimeString(value, out var parsed))
                {
                    _time = parsed;
                    OnPropertyChanged(nameof(Time));
                }
            }
        }
    }

    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; OnPropertyChanged(); } }
    }

    private static bool TryParseTimeString(string s, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Trim().Split(':');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out int min)) return false;
        var secParts = parts[1].Split('.');
        if (!int.TryParse(secParts[0], out int sec)) return false;
        double ms = 0;
        if (secParts.Length > 1 && double.TryParse("0." + secParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double frac))
        {
            ms = frac * 1000;
        }
        time = TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec) + TimeSpan.FromMilliseconds(ms);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class LyricsEditorWindow : Window
{
    private static LyricsEditorWindow? s_activeWindow;

    private Track _track;
    // 100 ms is plenty for a position label; 50 ms meant 20 dispatcher wake-ups a second per window.
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _closed;
    private readonly ObservableCollection<LrcEditLineVm> _lines = new();

    private double _stepMs = 0.5;
    private double _totalOffsetMs;
    private string _targetLrcPath = "";
    private bool _updatingFromSync;

    public static void OpenForTrack(Track track)
    {
        if (s_activeWindow != null)
        {
            try
            {
                // Reusing the window without retargeting it showed the *previous* track's title,
                // lines and .lrc path — so editing and saving overwrote the wrong file.
                if (!ReferenceEquals(s_activeWindow._track, track))
                {
                    s_activeWindow.LoadTrack(track);
                }
                s_activeWindow.Activate();
                return;
            }
            catch { s_activeWindow = null; }
        }

        var win = new LyricsEditorWindow(track);
        s_activeWindow = win;
        win.Closed += (_, _) => { if (s_activeWindow == win) s_activeWindow = null; };
        win.Activate();
    }

    public LyricsEditorWindow(Track track)
    {
        InitializeComponent();
        Title = AppStrings.Get("LyricsEditor_WindowTitle", "가사 편집기 — Dawn Player");
        _track = track;

        _stepMs = AppServices.Settings.Lyrics.DefaultOffsetStepMs > 0 ? AppServices.Settings.Lyrics.DefaultOffsetStepMs : 0.5;
        StepDeltaBox.Text = _stepMs.ToString("G", CultureInfo.InvariantCulture);
        UpdateStepButtonLabels();

        LinesListView.ItemsSource = _lines;

        LoadTrack(track);

        _pollTimer.Tick += OnPollTimerTick;
        _pollTimer.Start();

        // Custom keyboard shortcuts
        if (Content is FrameworkElement root)
        {
            root.KeyDown += OnWindowKeyDown;
        }

        Closed += OnWindowClosed;
    }

    /// <summary>Points the editor at another track, reloading its lyrics and save path.</summary>
    public void LoadTrack(Track track)
    {
        _track = track;
        TrackHeaderTitle.Text = track.Title;
        TrackHeaderArtist.Text = $"— {track.Artist}";
        _targetLrcPath = LyricsFinder.FindLrcPath(track, AppServices.Settings) ?? LyricsFinder.GetDefaultLrcSavePath(track);
        FilePathLabel.Text = _targetLrcPath;
        _totalOffsetMs = 0;
        UpdateOffsetUi();
        LoadTrackLyrics();
    }

    private void OnPollTimerTick(object? sender, object e) => OnPollPlayback();

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        // The timer kept ticking after close, pinning this window, its line collection and its
        // track for the rest of the session — once per editor the user ever opened.
        _closed = true;
        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTimerTick;
        Closed -= OnWindowClosed;
        if (Content is FrameworkElement root)
        {
            root.KeyDown -= OnWindowKeyDown;
        }
    }

    private void LoadTrackLyrics()
    {
        var doc = LyricsFinder.LoadLyrics(_track, AppServices.Settings);
        _lines.Clear();

        if (doc != null && doc.HasLines)
        {
            int no = 1;
            foreach (var l in doc.Lines)
            {
                _lines.Add(new LrcEditLineVm
                {
                    LineNo = no++,
                    Time = l.Time,
                    Text = l.Text
                });
            }
            StatusLabel.Text = AppStrings.Format("LyricsEditor_Status_LinesLoaded", doc.Lines.Count);
        }
        else
        {
            StatusLabel.Text = AppStrings.Get("LyricsEditor_Status_NoExistingLyrics", "기존 가사 없음 (신규 작성 가능)");
        }

        SyncToRawText();
    }

    private void OnPollPlayback()
    {
        if (_closed || AppServices.Playback == null) return;
        var pos = AppServices.Playback.Position;
        int min = (int)pos.TotalMinutes;
        int sec = pos.Seconds;
        int ms = pos.Milliseconds;
        PositionText.Text = $"{min}:{sec:D2}.{ms:D3}";

        bool isPlaying = AppServices.Playback.State == PlaybackState.Playing;
        PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        AppServices.Playback?.PlayPause();
    }

    // ---------------- Offset Controls ----------------

    private void OnStepDeltaTextChanged(object sender, TextChangedEventArgs e)
    {
        if (double.TryParse(StepDeltaBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) && val > 0)
        {
            _stepMs = val;
            UpdateStepButtonLabels();
        }
    }

    private void OnPresetStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
        {
            _stepMs = val;
            StepDeltaBox.Text = val.ToString("G", CultureInfo.InvariantCulture);
            UpdateStepButtonLabels();
        }
    }

    private void UpdateStepButtonLabels()
    {
        StepMinusBtn.Content = $"◀ - {_stepMs:G}ms";
        StepPlusBtn.Content = $"+ {_stepMs:G}ms ▶";
    }

    private void OnStepMinusClick(object sender, RoutedEventArgs e)
    {
        AdjustTotalOffset(-_stepMs);
    }

    private void OnStepPlusClick(object sender, RoutedEventArgs e)
    {
        AdjustTotalOffset(+_stepMs);
    }

    private void AdjustTotalOffset(double deltaMs)
    {
        _totalOffsetMs = Math.Round(_totalOffsetMs + deltaMs, 4);
        UpdateOffsetUi();
        ApplyOffsetDeltaToLines(deltaMs);
    }

    private void OnResetOffsetClick(object sender, RoutedEventArgs e)
    {
        double diff = -_totalOffsetMs;
        _totalOffsetMs = 0;
        UpdateOffsetUi();
        ApplyOffsetDeltaToLines(diff);
    }

    private void OnTotalOffsetBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingFromSync) return;
        if (double.TryParse(TotalOffsetBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
        {
            double delta = val - _totalOffsetMs;
            _totalOffsetMs = val;
            TotalOffsetSecLabel.Text = AppStrings.Format("LyricsEditor_TotalOffsetSecondsFormat", $"{(_totalOffsetMs / 1000.0):+0.0000;-0.0000;0.0000}");
            ApplyOffsetDeltaToLines(delta);
        }
    }

    private void UpdateOffsetUi()
    {
        _updatingFromSync = true;
        TotalOffsetBox.Text = _totalOffsetMs >= 0 ? $"+{_totalOffsetMs:F3}" : $"{_totalOffsetMs:F3}";
        TotalOffsetSecLabel.Text = AppStrings.Format("LyricsEditor_TotalOffsetSecondsFormat", $"{(_totalOffsetMs / 1000.0):+0.0000;-0.0000;0.0000}");
        _updatingFromSync = false;
    }

    private void ApplyOffsetDeltaToLines(double deltaMs)
    {
        var shift = TimeSpan.FromMilliseconds(deltaMs);
        foreach (var line in _lines)
        {
            var newTime = line.Time + shift;
            if (newTime < TimeSpan.Zero) newTime = TimeSpan.Zero;
            line.Time = newTime;
        }
        SyncToRawText();
    }

    // ---------------- Line-by-line Actions ----------------

    private void OnStampCurrentTimeClick(object sender, RoutedEventArgs e)
    {
        StampSelectedOrNextLine();
    }

    private void StampSelectedOrNextLine()
    {
        if (AppServices.Playback == null) return;
        var curPos = AppServices.Playback.Position;

        var sel = LinesListView.SelectedItem as LrcEditLineVm;
        if (sel != null)
        {
            sel.Time = curPos;
            // Move selection to next line for fluent flow
            int idx = _lines.IndexOf(sel);
            if (idx + 1 < _lines.Count) LinesListView.SelectedIndex = idx + 1;
        }
        else if (_lines.Count > 0)
        {
            _lines[0].Time = curPos;
            if (_lines.Count > 1) LinesListView.SelectedIndex = 1;
        }
        SyncToRawText();
    }

    private void OnAddLineClick(object sender, RoutedEventArgs e)
    {
        var sel = LinesListView.SelectedItem as LrcEditLineVm;
        int targetIdx = sel != null ? _lines.IndexOf(sel) + 1 : _lines.Count;
        var curPos = AppServices.Playback?.Position ?? TimeSpan.Zero;

        var newLine = new LrcEditLineVm
        {
            Time = curPos,
            Text = AppStrings.Get("LyricsEditor_NewLineText", "새 가사 줄")
        };
        _lines.Insert(targetIdx, newLine);
        ReindexLines();
        LinesListView.SelectedItem = newLine;
        SyncToRawText();
    }

    private void OnDeleteSelectedLineClick(object sender, RoutedEventArgs e)
    {
        if (LinesListView.SelectedItem is LrcEditLineVm sel)
        {
            int idx = _lines.IndexOf(sel);
            _lines.Remove(sel);
            ReindexLines();
            if (_lines.Count > 0)
            {
                LinesListView.SelectedIndex = Math.Clamp(idx, 0, _lines.Count - 1);
            }
            SyncToRawText();
        }
    }

    private void OnMoveLineUpClick(object sender, RoutedEventArgs e)
    {
        if (LinesListView.SelectedItem is LrcEditLineVm sel)
        {
            int idx = _lines.IndexOf(sel);
            if (idx > 0)
            {
                _lines.Move(idx, idx - 1);
                ReindexLines();
                LinesListView.SelectedItem = sel;
                SyncToRawText();
            }
        }
    }

    private void OnMoveLineDownClick(object sender, RoutedEventArgs e)
    {
        if (LinesListView.SelectedItem is LrcEditLineVm sel)
        {
            int idx = _lines.IndexOf(sel);
            if (idx < _lines.Count - 1)
            {
                _lines.Move(idx, idx + 1);
                ReindexLines();
                LinesListView.SelectedItem = sel;
                SyncToRawText();
            }
        }
    }

    private async void OnPasteFromClipboardClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = Clipboard.GetContent();
            if (package != null && package.Contains(StandardDataFormats.Text))
            {
                var text = await package.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var doc = LrcParser.Parse(text);
                    _lines.Clear();
                    int no = 1;
                    foreach (var l in doc.Lines)
                    {
                        _lines.Add(new LrcEditLineVm
                        {
                            LineNo = no++,
                            Time = l.Time,
                            Text = l.Text
                        });
                    }
                    ReindexLines();
                    SyncToRawText();
                    StatusLabel.Text = AppStrings.Format("LyricsEditor_Status_ClipboardImported", doc.Lines.Count);
                }
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = AppStrings.Format("LyricsEditor_Status_PasteError", ex.Message);
        }
    }

    private void OnSeekToLineClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LrcEditLineVm line && AppServices.Playback != null)
        {
            AppServices.Playback.Seek(line.Time);
        }
    }

    private void ReindexLines()
    {
        for (int i = 0; i < _lines.Count; i++) _lines[i].LineNo = i + 1;
    }

    // ---------------- Synchronization between Tabs ----------------

    private void SyncToRawText()
    {
        if (_updatingFromSync) return;
        _updatingFromSync = true;
        var doc = new LyricsDocument
        {
            Title = _track.Title,
            Artist = _track.Artist,
            Album = _track.Album,
            Lines = _lines.Select(l => new LrcLine(l.Time, l.Text)).ToList()
        };
        RawTextEditorBox.Text = LrcParser.Format(doc);
        _updatingFromSync = false;
    }

    private void OnRawTextChanged(object sender, TextChangedEventArgs e)
    {
        // Only parse if raw text tab is active
        if (_updatingFromSync || EditorTabs.SelectedIndex != 1) return;
        _updatingFromSync = true;
        try
        {
            var doc = LrcParser.Parse(RawTextEditorBox.Text);
            _lines.Clear();
            int no = 1;
            foreach (var l in doc.Lines)
            {
                _lines.Add(new LrcEditLineVm
                {
                    LineNo = no++,
                    Time = l.Time,
                    Text = l.Text
                });
            }
        }
        catch { }
        _updatingFromSync = false;
    }

    private void OnEditorTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EditorTabs.SelectedIndex == 0)
        {
            // Sync from raw text to list
            OnRawTextChanged(sender, null!);
        }
        else if (EditorTabs.SelectedIndex == 1)
        {
            // Sync from list to raw text
            SyncToRawText();
        }
    }

    // ---------------- Save & Close ----------------

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var doc = new LyricsDocument
            {
                Title = _track.Title,
                Artist = _track.Artist,
                Album = _track.Album,
                Lines = _lines.Select(l => new LrcLine(l.Time, l.Text)).ToList()
            };

            var content = LrcParser.Format(doc);
            LrcParser.SaveToFile(_targetLrcPath, content);

            AppServices.RaiseLyricsChanged(_track);
            StatusLabel.Text = AppStrings.Format("LyricsEditor_Status_SaveSuccess", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), _targetLrcPath);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = AppStrings.Format("LyricsEditor_Status_SaveFailed", ex.Message);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F5)
        {
            StampSelectedOrNextLine();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Space && FocusManager.GetFocusedElement(Content.XamlRoot) is not TextBox)
        {
            AppServices.Playback?.PlayPause();
            e.Handled = true;
        }
    }
}
