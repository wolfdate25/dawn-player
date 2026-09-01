using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DawnPlayer.Core.Models;

/// <summary>An entry inside a playlist. One <see cref="Track"/> may appear in
/// several items (e.g. same song twice); items are referenced by the queue.</summary>
public sealed class PlaylistItem : INotifyPropertyChanged
{
    public Track Track { get; }

    private int _queueIndex = -1;
    private long _queueVersion;
    private readonly object _indexLock = new();

    /// <summary>1-based position in the playback queue, -1 when not queued.</summary>
    public int QueueIndex
    {
        get
        {
            lock (_indexLock) return _queueIndex;
        }
        set
        {
            bool changed = false;
            lock (_indexLock)
            {
                if (_queueIndex != value)
                {
                    _queueIndex = value;
                    changed = true;
                }
            }
            if (changed)
            {
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Thread-safely updates QueueIndex only if version is monotonically greater or equal.
    /// Returns true if updated; false if rejected as a stale concurrent update.
    /// </summary>
    public bool UpdateQueueIndex(int newIndex, long version)
    {
        bool changed = false;
        lock (_indexLock)
        {
            if (version < _queueVersion)
            {
                return false; // Stale update from an earlier queue operation
            }

            _queueVersion = version;
            if (_queueIndex != newIndex)
            {
                _queueIndex = newIndex;
                changed = true;
            }
        }

        if (changed)
        {
            OnPropertyChanged(nameof(QueueIndex));
        }
        return true;
    }

    private bool _isPlaying;
    /// <summary>True while this exact item is the playing item.</summary>
    public bool IsPlaying
    {
        get => Volatile.Read(ref _isPlaying);
        set
        {
            if (Volatile.Read(ref _isPlaying) != value)
            {
                Volatile.Write(ref _isPlaying, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationDisplay));
            }
        }
    }

    private string? _remainingTimeText;
    /// <summary>Formatted remaining time (e.g. "-3:40") when playing.</summary>
    public string? RemainingTimeText
    {
        get => Volatile.Read(ref _remainingTimeText);
        set
        {
            if (!string.Equals(Volatile.Read(ref _remainingTimeText), value, StringComparison.Ordinal))
            {
                Volatile.Write(ref _remainingTimeText, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationDisplay));
            }
        }
    }

    /// <summary>Dynamic duration display string: shows "-m:ss" remaining time when playing, or "m:ss" duration otherwise.</summary>
    public string DurationDisplay
    {
        get
        {
            var isPlaying = Volatile.Read(ref _isPlaying);
            var rem = Volatile.Read(ref _remainingTimeText);
            if (isPlaying && !string.IsNullOrEmpty(rem))
            {
                return rem;
            }

            var dur = Track.Duration;
            if (dur.TotalHours >= 1)
            {
                return ((int)dur.TotalHours) + dur.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture);
            }
            return dur.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }
    }

    public PlaylistItem(Track track) => Track = track ?? throw new ArgumentNullException(nameof(track));

    public override string ToString() => Track.ToString();

    public static Action<Action>? UiDispatcher { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler == null) return;

        var dispatcher = UiDispatcher;
        if (dispatcher != null)
        {
            dispatcher(() => handler.Invoke(this, new PropertyChangedEventArgs(name)));
        }
        else
        {
            handler.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
