using System.Threading;
using DawnPlayer.App.Helpers;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Microsoft.UI.Xaml;

namespace DawnPlayer.App.Services;

/// <summary>
/// Manages session state restoration (playback queue, last played track and position) and application shutdown persistence.
/// </summary>
public static class SessionManager
{
    private static int _shutdownGate;

    /// <summary>
    /// Restores the queue and last played track/position from saved AppSettings.
    /// </summary>
    public static void RestoreSession(
        AppSettings settings,
        PlaylistManager playlists,
        MusicLibrary library,
        PlaybackController playback,
        Action<PlaylistItem>? onTrackRestored = null,
        Action<double, double>? onPositionRestored = null)
    {
        var p = settings.Playback;

        // Restore Queue
        if (p.QueueItems.Count > 0)
        {
            foreach (var qi in p.QueueItems)
            {
                if (!File.Exists(qi.TrackPath)) continue;
                var pl = playlists.Playlists.FirstOrDefault(x => x.Name == qi.PlaylistName) ?? playlists.Current;
                var track = library.GetTrack(qi.TrackPath) ?? TagReader.TryRead(qi.TrackPath);
                if (track != null)
                {
                    var item = pl.Items.FirstOrDefault(i => i.Track.Path == track.Path) ?? new PlaylistItem(track);
                    playback.Queue.Enqueue(pl, new List<PlaylistItem> { item });
                }
            }
        }

        // Restore last track in PlayerBar (ready state)
        if (!string.IsNullOrEmpty(p.LastPlayedTrackPath) && File.Exists(p.LastPlayedTrackPath))
        {
            var track = library.GetTrack(p.LastPlayedTrackPath) ?? TagReader.TryRead(p.LastPlayedTrackPath);
            if (track != null)
            {
                var pl = playlists.Playlists.FirstOrDefault(x => x.Name == p.LastPlayedPlaylistName) ?? playlists.Current;
                var item = pl.Items.FirstOrDefault(i => i.Track.Path == track.Path) ?? new PlaylistItem(track);
                onTrackRestored?.Invoke(item);
                if (p.LastPlayedPositionSeconds > 0)
                {
                    onPositionRestored?.Invoke(p.LastPlayedPositionSeconds, track.DurationMs / 1000.0);
                }
            }
        }
    }

    /// <summary>
    /// Saves current playback state (last track, position, queue) and window placement to settings.
    /// </summary>
    public static void SaveSession(
        AppSettings settings,
        PlaybackController playback,
        Window? window = null,
        IntPtr hwnd = default)
    {
        try
        {
            if (window != null)
            {
                WindowPlacementHelper.SavePlacement(window, settings.Ui, hwnd);
            }

            var p = settings.Playback;
            if (playback.CurrentItem?.Track is { } track)
            {
                p.LastPlayedTrackPath = track.Path;
                p.LastPlayedPlaylistName = playback.CurrentPlaylist?.Name;

                // A torn-down controller still reports CurrentItem but a position of zero, so a
                // second save pass would overwrite the real resume point with 0 and the app would
                // always restart the track from the beginning. Keep the previous value instead.
                var pos = playback.Position;
                if (pos > TimeSpan.Zero || playback.Duration <= TimeSpan.Zero)
                {
                    p.LastPlayedPositionSeconds = pos.TotalSeconds;
                }
            }

            p.QueueItems = playback.Queue.Entries
                .Select(e => new QueueSavedEntry { PlaylistName = e.Playlist?.Name ?? "", TrackPath = e.Item.Track.Path })
                .ToList();

            SettingsWriter.FlushNow(settings);
        }
        catch { }
    }

    /// <summary>
    /// Orchestrates graceful shutdown: saves state, stops services, and terminates application.
    /// </summary>
    public static void Shutdown(
        AppSettings settings,
        PlaybackController playback,
        Window? window = null,
        IntPtr hwnd = default)
    {
        // Application.Current.Exit() below raises Window.Closed, which re-enters this method.
        // The second pass runs against an already-disposed controller and used to persist garbage.
        if (Interlocked.Exchange(ref _shutdownGate, 1) != 0) return;

        SaveSession(settings, playback, window, hwnd);

        try { AppServices.Shutdown(); } catch { }
        try { Application.Current.Exit(); } catch { }
        Environment.Exit(0);
    }
}
