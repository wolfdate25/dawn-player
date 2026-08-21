using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DawnPlayer.App.Services;

/// <summary>
/// Pure mapping and formatting helpers for System Media Transport Controls (SMTC).
/// </summary>
public static class SmtcMapping
{
    /// <summary>
    /// Maps internal <see cref="PlaybackState"/> to WinRT <see cref="MediaPlaybackStatus"/>.
    /// </summary>
    public static MediaPlaybackStatus MapPlaybackState(PlaybackState state) => state switch
    {
        PlaybackState.Playing => MediaPlaybackStatus.Playing,
        PlaybackState.Paused => MediaPlaybackStatus.Paused,
        PlaybackState.Stopped => MediaPlaybackStatus.Stopped,
        _ => MediaPlaybackStatus.Stopped
    };

    /// <summary>
    /// Formats track metadata for SMTC display properties, applying fallback rules for Artist/AlbumArtist.
    /// </summary>
    public static (string Title, string Artist, string Album, string AlbumArtist, uint TrackNumber) FormatMetadata(Track? track)
    {
        if (track == null)
        {
            return (string.Empty, string.Empty, string.Empty, string.Empty, 0);
        }

        var title = track.Title ?? string.Empty;
        var artist = !string.IsNullOrWhiteSpace(track.Artist)
            ? track.Artist
            : (!string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.AlbumArtist : string.Empty);
        var album = track.Album ?? string.Empty;
        var albumArtist = track.AlbumArtist ?? string.Empty;
        var trackNumber = (uint)Math.Max(0, track.TrackNo);

        return (title, artist, album, albumArtist, trackNumber);
    }
}

/// <summary>
/// System Media Transport Controls for an unpackaged desktop window using Windows.Media.Playback.MediaPlayer:
/// media keys (play/pause/next/prev), OS media overlay and lock screen info.
/// </summary>
public sealed class SmtcService : ISmtcService
{
    private readonly PlaybackController _playback;
    private MediaPlayer? _mediaPlayer;
    private SystemMediaTransportControls? _smtc;
    private bool _isInitialized;
    private bool _isDisposed;
    private int _currentUpdateVersion;
    private PlaylistItem? _lastItem;

    public bool IsInitialized => _isInitialized && _smtc != null;

    public SmtcService(PlaybackController playback)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public bool TryInitialize(IntPtr hwnd)
    {
        if (_isDisposed) return false;
        if (_isInitialized && _smtc != null) return true;

        try
        {
            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.CommandManager.IsEnabled = false; // Disable automatic playback control since Dawn Player manages WASAPI engine
            _smtc = _mediaPlayer.SystemMediaTransportControls;

            if (_smtc == null) return false;

            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.IsStopEnabled = true;
            _smtc.ButtonPressed += OnButtonPressed;
            _smtc.PlaybackStatus = SmtcMapping.MapPlaybackState(_playback.State);

            _playback.CurrentChanged += OnPlaybackCurrentChanged;
            _playback.StateChanged += OnPlaybackStateChanged;

            _isInitialized = true;
            UpdateTrack(_playback.CurrentItem);
            UpdateState(_playback.State);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[SMTC] init failed: {ex.Message}");
            _smtc = null;
            _mediaPlayer?.Dispose();
            _mediaPlayer = null;
            _isInitialized = false;
            return false;
        }
    }

    private void OnPlaybackCurrentChanged(PlaylistItem? item) => UpdateTrack(item);

    private void OnPlaybackStateChanged() => UpdateState(_playback.State);

    private async void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (_isDisposed) return;
        try
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                case SystemMediaTransportControlsButton.Pause:
                    _playback.PlayPause();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    await _playback.NextAsync().ConfigureAwait(false);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    await _playback.PreviousAsync().ConfigureAwait(false);
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    _playback.Stop();
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SMTC] Error handling button press {args.Button}: {ex.Message}");
        }
    }

    /// <summary>Called on the UI thread or background thread when the playing track changes.</summary>
    public void UpdateTrack(PlaylistItem? item)
    {
        _ = UpdateTrackAsync(item, CancellationToken.None);
    }

    /// <summary>Asynchronously updates track metadata and thumbnail stream with sequence tracking.</summary>
    public async Task UpdateTrackAsync(PlaylistItem? item, CancellationToken ct = default)
    {
        int targetVersion = Interlocked.Increment(ref _currentUpdateVersion);
        _lastItem = item;

        if (_smtc == null || _isDisposed) return;

        try
        {
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;

            var meta = SmtcMapping.FormatMetadata(item?.Track);
            updater.MusicProperties.Title = meta.Title;
            updater.MusicProperties.Artist = meta.Artist;
            updater.MusicProperties.AlbumTitle = meta.Album;
            updater.MusicProperties.AlbumArtist = meta.AlbumArtist;
            updater.MusicProperties.TrackNumber = meta.TrackNumber;

            string? artPath = item?.Track?.ArtPath;
            if (!string.IsNullOrWhiteSpace(artPath) && File.Exists(artPath))
            {
                try
                {
                    var storageFile = await StorageFile.GetFileFromPathAsync(artPath).AsTask(ct).ConfigureAwait(false);
                    if (targetVersion != Volatile.Read(ref _currentUpdateVersion) || ct.IsCancellationRequested || _isDisposed)
                    {
                        return;
                    }
                    updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(storageFile);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SMTC] Failed to load thumbnail from {artPath}: {ex.Message}");
                    if (targetVersion != Volatile.Read(ref _currentUpdateVersion) || ct.IsCancellationRequested || _isDisposed)
                    {
                        return;
                    }
                    updater.Thumbnail = null;
                }
            }
            else
            {
                updater.Thumbnail = null;
            }

            if (targetVersion == Volatile.Read(ref _currentUpdateVersion) && !ct.IsCancellationRequested && !_isDisposed)
            {
                updater.Update();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SMTC] update track failed: {ex.Message}");
        }
    }

    public void UpdateState(PlaybackState state)
    {
        if (_smtc == null || _isDisposed) return;
        try
        {
            _smtc.PlaybackStatus = SmtcMapping.MapPlaybackState(state);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SMTC] update state failed: {ex.Message}");
        }
    }

    /// <summary>Called on the UI thread periodically (position is reflected where supported).</summary>
    public void UpdateTimeline(TimeSpan position, TimeSpan duration)
    {
        // TimelineProperties is not projected in this SDK surface; keep as safe no-op.
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Interlocked.Increment(ref _currentUpdateVersion);

        try
        {
            _playback.CurrentChanged -= OnPlaybackCurrentChanged;
            _playback.StateChanged -= OnPlaybackStateChanged;
        }
        catch { }

        if (_smtc != null)
        {
            try
            {
                _smtc.ButtonPressed -= OnButtonPressed;
                _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                _smtc.IsEnabled = false;
            }
            catch { }
            _smtc = null;
        }

        try
        {
            _mediaPlayer?.Dispose();
            _mediaPlayer = null;
        }
        catch { }

        _isInitialized = false;
    }
}
