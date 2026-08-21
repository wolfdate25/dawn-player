using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Playlists;

/// <summary>
/// View model for an individual track row inside the Eole in-line album tracklist inspector (Showlist).
/// </summary>
public sealed class AlbumTrackItemVm : INotifyPropertyChanged
{
    private bool _isPlaying;

    public Track Track { get; }

    public string TrackNoFormatted
    {
        get
        {
            int disc = Track.DiscNo > 0 ? Track.DiscNo : 1;
            int num = Track.TrackNo > 0 ? Track.TrackNo : 1;
            return $"{disc}.{num}";
        }
    }

    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Track.Title)) return Track.Title;
            var fallback = Path.GetFileNameWithoutExtension(Track.Path);
            return string.IsNullOrWhiteSpace(fallback) ? "(Unknown Title)" : fallback;
        }
    }

    public string DurationFormatted
    {
        get
        {
            var dur = Track.Duration;
            if (dur <= TimeSpan.Zero) return "0:00";
            if (dur.TotalHours >= 1)
            {
                return $"{(int)dur.TotalHours}:{dur:mm\\:ss}";
            }
            return $"{dur.Minutes}:{dur.Seconds:D2}";
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying != value)
            {
                _isPlaying = value;
                OnPropertyChanged();
            }
        }
    }

    public AlbumTrackItemVm(Track track, bool isPlaying = false)
    {
        Track = track ?? throw new ArgumentNullException(nameof(track));
        _isPlaying = isPlaying;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
