using System;
using System.Collections.Generic;
using System.IO;
using DawnPlayer.App.Views;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests;

public sealed class LibraryFilterServiceTests
{
    [Fact]
    public void IsPathInsideFolder_ExactMatch_ReturnsTrue()
    {
        string folder = @"C:\Music\Pop";
        string file = @"C:\Music\Pop\song.mp3";

        bool result = LibraryFilterService.IsPathInsideFolder(file, folder);

        Assert.True(result);
    }

    [Fact]
    public void IsPathInsideFolder_NestedSubfolder_ReturnsTrue()
    {
        string folder = @"C:\Music";
        string file = @"C:\Music\Pop\2026\hit.flac";

        bool result = LibraryFilterService.IsPathInsideFolder(file, folder);

        Assert.True(result);
    }

    [Fact]
    public void IsPathInsideFolder_SimilarPrefixDifferentFolder_ReturnsFalse()
    {
        string folder = @"C:\Music";
        string file = @"C:\Music2\song.mp3";

        bool result = LibraryFilterService.IsPathInsideFolder(file, folder);

        Assert.False(result);
    }

    [Theory]
    [InlineData(null, @"C:\Music")]
    [InlineData(@"", @"C:\Music")]
    [InlineData(@"C:\Music\song.mp3", null)]
    [InlineData(@"C:\Music\song.mp3", "")]
    [InlineData(null, null)]
    public void IsPathInsideFolder_NullOrEmpty_ReturnsFalseGracefully(string? file, string? folder)
    {
        bool result = LibraryFilterService.IsPathInsideFolder(file, folder);

        Assert.False(result);
    }

    [Fact]
    public void FilterAndSort_FolderFilter_OnlyReturnsContainedTracks()
    {
        var tracks = new List<Track>
        {
            new() { Path = @"D:\Audio\OST\track1.mp3", Title = "Track 1", Artist = "Artist", Album = "OST" },
            new() { Path = @"D:\Audio\OST\Disc2\track2.mp3", Title = "Track 2", Artist = "Artist", Album = "OST" },
            new() { Path = @"D:\Audio\Rock\track3.mp3", Title = "Track 3", Artist = "Artist", Album = "Rock" },
            new() { Path = @"E:\Audio\Jazz\track4.mp3", Title = "Track 4", Artist = "Artist", Album = "Jazz" },
        };

        var folderNode = new LibraryTreeNode
        {
            FilterType = "Folder",
            FilterValue = @"D:\Audio\OST",
            Title = "OST"
        };

        var result = LibraryFilterService.FilterAndSort(tracks, folderNode, "", SortColumn.None, true);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, t => t.Title == "Track 1");
        Assert.Contains(result, t => t.Title == "Track 2");
    }

    [Fact]
    public void FilterAndSort_EmptyTrackList_ReturnsEmptyResults()
    {
        var filtered = LibraryFilterService.FilterAndSort(new List<Track>(), null, "", SortColumn.None, true);
        Assert.Empty(filtered);

        var cards = LibraryFilterService.BuildAlbumCardModels(new List<Track>());
        Assert.Empty(cards);
    }

    [Fact]
    public void FilterAndSort_SpecialCharacterMetadata_FiltersAndSortsByMultipleColumns()
    {
        var tracks = new List<Track>
        {
            new() { Title = "Song (Acoustic) [Live]", Artist = "IU & Park", Album = "Palette (Special)", DurationMs = 210000, TrackNo = 1 },
            new() { Title = "Through the Night", Artist = "IU", Album = "Palette (Special)", DurationMs = 250000, TrackNo = 2 },
            new() { Title = "Bohemian Rhapsody", Artist = "Queen", Album = "A Night at the Opera", DurationMs = 354000, TrackNo = 11 }
        };

        var filtered = LibraryFilterService.FilterAndSort(tracks, null, "IU", SortColumn.TrackNo, true);
        Assert.Equal(2, filtered.Count);
        Assert.Equal("Song (Acoustic) [Live]", filtered[0].Title);
        Assert.Equal("Through the Night", filtered[1].Title);

        var sortedByDuration = LibraryFilterService.FilterAndSort(tracks, null, "", SortColumn.Duration, false);
        Assert.Equal("Bohemian Rhapsody", sortedByDuration[0].Title);
    }

    [Fact]
    public void FilterAndSort_FolderFilterWithNullFilterValue_ReturnsAllTracksWithoutCrash()
    {
        var tracks = new List<Track>
        {
            new() { Path = @"D:\Audio\OST\track1.mp3", Title = "Track 1" },
        };

        var invalidNode = new LibraryTreeNode
        {
            FilterType = "Folder",
            FilterValue = null!,
            Title = "Invalid"
        };

        var result = LibraryFilterService.FilterAndSort(tracks, invalidNode, "", SortColumn.None, true);

        Assert.Single(result);
    }
}
