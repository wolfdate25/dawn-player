using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DawnPlayer.App.Helpers;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.App.Controls;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace DawnPlayer.App.Views;

public sealed partial class LyricsEditorWindow : Window
{
    private static LyricsEditorWindow? s_activeWindow;

    private Track _track;
    private readonly IntPtr _hwnd;
    // 100 ms is plenty for a position label; 50 ms meant 20 dispatcher wake-ups a second per window.
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _closed;
    private readonly ObservableCollection<LrcEditLineVm> _lines = new();

    private double _stepMs = 500;
    private double _totalOffsetMs;
    private string _targetLrcPath = "";
    private bool _updatingFromSync;

    // Loaded-time (Time, Words) per line — the restore button recovers from destructive offset
    // moves even after lines hit the zero clamp, which a reverse delta can never undo.
    private readonly List<(TimeSpan Time, IReadOnlyList<LrcWord>? Words)> _originalTimes = new();
    private int _currentLineIndex = -1;

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
        // These windows are opened from the main shell but never received the shell's theme:
        // they kept raw default theming while the accent palette they reference mutated under
        // them. Register for accent changes as well.
        Services.ThemeService.ApplyTheme(this, AppServices.Settings.Ui);
        Services.ThemeService.RegisterAuxiliaryWindow(this);
        Title = AppStrings.Get("LyricsEditor_WindowTitle", "가사 편집기 — Dawn Player");
        _hwnd = WindowNative.GetWindowHandle(this);
        WindowPlacementHelper.TryRestoreWindowSize(this, AppServices.Settings.Ui.LyricsEditorWidth, AppServices.Settings.Ui.LyricsEditorHeight, _hwnd);
        _track = track;

        _stepMs = AppServices.Settings.Lyrics.DefaultOffsetStepMs >= 1 ? AppServices.Settings.Lyrics.DefaultOffsetStepMs : 500;
        StepDeltaBox.Value = _stepMs;
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

