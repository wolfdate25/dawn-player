using DawnPlayer.App.Views.Network;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests.Views;

/// <summary>
/// The station→track mapping is what the playlist row shows and what the reader will open; both
/// halves (name for display, URL for playback) are pinned here.
/// </summary>
public sealed class RadioStationCommandsTests
{
    [Fact]
    public void ToTrack_UsesStationNameAsTitleAndUrlAsPath()
    {
        var station = new RadioStation("Jazz Radio", "http://radio.example/jazz", null, 1, null);

        var track = RadioStationCommands.ToTrack(station);

        Assert.Equal("http://radio.example/jazz", track.Path);
        Assert.Equal("Jazz Radio", track.Title);
        Assert.Equal("Radio", track.Codec);
        Assert.True(RadioTrack.IsStreamUrl(track.Path));
    }

    [Fact]
    public void ToTrack_BlankName_FallsBackToUrl()
    {
        var station = new RadioStation("  ", "http://radio.example/x", null, 1, null);

        var track = RadioStationCommands.ToTrack(station);

        Assert.Equal(station.Url, track.Title);
    }
}
