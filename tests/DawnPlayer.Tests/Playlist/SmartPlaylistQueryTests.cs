using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Smart playlist query language: parsing (valid queries, error messages for broken ones) and
/// evaluation semantics (string/numeric/date comparisons, MISSING/PRESENT, boolean precedence,
/// temporal DURING LAST, LIMIT).
/// </summary>
public sealed class SmartPlaylistQueryTests
{
    private static Track Track(
        string path = "a",
        string title = "",
        string artist = "",
        string album = "",
        string genre = "",
        int rating = 0,
        int playCount = 0,
        long lastPlayed = 0,
        long firstSeen = 0,
        int year = 0,
        int discNo = 1,
        int trackNo = 1) => new()
    {
        Path = path,
        Title = title,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        Genre = genre,
        Rating = rating,
        PlayCount = playCount,
        LastPlayedUtcTicks = lastPlayed,
        FirstSeenUtcTicks = firstSeen,
        Year = year,
        DiscNo = discNo,
        TrackNo = trackNo,
    };

    private static IReadOnlyList<string> Apply(string query, params Track[] tracks)
    {
        Assert.True(SmartPlaylistQuery.TryParse(query, out var parsed, out var error), error);
        return parsed!.Apply(tracks).Select(t => t.Path).ToList();
    }

    // ---------------- text comparisons ----------------

    [Fact]
    public void Is_MatchesWholeString_CaseInsensitively()
    {
        var tracks = new[]
        {
            Track(path: "exact", artist: "Radiohead"),
            Track(path: "case", artist: "RADIOHEAD"),
            Track(path: "substr", artist: "Radiohead tribute"),
            Track(path: "other", artist: "Miles Davis"),
        };

        Assert.Equal(new[] { "exact", "case" }, Apply("%artist% IS radiohead", tracks));
    }

    [Fact]
    public void Has_MatchesSubstring()
    {
        var tracks = new[]
        {
            Track(path: "yes", genre: "Cool Jazz"),
            Track(path: "no", genre: "Rock"),
        };

        Assert.Equal(new[] { "yes" }, Apply("%genre% HAS jazz", tracks));
    }

    [Fact]
    public void QuotedValue_CanContainSpaces_AndNotIs_Excludes()
    {
        var tracks = new[]
        {
            Track(path: "a", album: "Kind of Blue"),
            Track(path: "b", album: "Bitches Brew"),
        };

        Assert.Equal(new[] { "b" }, Apply("%album% != \"Kind of Blue\"", tracks));
    }

    // ---------------- numeric comparisons ----------------

    [Fact]
    public void NumericComparisons_WorkOnRatingAndPlayCount()
    {
        var tracks = new[]
        {
            Track(path: "hi", rating: 5, playCount: 10),
            Track(path: "mid", rating: 3, playCount: 4),
            Track(path: "lo", rating: 1, playCount: 0),
            Track(path: "none", rating: 0, playCount: 0),
        };

        Assert.Equal(new[] { "hi", "mid" }, Apply("%rating% GREATER 2", tracks));
        Assert.Equal(new[] { "hi" }, Apply("%rating% >= 5", tracks));
        Assert.Equal(new[] { "hi" }, Apply("%play_count% > 4", tracks));
        // Unrated (0) is "missing" for the rating field: only != matches it.
        Assert.Equal(new[] { "mid", "lo", "none" }, Apply("%rating% != 5", tracks));
    }

    // ---------------- presence ----------------

    [Fact]
    public void Missing_And_Present_TestFieldPresence()
    {
        var neverPlayed = Track(path: "never", lastPlayed: 0);
        var played = Track(path: "played", lastPlayed: DateTime.UtcNow.Ticks);
        var unrated = Track(path: "unrated", rating: 0);
        var rated = Track(path: "rated", rating: 4);

        Assert.Equal(new[] { "never" }, Apply("%last_played% MISSING", neverPlayed, played));
        Assert.Equal(new[] { "rated" }, Apply("%rating% PRESENT", unrated, rated));
    }

    // ---------------- temporal ----------------

    [Fact]
    public void DuringLast_MatchesRecentButNotOldOrNever()
    {
        long days(int d) => (DateTime.UtcNow - TimeSpan.FromDays(d)).Ticks;
        var fresh = Track(path: "fresh", lastPlayed: days(3));
        var stale = Track(path: "stale", lastPlayed: days(45));
        var never = Track(path: "never", lastPlayed: 0);

        Assert.Equal(new[] { "fresh" }, Apply("%last_played% DURING LAST 30 DAYS", fresh, stale, never));
        Assert.Equal(new[] { "fresh", "stale" }, Apply("%last_played% DURING LAST 2 MONTHS", fresh, stale, never));
    }

    // ---------------- boolean logic ----------------

    [Fact]
    public void AndBindsTighterThanOr_AndParenthesesOverride()
    {
        var a = Track(path: "a", rating: 5, genre: "Rock");
        var b = Track(path: "b", rating: 1, genre: "Jazz");
        var c = Track(path: "c", rating: 3, genre: "Jazz");

        // a OR (b AND jazz): b itself is rating<2 + jazz, so the OR keeps a and b; c stays out.
        Assert.Equal(new[] { "a", "b" }, Apply("%rating% GREATER 4 OR %rating% LESS 2 AND %genre% IS Jazz", a, b, c));
        Assert.Equal(new[] { "b", "c" }, Apply("(%rating% GREATER 4 OR %rating% LESS 5) AND %genre% IS Jazz", a, b, c));
    }

    [Fact]
    public void Not_NegatesFollowingExpression()
    {
        var rock = Track(path: "rock", genre: "Rock");
        var jazz = Track(path: "jazz", genre: "Jazz");

        Assert.Equal(new[] { "jazz" }, Apply("NOT %genre% IS Rock", rock, jazz));
    }

    // ---------------- LIMIT ----------------

    [Fact]
    public void Limit_CapsResultSet_PreservingSourceOrder()
    {
        var tracks = new[]
        {
            Track(path: "a", playCount: 1),
            Track(path: "b", playCount: 2),
            Track(path: "c", playCount: 3),
        };

        Assert.Equal(new[] { "a", "b" }, Apply("%play_count% GREATER 0 LIMIT 2", tracks));
    }

    // ---------------- parse errors ----------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%nosuchfield% IS x")]
    [InlineData("%artist%")]
    [InlineData("%artist% IS")]
    [InlineData("%artist% IS \"unterminated")]
    [InlineData("(%artist% IS x")]
    [InlineData("%artist% IS x)")]
    [InlineData("%title% GREATER 5")]           // text field with ordering operator
    [InlineData("%rating% DURING LAST 3 DAYS")]  // DURING on a non-date field
    [InlineData("%last_played% DURING LAST x DAYS")]
    [InlineData("%last_played% DURING LAST 3 FORTNIGHTS")]
    [InlineData("%rating% GREATER 3 LIMIT 0")]
    [InlineData("%rating% GREATER 3 LIMIT abc")]
    [InlineData("%rating% GREATER 3 TRAILING")]
    public void MalformedQueries_FailToParse(string query)
    {
        Assert.False(SmartPlaylistQuery.TryParse(query, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void BareFieldName_WithoutPercentSigns_Works()
    {
        var t = Track(path: "a", rating: 4);
        Assert.Equal(new[] { "a" }, Apply("rating GREATER 3", t));
    }
}
