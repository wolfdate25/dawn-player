using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// The source-kind routing rules of the reader factory. The compatibility invariants under test:
/// a bare http URL with no kind keeps behaving like live radio (today's playlists), DLNA is
/// pinned to the spooling reader, YouTube to the pipe reader — and local files ignore the kind.
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

    [Fact]
    public void DlnaKind_RoutesToSpoolingReader()
    {
        Assert.IsType<HttpFileTrackReaderProvider>(
            AudioFileReaderFactory.SelectProvider(Url, ".flac", TrackSourceKind.Dlna));
    }

    [Fact]
    public void YouTubeKind_RoutesToPipeReader()
    {
        // N3: the YouTube kind left the spooling reader for the yt-dlp/ffmpeg pipe reader
        // (approved spec change — the spool path cannot interpret a page URL at all).
        Assert.IsType<YouTubeTrackReaderProvider>(
            AudioFileReaderFactory.SelectProvider("https://www.youtube.com/watch?v=abc", ".flac", TrackSourceKind.YouTube));
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
