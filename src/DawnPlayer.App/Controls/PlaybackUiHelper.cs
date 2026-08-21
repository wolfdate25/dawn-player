using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;

namespace DawnPlayer.App.Controls;

/// <summary>
/// Consolidates common view logic, playlist operations, and dynamic UI state synchronization
/// across LibraryPage, PlaylistPage, and NowPlayingBar.
/// </summary>
public static class PlaybackUiHelper
{
    public static Action<string>? Logger { get; set; }

    private static void Log(string message)
    {
        try { Logger?.Invoke(message); } catch { }
    }

    /// <summary>
    /// Efficiently synchronizes the <see cref="PlaylistItem.IsPlaying"/> state across items,
    /// avoiding redundant property change notifications for items whose state did not change.
    /// </summary>
    public static void UpdatePlayingState(IEnumerable<PlaylistItem>? items, PlaylistItem? currentItem)
    {
        UpdatePlayingState(items, currentItem?.Track?.Path);
    }

    /// <summary>
    /// Efficiently synchronizes the <see cref="PlaylistItem.IsPlaying"/> state across items
    /// based on the currently playing track path.
    /// </summary>
    public static void UpdatePlayingState(IEnumerable<PlaylistItem>? items, string? playingTrackPath)
    {
        if (items == null) return;
        bool hasPath = !string.IsNullOrEmpty(playingTrackPath);

        var snapshot = CollectionSnapshot.Capture(items);
        for (int i = 0; i < snapshot.Length; i++)
        {
            var pi = snapshot[i];
            if (pi?.Track == null) continue;
            bool shouldBePlaying = hasPath && string.Equals(pi.Track.Path, playingTrackPath, StringComparison.OrdinalIgnoreCase);
            if (pi.IsPlaying != shouldBePlaying)
            {
                pi.IsPlaying = shouldBePlaying;
            }
        }
    }

    /// <summary>
    /// Formats Eole-style playlist summary statistics (e.g. "1 h 12 min 30s, 24 items" or "0 items").
    /// </summary>
    public static string FormatEolePlaylistStats(Playlist? playlist)
    {
        if (playlist == null || playlist.Items.Count == 0) return "0 items";
        return FormatEolePlaylistStats(playlist.Items.Count, playlist.TotalDuration);
    }

    /// <summary>
    /// Formats Eole-style count and duration summary statistics.
    /// </summary>
    public static string FormatEolePlaylistStats(int itemCount, TimeSpan totalDuration)
    {
        if (itemCount <= 0) return "0 items";
        string dur = FormatEoleDuration(totalDuration);
        return $"{dur}, {itemCount:N0} items";
    }

