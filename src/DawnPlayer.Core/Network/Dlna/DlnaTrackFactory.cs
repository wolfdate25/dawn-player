using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Network.Dlna;

/// <summary>
/// Maps a DIDL item to a playable <see cref="Track"/>: picks the best res by native-format
/// preference (the server's original encoding outranks anything it would transcode to), resolves
/// relative res URLs, and fills the display metadata. The remote art URL is carried in
/// <see cref="Track.ArtUrl"/> (persisted with the track, resolved lazily); the caller may resolve
/// it into a local ArtPath up front via <see cref="DlnaArtCache"/>.
/// </summary>
public static class DlnaTrackFactory
{
    /// <summary>Mime → (spool extension, codec label, preference). Higher preference wins; only
    /// formats the local reader chain actually decodes are listed.</summary>
    private static readonly (string Mime, string Extension, string Codec, int Preference)[] FormatTable =
    {
        ("audio/flac", ".flac", "FLAC", 100),
        ("audio/x-flac", ".flac", "FLAC", 100),
        ("audio/wav", ".wav", "WAV", 90),
        ("audio/x-wav", ".wav", "WAV", 90),
        ("audio/wave", ".wav", "WAV", 90),
        ("audio/alac", ".m4a", "ALAC", 80),
        ("audio/aac", ".m4a", "AAC", 70),
        ("audio/mp4", ".m4a", "AAC", 70),
        ("audio/mpeg", ".mp3", "MP3", 60),
        ("audio/ogg", ".ogg", "OGG", 50),
        ("audio/vorbis", ".ogg", "OGG", 50),
        // Server-transcoded LPCM: playable but never preferred over an original.
        ("audio/l16", ".wav", "LPCM", 10),
        ("audio/l8", ".wav", "LPCM", 10),
    };

    /// <summary>Returns null when none of the resources is an audio format we decode.</summary>
    public static Track? TryCreate(DidlItemEntry item, Uri baseUrl)
    {
        var best = item.Resources
            .Select(res => (Res: res, Match: MatchFormat(res.ProtocolInfo)))
            .Where(x => x.Match != null)
            .OrderByDescending(x => x.Match!.Value.Preference)
            .ThenByDescending(x => x.Res.SizeBytes ?? 0)
            .FirstOrDefault();
        if (best.Res is null) return null;

        // Rare servers emit res URLs relative to the description base; absolute ones pass through.
        if (!best.Res.Uri.IsAbsoluteUri) return null;
        var url = best.Res.Uri;

        var track = new Track
        {
            Path = url.AbsoluteUri,
            // Untitled DIDL items happen; a blank playlist row is worse than a URL.
            Title = string.IsNullOrWhiteSpace(item.Title) ? url.AbsoluteUri : item.Title,
            Artist = item.Artist ?? "",
            Album = item.Album ?? "",
            Genre = item.Genre ?? "",
            Codec = best.Match!.Value.Codec,
            DurationMs = (long)(item.Duration?.TotalMilliseconds ?? 0),
            SourceKind = TrackSourceKind.Dlna,
            ArtUrl = item.AlbumArtUri?.AbsoluteUri,
        };
        return track;
    }

    private static (string Extension, string Codec, int Preference)? MatchFormat(string protocolInfo)
    {
        // "http-get:*:audio/mpeg:DLNA.ORG_PN=MP3;..." — the mime is the third token.
        var parts = protocolInfo.Split(':');
        if (parts.Length < 3) return null;
        var mime = parts[2].Split(';')[0].Trim().ToLowerInvariant();
        if (mime.Length == 0) return null;

        foreach (var row in FormatTable)
        {
            if (string.Equals(row.Mime, mime, StringComparison.OrdinalIgnoreCase)) return (row.Extension, row.Codec, row.Preference);
        }
        return null;
    }
}
