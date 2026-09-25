using DawnPlayer.Core.Models;

namespace DawnPlayer.App.Views.Network;

/// <summary>
/// WinUI-free logic for the radio section so it can be linked into the test project.
/// </summary>
public static class RadioStationCommands
{
    /// <summary>
    /// A station becomes a normal radio track; the playlist shows the station's name instead of
    /// its URL (the URL stays in <see cref="Track.Path"/>, which is what the reader opens).
    /// </summary>
    public static Track ToTrack(RadioStation station) =>
        Core.Audio.RadioTrack.Create(station.Url) with
        {
            Title = string.IsNullOrWhiteSpace(station.Name) ? station.Url : station.Name
        };
}
