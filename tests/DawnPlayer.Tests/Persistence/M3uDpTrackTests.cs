using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.Persistence;

/// <summary>
/// The <c>#DPTRACK</c> directive round-trip: remote tracks must survive save→load with their kind
/// and display metadata, local tracks must not grow a directive, and a directive written by
/// anything else (corrupt, future, misapplied) must degrade to the legacy plain-URL behavior.
/// </summary>
public sealed class M3uDpTrackTests : IDisposable
{
    private readonly string _dir;

    public M3uDpTrackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DawnPlayerDpTrackTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteAndReadBack(params PlaylistItem[] items)
    {
        var file = Path.Combine(_dir, "pl.m3u8");
        M3u.Write(file, items, "test");
        return file;
    }

    [Fact]
    public void RemoteTracks_RoundTripWithKindAndMetadata()
    {
        var radio = new PlaylistItem(RadioRoundTrip("http://radio.example/jazz", "Jazz FM"));
        var dlna = new PlaylistItem(new Track
        {
            Path = "http://nas.example/audio/song.flac",
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            DurationMs = 213_000,
            SourceKind = TrackSourceKind.Dlna,
        });

        var file = WriteAndReadBack(radio, dlna);
        var entries = M3u.Read(file);

        Assert.Equal(2, entries.Count);

        var radioMeta = DpTrackMeta.TryDecode(entries[0].DpTrackDirective);
        Assert.NotNull(radioMeta);
        Assert.Equal((int)TrackSourceKind.Radio, radioMeta!.SourceKind);
        Assert.Equal("Jazz FM", radioMeta.Title);

        var radioTrack = RemoteTrackCodec.ToTrack(entries[0].Path, radioMeta);
        Assert.NotNull(radioTrack);
        Assert.Equal(TrackSourceKind.Radio, radioTrack!.SourceKind);
        Assert.Equal("Jazz FM", radioTrack.Title);
        Assert.Equal("http://radio.example/jazz", radioTrack.Path);

        var dlnaMeta = DpTrackMeta.TryDecode(entries[1].DpTrackDirective);
        Assert.NotNull(dlnaMeta);
        var dlnaTrack = RemoteTrackCodec.ToTrack(entries[1].Path, dlnaMeta!);
        Assert.Equal(TrackSourceKind.Dlna, dlnaTrack!.SourceKind);
        Assert.Equal("Artist", dlnaTrack.Artist);
        Assert.Equal("Album", dlnaTrack.Album);
        Assert.Equal(TimeSpan.FromSeconds(213), dlnaTrack.Duration);
    }

    [Fact]
    public void DlnaArtUrl_SurvivesRoundTrip_AndRestoresToTrack()
    {
        var dlna = new PlaylistItem(new Track
        {
            Path = "http://nas.example/audio/song.flac",
            Title = "Song",
            SourceKind = TrackSourceKind.Dlna,
            ArtUrl = "http://nas.example/art/cover.jpg",
        });

        var file = WriteAndReadBack(dlna);
        var meta = DpTrackMeta.TryDecode(M3u.Read(file)[0].DpTrackDirective);

        Assert.NotNull(meta);
        Assert.Equal("http://nas.example/art/cover.jpg", meta!.ArtUrl);

        var track = RemoteTrackCodec.ToTrack("http://nas.example/audio/song.flac", meta!);
        Assert.NotNull(track);
        Assert.Equal("http://nas.example/art/cover.jpg", track!.ArtUrl);
    }

    [Theory]
    [InlineData("file:///C:/Users/x/cover.jpg")]    // a playlist file is user-editable; local-resource
    [InlineData("ftp://nas.example/cover.jpg")]     // schemes must never reach the art loader
    [InlineData("not an absolute uri")]
    [InlineData("")]
    public void RemoteArtUrl_NonHttpSchemes_AreDroppedOnRestore(string artUrl)
    {
        var meta = new DpTrackMeta((int)TrackSourceKind.Dlna, "T", null, null, null, artUrl);

        var track = RemoteTrackCodec.ToTrack("http://nas.example/audio/song.flac", meta);

        Assert.NotNull(track);
        Assert.Null(track!.ArtUrl);
    }

