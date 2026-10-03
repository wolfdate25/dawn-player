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
    public void StatusText_SitsBeforeTabs_InTitleBarContent()
    {
        var xaml = ReadMainWindowXaml();
        var bar = AppTitleBarBlock(xaml);

        // 변형 OLD (2026-10-03): 상태 텍스트가 탭 왼쪽 — Content 슬롯 안에서 TitleBarTrack이
        // TopNavPanel보다 앞에 온다(문서 순서 계약). 이전 결함(상태가 브랜드 패널 안에 있어
        // 탭을 캡션 쪽으로 밀던 것)의 재발 방지.
        var track = bar.IndexOf("x:Name=\"TitleBarTrack\"", StringComparison.Ordinal);
        var nav = bar.IndexOf("x:Name=\"TopNavPanel\"", StringComparison.Ordinal);
        Assert.True(track >= 0 && nav > track, "TitleBarTrack must precede TopNavPanel in the TitleBar content");

        // 브랜드 패널(LeftHeader) 안에는 상태 텍스트가 없어야 한다.
        var brandStart = bar.IndexOf("x:Name=\"AppBrandText\"", StringComparison.Ordinal);
        var brandEnd = brandStart >= 0 ? bar.IndexOf("</StackPanel>", brandStart, StringComparison.Ordinal) : -1;
        Assert.True(brandStart >= 0 && brandEnd > brandStart, "brand StackPanel not found");
        Assert.DoesNotContain("TitleBarTrack", bar[brandStart..brandEnd]);

        Assert.Matches(@"x:Name=""TitleBarTrack""[^>]*TextTrimming=""\w+""", bar);
    }

    [Fact]
    public void TitleBarControl_WithThemedSystemCaptions_IsUsed()
    {
        // 2026-10-03 (3차 수정): WinUI TitleBar 컨트롤 + 시스템 캡션 색 튜닝. 공식 문서 계약상
        // 캡션 글리프 전경은 투명화 불가·캡션 영역 입력은 시스템 독점이라 "앱이 캡션을 직접
        // 그리는" 패턴(1~2차 시도)은 불가능 — 앱 버튼 + 시스템 글리프가 겹쳐 보였던 것이 그
        // 증상이었다. 정석은 TitleBar 컨트롤(레이아웃/인셋 관리) + StyleSystemCaptionButtons
        // (배경 투명 = 재질 통일, 글리프 색 = 테마 팔레트). 앱 캡션 버튼 요소는 존재해선 안 된다.
        var xaml = ReadMainWindowXaml();
        Assert.Contains("<TitleBar x:Name=\"AppTitleBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<TitleBar.LeftHeader>", xaml, StringComparison.Ordinal);
        Assert.Contains("<TitleBar.Content>", xaml, StringComparison.Ordinal);
        Assert.Contains("<TitleBar.RightHeader>", xaml, StringComparison.Ordinal);
        // The overlap regression guard: no app-drawn caption buttons may exist.
        Assert.DoesNotContain("CaptionMinimize", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptionMaximize", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptionClose", xaml, StringComparison.Ordinal);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) continue;
            var source = File.ReadAllText(Path.Combine(dir.FullName, "src", "DawnPlayer.App", "MainWindow.xaml.cs"));
            Assert.Contains("StyleSystemCaptionButtons", source, StringComparison.Ordinal);
            // Material unification: caption backgrounds must be transparent (the doc-allowed set).
            Assert.Contains("t.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent", source, StringComparison.Ordinal);
            // Glyph colors are opaque-only by contract — themed, never transparent.
            Assert.DoesNotContain("t.ButtonForegroundColor = Microsoft.UI.Colors.Transparent", source, StringComparison.Ordinal);
            return;
        }
        Assert.Fail("repository root not found; the gate needs a source checkout");
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