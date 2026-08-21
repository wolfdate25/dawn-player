using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Comprehensive unit tests for Track model properties, sort keys, album keys, and AppPaths validation:
/// 1. SortArtist fallback: AlbumArtist when present, otherwise Artist.
/// 2. AlbumSortKey bitwise encoding: ((long)DiscNo << 32) | (uint)TrackNo.
/// 3. AlbumKey cache key: lowercase artist + \u0001 + lowercase album, falling back to
///    a per-file "file:" key when neither tag is present.
/// 4. Duration and ToString representation.
/// 5. AppPaths audio extension support and validation.
/// 6. Track immutability: init-only properties, and equality unaffected by the AlbumKey cache.
/// </summary>
public class TrackModelAndSortingKeyTests
{
    #region 1. SortArtist Fallback Tests

    [Theory]
    [InlineData("Queen", "Freddie Mercury", "Queen")]               // AlbumArtist takes precedence
    [InlineData("Various Artists", "Daft Punk", "Various Artists")]  // Compilation album
    [InlineData("", "Pink Floyd", "Pink Floyd")]                    // Empty AlbumArtist falls back to Artist
    [InlineData("   ", "Radiohead", "Radiohead")]                   // Whitespace AlbumArtist falls back to Artist
    [InlineData(null, "Coldplay", "Coldplay")]                      // Null AlbumArtist falls back to Artist
    [InlineData("", "", "")]                                        // Both empty -> empty
    public void SortArtist_FallsBackToPerformer_WhenAlbumArtistMissing(string? albumArtist, string artist, string expectedSortArtist)
    {
        var track = new Track
        {
            AlbumArtist = albumArtist!,
            Artist = artist
        };

        Assert.Equal(expectedSortArtist, track.SortArtist);
    }

    #endregion

    #region 2. AlbumSortKey Encoding & Sorting Tests

    [Fact]
    public void AlbumSortKey_EncodesDiscAndTrackCorrectly()
    {
        var track = new Track
        {
            DiscNo = 1,
            TrackNo = 5
        };

        long expectedKey = (1L << 32) | 5;
        Assert.Equal(expectedKey, track.AlbumSortKey);
        Assert.Equal(4294967296L + 5L, track.AlbumSortKey);
    }

    [Fact]
    public void AlbumSortKey_EnsuresMultiDiscOrderingIntegrity()
    {
        var tracks = new List<Track>
        {
            new Track { Title = "D2T1", DiscNo = 2, TrackNo = 1 },
            new Track { Title = "D1T10", DiscNo = 1, TrackNo = 10 },
            new Track { Title = "D1T2", DiscNo = 1, TrackNo = 2 },
            new Track { Title = "D1T1", DiscNo = 1, TrackNo = 1 },
            new Track { Title = "D3T1", DiscNo = 3, TrackNo = 1 },
            new Track { Title = "D2T12", DiscNo = 2, TrackNo = 12 }
        };

        var sorted = tracks.OrderBy(t => t.AlbumSortKey).Select(t => t.Title).ToList();

        // Disc 1 tracks must precede Disc 2 tracks, and Track 10 must precede Disc 2 Track 1
        Assert.Equal(new[] { "D1T1", "D1T2", "D1T10", "D2T1", "D2T12", "D3T1" }, sorted);
    }

    [Theory]
    [InlineData(0, 0, 0L)]
    [InlineData(0, 15, 15L)]
    [InlineData(99, 9999, (99L << 32) | 9999L)]
    public void AlbumSortKey_BoundaryValues(int discNo, int trackNo, long expectedKey)
    {
        var track = new Track { DiscNo = discNo, TrackNo = trackNo };
        Assert.Equal(expectedKey, track.AlbumSortKey);
    }

    #endregion

    #region 3. AlbumKey Normalization Tests

