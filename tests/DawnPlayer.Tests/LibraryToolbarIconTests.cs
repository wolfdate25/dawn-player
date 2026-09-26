using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Library toolbar icon gate. The cover-size button went through two misleading
/// font glyphs (E71E magnifier, then E741 resize-arrows reading as "go") next to
/// the search box, then a custom diagonal-arrow PathIcon and a Lucide outline
/// Path; the outline mark stuck out beside the solid MDL2 toolbar glyphs, so the
/// button now uses the MDL2 Picture glyph (E91B) at the same size (11) as its
/// neighbors. E93C was rejected (already means album nodes in the adjacent tree),
/// E7C5 (picture+search) repeats the magnifier trap, and E8B9/E8A9 would collide
/// with the tree header / grid toggle. The track-list view toggle used E8D2
/// ("Font"), not a list icon at all.
/// Rule: the zoom button uses E91B only, bans the misleading glyphs, and holds
/// no Path/PathIcon; the list toggle must use EA37 (List). The accessible names
/// stay sourced from resw via x:Uid (see <see cref="AutomationNameGateTests"/>).
/// </summary>
public class LibraryToolbarIconTests
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

    /// <summary>
    /// Extracts the cover-size (zoom) toolbar button block. Returns null when missing.
    /// </summary>
    internal static string? FindZoomButtonBlock(string xaml)
    {
        var buttonAt = xaml.IndexOf("x:Uid=\"Library_Zoom_Button\"", StringComparison.Ordinal);
        if (buttonAt < 0) return null;
        var blockStart = xaml.LastIndexOf("<Button", buttonAt, StringComparison.Ordinal);
        var blockEnd = xaml.IndexOf("</Button>", buttonAt, StringComparison.Ordinal);
        if (blockStart < 0 || blockEnd < 0) return null;
        return xaml.Substring(blockStart, blockEnd - blockStart);
    }

    [Fact]
    public void ZoomButton_UsesPictureGlyph()
    {
        var block = FindZoomButtonBlock(ReadLibraryPageXaml());
        Assert.True(block != null, "cover-size button not found — scan scope broken");
        // Same MDL2 language as the neighboring toolbar glyphs (E80A/EA37/E72C).
        Assert.Contains("&#xE91B;", block!, StringComparison.Ordinal);
        Assert.DoesNotContain("<Path", block!, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomButton_HasNoMisleadingGlyph()
    {
        var block = FindZoomButtonBlock(ReadLibraryPageXaml());
        Assert.True(block != null, "cover-size button not found — scan scope broken");
        // E71E magnifier read as search next to the search box; E741 arrows read
        // as "go"; E8A3 is the magnifier family again.
        Assert.DoesNotContain("&#xE71E;", block!, StringComparison.Ordinal);
        Assert.DoesNotContain("&#xE741;", block!, StringComparison.Ordinal);
        Assert.DoesNotContain("&#xE8A3;", block!, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomButton_KeepsReswSourcedName()
    {
        var xaml = ReadLibraryPageXaml();
        Assert.Contains("x:Uid=\"Library_Zoom_Button\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts the FontIcon glyph of the track-list view toggle button.
    /// Returns null when the button or its glyph is missing.
    /// </summary>
    internal static string? FindViewListGlyph(string xaml)
    {
        var buttonAt = xaml.IndexOf("x:Uid=\"Library_ViewListBtn\"", StringComparison.Ordinal);
        if (buttonAt < 0) return null;
        var blockStart = xaml.LastIndexOf("<RadioButton", buttonAt, StringComparison.Ordinal);
        var blockEnd = xaml.IndexOf("</RadioButton>", buttonAt, StringComparison.Ordinal);
        if (blockStart < 0 || blockEnd < 0) return null;
        var block = xaml.Substring(blockStart, blockEnd - blockStart);
        var m = Regex.Match(block, @"Glyph=""&#x([0-9A-Fa-f]+);""");
        return m.Success ? m.Groups[1].Value : null;
    }

    [Fact]
    public void ViewList_UsesListGlyph()
    {
        var glyph = FindViewListGlyph(ReadLibraryPageXaml());
        Assert.True(glyph != null, "list-view toggle glyph not found — scan scope broken");
        Assert.Equal("EA37", glyph!.ToUpperInvariant());
    }

    [Fact]
    public void ViewList_Glyph_NotFontGlyph()
    {
        var glyph = FindViewListGlyph(ReadLibraryPageXaml());
        Assert.True(glyph != null, "list-view toggle glyph not found — scan scope broken");
        Assert.NotEqual("E8D2", glyph!.ToUpperInvariant());
    }

    [Theory]
    [InlineData("<Button x:Uid=\"Library_Zoom_Button\"><PathIcon Data=\"M1,1\"/></Button>", true)]
    [InlineData("<Button x:Uid=\"Other\"><PathIcon Data=\"M1,1\"/></Button>", false)]
    [InlineData("<Button x:Uid=\"Library_Zoom_Button\">", false)]
    public void Extractor_FindsZoomButtonBlock(string xaml, bool found)
    {
        Assert.Equal(found, FindZoomButtonBlock(xaml) != null);
    }
}