        // Unsaved-change guard (PT4-04): Window.Closed cannot cancel, so the cancellable
        // AppWindow.Closing carries the confirm. The dirty check is a content snapshot compare,
        // which covers every mutation path (raw tab typing, line edits, stamps, offsets) without
        // per-handler wiring.
        _savedSnapshot = CurrentContent();
        AppWindow.Closing += OnAppWindowClosing;

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
        _currentLineIndex = -1;
        LoadTrackLyrics();
    }

    private void OnPollTimerTick(object? sender, object e) => OnPollPlayback();

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        // The timer kept ticking after close, pinning this window, its line collection and its
        // track for the rest of the session — once per editor the user ever opened.
        if (WindowPlacementHelper.TrySaveWindowSize(this, _hwnd, out var w, out var h))
        {
            AppServices.Settings.Ui.LyricsEditorWidth = w;
            AppServices.Settings.Ui.LyricsEditorHeight = h;
            SettingsWriter.Schedule(AppServices.Settings);
        }
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
                    Text = l.Text,
                    Translation = l.Translation,
                    Words = l.Words
                });
            }
            StatusLabel.Text = AppStrings.Format("LyricsEditor_Status_LinesLoaded", "가사 {0}줄 로드됨", doc.Lines.Count);
        }
        else
        {
            StatusLabel.Text = AppStrings.Get("LyricsEditor_Status_NoExistingLyrics", "기존 가사 없음 (신규 작성 가능)");
        }

        _currentLineIndex = -1;
        CaptureOriginalTimes();
        SyncToRawText();
        // Baseline for the unsaved-change guard — also refreshed when LoadTrack re-points the
        // editor at another track.
        _savedSnapshot = CurrentContent();
    }

    private void CaptureOriginalTimes()
    {
        _originalTimes.Clear();
        foreach (var l in _lines) _originalTimes.Add((l.Time, l.Words));
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

        UpdateCurrentLineHighlight(pos);
    }

    /// <summary>
    /// Playhead: the line the playback position lands on gets a row highlight, so turning the
    /// offset moves the highlight immediately — the edit's effect is visible without saving.
    /// </summary>
    private void UpdateCurrentLineHighlight(TimeSpan pos)
    {
        int target = LyricsScrollSynchronizer.FindActiveLineIndex(_lines, pos, 0);
        if (target == _currentLineIndex) return;

        if (_currentLineIndex >= 0 && _currentLineIndex < _lines.Count)
        {
            _lines[_currentLineIndex].IsCurrentLine = false;
        }
        _currentLineIndex = target;
        if (target >= 0 && target < _lines.Count)
        {
            _lines[target].IsCurrentLine = true;
            // ScrollIntoView is a no-op while the item is already on screen, so this only
            // follows the playhead when it drifts out of view — explicit list editing is never
            // yanked around while the line stays visible.
            LinesListView.ScrollIntoView(_lines[target]);
        }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        AppServices.Playback?.PlayPause();
    }

    // ---------------- Offset Controls ----------------

    private void OnStepDeltaValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // The box clamps to [1, 5000] itself; anything that still arrives out of band (program
        // set, corner rounding) is clamped again rather than trusted.
        var val = Math.Clamp(args.NewValue, 1, 5000);
        if (val != args.NewValue) StepDeltaBox.Value = val;
        if (_stepMs == val) return;
        _stepMs = val;
        UpdateStepButtonLabels();
    }

    private void OnPresetStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
        {
            StepDeltaBox.Value = val; // ValueChanged routes through OnStepDeltaValueChanged
        }
    }

    private void UpdateStepButtonLabels()
    {
        // Direction words beat signs: "앞당김" (minus — lines fire earlier) / "뒤로 미룀" (plus).
        // Seconds read better than raw ms at the 0.5 s default.
        string sec = $"{_stepMs / 1000.0:0.###}s";
        StepMinusBtn.Content = AppStrings.Format("LyricsEditor_StepMinusFormat", "◀ 가사 앞당김 {0}", sec);
        StepPlusBtn.Content = AppStrings.Format("LyricsEditor_StepPlusFormat", "가사 뒤로 미룀 {0} ▶", sec);
    }

    private void OnStepMinusClick(object sender, RoutedEventArgs e)
    {
        AdjustTotalOffset(-_stepMs);
    }

    private void OnStepPlusClick(object sender, RoutedEventArgs e)
    {
        AdjustTotalOffset(+_stepMs);
    }

    // Offset magnitudes beyond an hour are always typos, and TimeSpan.FromMilliseconds overflows
    // (uncaught handler abort mid-way) somewhere near ±9e11 ms — an extreme paste then updated the
    // total display while the line times stayed put. Clamp everything to ±1 h.
    private const double MaxOffsetMs = 3600_000;

    private static double ClampOffsetMs(double ms) => Math.Clamp(ms, -MaxOffsetMs, MaxOffsetMs);

    private void AdjustTotalOffset(double deltaMs)
    {
        deltaMs = ClampOffsetMs(deltaMs);
        _totalOffsetMs = ClampOffsetMs(Math.Round(_totalOffsetMs + deltaMs, 4));
        UpdateOffsetUi();
        ApplyOffsetDeltaToLines(deltaMs);
    }

    /// <summary>
    /// Restores every line to its loaded-time (Time, Words). Unlike a reverse delta, this also
    /// undoes damage from the zero clamp — a line pushed below 0 ms by a negative move comes
    /// back exactly. Explicit user edits to individual stamps (typing in the time box, F5) are
    /// also reverted by design: the button promises the load-time original, nothing partial.
    /// </summary>
    private void OnRestoreOriginalTimesClick(object sender, RoutedEventArgs e)
    {
        if (_lines.Count == 0) return;
        if (_lines.Count != _originalTimes.Count)
        {
            // Lines were added/deleted since load — "original times" no longer line up 1:1, and
            // pairing them by index would silently retime the wrong rows.
            StatusLabel.Text = AppStrings.Get("LyricsEditor_Status_RestoreUnavailable", "줄 수가 달라져 원본 시간 복원 불가 — 저장 전이라면 닫기(취소)로 버릴 수 있습니다");
            return;
        }

        for (int i = 0; i < _lines.Count; i++)
        {
            _lines[i].Time = _originalTimes[i].Time;
            _lines[i].Words = _originalTimes[i].Words;
        }
        _totalOffsetMs = 0;
        UpdateOffsetUi();
        SyncToRawText();
        StatusLabel.Text = AppStrings.Get("LyricsEditor_Status_Restored", "라인 시간을 로드 시점 원본으로 되돌렸습니다");
    }

    // The total box commits on Enter / focus leave — a per-keystroke apply meant typing "-500"
    // sequentially applied -5 and -50, and any mid-typing zero-clamp permanently ate the line's
    // original timestamp.
    private void OnTotalOffsetBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ApplyTotalOffsetFromBox();
            e.Handled = true;
        }
    }

    private void OnTotalOffsetBoxLostFocus(object sender, RoutedEventArgs e) => ApplyTotalOffsetFromBox();

    private void ApplyTotalOffsetFromBox()
    {
        if (_updatingFromSync) return;
        var val = ClampOffsetMs(TotalOffsetBox.Value);
        if (Math.Abs(val - _totalOffsetMs) < 0.0001) return;
        double delta = val - _totalOffsetMs;
        _totalOffsetMs = val;
        UpdateOffsetUi();
        ApplyOffsetDeltaToLines(delta);
    }

    private void UpdateOffsetUi()
    {
        _updatingFromSync = true;
        TotalOffsetBox.Value = _totalOffsetMs;
        TotalOffsetSecLabel.Text = AppStrings.Format("LyricsEditor_TotalOffsetSecondsFormat", "(= {0}초)", $"{(_totalOffsetMs / 1000.0):+0.0000;-0.0000;0.0000}");
        _updatingFromSync = false;
    }

    private void ApplyOffsetDeltaToLines(double deltaMs)
    {
        var shift = TimeSpan.FromMilliseconds(deltaMs);
        foreach (var line in _lines)
        {
            var newTime = line.Time + shift;
            if (newTime < TimeSpan.Zero) newTime = TimeSpan.Zero;
            newTime = LrcEditLineVm.SnapToWholeMs(newTime);
            line.Time = newTime;

            // Word timings ride along with the line — a re-timed karaoke line whose word tags
            // stayed behind would light up words before the line even starts.
            if (line.Words is { Count: > 0 })
            {
                var shifted = new List<LrcWord>(line.Words.Count);
                foreach (var w in line.Words)
                {
                    var ws = w.Start + shift;
                    if (ws < TimeSpan.Zero) ws = TimeSpan.Zero;
                    ws = LrcEditLineVm.SnapToWholeMs(ws);
                    if (ws < newTime) ws = newTime;
                    shifted.Add(w with { Start = ws });
                }
                line.Words = shifted;
            }
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
                            Text = l.Text,
                            Translation = l.Translation,
                            Words = l.Words
                        });
                    }
                    ReindexLines();
                    _currentLineIndex = -1;
                    CaptureOriginalTimes(); // wholesale document load — offset restores promise these times
                    SyncToRawText();
                    StatusLabel.Text = AppStrings.Format("LyricsEditor_Status_ClipboardImported", "클립보드에서 {0}줄 가사를 불러왔습니다.", doc.Lines.Count);
                }
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = AppStrings.Format("LyricsEditor_Status_PasteError", "붙여넣기 오류: {0}", ex.Message);
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
            Lines = _lines.Select(l => new LrcLine(l.Time, l.Text, l.Words, l.Translation)).ToList()
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
            _currentLineIndex = -1; // highlight flags died with the old items
            int no = 1;
            foreach (var l in doc.Lines)
            {
                _lines.Add(new LrcEditLineVm
                {
                    LineNo = no++,
                    Time = l.Time,
                    Text = l.Text,
                    Translation = l.Translation,
                    Words = l.Words
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

    private string _savedSnapshot = "";
    private bool _forceClose;
    private bool _confirmPending;

    /// <summary>The exact bytes a save would write right now. Dirty = differs from the snapshot
    /// taken at load (and refreshed after each save).</summary>
    private string CurrentContent()
    {
        var doc = new LyricsDocument
        {
            Title = _track.Title,
            Artist = _track.Artist,
            Album = _track.Album,
            Lines = _lines.Select(l => new LrcLine(l.Time, l.Text, l.Words, l.Translation)).ToList()
        };
        return LrcParser.Format(doc);
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_forceClose) return;
        if (string.Equals(_savedSnapshot, CurrentContent(), StringComparison.Ordinal)) return;

        // A sync edit lost to an accidental close is unrecoverable — block the close and ask.
        args.Cancel = true;
        if (_confirmPending) return; // impatient second close must not stack a second dialog
        _confirmPending = true;
        _ = ConfirmDiscardAndCloseAsync();
    }

    private async Task ConfirmDiscardAndCloseAsync()
    {
        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("LyricsEditor_UnsavedTitle", "저장하지 않은 변경"),
            Content = AppStrings.Get("LyricsEditor_UnsavedMessage", "저장하지 않은 가사 편집이 있습니다. 변경 내용을 버리고 닫을까요?"),
            PrimaryButtonText = AppStrings.Get("LyricsEditor_UnsavedDiscard", "버리고 닫기"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _forceClose = true;
                Close();
            }
        }
        finally
        {
            _confirmPending = false;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var doc = new LyricsDocument
            {
                Title = _track.Title,
                Artist = _track.Artist,
                Album = _track.Album,
                Lines = _lines.Select(l => new LrcLine(l.Time, l.Text, l.Words, l.Translation)).ToList()
            };

            var content = LrcParser.Format(doc);
            LrcParser.SaveToFile(_targetLrcPath, content);
            _savedSnapshot = content;

            // The edited file is authoritative now; drop any search-window pick for this track.
            AppServices.LyricsOnline?.ClearAppliedResult(_track.Path);

            AppServices.RaiseLyricsChanged(_track);
            StatusLabel.Text = AppStrings.Format("LyricsEditor_Status_SaveSuccess", "저장됨 ({0}) — 가사 패널에 즉시 반영됨: {1}", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), _targetLrcPath);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = AppStrings.Format("LyricsEditor_Status_SaveFailed", "저장 실패: {0}", ex.Message);
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
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            // Routes through the unsaved-change guard above (PT5-20: this window was one of the
            // few surfaces without an Esc exit).
            e.Handled = true;
            Close();
        }
    }
}
