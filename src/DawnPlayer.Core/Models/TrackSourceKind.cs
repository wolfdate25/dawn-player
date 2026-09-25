namespace DawnPlayer.Core.Models;

/// <summary>
/// Where a track's bytes come from. Local files need no marker (the default); remote kinds decide
/// which reader chain opens them and which subsystems (scanner, stats, scrobbling) skip them.
/// Persisted as the JSON payload of the M3U8 <c>#DPTRACK</c> directive — keep the numeric values
/// stable, and treat unknown values as <see cref="File"/> on load.
/// </summary>
public enum TrackSourceKind
{
    File = 0,
    Radio = 1,
    Dlna = 2,
    YouTube = 3,
}
