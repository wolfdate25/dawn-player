using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// A reader whose source publishes live now-playing metadata (radio ICY today; YouTube's track
/// title later). The controller relays these to the UI without knowing the source protocol.
/// </summary>
public interface ILiveMetadataSource
{
    /// <summary>Station/source display name from the response headers (ICY <c>icy-name</c>);
    /// empty when the source did not announce one.</summary>
    string StationName { get; }

    /// <summary>Raised on the reader's fill thread whenever a new now-playing title is parsed.
    /// Handlers must not block.</summary>
    event Action<string>? StreamTitleChanged;
}

/// <summary>One live-metadata notification for a playlist item. Carried as a record so relays and
/// UI handlers cannot mix up the station and the song.</summary>
public readonly record struct LiveStreamMetadata(PlaylistItem Item, string StationName, string StreamTitle);
