using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Playlists;

/// <summary>
/// Domain service contract for playlist lifecycle, item management, sorting, and persistence.
/// </summary>
public interface IPlaylistManager
{
    /// <summary>Live collection of all loaded playlists.</summary>
    ObservableCollection<Playlist> Playlists { get; }

    /// <summary>The currently active playlist. Creates one if none exists — UI thread only.</summary>
    Playlist Current { get; }

    /// <summary>
    /// The currently active playlist, or null when none exists. Creates nothing, so it is safe to
    /// call from background threads (see the remarks on the implementation).
    /// </summary>
    Playlist? TryGetCurrent();

    /// <summary>Dedicated Now Playing playback queue playlist.</summary>
    Playlist NowPlaying { get; }

    /// <summary>The active playlist (convenience property conforming to project spec).</summary>
    Playlist? ActivePlaylist { get; set; }

    /// <summary>Raised when items are removed from a playlist (used to purge queue).</summary>
    event Action<Playlist, IReadOnlyList<PlaylistItem>>? ItemsRemoved;

    Playlist CreatePlaylist(string? name = null);
    Playlist CreatePlaylistFromTracks(string? name, IEnumerable<Track> tracks);
    void AddPlaylist(Playlist playlist);
    void SelectPlaylist(Playlist pl);
    void RemovePlaylist(Playlist pl);
    void RemovePlaylist(string playlistIdOrName);
    void RenamePlaylist(Playlist pl, string newName);

    List<PlaylistItem> AddPaths(Playlist pl, IEnumerable<string> paths, int? insertAt = null);
    Task<List<PlaylistItem>> AddPathsAsync(Playlist pl, IEnumerable<string> paths, int? insertAt = null, CancellationToken ct = default);
    List<PlaylistItem> AddFiles(Playlist pl, IEnumerable<string> files, int? insertAt = null);
    Task<List<PlaylistItem>> AddFilesAsync(Playlist pl, IEnumerable<string> files, int? insertAt = null, CancellationToken ct = default);
    List<PlaylistItem> AddTracks(Playlist pl, IEnumerable<Track> tracks, int? insertAt = null);

    Playlist? ImportPlaylist(string filePath, string? playlistName = null);
    Task<Playlist?> ImportPlaylistAsync(string filePath, string? playlistName = null, CancellationToken ct = default);

    void RemoveItems(Playlist pl, IReadOnlyList<PlaylistItem> items);
    void RemoveAll(Playlist pl);
    List<PlaylistItem> ReplaceWithTracks(Playlist pl, IEnumerable<Track> tracks);

    void Sort(Playlist pl, PlaylistSort sort);
    void RemoveDuplicates(Playlist pl);
    bool MoveSelection(Playlist pl, IReadOnlyList<PlaylistItem> selected, bool up);
    void MoveItem(Playlist pl, int oldIndex, int newIndex);

    int RemoveDeadItems(Playlist pl);
    Task<int> RemoveDeadItemsAsync(Playlist pl, CancellationToken ct = default);

    void SavePlaylist(Playlist playlist);
    void SaveAll();
    void LoadAll();
}
