using System;
using System.IO;
using System.Linq;
using DawnPlayer.App.Views;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// V1 folder-icon gate: in Folder mode every node is a folder, so the per-row
/// folder glyph is redundant next to the expander chevron. The view hides it
/// (returning ~17px per row to the title); other modes keep their icons.
/// </summary>
public sealed class TreeFolderIconGateTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) return dir;
        }
        return null;
    }

    [Theory]
    [InlineData("Folder", true)]
    [InlineData("Artist", false)]
    [InlineData("Album", false)]
    [InlineData("All", false)]
    public void IsFolder_OnlyTrueForFolderMode(string filterType, bool expected)
    {
        Assert.Equal(expected, new LibraryTreeNode { FilterType = filterType }.IsFolder);
    }

    [Fact]
    public void RowIcon_HiddenInFolderMode()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; the gate needs a source checkout");
        var path = Path.Combine(root!.FullName, "src", "DawnPlayer.App", "Views", "LibraryPage.xaml");
        var xaml = File.ReadAllText(path);
        Assert.Contains("Content.IsFolder", xaml, StringComparison.Ordinal);
    }
}
