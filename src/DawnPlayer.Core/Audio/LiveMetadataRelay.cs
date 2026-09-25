using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Wires a live-metadata reader to a per-item notification with a deterministic detach. Extracted
/// from the controller so the attach/detach contract is testable without an audio session.
/// </summary>
public static class LiveMetadataRelay
{
    /// <summary>
    /// Subscribes the source's title changes for one item and returns a detach action (idempotent;
    /// safe to call from any thread). <see cref="ILiveMetadataSource.StationName"/> is read at raise
    /// time, so a station name learned after attach still surfaces.
    /// </summary>
    public static Action Attach(ILiveMetadataSource source, PlaylistItem item, Action<LiveStreamMetadata> raise)
    {
        void OnTitle(string title) => raise(new LiveStreamMetadata(item, source.StationName, title));
        source.StreamTitleChanged += OnTitle;
        bool detached = false;
        return () =>
        {
            if (detached) return;
            detached = true;
            source.StreamTitleChanged -= OnTitle;
        };
    }
}
