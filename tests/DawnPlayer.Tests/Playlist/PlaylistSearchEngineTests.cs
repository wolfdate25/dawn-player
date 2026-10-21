using System;
using System.Collections.Generic;
using DawnPlayer.App.Views;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests.PlaylistTests;

public class PlaylistSearchEngineTests
{
    private static PlaylistItem MakeItem(string title, string artist, string album, string path = "test.mp3")
    {
        var track = new Track
        {
            Title = title,
            Artist = artist,
            Album = album,
            Path = path
        };
        return new PlaylistItem(track);
    }

    [Fact]
    public void Search_MatchesTitleArtistAlbum_CaseInsensitive()
    {
        var items = new List<PlaylistItem>
        {
            MakeItem("Celebrity", "IU", "LILAC"),
            MakeItem("Dynamite", "BTS", "BE"),
            MakeItem("Lilac", "IU", "LILAC"),
            MakeItem("Butter", "BTS", "Butter")
        };

        // Title match (case-insensitive)
        var matchesTitle = PlaylistSearchEngine.FindMatches(items, "celebrity");
        Assert.Single(matchesTitle);
        Assert.Equal(0, matchesTitle[0]);

        // Artist match
        var matchesArtist = PlaylistSearchEngine.FindMatches(items, "iu");
        Assert.Equal(2, matchesArtist.Count);
        Assert.Equal(new[] { 0, 2 }, matchesArtist);

        // Album match
        var matchesAlbum = PlaylistSearchEngine.FindMatches(items, "lilac");
        Assert.Equal(2, matchesAlbum.Count);
        Assert.Equal(new[] { 0, 2 }, matchesAlbum);
    }

    [Fact]
    public void Search_MultiTokenAndQuery()
    {
        var items = new List<PlaylistItem>
        {
            MakeItem("Celebrity", "IU", "LILAC"),
            MakeItem("Celebrity", "Another Artist", "Single"),
            MakeItem("Coin", "IU", "LILAC")
        };

        // "iu celeb" should only match items having both tokens
        var matches = PlaylistSearchEngine.FindMatches(items, "iu celeb");
        Assert.Single(matches);
        Assert.Equal(0, matches[0]);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        var items = new List<PlaylistItem>
        {
            MakeItem("Title", "Artist", "Album")
        };

        Assert.Empty(PlaylistSearchEngine.FindMatches(items, ""));
        Assert.Empty(PlaylistSearchEngine.FindMatches(items, "   "));
        Assert.Empty(PlaylistSearchEngine.FindMatches(null, "test"));
        Assert.Empty(PlaylistSearchEngine.FindMatches(new List<PlaylistItem>(), "test"));
    }

    [Fact]
    public void Search_SanitizesSpecialCharacters_NoRegexCrash()
    {
        var items = new List<PlaylistItem>
        {
            MakeItem("Track (Remix) [feat. Singer]", "Artist", "Album * Special ?"),
            MakeItem("Normal Track", "Artist", "Album")
        };

        // Special regex characters should be treated as plain literals without exception
        var matchesParen = PlaylistSearchEngine.FindMatches(items, "(Remix)");
        Assert.Single(matchesParen);
        Assert.Equal(0, matchesParen[0]);

        var matchesBracket = PlaylistSearchEngine.FindMatches(items, "[feat.");
        Assert.Single(matchesBracket);
        Assert.Equal(0, matchesBracket[0]);

        var matchesAsterisk = PlaylistSearchEngine.FindMatches(items, "* Special");
        Assert.Single(matchesAsterisk);
        Assert.Equal(0, matchesAsterisk[0]);
    }

    [Fact]
    public void Search_NextPrevNavigation_WrapAround()
    {
        var matches = new List<int> { 2, 5, 8 };

        // Next navigation
        Assert.Equal(2, PlaylistSearchEngine.GetNextMatchIndex(matches, 0)); // Before first
        Assert.Equal(5, PlaylistSearchEngine.GetNextMatchIndex(matches, 2)); // Exact first -> second
        Assert.Equal(5, PlaylistSearchEngine.GetNextMatchIndex(matches, 3)); // Between 1 and 2
        Assert.Equal(8, PlaylistSearchEngine.GetNextMatchIndex(matches, 5)); // Exact second -> third
        Assert.Equal(2, PlaylistSearchEngine.GetNextMatchIndex(matches, 8)); // Exact third -> wrap to first
        Assert.Equal(2, PlaylistSearchEngine.GetNextMatchIndex(matches, 10)); // Past last -> wrap to first

        // Prev navigation
        Assert.Equal(8, PlaylistSearchEngine.GetPrevMatchIndex(matches, 0)); // Before first -> wrap to last
        Assert.Equal(8, PlaylistSearchEngine.GetPrevMatchIndex(matches, 2)); // Exact first -> wrap to last
        Assert.Equal(2, PlaylistSearchEngine.GetPrevMatchIndex(matches, 4)); // Between 1 and 2 -> first
        Assert.Equal(2, PlaylistSearchEngine.GetPrevMatchIndex(matches, 5)); // Exact second -> first
        Assert.Equal(5, PlaylistSearchEngine.GetPrevMatchIndex(matches, 8)); // Exact third -> second
        Assert.Equal(8, PlaylistSearchEngine.GetPrevMatchIndex(matches, 10)); // Past last -> last
    }

    [Fact]
    public void Search_EmptyMatches_NavigationReturnsNegativeOne()
    {
        Assert.Equal(-1, PlaylistSearchEngine.GetNextMatchIndex(null, 0));
        Assert.Equal(-1, PlaylistSearchEngine.GetNextMatchIndex(Array.Empty<int>(), 5));
        Assert.Equal(-1, PlaylistSearchEngine.GetPrevMatchIndex(null, 0));
        Assert.Equal(-1, PlaylistSearchEngine.GetPrevMatchIndex(Array.Empty<int>(), 5));
    }
}
