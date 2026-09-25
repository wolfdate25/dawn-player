namespace DawnPlayer.Core.Models;

/// <summary>
/// One saved internet-radio station for the Network tab. Like radio tracks, stations never enter
/// the library database; this list is persisted on its own (<see cref="Persistence.RadioStationStore"/>).
/// </summary>
public sealed record RadioStation(
    string Name,
    string Url,
    string? Genre,
    long AddedUtcTicks,
    long? LastPlayedUtcTicks);
