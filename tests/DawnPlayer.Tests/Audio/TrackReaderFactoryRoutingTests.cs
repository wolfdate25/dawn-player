using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// The source-kind routing rules of the reader factory. The compatibility invariant under test:
/// a bare http URL with no kind keeps behaving like live radio (today's playlists), while
/// file-over-http kinds are pinned to the spooling reader — and local files ignore the kind.
/// </summary>
public sealed class TrackReaderFactoryRoutingTests
{
    private const string Url = "https://media.example/foo.flac";

    [Fact]
    public void RadioKind_RoutesToLiveStreamReader()
    {
        Assert.IsType<RadioStreamTrackReaderProvider>(
            AudioFileReaderFactory.SelectProvider(Url, ".flac", TrackSourceKind.Radio));
    }

    [Fact]
    public void LegacyFileKind_OnHttpUrl_StillRoutesToRadio()
    {
        // This is how existing M3U8 playlists (URL without #DPTRACK) and the pre-N1 code behave.
        Assert.IsType<RadioStreamTrackReaderProvider>(
            AudioFileReaderFactory.SelectProvider(Url, ".flac", TrackSourceKind.File));
    }

    [Theory]
    [InlineData(TrackSourceKind.Dlna)]
    [InlineData(TrackSourceKind.YouTube)]
    public void FileOverHttpKinds_RouteToSpoolingReader(TrackSourceKind kind)
    {
        Assert.IsType<HttpFileTrackReaderProvider>(
            AudioFileReaderFactory.SelectProvider(Url, ".flac", kind));
    }

    [Fact]
    public void RadioKind_OnLocalPath_IgnoresKindAndUsesLocalChain()
    {
        // Kind must never reroute a local file; the radio provider only claims URLs.
        Assert.IsType<MfTrackReaderProvider>(
            AudioFileReaderFactory.SelectProvider(@"C:\music\a.flac", ".flac", TrackSourceKind.Radio));
    }

    [Fact]
    public void LocalOgg_KeepsVorbisProvider()
    {
        Assert.IsType<VorbisTrackReaderProvider>(
            AudioFileReaderFactory.SelectProvider(@"C:\music\a.ogg", ".ogg", TrackSourceKind.File));
    }
}