    /// <summary>
    /// Formats Eole-style duration string (e.g. "1 h 12 min 30s", "3 min 20s", "45s").
    /// </summary>
    public static string FormatEoleDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} h {ts.Minutes} min {ts.Seconds}s";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes} min {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    /// <summary>
    /// Adds tracks to the target playlist and optionally starts playback on the first added item.
    /// </summary>
    public static async Task<IReadOnlyList<PlaylistItem>> PlayTracksAsync(
        PlaylistManager? playlistManager,
        PlaybackController? playbackController,
        Playlist? targetPlaylist,
        IEnumerable<Track>? tracks,
        bool play = true,
        int startIndex = 0)
    {
        if (playlistManager == null || targetPlaylist == null || tracks == null) return Array.Empty<PlaylistItem>();
        try
        {
            var items = playlistManager.AddTracks(targetPlaylist, tracks);
            if (play && playbackController != null && items.Count > 0)
            {
                int safeIndex = Math.Clamp(startIndex, 0, items.Count - 1);
                await playbackController.PlayAsync(targetPlaylist, items[safeIndex]);
            }
            return items;
        }
        catch (Exception ex)
        {
            Log($"[PlaybackUiHelper.PlayTracksAsync Error] {ex}");
            return Array.Empty<PlaylistItem>();
        }
    }

    /// <summary>
    /// Plays an album or track collection by setting the dedicated Now Playing playlist
    /// and starting playback at <paramref name="startIndex"/>, leaving user playlists untouched.
    /// </summary>
    public static async Task<IReadOnlyList<PlaylistItem>> PlayAlbumNowPlayingAsync(
        PlaylistManager? playlistManager,
        PlaybackController? playbackController,
        IEnumerable<Track>? tracks,
        int startIndex = 0)
    {
        if (playlistManager == null || tracks == null) return Array.Empty<PlaylistItem>();
        try
        {
            var nowPlaying = playlistManager.NowPlaying;
            var items = playlistManager.ReplaceWithTracks(nowPlaying, tracks);
            if (playbackController != null)
            {
                playbackController.Queue.Clear();
                if (items.Count > 0)
                {
                    int safeIndex = Math.Clamp(startIndex, 0, items.Count - 1);
                    await playbackController.PlayAsync(nowPlaying, items[safeIndex]);
                }
            }
            return items;
        }
        catch (Exception ex)
        {
            Log($"[PlaybackUiHelper.PlayAlbumNowPlayingAsync Error] {ex}");
            return Array.Empty<PlaylistItem>();
        }
    }

    /// <summary>
    /// Appends tracks to the dedicated Now Playing playlist WITHOUT touching the playback queue.
    /// This is the "현재 재생목록에 추가" action; use <see cref="EnqueueAlbumNowPlaying"/> for "대기열에 추가".
    /// </summary>
    public static IReadOnlyList<PlaylistItem> AddTracksToNowPlaying(
        PlaylistManager? playlistManager,
        IEnumerable<Track>? tracks)
    {
        if (playlistManager == null || tracks == null) return Array.Empty<PlaylistItem>();
        try
        {
            return playlistManager.AddTracks(playlistManager.NowPlaying, tracks);
        }
        catch (Exception ex)
        {
            Log($"[PlaybackUiHelper.AddTracksToNowPlaying Error] {ex}");
            return Array.Empty<PlaylistItem>();
        }
    }

    /// <summary>
    /// Enqueues an album or track collection into the dedicated Now Playing playlist and playback queue,
    /// without clearing existing tracks or interrupting active playback.
    /// </summary>
    public static IReadOnlyList<PlaylistItem> EnqueueAlbumNowPlaying(
        PlaylistManager? playlistManager,
        PlaybackController? playbackController,
        IEnumerable<Track>? tracks,
        bool playNext = false)
    {
        if (playlistManager == null || tracks == null) return Array.Empty<PlaylistItem>();
        try
        {
            var nowPlaying = playlistManager.NowPlaying;
            var items = playlistManager.AddTracks(nowPlaying, tracks);
            if (items.Count > 0 && playbackController != null)
            {
                if (playNext)
                    playbackController.Queue.EnqueueNext(nowPlaying, items);
                else
                    playbackController.Queue.Enqueue(nowPlaying, items);

                // If playback is completely stopped, start playing the first enqueued item
                if (playbackController.State == PlaybackState.Stopped && playbackController.CurrentItem == null)
                {
                    _ = playbackController.PlayAsync(nowPlaying, items[0]);
                }
            }
            return items;
        }
        catch (Exception ex)
        {
            Log($"[PlaybackUiHelper.EnqueueAlbumNowPlaying Error] {ex}");
            return Array.Empty<PlaylistItem>();
        }
    }

    /// <summary>
    /// Replaces the playlist with given tracks and starts playback on the designated track item.
    /// </summary>
    public static async Task<IReadOnlyList<PlaylistItem>> ReplaceAndPlayTracksAsync(
        PlaylistManager? playlistManager,
        PlaybackController? playbackController,
        Playlist? targetPlaylist,
        IEnumerable<Track>? tracks,
        int startIndex = 0)
    {
        if (playlistManager == null || targetPlaylist == null || tracks == null) return Array.Empty<PlaylistItem>();
        try
        {
            var items = playlistManager.ReplaceWithTracks(targetPlaylist, tracks);
            if (playbackController != null && items.Count > 0)
            {
                int safeIndex = Math.Clamp(startIndex, 0, items.Count - 1);
                await playbackController.PlayAsync(targetPlaylist, items[safeIndex]);
            }
            return items;
        }
        catch (Exception ex)
        {
            Log($"[PlaybackUiHelper.ReplaceAndPlayTracksAsync Error] {ex}");
            return Array.Empty<PlaylistItem>();
        }
    }

    /// <summary>
    /// Starts playback of a single playlist item with safe error logging.
    /// </summary>
    public static async Task PlayItemAsync(PlaybackController? playbackController, Playlist? targetPlaylist, PlaylistItem? item)
    {
        if (playbackController == null || targetPlaylist == null || item == null) return;
        try
        {
            await playbackController.PlayAsync(targetPlaylist, item);
        }
        catch (Exception ex)
        {
            Log($"[PlaybackUiHelper.PlayItemAsync Error] {ex}");
        }
    }

    /// <summary>
    /// Adds tracks to the playlist and enqueues them into the playback queue.
    /// </summary>
    public static void EnqueueTracks(
        PlaylistManager? playlistManager,
        PlaybackController? playbackController,
        Playlist? targetPlaylist,
        IEnumerable<Track>? tracks,
        bool playNext = false)
    {
        if (playlistManager == null || playbackController == null || targetPlaylist == null || tracks == null) return;
        try
        {
            var items = playlistManager.AddTracks(targetPlaylist, tracks);
            if (items.Count > 0)
            {
                if (playNext)
                    playbackController.Queue.EnqueueNext(targetPlaylist, items);
                else
                    playbackController.Queue.Enqueue(targetPlaylist, items);
            }
        }
        catch (Exception ex)
        {
            Log($"[PlaybackUiHelper.EnqueueTracks Error] {ex}");
        }
    }

    /// <summary>
    /// Enqueues existing playlist items into the playback queue.
    /// </summary>
    public static void EnqueueItems(
        PlaybackController? playbackController,
        Playlist? targetPlaylist,
        IEnumerable<PlaylistItem>? items,
        bool playNext = false)
    {
        if (playbackController == null || targetPlaylist == null || items == null) return;
        try
        {
            var list = items.ToList();
            if (list.Count > 0)
            {
                if (playNext)
                    playbackController.Queue.EnqueueNext(targetPlaylist, list);
                else
                    playbackController.Queue.Enqueue(targetPlaylist, list);
            }
        }
        catch (Exception ex)
        {
            Log($"[PlaybackUiHelper.EnqueueItems Error] {ex}");
        }
    }

    /// <summary>
    /// Removes selected items from the target playlist.
    /// </summary>
    public static void RemoveItems(PlaylistManager? playlistManager, Playlist? targetPlaylist, IEnumerable<PlaylistItem>? items)
    {
        if (playlistManager == null || targetPlaylist == null || items == null) return;
        try
        {
            var list = items.ToList();
            if (list.Count > 0)
            {
                playlistManager.RemoveItems(targetPlaylist, list);
            }
        }
        catch (Exception ex)
        {
            Log($"[PlaybackUiHelper.RemoveItems Error] {ex}");
        }
    }

    /// <summary>
    /// Orchestrates standard transport Play/Pause click behavior: toggling active playback,
    /// or falling back to current/first non-empty playlist or library tracks if stopped.
    /// </summary>
    public static async Task TriggerPlayOrResumeAsync(
        PlaybackController? playback,
        PlaylistManager? playlists,
        MusicLibrary? library)
    {
        if (playback == null) return;
        try
        {
            if (playback.State == PlaybackState.Playing || playback.State == PlaybackState.Paused)
            {
                playback.PlayPause();
                return;
            }

            var pl = playlists?.Current;
            if (pl != null && pl.Items.Count > 0)
            {
                playback.PlayPause();
                return;
            }

            var anyPl = playlists?.Playlists.FirstOrDefault(p => p.Items.Count > 0);
            if (anyPl != null)
            {
                playlists?.SelectPlaylist(anyPl);
                playback.PlayPause();
                return;
            }

            var libTracks = library?.Tracks;
            if (libTracks != null && libTracks.Count > 0 && playlists != null)
            {
                var targetPl = playlists.Current ?? playlists.Playlists.FirstOrDefault() ?? playlists.CreatePlaylist();
                var added = playlists.AddTracks(targetPl, libTracks);
                if (added.Count > 0)
                {
                    await playback.PlayAsync(targetPl, added[0]);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[PlaybackUiHelper.TriggerPlayOrResumeAsync Error] {ex}");
        }
    }

    /// <summary>
    /// Finds the corresponding item to scroll into view from the items collection.
    /// </summary>
    public static PlaylistItem? FindItemToScroll(IEnumerable<PlaylistItem>? items, PlaylistItem? currentItem)
    {
        if (items == null || currentItem == null) return null;
        return items.FirstOrDefault(i => ReferenceEquals(i, currentItem))
               ?? items.FirstOrDefault(i => string.Equals(i.Track?.Path, currentItem.Track?.Path, StringComparison.OrdinalIgnoreCase));
    }
}
