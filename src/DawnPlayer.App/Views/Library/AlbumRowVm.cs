using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using DawnPlayer.Core.Util;

namespace DawnPlayer.App.Views;

/// <summary>
/// View model representing a single horizontal row of album cards in the library grid,
/// with an in-line expandable tracklist drawer directly underneath it (Eole Showlist).
/// </summary>
public sealed class AlbumRowVm : INotifyPropertyChanged
{
    private bool _isDrawerOpen;
    private AlbumCard? _selectedAlbum;
    private string _drawerAlbumTitle = "";
    private string _drawerArtist = "";
    private string _drawerStats = "";
    private string _drawerGenre = "";

    public int RowIndex { get; set; }
    public ObservableCollection<AlbumCard> Cards { get; } = new();

    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        set
        {
            if (_isDrawerOpen != value)
            {
                _isDrawerOpen = value;
                OnPropertyChanged();
            }
        }
    }

    public AlbumCard? SelectedAlbum
    {
        get => _selectedAlbum;
        set
        {
            if (_selectedAlbum != value)
            {
                _selectedAlbum = value;
                OnPropertyChanged();
            }
        }
    }

    public string DrawerAlbumTitle
    {
        get => _drawerAlbumTitle;
        set
        {
            if (_drawerAlbumTitle != value)
            {
                _drawerAlbumTitle = value;
                OnPropertyChanged();
            }
        }
    }

    public string DrawerArtist
    {
        get => _drawerArtist;
        set
        {
            if (_drawerArtist != value)
            {
                _drawerArtist = value;
                OnPropertyChanged();
            }
        }
    }

    public string DrawerStats
    {
        get => _drawerStats;
        set
        {
            if (_drawerStats != value)
            {
                _drawerStats = value;
                OnPropertyChanged();
            }
        }
    }

    public string DrawerGenre
    {
        get => _drawerGenre;
        set
        {
            if (_drawerGenre != value)
            {
                _drawerGenre = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HasGenre => !string.IsNullOrWhiteSpace(DrawerGenre);

    public FastObservableCollection<AlbumTrackItemVm> LeftTracks { get; } = new();
    public FastObservableCollection<AlbumTrackItemVm> RightTracks { get; } = new();

    public void OpenDrawer(AlbumCard card, string? currentPlayingPath)
    {
        if (card == null) return;
        SelectedAlbum = card;

        string albumName = !string.IsNullOrWhiteSpace(card.Album) ? card.Album : "(Unknown Album)";
        string artistName = !string.IsNullOrWhiteSpace(card.Artist) ? card.Artist : "(Unknown Artist)";
        string yearStr = card.Year > 0 ? $" ({card.Year})" : "";
        DrawerAlbumTitle = $"{albumName}{yearStr}";
        DrawerArtist = artistName;

        long totalMs = card.Tracks.Sum(t => t.DurationMs);
        string durFormatted = AlbumGroup.FormatEoleDuration(TimeSpan.FromMilliseconds(totalMs));
        DrawerStats = $"{durFormatted}, {card.Tracks.Count} tracks";

        var genre = card.Tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Genre))?.Genre ?? "";
        DrawerGenre = genre;
        OnPropertyChanged(nameof(HasGenre));

        var sortedTracks = card.Tracks
            .OrderBy(t => t.DiscNo > 0 ? t.DiscNo : 1)
            .ThenBy(t => t.TrackNo > 0 ? t.TrackNo : 1)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var trackVms = new List<AlbumTrackItemVm>();
        foreach (var t in sortedTracks)
        {
            bool isPlaying = !string.IsNullOrEmpty(currentPlayingPath) && string.Equals(t.Path, currentPlayingPath, StringComparison.OrdinalIgnoreCase);
            trackVms.Add(new AlbumTrackItemVm(t, isPlaying));
        }

        int leftCount = (trackVms.Count + 1) / 2;
        LeftTracks.ReplaceAll(trackVms.Take(leftCount));
        RightTracks.ReplaceAll(trackVms.Skip(leftCount));

        foreach (var c in Cards)
        {
            c.IsSelected = ReferenceEquals(c, card);
        }

        IsDrawerOpen = true;
    }

    public void CloseDrawer()
    {
        foreach (var c in Cards)
        {
            c.IsSelected = false;
        }

        IsDrawerOpen = false;
        SelectedAlbum = null;
        LeftTracks.Clear();
        RightTracks.Clear();
    }

    public void UpdatePlayingState(string? currentPlayingPath)
    {
        if (!IsDrawerOpen) return;
        bool hasPath = !string.IsNullOrEmpty(currentPlayingPath);

        foreach (var item in LeftTracks)
        {
            item.IsPlaying = hasPath && string.Equals(item.Track?.Path, currentPlayingPath, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var item in RightTracks)
        {
            item.IsPlaying = hasPath && string.Equals(item.Track?.Path, currentPlayingPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
