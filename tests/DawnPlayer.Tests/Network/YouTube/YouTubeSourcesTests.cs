using DawnPlayer.Core.Network.YouTube;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.Network.YouTube;

/// <summary>
/// Page-URL gate: every YouTube track starts from a page URL the app can actually play. Canonical
/// shapes normalize to a single watch URL (so the same video is the same track), volatile
/// parameters are dropped, and non-video inputs are rejected before they can become a dead row.
/// </summary>
public sealed class YouTubePageUrlTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ",
                "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ",
                "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ",
                "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ",
                "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ",
                "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ",
                "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    public void AcceptedShapes_NormalizeToCanonicalWatchUrl(string input, string expected)
    {
        Assert.Equal(expected, YouTubePageUrl.TryNormalize(input));
    }

    [Fact]
    public void VolatileParameters_AreDropped()
    {
        // list=/t=/start= do not change what should play; the canonical form drops them so a
        // restored playlist entry matches a freshly added one.
        var normalized = YouTubePageUrl.TryNormalize(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL123&t=42s");
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://www.youtube.com/watch?v=dQw4w9WgXcQ")]   // non-http scheme
    [InlineData("https://vimeo.com/12345")]                     // foreign host
    [InlineData("https://www.youtube.com/playlist?list=PL123")] // not a video page
    [InlineData("https://www.youtube.com/watch")]               // no v= parameter
    [InlineData("https://www.youtube.com/watch?v=")]            // empty v=
    public void NonVideoInputs_AreRejected(string? input)
    {
        Assert.Null(YouTubePageUrl.TryNormalize(input));
    }
}

/// <summary>Parses yt-dlp -J JSON; malformed payloads must degrade to null, never throw, and a
/// missing/zero duration must present as live (0 ms) rather than a bogus length.</summary>
public sealed class YouTubeJsonParserTests
{
    [Fact]
    public void ValidPayload_ParsesTitleUploaderDurationThumbnail()
    {
        const string json = """
            {
              "title": "Me at the zoo",
              "uploader": "jawed",
              "duration": 19.2,
              "thumbnail": "https://i.ytimg.com/vi/x/maxresdefault.jpg",
              "formats": [ { "format_id": "140" } ]
            }
            """;

        var meta = YouTubeJsonParser.Parse(json);

        Assert.NotNull(meta);
        Assert.Equal("Me at the zoo", meta!.Title);
        Assert.Equal("jawed", meta.Uploader);
        Assert.Equal(19_200, meta.DurationMs);
        Assert.Equal("https://i.ytimg.com/vi/x/maxresdefault.jpg", meta.ThumbnailUrl);
    }

    [Fact]
    public void MissingFields_DecodeAsEmptyAndLive()
    {
        var meta = YouTubeJsonParser.Parse("{}");

        Assert.NotNull(meta);
        Assert.Equal("", meta!.Title);
        Assert.Equal("", meta.Uploader);
        Assert.Equal(0, meta.DurationMs); // live/unknown — reader switches to radio semantics
        Assert.Null(meta.ThumbnailUrl);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]      // valid JSON, wrong shape
    [InlineData("")]
    [InlineData("   ")]
    public void MalformedPayloads_YieldNull_NeverThrow(string? json)
    {
        Assert.Null(YouTubeJsonParser.Parse(json));
        Assert.Null(YouTubeJsonParser.Parse(null));
    }
}

/// <summary>The tool-path override settings survive a settings round-trip (the store's options
/// only affect formatting, so plain JsonSerializer round-trips the same payload); empty strings
/// (PATH default) survive as empty.</summary>
public sealed class YouTubeSettingsRoundTripTests
{
    private static AppSettings RoundTrip(AppSettings settings)
    {
        var json = SettingsStore.Serialize(settings);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
    }

    [Fact]
    public void OverriddenPaths_SurviveSerializeAndLoad()
    {
        var settings = new AppSettings();
        settings.YouTube.YtDlpPath = @"C:\tools\yt-dlp.exe";
        settings.YouTube.FfmpegPath = @"D:\bin\ffmpeg.exe";

        var loaded = RoundTrip(settings);

        Assert.Equal(@"C:\tools\yt-dlp.exe", loaded.YouTube.YtDlpPath);
        Assert.Equal(@"D:\bin\ffmpeg.exe", loaded.YouTube.FfmpegPath);
    }

    [Fact]
    public void EmptyPaths_Defaults_ArePreserved()
    {
        var loaded = RoundTrip(new AppSettings());

        Assert.Equal("", loaded.YouTube.YtDlpPath);
        Assert.Equal("", loaded.YouTube.FfmpegPath);
    }
}

/// <summary>
/// The resolve cache's contract: a section-initiated -J result is reused verbatim by the
/// reader's Open (no second yt-dlp spawn), entries are keyed by page URL, stale entries expire,
/// and the cache stays bounded.
/// </summary>
public sealed class YouTubeResolveCacheTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private static YouTubeTrackMeta Meta(string title) => new(title, "Uploader", 19_000, null);

    [Fact]
    public void PutThenGet_ReturnsTheSameMeta_ForTheSameUrl()
    {
        YouTubeResolveCache.Clear();
        YouTubeResolveCache.Put("https://www.youtube.com/watch?v=a", Meta("A"), Now);

        Assert.Equal("A", YouTubeResolveCache.Get("https://www.youtube.com/watch?v=a", Now)?.Title);
        YouTubeResolveCache.Clear();
    }

    [Fact]
    public void DifferentUrl_IsAMiss()
    {
        YouTubeResolveCache.Clear();
        YouTubeResolveCache.Put("https://www.youtube.com/watch?v=a", Meta("A"), Now);

        Assert.Null(YouTubeResolveCache.Get("https://www.youtube.com/watch?v=b", Now));
        YouTubeResolveCache.Clear();
    }

    [Fact]
    public void StaleEntry_Expires()
    {
        YouTubeResolveCache.Clear();
        YouTubeResolveCache.Put("https://www.youtube.com/watch?v=a", Meta("A"), Now);

        Assert.Null(YouTubeResolveCache.Get("https://www.youtube.com/watch?v=a", Now + TimeSpan.FromMinutes(16)));
        YouTubeResolveCache.Clear();
    }

    [Fact]
    public void Overflow_ClearsTheCache_Bounded()
    {
        YouTubeResolveCache.Clear();
        for (var i = 0; i < 40; i++)
        {
            YouTubeResolveCache.Put($"https://www.youtube.com/watch?v=v{i}", Meta($"M{i}"), Now);
        }

        // Beyond the cap the cache resets instead of growing — the next open re-resolves.
        Assert.Null(YouTubeResolveCache.Get("https://www.youtube.com/watch?v=v0", Now));
        YouTubeResolveCache.Clear();
    }
}
