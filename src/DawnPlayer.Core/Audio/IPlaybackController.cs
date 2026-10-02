using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Orchestrates audio decoding, gapless sequencing, audio device sessions,
/// playback queue management, and playback history.
/// </summary>
public interface IPlaybackController : IDisposable
{
    PlaybackState State { get; }
    /// <summary>Buffering feedback: an open in flight from a non-playing state, or a live stream
    /// underrun (silence served while the source is alive). Poll-safe; pairs with StateChanged.</summary>
    bool IsBuffering { get; }
    IPlaybackQueue Queue { get; }
    bool StopAfterCurrent { get; set; }
    PlaylistItem? CurrentItem { get; }
    Playlist? CurrentPlaylist { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    double Volume { get; set; }
    bool IsExclusiveSession { get; }
    SessionInfo? CurrentSessionInfo { get; }

    event Action<PlaylistItem?>? CurrentChanged;
    event Action? StateChanged;
    event Action? StopAfterCurrentChanged;
    event Action<string>? Warning;
    event Action<SessionInfo>? SessionStarted;

    Task PlayAsync(Playlist playlist, PlaylistItem item);
    void PlayPause();
    void Stop();
    /// <summary>Cancels an open in flight and lands Paused/Stopped without resuming anything —
    /// the "want silence now" primitive (sleep-timer expiry). No-op when not buffering.</summary>
    void CancelPendingOpen();
    Task NextAsync();
    Task PreviousAsync();
    void Seek(TimeSpan position);
    void ApplyEqualizer();
    void ApplyNormalizer();
    void ApplySpatial();
    bool TryCopySpectrumWindow(float[] destination, out int sampleRate, out long version);
    void RestartIfPlaying();
}
