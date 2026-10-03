using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Tree row-density gate: the library TreeView rows draw their own 26px template, and the
/// container geometry is a pinned contract — 2026-10-03 the user approved 셰브런 8px /
/// 셰브런→제목 1px / 상하 패딩 1px, i.e. row total 28px (26 body + 1+1 padding) delivered by
/// the EoleTreeItemStyle custom template (chevron column 8px, glyph 8x8, no presenter
/// margin/padding). Keep this gate in step whenever the geometry changes again.
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
        // Row total 28 = 26px template body + 1px top/bottom padding (EoleTreeItemStyle).
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"28\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"1,1\"/>", xaml, StringComparison.Ordinal);
        // The chevron geometry is template-owned and was hand-tuned — pin it too.
        Assert.Contains("Width=\"8\"", xaml, StringComparison.Ordinal);
    }
}
