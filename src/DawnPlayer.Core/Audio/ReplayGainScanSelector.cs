using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Target selection for ReplayGain scans (plan §12, I12): one filter for every entry point —
/// the automatic library pass keeps only tracks missing tags, rescan-all and explicit scoped
/// scans (album/track context menus) keep everything, and radio stream URLs never enter either
/// way (decoding a live stream would never finish).
/// </summary>
public static class ReplayGainScanSelector
{
    public static List<Track> Select(IEnumerable<Track> source, bool rescanAll)
    {
        return source
            .Where(t => rescanAll
                || !t.RgTrackGainDb.HasValue || !t.RgTrackPeak.HasValue
                || !t.RgAlbumGainDb.HasValue || !t.RgAlbumPeak.HasValue)
            .Where(t => !RadioTrack.IsStreamUrl(t.Path))
            .ToList();
    }
}
