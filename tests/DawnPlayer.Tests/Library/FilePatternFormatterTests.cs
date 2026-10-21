using System;
using System.IO;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests.Library;

public class FilePatternFormatterTests
{
    [Fact]
    public void Format_ReplacesStandardTokens()
    {
        var track = new Track
        {
            Path = @"C:\Music\orig.flac",
            Artist = "Pink Floyd",
            AlbumArtist = "Pink Floyd",
            Album = "The Dark Side of the Moon",
            Title = "Time",
            TrackNo = 4,
            DiscNo = 1,
            Year = 1973,
            Genre = "Progressive Rock"
        };

        var result = FilePatternFormatter.Format(
            "%artist%/%album%/%tracknumber% - %title%",
            track);

        // Path should use system directory separators or forward slashes normalized
        var normalized = result.Replace('\\', '/');
        Assert.Equal("Pink Floyd/The Dark Side of the Moon/04 - Time.flac", normalized);
    }

    [Fact]
    public void Format_ConditionalBlocks_WhenPresent_IncludesEnclosedText()
    {
        var track = new Track
        {
            Path = @"C:\Music\song.mp3",
            Artist = "Queen",
            Title = "Bohemian Rhapsody",
            TrackNo = 11
        };

        var result = FilePatternFormatter.Format(
            "%artist% - [%track%. ]%title%",
            track);

        Assert.Equal("Queen - 11. Bohemian Rhapsody.mp3", result);
    }

    [Fact]
    public void Format_ConditionalBlocks_WhenTokenEmpty_OmitsBlockEntirely()
    {
        var track = new Track
        {
            Path = @"C:\Music\song.mp3",
            Artist = "Queen",
            Title = "Bohemian Rhapsody",
            TrackNo = 0 // Missing track number
        };

        var result = FilePatternFormatter.Format(
            "%artist% - [%track%. ]%title%",
            track);

        Assert.Equal("Queen - Bohemian Rhapsody.mp3", result);
    }

    [Fact]
    public void Format_SanitizesIllegalCharactersInPathComponents()
    {
        var track = new Track
        {
            Path = @"C:\Music\file.m4a",
            Artist = "AC/DC", // '/' should not accidentally create a nested directory inside artist
            Album = "Who Made Who?",
            Title = "D.T.: <Live> *Special* | \"Edit\"",
            TrackNo = 1
        };

        // Note: when artist has '/', SanitizePathComponent or Format should handle it safely
        var result = FilePatternFormatter.Format(
            "%artist% - %album%/[%tracknumber% - ]%title%",
            track);

        var normalized = result.Replace('\\', '/');
        // Check illegal chars (?, :, <, >, *, |, ") are stripped or replaced
        Assert.DoesNotContain("?", normalized);
        Assert.DoesNotContain("<", normalized);
        Assert.DoesNotContain(">", normalized);
        Assert.DoesNotContain("*", normalized);
        Assert.DoesNotContain("|", normalized);
        Assert.DoesNotContain("\"", normalized);
        Assert.EndsWith(".m4a", normalized);
    }

    [Fact]
    public void Format_FallbackDefaults_WhenMetadataMissing()
    {
        var track = new Track
        {
            Path = @"C:\Music\unknown_track.opus",
            Artist = "",
            Album = "",
            Title = ""
        };

        var result = FilePatternFormatter.Format(
            "%artist% - %album%/%title%",
            track);

        var normalized = result.Replace('\\', '/');
        Assert.Contains("Unknown Artist", normalized);
        Assert.Contains("Unknown Album", normalized);
        Assert.Contains("unknown_track.opus", normalized);
    }
}
