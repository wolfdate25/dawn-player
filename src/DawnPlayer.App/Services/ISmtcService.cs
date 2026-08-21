using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;

namespace DawnPlayer.App.Services;

/// <summary>
/// Service contract for Windows System Media Transport Controls (SMTC).
/// Manages OS media overlay, hardware media keys, and lock screen metadata/controls.
/// </summary>
public interface ISmtcService : IDisposable
{
    /// <summary>Gets whether the SMTC interop was successfully initialized for the main window handle.</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Attempts to initialize the SMTC instance with the native window handle.
    /// </summary>
    /// <param name="hwnd">Native Win32 HWND of the application window.</param>
    /// <returns>True if initialization succeeded; otherwise false.</returns>
    bool TryInitialize(IntPtr hwnd);

    /// <summary>
    /// Synchronously updates the SMTC display updater with the specified playlist item.
    /// </summary>
    /// <param name="item">The playlist item to display, or null to clear metadata.</param>
    void UpdateTrack(PlaylistItem? item);

    /// <summary>
    /// Asynchronously updates the SMTC display updater with metadata and thumbnail art.
    /// Guarded by sequence counters to ensure out-of-order async completions do not overwrite newer state.
    /// </summary>
    /// <param name="item">The playlist item to display, or null to clear metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateTrackAsync(PlaylistItem? item, CancellationToken ct = default);

    /// <summary>
    /// Updates the SMTC playback status (Playing, Paused, Stopped).
    /// </summary>
    /// <param name="state">Current playback state.</param>
    void UpdateState(PlaybackState state);

    /// <summary>
    /// Updates the playback timeline position and total duration where supported.
    /// </summary>
    /// <param name="position">Current playback position.</param>
    /// <param name="duration">Total track duration.</param>
    void UpdateTimeline(TimeSpan position, TimeSpan duration);
}
