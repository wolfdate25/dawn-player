using System;
using System.Collections.Generic;
using System.IO;
using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Comprehensive unit tests for LyricsFinder candidate generation, token replacement, illegal char sanitization, and path resolution:
/// 1. Token replacement (%filename%, %artist%, %title%, %album%).
/// 2. Illegal character sanitization (replacing invalid file characters :, \, /, ?, *, ", <, >, | with _).
/// 3. Deduplication and combining search folders with track directory.
/// 4. Lyrics file existence detection (FindLrcPath and ExistsFor).
/// </summary>
public class LyricsCandidateBuilderTests
{
    [Fact]
    public void BuildCandidates_ReplacesStandardTokens_Correctly()
    {
        var settings = AppSettings.CreateDefault();
        settings.Lyrics.SearchFolders.Clear(); // only track dir
        settings.Lyrics.FilePatterns = new List<string>
        {
            "%filename%.lrc",
            "%artist% - %title%.lrc",
            "%title%.lrc",
            "%album% - %title%.lrc"
        };

        var track = new Track
        {
            Path = @"C:\Music\Pop\track01.mp3",
            Artist = "IU",
            Title = "Good Day",
            Album = "Real"
        };

        var candidates = LyricsFinder.BuildCandidates(track, settings);

        Assert.Equal(4, candidates.Count);
        Assert.Equal(@"C:\Music\Pop\track01.lrc", candidates[0]);
        Assert.Equal(@"C:\Music\Pop\IU - Good Day.lrc", candidates[1]);
        Assert.Equal(@"C:\Music\Pop\Good Day.lrc", candidates[2]);
        Assert.Equal(@"C:\Music\Pop\Real - Good Day.lrc", candidates[3]);
    }

    [Fact]
    public void BuildCandidates_ReplacesTokensCaseInsensitively()
    {
        var settings = AppSettings.CreateDefault();
        settings.Lyrics.SearchFolders.Clear();
        settings.Lyrics.FilePatterns = new List<string>
        {
            "%FILENAME%.lrc",
            "%ARTIST% - %TITLE%.LRC",
            "%Album%.lrc"
        };

        var track = new Track
        {
            Path = @"C:\Music\song.flac",
            Artist = "Queen",
            Title = "Bohemian Rhapsody",
            Album = "A Night at the Opera"
        };

        var candidates = LyricsFinder.BuildCandidates(track, settings);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(@"C:\Music\song.lrc", candidates[0]);
        Assert.Equal(@"C:\Music\Queen - Bohemian Rhapsody.LRC", candidates[1]);
        Assert.Equal(@"C:\Music\A Night at the Opera.lrc", candidates[2]);
    }

    [Fact]
    public void BuildCandidates_SanitizesIllegalCharactersInArtistTitleAlbum()
    {
        var settings = AppSettings.CreateDefault();
        settings.Lyrics.SearchFolders.Clear();
        settings.Lyrics.FilePatterns = new List<string>
        {
            "%artist% - %title%.lrc",
            "%album%.lrc"
        };

        var track = new Track
        {
            Path = @"C:\Music\song.mp3",
            Artist = "AC/DC",                   // Contains '/'
            Title = "Who Made Who?: Live* <HQ>", // Contains '?', ':', '*', '<', '>'
            Album = "Back in Black | \"Remaster\"" // Contains '|', '"'
        };

        var candidates = LyricsFinder.BuildCandidates(track, settings);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(@"C:\Music\AC_DC - Who Made Who__ Live_ _HQ_.lrc", candidates[0]);
        Assert.Equal(@"C:\Music\Back in Black _ _Remaster_.lrc", candidates[1]);
    }

    [Fact]
    public void LyricsFinder_SanitizesReservedPathCharacters_AndAvoidsInjection()
    {
        // A '\' left in a tag would splice a directory segment into the candidate path.
        var settings = AppSettings.CreateDefault();
        settings.Lyrics.SearchFolders.Clear();
        settings.Lyrics.FilePatterns = new List<string> { "%artist% - %title%.lrc" };

        var track = new Track
        {
            Path = @"C:\Music\test.mp3",
            Artist = @"AC/DC\Band:Rock*<Super>?|Hero",
            Title = "Song\"Quotes\""
        };

        var candidates = LyricsFinder.BuildCandidates(track, settings);
        Assert.Single(candidates);

        // None of the illegal characters should be in candidate filename
        var filename = Path.GetFileName(candidates[0]);
        Assert.DoesNotContain("/", filename);
        Assert.DoesNotContain(@"\", filename);
        Assert.DoesNotContain(":", filename);
        Assert.DoesNotContain("*", filename);
        Assert.DoesNotContain("<", filename);
        Assert.DoesNotContain(">", filename);
        Assert.DoesNotContain("?", filename);
        Assert.DoesNotContain("|", filename);
        Assert.DoesNotContain("\"", filename);
    }

    [Fact]
    public void BuildCandidates_DeduplicatesIdenticalFilenames()
    {
        var settings = AppSettings.CreateDefault();
        settings.Lyrics.SearchFolders.Clear();
        settings.Lyrics.FilePatterns = new List<string>
        {
            "%filename%.lrc",
            "%FILENAME%.LRC", // Duplicate case-insensitive
            "%filename%.lrc"  // Duplicate exact
        };

        var track = new Track
        {
            Path = @"C:\Music\track.mp3"
        };

        var candidates = LyricsFinder.BuildCandidates(track, settings);

        Assert.Single(candidates);
        Assert.Equal(@"C:\Music\track.lrc", candidates[0]);
    }

    [Fact]
    public void BuildCandidates_AppendsSearchFoldersAndTrackDirectory()
    {
        var settings = AppSettings.CreateDefault();
        settings.Lyrics.SearchFolders = new List<string>
        {
            @"D:\GlobalLyrics",
            @"E:\Shared\Lyrics",
            "",        // Empty should be skipped
            "   "      // Whitespace should be skipped
        };
        settings.Lyrics.FilePatterns = new List<string>
        {
            "%filename%.lrc"
        };

        var track = new Track
        {
            Path = @"C:\Music\track.mp3"
        };

        var candidates = LyricsFinder.BuildCandidates(track, settings);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(@"D:\GlobalLyrics\track.lrc", candidates[0]);
        Assert.Equal(@"E:\Shared\Lyrics\track.lrc", candidates[1]);
        Assert.Equal(@"C:\Music\track.lrc", candidates[2]);
    }

    [Fact]
    public void FindLrcPath_And_ExistsFor_ResolvesExistingFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_LyricsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var audioPath = Path.Combine(tempDir, "mysong.mp3");
            var lrcPath = Path.Combine(tempDir, "mysong.lrc");

            File.WriteAllText(audioPath, "dummy audio");
            File.WriteAllText(lrcPath, "[00:00.00]Hello world");

            var settings = AppSettings.CreateDefault();
            var track = new Track
            {
                Path = audioPath,
                Artist = "Artist",
                Title = "Title"
            };

            Assert.True(LyricsFinder.ExistsFor(track, settings));
            var found = LyricsFinder.FindLrcPath(track, settings);
            Assert.Equal(lrcPath, found);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FindLrcPath_ReturnsNull_WhenNoMatchingFileExists()
    {
        var settings = AppSettings.CreateDefault();
        var track = new Track
        {
            Path = @"C:\NonExistentDirectory\song.mp3",
            Artist = "Unknown",
            Title = "Unknown"
        };

        Assert.False(LyricsFinder.ExistsFor(track, settings));
        Assert.Null(LyricsFinder.FindLrcPath(track, settings));
    }
}
