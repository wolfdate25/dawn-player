using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Persistence;

/// <summary>
/// Rebuilds a remote track from its <c>#DPTRACK</c> payload. Pure mapping, kept beside the
/// directive types so the round-trip contract (write → save → load) is testable end to end
/// without a playlist manager.
/// </summary>
public static class RemoteTrackCodec
{
    /// <summary>
    /// A radio directive reuses the radio factory (live semantics); any other remote kind becomes
    /// a finite HTTP track routed by <see cref="Audio.AudioFileReaderFactory"/> at open time.
    /// Returns null for a directive sitting on a local path (stale) so the caller can fall
    /// through to ordinary file resolution; unknown kind values degrade to the URL heuristic so a
    /// future directive never breaks an older load.
    /// </summary>
    public static Track? ToTrack(string path, DpTrackMeta meta)
    {
        if (!Audio.RadioTrack.IsStreamUrl(path)) return null;

        Track track;
        if (meta.SourceKind == (int)TrackSourceKind.Radio || !Enum.IsDefined(typeof(TrackSourceKind), meta.SourceKind))
        {
            track = Audio.RadioTrack.Create(path);
        }
        else
        {
            track = new Track { Path = path, Codec = "HTTP" };
            track.SourceKind = (TrackSourceKind)meta.SourceKind;
        }

        if (!string.IsNullOrWhiteSpace(meta.Title)) track.Title = meta.Title;
        if (!string.IsNullOrWhiteSpace(meta.Artist)) track.Artist = meta.Artist;
        if (!string.IsNullOrWhiteSpace(meta.Album)) track.Album = meta.Album;
        if (meta.DurationSeconds is > 0) track.DurationMs = (long)(meta.DurationSeconds.Value * 1000);
        // Restored art URLs are re-validated against the schemes the art cache can actually fetch:
        // a playlist file is user-editable, so "file://"-style payloads must not reach the loader.
        track.ArtUrl = ValidateRemoteArtUrl(meta.ArtUrl);
        return track;
    }

    private static string? ValidateRemoteArtUrl(string? artUrl)
    {
        if (string.IsNullOrWhiteSpace(artUrl)) return null;
        if (!Uri.TryCreate(artUrl, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }
}