    [Fact]
    public void YouTubeTrack_RoundTripsWithKind()
    {
        // N3: a YouTube track persists its page URL (never the expiring media URL) and comes back
        // with the kind intact so open-time routing reaches the pipe reader.
        const string pageUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
        var yt = new PlaylistItem(new Track
        {
            Path = pageUrl,
            Title = pageUrl,
            SourceKind = TrackSourceKind.YouTube,
        });

        var file = WriteAndReadBack(yt);
        var meta = DpTrackMeta.TryDecode(M3u.Read(file)[0].DpTrackDirective);

        Assert.NotNull(meta);
        Assert.Equal((int)TrackSourceKind.YouTube, meta!.SourceKind);
        Assert.Equal(pageUrl, meta.Title);

        var track = RemoteTrackCodec.ToTrack(pageUrl, meta!);
        Assert.NotNull(track);
        Assert.Equal(TrackSourceKind.YouTube, track!.SourceKind);
        Assert.Equal(pageUrl, track.Path);
    }

    [Fact]
    public void LocalTracks_DoNotGrowADirective()
    {
        var local = new PlaylistItem(new Track { Path = Path.Combine(_dir, "a.flac"), Title = "A" });

        var file = WriteAndReadBack(local);

        var content = File.ReadAllText(file);
        Assert.DoesNotContain("#DPTRACK", content, StringComparison.Ordinal);
        Assert.Null(M3u.Read(file)[0].DpTrackDirective);
    }

    [Fact]
    public void LegacyPlaylist_PlainUrlWithoutDirective_LoadsAsRadio()
    {
        var file = Path.Combine(_dir, "legacy.m3u8");
        File.WriteAllLines(file, new[]
        {
            "#EXTM3U",
            "#EXTINF:-1,Some Stream",
            "http://stream.example.com:8000/live",
        });

        var entry = Assert.Single(M3u.Read(file));
        Assert.Null(entry.DpTrackDirective);
        // The playlist manager's fallback path: bare stream URL → radio factory.
        var track = DawnPlayer.Core.Audio.RadioTrack.Create(entry.Path);
        Assert.Equal(TrackSourceKind.Radio, track.SourceKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%7Bcorrupt-json")]                 // not valid JSON after unescaping
    [InlineData("%7B%22SourceKind%22%3A0%7D")]      // {"SourceKind":0} — file kind has no directive meaning
    public void TryDecode_InvalidPayloads_ReturnNull(string payload)
    {
        Assert.Null(DpTrackMeta.TryDecode(payload));
    }

    [Fact]
    public void TryDecode_FutureUnknownKind_StillDecodes_AndCodecFallsBackToRadio()
    {
        // A directive from a newer build (kind 99) must load, degrading to radio semantics.
        var future = new DpTrackMeta(99, "Future", null, null, null, null);
        var decoded = DpTrackMeta.TryDecode(future.Encode());

        Assert.NotNull(decoded);
        var track = RemoteTrackCodec.ToTrack("http://x.example/y", decoded!);
        Assert.Equal(TrackSourceKind.Radio, track!.SourceKind);
        Assert.Equal("Future", track.Title);
    }

    [Fact]
    public void DirectiveOnLocalPath_IsIgnored()
    {
        var meta = new DpTrackMeta((int)TrackSourceKind.Dlna, "T", null, null, null, null);

        Assert.Null(RemoteTrackCodec.ToTrack(@"C:\music\a.flac", meta));
    }

    [Fact]
    public void UnicodeMetadata_SurvivesRoundTrip()
    {
        var meta = new DpTrackMeta((int)TrackSourceKind.Dlna, "곡 제목 — 별명", "아티스트", "앨범", 61.5, null);

        var decoded = DpTrackMeta.TryDecode(meta.Encode());

        Assert.Equal(meta, decoded);
    }

    private static Track RadioRoundTrip(string url, string title) =>
        DawnPlayer.Core.Audio.RadioTrack.Create(url) with { Title = title };
}