    [Theory]
    [InlineData("Pink Floyd", "The Wall", "pink floyd\u0001the wall")]
    [InlineData("  Led Zeppelin  ", "  IV  ", "led zeppelin\u0001iv")]
    [InlineData("아이유", "Palette", "아이유\u0001palette")]
    [InlineData("BEATLES", "ABBEY ROAD", "beatles\u0001abbey road")]
    public void AlbumKey_NormalizesToLowercaseAndTrims(string artist, string album, string expectedKey)
    {
        var track = new Track
        {
            Artist = artist,
            Album = album
        };

        Assert.Equal(expectedKey, track.AlbumKey);
    }

    [Fact]
    public void AlbumKey_UsesSortArtistWhenAlbumArtistPresent()
    {
        var track = new Track
        {
            Artist = "Miles Davis & John Coltrane",
            AlbumArtist = "Miles Davis",
            Album = "Kind of Blue"
        };

        Assert.Equal("miles davis\u0001kind of blue", track.AlbumKey);
    }

    [Fact]
    public void AlbumKey_TaggedTrack_KeepsArtistSeparatorAlbumFormat()
    {
        var track = new Track
        {
            Path = @"C:\Music\Pink Floyd\The Wall\01.flac",
            Artist = "Pink Floyd",
            Album = "The Wall"
        };

        // Art-cache file names on disk are derived from this exact string.
        Assert.Equal("pink floyd\u0001the wall", track.AlbumKey);
        Assert.Equal(AlbumArtService.ComputeAlbumKey(track), track.AlbumKey);
    }

    [Fact]
    public void AlbumKey_UntaggedFilesInDifferentFolders_GetDifferentKeys()
    {
        var a = new Track { Path = @"C:\rips\disc1\01.flac" };
        var b = new Track { Path = @"C:\rips\disc2\01.flac" };

        Assert.NotEqual(a.AlbumKey, b.AlbumKey);
        Assert.Equal(@"file:c:\rips\disc1\01.flac", a.AlbumKey);
        Assert.Equal(@"file:c:\rips\disc2\01.flac", b.AlbumKey);
    }

    [Fact]
    public void AlbumKey_UntaggedFilesInSameFolder_GetOneKeyPerFile()
    {
        var a = new Track { Path = @"C:\rips\disc1\01.flac" };
        var b = new Track { Path = @"C:\rips\disc1\02.flac" };

        // The fallback is per file, not per folder: sibling untagged files must not share a key, or
        // they resolve to each other's cached cover.
        Assert.NotEqual(a.AlbumKey, b.AlbumKey);
        Assert.Equal(AlbumArtService.ComputeAlbumKey(a), a.AlbumKey);
        Assert.Equal(AlbumArtService.ComputeAlbumKey(b), b.AlbumKey);
    }

    [Fact]
    public void AlbumKey_UntaggedTrackWithNoPath_FallsBackToSeparatorOnly()
    {
        var track = new Track();

        Assert.Equal("\u0001", track.AlbumKey);
    }

    [Fact]
    public void AlbumKey_RepeatedAccess_ReturnsCachedStableValue()
    {
        var tagged = new Track { Path = @"C:\Music\a.flac", Artist = "IU", Album = "Palette" };
        var untagged = new Track { Path = @"C:\Music\b.flac" };

        Assert.Same(tagged.AlbumKey, tagged.AlbumKey);
        Assert.Same(untagged.AlbumKey, untagged.AlbumKey);
        Assert.Equal("iu\u0001palette", tagged.AlbumKey);
    }

    [Fact]
    public void AlbumKey_AfterWithExpressionChangesAlbum_RecomputesKey()
    {
        var original = new Track { Path = @"C:\Music\a.flac", Artist = "IU", Album = "Palette" };
        Assert.Equal("iu\u0001palette", original.AlbumKey);

        var copy = original with { Album = "Lilac" };

        Assert.Equal("iu\u0001lilac", copy.AlbumKey);
        Assert.Equal("iu\u0001palette", original.AlbumKey);
    }

    [Fact]
    public void AlbumKey_TracksSharingArtistAndAlbum_ShareOneKey()
    {
        var a = new Track { Path = @"C:\Music\01.flac", AlbumArtist = "Queen", Album = "A Night at the Opera" };
        var b = new Track { Path = @"C:\Music\02.flac", AlbumArtist = "QUEEN", Album = "  A Night at the Opera  " };

        Assert.Equal(a.AlbumKey, b.AlbumKey);
    }

