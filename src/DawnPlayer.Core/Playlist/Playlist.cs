using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Playlists;

/// <summary>A named, ordered list of playlist items backed by a fast batch collection with O(1) duration tracking.</summary>
public sealed class Playlist : INotifyPropertyChanged
{
    private string _name;
    private long _totalDurationMs;

    public Playlist(string name)
    {
        _name = name;
        Items = new FastObservableCollection<PlaylistItem>();
        Items.CollectionChanged += OnItemsCollectionChanged;
    }

    private bool _isSystem;
    public bool IsSystem
    {
        get => _isSystem;
        set { if (_isSystem != value) { _isSystem = value; OnPropertyChanged(); } }
    }

    private bool _isSmart;
    /// <summary>
    /// A virtual playlist synthesized from library queries (most played, recently added, ...).
    /// Smart playlists are rebuilt, never renamed/deleted/saved, and are not eligible as the
    /// "current" playlist fallback.
    /// </summary>
    public bool IsSmart
    {
        get => _isSmart;
        set { if (_isSmart != value) { _isSmart = value; OnPropertyChanged(); } }
    }

    public object SyncRoot { get; } = new();

    /// <summary>
    /// Path lines from the playlist file that could not be resolved to a track at load time —
    /// almost always because the volume holding them was not mounted. They are kept verbatim and
    /// written back out, so opening the app with a drive unplugged no longer erases those entries
    /// from the saved playlist.
    /// </summary>
    public List<string> UnresolvedPaths { get; } = new();

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    public FastObservableCollection<PlaylistItem> Items { get; }

    /// <summary>
    /// Immutable copy of the items. Every mutator takes <see cref="SyncRoot"/>, so the copy is
    /// atomic with respect to them and needs no retry.
    /// </summary>
    public PlaylistItem[] GetSnapshot() => CollectionSnapshot.CaptureSafe(Items, SyncRoot);

    public TimeSpan TotalDuration
    {
        get
        {
            lock (SyncRoot)
            {
                return TimeSpan.FromMilliseconds(_totalDurationMs);
            }
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateTotalDuration(e);
        OnPropertyChanged(nameof(TotalDuration));
    }

    private void UpdateTotalDuration(NotifyCollectionChangedEventArgs e)
    {
        lock (SyncRoot)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                long added = 0;
                foreach (PlaylistItem item in e.NewItems)
                {
                    if (item?.Track != null) added += item.Track.DurationMs;
                }
                _totalDurationMs += added;
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                long removed = 0;
                foreach (PlaylistItem item in e.OldItems)
                {
                    if (item?.Track != null) removed += item.Track.DurationMs;
                }
                _totalDurationMs = Math.Max(0, _totalDurationMs - removed);
            }
            else if (e.Action == NotifyCollectionChangedAction.Replace && e.NewItems != null && e.OldItems != null)
            {
                long delta = 0;
                foreach (PlaylistItem item in e.NewItems) if (item?.Track != null) delta += item.Track.DurationMs;
                foreach (PlaylistItem item in e.OldItems) if (item?.Track != null) delta -= item.Track.DurationMs;
                _totalDurationMs = Math.Max(0, _totalDurationMs + delta);
            }
            else
            {
                // Reset, Move, or other composite action: full single-pass recalculation
                long sum = 0;
                for (int i = 0; i < Items.Count; i++)
                {
                    var item = Items[i];
                    if (item?.Track != null) sum += item.Track.DurationMs;
                }
                _totalDurationMs = sum;
            }
        }
    }

    public override string ToString() => Name;

    /// <summary>
    /// Marshals <see cref="PropertyChanged"/> onto the UI thread. Collection changes reach a
    /// playlist from scan and playback threads while <see cref="TotalDuration"/> is bound to XAML.
    /// Null means raise inline, which is what non-UI hosts (and tests) run with.
    /// </summary>
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
