using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DawnPlayer.App.Views;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests.Library;

/// <summary>
/// P0 folder-tree advancement: full-path single-child chain compression (A2),
/// non-rooted path guard (A0), and the lazy-expansion model contract (A1).
/// </summary>
public sealed class LibraryFolderTreeAdvancedTests
{
    private static Track MakeTrack(
        string path,
        string title = "Song",
        string artist = "Artist",
        string album = "Album",
        string genre = "Rock")
    {
        return new Track
        {
            Path = path,
            Title = title,
            Artist = artist,
            AlbumArtist = "",
            Album = album,
            Genre = genre,
            TrackNo = 1,
            Year = 2024,
            DurationMs = 180000
        };
    }

    private static List<LibraryTreeNode> BuildFolder(params Track[] tracks)
    {
        var roots = new List<LibraryTreeNode>();
        LibraryTreeModelBuilder.BuildTree(tracks, TreeGroupMode.Folder, roots);
        return roots;
    }

    private static void AssertAllFalse(IEnumerable<LibraryTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            Assert.False(n.DeferChildren);
            AssertAllFalse(n.Children);
        }
    }

    [Fact]
    public void FolderMode_NestedSingleChildChain_MergesIntoOneNode()
    {
        var roots = BuildFolder(
            MakeTrack(@"C:\R\t0.mp3", title: "Root song"),
            MakeTrack(@"C:\R\A\B\t1.mp3", title: "Deep song"),
            MakeTrack(@"C:\R\C\t2.mp3", title: "Other song"));

        var drive = Assert.Single(roots, r => r.FilterType == "Folder");
        Assert.Equal(2, drive.Children.Count);

        var merged = Assert.Single(drive.Children, n => n.Title == "A / B");
        Assert.Equal("Folder", merged.FilterType);
        Assert.Equal(Path.GetFullPath(@"C:\R\A\B"), merged.FilterValue);
        Assert.Equal(1, merged.Count);
        Assert.Empty(merged.Children);

        var sibling = Assert.Single(drive.Children, n => n.Title == "C");
        Assert.Equal(1, sibling.Count);
    }

    [Fact]
    public void FolderMode_ChainStopsWhenIntermediateHoldsTracks()
    {
        var roots = BuildFolder(
            MakeTrack(@"C:\R\A\t0.mp3", title: "Mid song"),
            MakeTrack(@"C:\R\A\B\t1.mp3", title: "Leaf song"));

        // R holds no tracks and has one child, so the root simplifies to A;
        // A holds tracks itself, so it must stay addressable and keep B as a child.
        var root = Assert.Single(roots, r => r.FilterType == "Folder");
        Assert.Equal(Path.GetFullPath(@"C:\R\A"), root.FilterValue);
        var leaf = Assert.Single(root.Children);
        Assert.Equal("B", leaf.Title);
        Assert.Equal(Path.GetFullPath(@"C:\R\A\B"), leaf.FilterValue);
        Assert.Equal(1, leaf.Count);
    }

    [Fact]
    public void FolderMode_RelativePath_Skipped()
    {
        var roots = BuildFolder(
            MakeTrack(@"C:\M\s.mp3", title: "Rooted song"),
            MakeTrack(@"relative\song.mp3", title: "Relative song"));

        var folders = roots.Where(r => r.FilterType == "Folder").ToList();
        Assert.NotEmpty(folders);
        Assert.DoesNotContain(folders, f =>
            f.FilterValue.Contains("relative", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(folders, f =>
            string.Equals(f.FilterValue, Path.GetFullPath(@"C:\M"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FolderMode_NonLeafNodes_DeferChildren()
    {
        var roots = BuildFolder(
            MakeTrack(@"C:\R\t0.mp3", title: "Root song"),
            MakeTrack(@"C:\R\A\B\t1.mp3", title: "Deep song"));

        var all = Assert.Single(roots, r => r.FilterType == "All");
        Assert.False(all.DeferChildren);

        var drive = Assert.Single(roots, r => r.FilterType == "Folder");
        Assert.True(drive.DeferChildren);

        var merged = Assert.Single(drive.Children);
        Assert.Equal("A / B", merged.Title);
        Assert.False(merged.DeferChildren);
    }

    [Fact]
    public void NonFolderModes_NeverDeferChildren()
    {
        var tracks = new List<Track>
        {
            MakeTrack(@"C:\M\a.mp3", artist: "Artist One", album: "Album One", genre: "Rock"),
            MakeTrack(@"C:\M\b.mp3", artist: "Artist One", album: "Album Two", genre: "Rock")
        };

        foreach (var mode in Enum.GetValues<TreeGroupMode>())
        {
            if (mode == TreeGroupMode.Folder) continue;
            var roots = new List<LibraryTreeNode>();
            LibraryTreeModelBuilder.BuildTree(tracks, mode, roots);
            AssertAllFalse(roots);
        }
    }
}
