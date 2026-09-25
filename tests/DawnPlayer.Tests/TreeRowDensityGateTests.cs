using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Tree row-density gate: the library TreeView rows draw their own 26px template,
/// so the TreeViewItem container must not add extra height or horizontal gutter
/// beyond it (chevron column and indent stay template-owned).
/// </summary>
public sealed class TreeRowDensityGateTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) return dir;
        }
        return null;
    }

    private static string ReadLibraryPageXaml()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; the gate needs a source checkout");
        var path = Path.Combine(root!.FullName, "src", "DawnPlayer.App", "Views", "LibraryPage.xaml");
        Assert.True(File.Exists(path), $"LibraryPage.xaml not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void LibraryTree_HasCompactContainerStyle()
    {
        var xaml = ReadLibraryPageXaml();
        Assert.Contains("<TreeView.ItemContainerStyle>", xaml, StringComparison.Ordinal);
        Assert.Contains("TargetType=\"TreeViewItem\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryTree_ContainerMatchesRowHeight()
    {
        var xaml = ReadLibraryPageXaml();
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"26\"/>", xaml, StringComparison.Ordinal);
    }
}
