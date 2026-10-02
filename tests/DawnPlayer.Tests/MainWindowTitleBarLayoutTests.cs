using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Title-bar overflow gate (2026-10-02 user report). The AppTitleBar grid used two Auto
/// columns (brand+status, nav tabs) that never shrink, so below ~870 effective px the tabs
/// slid under the system caption buttons drawn on top by ExtendsContentIntoTitleBar — the
/// minimize/close glyphs visibly overlapped "Playlists"/"Network". The fix contract:
/// <list type="bullet">
/// <item>the status text lives in a flexible (*) column and ellipsizes instead of pushing the
///       tabs right,</item>
/// <item>content sheds stepwise from OnTitleBarSizeChanged — track hides below 840 effective
///       px, brand text below 740 — sized so the widest localization (ko nav ≈ 352px) still
///       fits with slack,</item>
/// <item>the window enforces PreferredMinimumWidth 620 (code-behind; WinUI Window has no
///       MinWidth in XAML), the last line of defense under the shed states. Mini mode lifts
///       it because it legally shrinks to 500px with the title bar hidden.</item>
/// </list>
/// Like <see cref="MiniPlayerLayoutTests"/>, the thresholds are pinned as a contract: a
/// breakpoint changed in code without updating this gate is a regression (the 640→730 lesson).
/// </summary>
public class MainWindowTitleBarLayoutTests
{
    private static string ReadMainWindowXaml()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) continue;
            var path = Path.Combine(dir.FullName, "src", "DawnPlayer.App", "MainWindow.xaml");
            Assert.True(File.Exists(path), $"MainWindow.xaml not found at {path}");
            return File.ReadAllText(path);
        }
        Assert.Fail("repository root not found; the gate needs a source checkout");
        return string.Empty;
    }

    private static string AppTitleBarBlock(string xaml)
    {
        var start = xaml.IndexOf("x:Name=\"AppTitleBar\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "AppTitleBar grid not found");
        var end = xaml.IndexOf("<!-- 2. Main Content Viewport", start, StringComparison.Ordinal);
        Assert.True(end > start, "AppTitleBar block end marker not found");
        return xaml[start..end];
    }

    [Fact]
    public void StatusText_LivesInFlexibleColumn_NotInsideBrandStackPanel()
    {
        var xaml = ReadMainWindowXaml();
        var bar = AppTitleBarBlock(xaml);

        // The old defect: TitleBarTrack sat inside the brand StackPanel (an Auto column) and
        // its fixed MaxWidth pushed the nav tabs into the caption buttons on narrow windows.
        var brandPanel = bar.IndexOf("Brand & Status Text", StringComparison.Ordinal);
        var brandPanelEnd = bar.IndexOf("</StackPanel>", brandPanel, StringComparison.Ordinal);
        Assert.True(brandPanel >= 0 && brandPanelEnd > brandPanel, "brand StackPanel not found");
        Assert.DoesNotContain("TitleBarTrack", bar[brandPanel..brandPanelEnd]);

        // Flexible column: 4 columns with a star in second position, track bound to it.
        var columns = Regex.Matches(bar, @"<ColumnDefinition Width=""([^""]+)""/>")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "Auto", "*", "Auto", "Auto" }, columns);
        Assert.Matches(@"Grid\.Column=""1""", bar);
        Assert.Matches(@"x:Name=""TitleBarTrack""[^>]*TextTrimming=""\w+""", bar);
    }

    [Fact]
    public void SheddingThresholds_ArePinned_AndWiredToSizeChanged()
    {
        var xaml = ReadMainWindowXaml();

        // The shedding must react to the bar's own SizeChanged: AdaptiveTrigger was tried
        // first and did not re-evaluate on live resizes in this window (2026-10-02 finding).
        Assert.Contains("SizeChanged=\"OnTitleBarSizeChanged\"", xaml);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) continue;
            var source = File.ReadAllText(Path.Combine(dir.FullName, "src", "DawnPlayer.App", "MainWindow.xaml.cs"));

            Assert.Contains("TrackVisibleMinWidth = 840", source);
            Assert.Contains("BrandVisibleMinWidth = 740", source);
            Assert.Contains("TitleBarTrack.Visibility = width >= TrackVisibleMinWidth", source);
            Assert.Contains("AppBrandText.Visibility = width >= BrandVisibleMinWidth", source);
            return;
        }
        Assert.Fail("repository root not found; the gate needs a source checkout");
    }

    [Fact]
    public void Window_MinWidth_CoversNarrowStateContent()
    {
        // WinUI's Window has no MinWidth in XAML and the preferred minimum lives on the
        // OverlappedPresenter, not AppWindow — contract is the code-behind assignments.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) continue;
            var source = File.ReadAllText(Path.Combine(dir.FullName,
                "src", "DawnPlayer.App", "MainWindow.xaml.cs"));

            var m = Regex.Match(source, @"PreferredMinimumWidth = (\d+);");
            Assert.True(m.Success, "MainWindow must set OverlappedPresenter.PreferredMinimumWidth — below it the nav " +
                "tabs slide under the caption buttons no matter how the bar sheds content");
            Assert.True(int.Parse(m.Groups[1].Value) >= 620,
                "Minimum below 620 leaves the ko-localized nav (≈352px) + captions (140) + menu/gear without room");

            // Mini mode legally shrinks the same window to 500px with the title bar hidden, so
            // it must lift the minimum — and restore it on exit.
            Assert.Contains("presenter.PreferredMinimumWidth = 0;", source);
            Assert.Contains("presenter.PreferredMinimumWidth = 620;", source);
            return;
        }
        Assert.Fail("repository root not found; the gate needs a source checkout");
    }
}