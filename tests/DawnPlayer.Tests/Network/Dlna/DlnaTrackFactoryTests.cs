using DawnPlayer.Core.Models;
using DawnPlayer.Core.Network.Dlna;
using Xunit;

namespace DawnPlayer.Tests.Network.Dlna;

/// <summary>
/// Resource selection is where server quirks meet our decoder support: originals outrank
/// transcodes, non-audio resources are invisible, and the resulting Track must be immediately
/// playable through the N1 routing (Dlna kind → spool reader).
/// </summary>
public sealed class DlnaTrackFactoryTests
{
    private static readonly Uri Base = new("http://192.168.0.10:8200/rootDesc.xml");

    private static DidlItemEntry Item(params DidlResource[] resources) => new(
        "i1", "p", "Song Title", "object.item.audioItem.musicTrack",
        "Artist", "Album", "Genre", TimeSpan.FromSeconds(180), null, resources);

    private static DidlResource Res(string protocolInfo, string uri, long? size = null) =>
        new(new Uri(uri), protocolInfo, size, TimeSpan.FromSeconds(180), null);

    [Fact]
    public void NativeFlac_BeatsTranscodedLpcm_AndMimeWithParametersMatches()
    {
        var item = Item(
            Res("http-get:*:audio/L16:DLNA.ORG_PN=LPCM;DLNA.ORG_FLAGS=017000000000000000000000000000000", "http://h/stream.wav"),
            Res("http-get:*:audio/flac:DLNA.ORG_PN=FLAC_2_44100_16", "http://h/song.flac", 1_000_000),
            Res("http-get:*:audio/mpeg:DLNA.ORG_PN=MP3", "http://h/song.mp3", 500_000));

        var track = DlnaTrackFactory.TryCreate(item, Base);

        Assert.NotNull(track);
        Assert.Equal("http://h/song.flac", track!.Path);
        Assert.Equal("FLAC", track.Codec);
        Assert.Equal(TrackSourceKind.Dlna, track.SourceKind);
        Assert.Equal("Song Title", track.Title);
        Assert.Equal("Artist", track.Artist);
        Assert.Equal("Album", track.Album);
        Assert.Equal(180_000, track.DurationMs);
    }

    [Fact]
    public void AmongSameFormat_LargerResourceWins()
    {
        var item = Item(
            Res("http-get:*:audio/mpeg:*", "http://h/low.mp3", 96_000),
            Res("http-get:*:audio/mpeg:*", "http://h/high.mp3", 320_000));

        var track = DlnaTrackFactory.TryCreate(item, Base);

        Assert.Equal("http://h/high.mp3", track!.Path);
    }

    [Fact]
    public void VideoAndImageOnlyItems_YieldNull()
    {
        var item = Item(
            Res("http-get:*:video/mp4:*", "http://h/v.mp4"),
            Res("http-get:*:image/jpeg:*", "http://h/cover.jpg"),
            Res("http-get:*:audio/unknown-brand-new-codec:*", "http://h/x.xyz"));

        Assert.Null(DlnaTrackFactory.TryCreate(item, Base));
    }

    [Fact]
    public void CodecLabelAndExtension_ComeFromTheChosenResource()
    {
        var mp3Only = Item(Res("http-get:*:audio/mpeg:*", "http://h/a.mp3"));
        Assert.Equal("MP3", DlnaTrackFactory.TryCreate(mp3Only, Base)!.Codec);

        var wavOnly = Item(Res("http-get:*:audio/x-wav:*", "http://h/a.wav"));
        Assert.Equal("WAV", DlnaTrackFactory.TryCreate(wavOnly, Base)!.Codec);

        var lpcmOnly = Item(Res("http-get:*:audio/L16:*", "http://h/a.wav"));
        Assert.Equal("LPCM", DlnaTrackFactory.TryCreate(lpcmOnly, Base)!.Codec);
    }

    [Fact]
    public void EmptyTitle_StillProducesTrack_TitleFallsBackToPath()
    {
        var item = new DidlItemEntry("i1", "p", "", "object.item.audioItem", null, null, null, null, null,
            [Res("http-get:*:audio/flac:*", "http://h/song.flac")]);

        var track = DlnaTrackFactory.TryCreate(item, Base);

        // Empty DIDL titles happen; the playlist row should not be blank.
        Assert.Equal("http://h/song.flac", track!.Title);
    }
}
