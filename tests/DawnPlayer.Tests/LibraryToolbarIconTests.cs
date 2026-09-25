using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Library toolbar icon gate. The cover-size button used the Segoe MDL2 "Zoom"
/// glyph (E71E, a magnifier) directly next to the search box (QueryIcon="Find",
/// also a magnifier), so search and cover-size were visually indistinguishable.
/// The track-list view toggle used E8D2 ("Font"), not a list icon at all.
/// Rule: the zoom button glyph must not belong to the magnifier family and must
/// not duplicate a neighboring toolbar glyph; the list toggle must use EA37
/// (List). The accessible names stay sourced from resw via x:Uid
/// (see <see cref="AutomationNameGateTests"/>).
/// </summary>
public class LibraryToolbarIconTests
{
    // Segoe MDL2 Assets magnifier family (official code chart): all read as "search".
    private static readonly HashSet<string> MagnifierGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        "E71E", // Zoom
        "E71F", // ZoomOut
        "E8A3", // ZoomIn
        "ECE8", // ZoomMode
    };

    // Glyphs of the adjacent LibraryPage header controls: grid/list view toggle
    // and rescan. Reusing one of them would trade one confusion for another.
    private static readonly HashSet<string> NeighborGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        "E80A", // view-grid toggle
        "EA37", // view-list toggle (List)
        "E72C", // rescan
    };

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
    /// Extracts the FontIcon glyph of the cover-size (zoom) toolbar button.
    /// Returns null when the button or its glyph is missing.
    /// </summary>
    internal static string? FindZoomButtonGlyph(string xaml)
    {
        var buttonAt = xaml.IndexOf("x:Uid=\"Library_Zoom_Button\"", StringComparison.Ordinal);
        if (buttonAt < 0) return null;
        var blockStart = xaml.LastIndexOf("<Button", buttonAt, StringComparison.Ordinal);
        var blockEnd = xaml.IndexOf("</Button>", buttonAt, StringComparison.Ordinal);
        if (blockStart < 0 || blockEnd < 0) return null;
        var block = xaml.Substring(blockStart, blockEnd - blockStart);
        var m = Regex.Match(block, @"Glyph=""&#x([0-9A-Fa-f]+);""");
        return m.Success ? m.Groups[1].Value : null;
    }

    [Fact]
    public void ZoomButton_UsesNonMagnifierGlyph()
    {
        var glyph = FindZoomButtonGlyph(ReadLibraryPageXaml());
        Assert.True(glyph != null, "cover-size button glyph not found — scan scope broken");
        Assert.False(MagnifierGlyphs.Contains(glyph!),
            "Cover-size button uses a magnifier glyph next to the search box — " +
            "pick a non-magnifier size/resize glyph (e.g. E741 ResizeTouchLarger).");
    }

    [Fact]
    public void ZoomButton_Glyph_DistinctFromToolbarNeighbors()
    {
        var glyph = FindZoomButtonGlyph(ReadLibraryPageXaml());
        Assert.True(glyph != null, "cover-size button glyph not found — scan scope broken");
        Assert.False(NeighborGlyphs.Contains(glyph!),
            "Cover-size button duplicates a neighboring toolbar glyph — pick a distinct one.");
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
    [InlineData("<Button x:Uid=\"Library_Zoom_Button\"><FontIcon Glyph=\"&#xE71E;\"/></Button>", "E71E")]
    [InlineData("<Button x:Uid=\"Library_Zoom_Button\"><FontIcon Glyph=\"&#xE741;\"/></Button>", "E741")]
    [InlineData("<Button x:Uid=\"Other\"><FontIcon Glyph=\"&#xE741;\"/></Button>", null)]
    [InlineData("<Button x:Uid=\"Library_Zoom_Button\"></Button>", null)]
    public void Extractor_FindsZoomButtonGlyph(string xaml, string? expected)
    {
        Assert.Equal(expected, FindZoomButtonGlyph(xaml));
        if (expected != null)
        {
            var isMagnifier = MagnifierGlyphs.Contains(expected);
            Assert.Equal(expected == "E71E", isMagnifier);
        }
    }
}
