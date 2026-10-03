using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Tree row-density gate: the container geometry is a pinned contract. 2026-10-03 the user
/// picked 변형 D (card-style) from the mockups: 32px card rows with 1px gap (pitch 34 = the
/// track-list rhythm), card background #1F1F25@40% with hover/amber-tinted selected, chevron
/// column 10px, chevron→title 5px, count margins 8px — delivered by the EoleTreeItemStyle
/// custom template + TreeView-scoped background brushes. Keep this gate in step whenever the
/// geometry changes again.
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
        // 변형 D: 32px card row (pitch 34 with the 1px gap), chevron column 10px.
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"32\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ExpandCollapseChevron\" Grid.Column=\"1\" Padding=\"0\" Width=\"10\"", xaml, StringComparison.Ordinal);
        // Card look consumes the token (hexes live in DawnTheme — token-layer gate).
        Assert.Contains("<Setter Property=\"Background\" Value=\"{ThemeResource TreeRowCardBrush}\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{ThemeResource CardHoverBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{ThemeResource ListViewItemBackgroundSelected}\"", xaml, StringComparison.Ordinal);
        // The row template matches the card height and the widened count margins.
        Assert.Contains("<Grid Height=\"32\" ColumnSpacing=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"8,0,8,0\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TreeRowCardBrush_IsTokenized_ForBothThemes()
    {
        // The card token must exist per theme (dark = Panel@40% slate, light = Panel@40% warm).
        var root = FindRepoRoot();
        Assert.True(root != null);
        var theme = File.ReadAllText(Path.Combine(root!.FullName, "src", "DawnPlayer.App", "DawnTheme.xaml"));
        Assert.Contains("x:Key=\"TreeRowCardBrush\" Color=\"#661F1F25\"", theme, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TreeRowCardBrush\" Color=\"#66F0EFEB\"", theme, StringComparison.Ordinal);
    }
}
