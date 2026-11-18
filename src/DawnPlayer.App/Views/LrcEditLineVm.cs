using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DawnPlayer.App.Controls;
using DawnPlayer.Core.Lyrics;

namespace DawnPlayer.App.Views;

public sealed class LrcEditLineVm : INotifyPropertyChanged, ITimedLyricsLine
{
    private int _lineNo;
    private TimeSpan _time;
    private string _timeString = "00:00.000";
    private string _text = "";
    private bool _isCurrentLine;

    /// <summary>
    /// Snaps to whole milliseconds. The list TimeString and the saved [mm:ss.fff] stamps both
    /// resolve to whole ms, so a sub-ms time could never surface anywhere — every setter path
    /// (offset moves, stamps, direct edits) funnels through this (invariant: displayed ==
    /// saved == actual).
    /// </summary>
    public static TimeSpan SnapToWholeMs(TimeSpan t)
    {
        double ms = Math.Round(t.TotalMilliseconds, MidpointRounding.AwayFromZero);
        return ms <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms);
    }

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
            var snapped = SnapToWholeMs(value);
            if (_time != snapped)
            {
                _time = snapped;
                int min = (int)snapped.TotalMinutes;
                int sec = snapped.Seconds;
                int ms = snapped.Milliseconds;
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
                    _time = SnapToWholeMs(parsed);
                    OnPropertyChanged(nameof(Time));
                }
            }
        }
    }

    /// <summary>Playhead marker — the line the playback position is on right now (drives the
    /// row highlight, so an offset nudge shows its effect immediately).</summary>
    public bool IsCurrentLine
    {
        get => _isCurrentLine;
        set
        {
            if (_isCurrentLine != value)
            {
                _isCurrentLine = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentRowHighlightOpacity));
                OnPropertyChanged(nameof(CurrentAccentBarWidth));
            }
        }
    }

    /// <summary>Soft row-highlight opacity for the template (numeric x:Bind — the pane-VM pattern;
    /// avoids a bool→brush converter and keeps theme brushes in XAML). Pairs with the pre-baked
    /// translucent DawnAccentMutedBrush at full opacity.</summary>
    public double CurrentRowHighlightOpacity => IsCurrentLine ? 1 : 0;

    /// <summary>Left accent-bar width; 0 hides the bar without a visibility converter.</summary>
    public double CurrentAccentBarWidth => IsCurrentLine ? 3 : 0;

    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; OnPropertyChanged(); } }
    }

    /// <summary>Merged translation sub-line, carried through untouched so saves never drop it.</summary>
    public string? Translation { get; set; }

    /// <summary>Enhanced-LRC word timings, carried through so saves never drop them.</summary>
    public IReadOnlyList<LrcWord>? Words { get; set; }

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
        // A negative field parsed fine and was written back as "[00:-30.000]" — a timestamp the
        // parser then rejected on the next load. Refuse negatives instead.
        if (min < 0 || sec < 0) return false;
        time = TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec) + TimeSpan.FromMilliseconds(ms);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