    #endregion

    #region 4. Track General Properties Tests

    [Fact]
    public void Track_Duration_ComputedFromDurationMs()
    {
        var track = new Track { DurationMs = 215000 };
        Assert.Equal(TimeSpan.FromMilliseconds(215000), track.Duration);
        Assert.Equal(3, track.Duration.Minutes);
        Assert.Equal(35, track.Duration.Seconds);
    }

    [Fact]
    public void Track_ToString_FormatsArtistAndTitle()
    {
        var track = new Track
        {
            Artist = "IU",
            Title = "Good Day"
        };

        Assert.Equal("IU - Good Day", track.ToString());
    }

    #endregion

    #region 5. AppPaths Audio Support Tests

    [Theory]
    [InlineData(@"C:\Music\song.mp3", true)]
    [InlineData(@"C:\Music\song.MP3", true)]
    [InlineData(@"C:\Music\track.flac", true)]
    [InlineData(@"C:\Music\track.FLAC", true)]
    [InlineData(@"C:\Music\audio.ogg", true)]
    [InlineData(@"C:\Music\audio.oga", true)]
    [InlineData(@"C:\Music\audio.aac", true)]
    [InlineData(@"C:\Music\audio.m4a", true)]
    [InlineData(@"C:\Music\audio.m4b", true)]
    [InlineData(@"C:\Music\video.mp4", true)]
    [InlineData(@"C:\Music\lossless.wav", true)]
    [InlineData(@"C:\Music\apple.alac", true)]
    [InlineData(@"C:\Music\song.lrc", false)]
    [InlineData(@"C:\Music\playlist.m3u", false)]
    [InlineData(@"C:\Music\playlist.m3u8", false)]
    [InlineData(@"C:\Music\cover.jpg", false)]
    [InlineData(@"C:\Music\cover.png", false)]
    [InlineData(@"C:\Music\document.txt", false)]
    [InlineData(@"C:\Music\program.exe", false)]
    public void AppPaths_IsSupportedAudioFile_ValidatesExtensions(string path, bool expected)
    {
        Assert.Equal(expected, AppPaths.IsSupportedAudioFile(path));
    }

    #endregion

    #region 6. Track Immutability Tests

    [Fact]
    public void Track_DerivedCopy_LeavesTheOriginalUntouched()
    {
        // One Track instance is shared by the library dictionary, several PlaylistItems and the
        // audio thread, and the AlbumKey cache assumes the key inputs never change. The properties
        // cannot be init-only — WinUI's XAML type-info generator emits setters for every property
        // of an x:DataType, and LibraryPage.xaml binds core:Track — so the invariant this pins is
        // the one the code actually follows: derived values come from `with`, not from writing to
        // a shared instance.
        var original = new Track { Path = @"C:\Music\a.flac", Artist = "IU", Album = "Palette" };
        var originalKey = original.AlbumKey;

        var withArt = original with { ArtPath = @"C:\cache\cover.jpg" };

        Assert.Null(original.ArtPath);
        Assert.Equal(@"C:\cache\cover.jpg", withArt.ArtPath);
        Assert.Equal(originalKey, original.AlbumKey);
        // The copy differs only in a field the album key does not depend on.
        Assert.Equal(originalKey, withArt.AlbumKey);
    }

    [Fact]
    public void Track_DerivedCopyWithDifferentAlbum_GetsItsOwnAlbumKey()
    {
        var original = new Track { Path = @"C:\Music\a.flac", Artist = "IU", Album = "Palette" };
        _ = original.AlbumKey; // populate the memo before copying

        var moved = original with { Album = "Lilac" };

        Assert.NotEqual(original.AlbumKey, moved.AlbumKey);
    }

    [Fact]
    public void Track_ValueEquality_UnaffectedByAlbumKeyCaching()
    {
        var a = new Track { Path = @"C:\Music\a.flac", Artist = "IU", Album = "Palette" };
        var b = new Track { Path = @"C:\Music\a.flac", Artist = "IU", Album = "Palette" };

        _ = a.AlbumKey;

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    #endregion
}
