using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Title-bar contract gate (2026-10-02 user report, re-pinned 2026-10-03 after the
/// doc-aligned TitleBar-control refactor, re-pinned 2026-10-05 for the user-approved
/// tab/status order swap — track-title length no longer moves the nav tabs). History: the AppTitleBar grid used two Auto
/// columns (brand+status, nav tabs) that never shrink, so below ~870 effective px the tabs
/// slid under the system caption buttons drawn on top by ExtendsContentIntoTitleBar — the
/// minimize/close glyphs visibly overlapped "Playlists"/"Network". The current contract,
/// per the official docs (learn.microsoft.com /windows/apps/design/basics/titlebar-design,
/// /windows/apps/develop/title-bar, /windows/apps/design/controls/title-bar):
/// <list type="bullet">
/// <item>the WinUI TitleBar control hosts the bar; app-drawn caption buttons are forbidden
///       (the system owns caption input, and its glyph colors ignore alpha so they cannot be
///       hidden),</item>
/// <item>the user-picked B layout (2026-10-05, superseding the 2026-10-03 OLD pick) keeps
///       ☰ · brand · nav tabs · status in LeftHeader and the gear in RightHeader; the tabs
///       sit directly after the brand so the variable-width track text, as the last element,
///       can only spill into the free space to its right and never moves them (user report:
///       track-title length shifted the three nav tabs). The Content slot stays empty
///       because it is center-aligned and would push the tabs mid-window,</item>
/// <item>content sheds stepwise from the Window's SizeChanged — track hides below 840
///       effective px, brand text below 740 — sized so the widest localization (ko nav
///       ≈ 352px) still fits with slack. The control's own SizeChanged reported
///       unpredictable widths in narrow windows (2026-10-03 4차 실측), so window width is
///       the single source of truth,</item>
/// <item>the window enforces PreferredMinimumWidth 620 (code-behind; WinUI Window has no
///       MinWidth in XAML), the last line of defense under the shed states. Mini mode lifts
///       it because it legally shrinks to 500px with the title bar hidden,</item>
/// <item>caption colors have a single implementation — ThemeService.UpdateTitleBar — with
///       transparent backgrounds (the doc-allowed family), opaque palette-driven glyphs, and
///       a full reset to system colors under high contrast (titlebar-design: colors must
///       adjust for high-contrast themes),</item>
/// <item>the bar dims while the window is inactive (titlebar-design "Do": all title bar
///       elements turn semi-transparent when the window is inactive).</item>
/// </list>
/// Like <see cref="MiniPlayerLayoutTests"/>, the thresholds are pinned as a contract: a
/// breakpoint changed in code without updating this gate is a regression (the 640→730 lesson).
/// </summary>
public class MainWindowTitleBarLayoutTests
{
    private static string ReadRepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) continue;
            var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), $"{string.Join('/', parts)} not found at {path}");
            return File.ReadAllText(path);
        }
        Assert.Fail("repository root not found; the gate needs a source checkout");
        return string.Empty;
    }

    private static string ReadMainWindowXaml() => ReadRepoFile("src", "DawnPlayer.App", "MainWindow.xaml");

    private static string ReadMainWindowSource() => ReadRepoFile("src", "DawnPlayer.App", "MainWindow.xaml.cs");

    private static string ReadThemeServiceSource() => ReadRepoFile("src", "DawnPlayer.App", "Services", "ThemeService.cs");

    private static string AppTitleBarBlock(string xaml)
    {
        var start = xaml.IndexOf("x:Name=\"AppTitleBar\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "AppTitleBar grid not found");
        var end = xaml.IndexOf("<!-- 2. Main Content Viewport", start, StringComparison.Ordinal);
        Assert.True(end > start, "AppTitleBar block end marker not found");
        return xaml[start..end];
    }

    [Fact]
    public void StatusText_TrailsTabs_TabsSitBesideBrand_InLeftHeader()
    {
        var bar = AppTitleBarBlock(ReadMainWindowXaml());

        // 변형 B (2026-10-05 승인): 탭은 브랜드 바로 뒤(주 내비 자리), 상태 텍스트는 탭 뒤
        // 마지막 요소 — 곡 제목 폭 변동이 오른쪽 빈 공간으로만 흡수되어 브랜드·탭의 X 위치는
        // 절대 움직이지 않는다("곡 제목 길이에 따라 후순위 탭 3개가 밀린다" 사용자 보고,
        // 2026-10-05의 재발 방지). 구 OLD 계약(상태가 탭보다 앞)은 이로 대체했다.
        var brand = bar.IndexOf("x:Name=\"AppBrandText\"", StringComparison.Ordinal);
        var nav = bar.IndexOf("x:Name=\"TopNavPanel\"", StringComparison.Ordinal);
        var track = bar.IndexOf("x:Name=\"TitleBarTrack\"", StringComparison.Ordinal);
        Assert.True(brand >= 0 && nav > brand, "TopNavPanel must sit beside (right of) the brand panel");
        Assert.True(track > nav, "TitleBarTrack must trail TopNavPanel in the TitleBar content");

        // 브랜드 패널 안에는 상태 텍스트가 없어야 한다(브랜드+구분선만).
        var brandEnd = brand >= 0 ? bar.IndexOf("</StackPanel>", brand, StringComparison.Ordinal) : -1;
        Assert.True(brandEnd > brand, "brand StackPanel not found");
        Assert.DoesNotContain("TitleBarTrack", bar[brand..brandEnd]);

        // 상태 텍스트는 밀리지 말고 말줄임으로 흡수된다(좁은 창 첫 방어선).
        Assert.Matches(@"x:Name=""TitleBarTrack""[^>]*TextTrimming=""\w+""", bar);

        // 상한 320은 미적 천장이다(넓은 창에서 상태 텍스트가 바를 채우지 않게 한다). 캡션 침범
        // 방어는 창 폭 유동 상한(UpdateTitleBarTrackWidthCap — SheddingThresholds 게이트가 잠금)이
        // 담당한다: TitleBar 템플릿이 LeftHeader 측정에 캡션 폭을 반영하지 않아 고정값만으로는
        // 좁은 창에서 제목이 캡션을 침범한다(2026-10-05 UIA 실측).
        Assert.Matches(@"x:Name=""TitleBarTrack""[^>]*MaxWidth=""320""", bar);

        // 잘린 제목의 전문은 툴팁으로 회수 가능해야 한다(자기 Text 바인딩).
        Assert.Matches(@"x:Name=""TitleBarTrack""[^>]*ToolTipService\.ToolTip=""\{Binding", bar);
    }

    [Fact]
    public void TitleBarControl_HostsOldLayout_WithoutAppDrawnCaptions()
    {
        // 2026-10-03 (3차 수정): WinUI TitleBar 컨트롤 + 시스템 캡션 색 튜닝. 공식 문서 계약상
        // 캡션 글리프 전경은 투명화 불가·캡션 영역 입력은 시스템 독점이라 "앱이 캡션을 직접
        // 그리는" 패턴(1~2차 시도)은 불가능 — 앱 버튼 + 시스템 글리프가 겹쳐 보였던 것이 그
        // 증상이었다. 정석은 TitleBar 컨트롤(레이아웃/인셋 관리) + ThemeService.UpdateTitleBar
        // (배경 투명 = 재질 통일, 글리프 색 = 테마 팔레트).
        var xaml = ReadMainWindowXaml();
        Assert.Contains("<TitleBar x:Name=\"AppTitleBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<TitleBar.LeftHeader>", xaml, StringComparison.Ordinal);
        Assert.Contains("<TitleBar.RightHeader>", xaml, StringComparison.Ordinal);

        // Content 슬롯은 의도적으로 비운다 — 중앙 배치 전용이라 탭이 창 한가운데로 밀린다
        // (사용자 선택 OLD 배치는 전부 LeftHeader). 재발 방지 게이트.
        Assert.DoesNotContain("<TitleBar.Content>", xaml, StringComparison.Ordinal);

        // The overlap regression guard: no app-drawn caption buttons may exist.
        Assert.DoesNotContain("CaptionMinimize", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptionMaximize", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptionClose", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptionColors_HaveSingleDocCompliantImplementation()
    {
        // 단일 진실 원천: 캡션 색은 ThemeService.UpdateTitleBar 한 곳에서만 결정된다.
        // MainWindow 전용 사본(값이 갈라져 보조 창과 불일치했던 2026-10-03 결함)은 재발 금지.
        var mainWindow = ReadMainWindowSource();
        Assert.DoesNotContain("StyleSystemCaptionButtons", mainWindow, StringComparison.Ordinal);

        var theme = ReadThemeServiceSource();
        Assert.Contains("public static void UpdateTitleBar(Window window, bool isLight)", theme, StringComparison.Ordinal);

        // 재질 통일: 캡션 배경은 투명 — 문서가 투명을 허용하는 유일한 계열이다.
        Assert.Contains("titleBar.ButtonBackgroundColor = Colors.Transparent", theme, StringComparison.Ordinal);

        // 글리프 색은 불투명만 허용(알파 무시) — 투명 지정은 문서 위반이자 결함.
        Assert.DoesNotContain("ButtonForegroundColor = Colors.Transparent", theme, StringComparison.Ordinal);
        Assert.DoesNotContain("ButtonHoverForegroundColor = Colors.Transparent", theme, StringComparison.Ordinal);

        // 캡션 호버·누름은 앱 Card 토큰 — 탭 칩·트리 카드와 동일한 호버 언어(2026-10-03
        // "Dawn Player 커스텀" 캡션 계약). 중립 알파 틴트로의 역퇴 금지.
        Assert.Contains("PaletteColor(\"CardHoverColor\"", theme, StringComparison.Ordinal);
        Assert.Contains("PaletteColor(\"CardPressedColor\"", theme, StringComparison.Ordinal);

        // 고대비: 문서(titlebar-design) 요구 — 색 사용자 지정을 걷어내고 시스템 색으로 복귀
        // (null 리셋). 커스텀 팔레트를 HC 위에 덧칠하던 2026-10-03 결함의 재발 방지.
        var updateBar = theme[theme.IndexOf("public static void UpdateTitleBar", StringComparison.Ordinal)..];
        Assert.Contains("IsHighContrastActive()", updateBar, StringComparison.Ordinal);
        Assert.Matches(@"titleBar\.ButtonForegroundColor = null;", updateBar);
        Assert.Matches(@"titleBar\.ButtonHoverBackgroundColor = null;", updateBar);
    }

    [Fact]
    public void TitleBar_DimsWhileWindowInactive()
    {
        // 문서 "Do"(titlebar-design): 창 비활성 시 타이틀바의 모든 요소가 반투명해야 한다.
        // 시스템 캡션은 스스로 디밍되므로 커스텀 Left/RightHeader 콘텐츠를 Window.Activated가
        // 처리한다(2026-10-03 문서 정합 리팩터링에서 추가).
        var source = ReadMainWindowSource();
        Assert.Contains("Activated +=", source, StringComparison.Ordinal);
        Assert.Contains("WindowActivationState.Deactivated", source, StringComparison.Ordinal);
        Assert.Contains("AppTitleBar.Opacity", source, StringComparison.Ordinal);

        // 디밍 강도 계약: 반투명(0.5) — 바꾸려면 이 게이트를 의도적으로 갱신할 것.
        Assert.Contains("InactiveTitleBarOpacity = 0.5", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleBar_Height_MatchesTallCaptionContract()
    {
        // 변형 A + Tall 48 (2026-10-03 승인): 바(XAML 행·MinHeight)와 시스템 캡션(
        // PreferredHeightOption.Tall = 48px)이 일치해야 캡션 호버 배경이 바를 꽉 채운다 —
        // 40px 바 + 32px 캡션의 8px 괴리(사용자 스크린샷 빨간 선 지적)의 재발 방지.
        // 2026-10-04 44 완화를 시도했다가 롤백: 캡션은 32/48 둘뿐이라 Tall 유지 시 호버가
        // 바 아래로 4px 블리드됐고, 32는 글리프를 바 중앙보다 6px 위로 밀었다 — 48이 유일하게
        // 정합인 높이. 문서 경고상 PreferredHeightOption은 ExtendsContentIntoTitleBar=true
        // 이후 설정한다.
        var xaml = ReadMainWindowXaml();
        Assert.Contains("<RowDefinition Height=\"48\"/>", xaml, StringComparison.Ordinal);
        Assert.Matches(@"x:Name=""AppTitleBar""[^>]*MinHeight=""48""", xaml);

        var source = ReadMainWindowSource();
        var extends = source.IndexOf("ExtendsContentIntoTitleBar = true;", StringComparison.Ordinal);
        var tall = source.IndexOf("TitleBarHeightOption.Tall", StringComparison.Ordinal);
        Assert.True(extends >= 0 && tall > extends,
            "PreferredHeightOption=Tall must follow ExtendsContentIntoTitleBar=true (doc: throws otherwise)");
        Assert.Contains("private const double TitleBarRowHeight = 48;", source);
        Assert.Contains("GridLength(TitleBarRowHeight)", source);
        Assert.DoesNotContain("GridLength(40)", source);
        Assert.DoesNotContain("TitleBarRowHeight = 44", source);

        // 미니 모드(104px 창)는 Standard로 낮추고 복귀 시 Tall — 캡션이 미니 창을 잠식하지 않게.
        Assert.Contains("TitleBarHeightOption.Standard", source);
        Assert.Contains("TitleBarHeightOption.Tall", source);
    }

    [Fact]
    public void TitleBarTab_UsesTreeCardLanguage()
    {
        // 변형 A (2026-10-03 승인): 상단바 탭은 사이드바 트리 D 카드형과 같은 언어 — 28px 칩,
        // 코너 5, 호버 CardHover, 체크 = 선택 틴트(액센트 연동 토큰) + DawnAccentText.
        // 밑줄(ActiveIndicator)·SemiBold 언어의 재발 방지. 네트워크 페이지의 EoleNavTabStyle은
        // L13(2026-10-04)에서 데이터 소스 사이드바 전환과 함께 삭제됐다.
        var theme = ReadRepoFile("src", "DawnPlayer.App", "DawnTheme.xaml");
        var start = theme.IndexOf("x:Key=\"TitleBarTabStyle\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "TitleBarTabStyle not found");
        var block = theme[start..theme.IndexOf("</Style>", start, StringComparison.Ordinal)];

        Assert.Contains("MinHeight\" Value=\"28\"", block, StringComparison.Ordinal);
        Assert.Contains("CornerRadius\" Value=\"5\"", block, StringComparison.Ordinal);
        Assert.Contains("{ThemeResource CardHoverBrush}", block, StringComparison.Ordinal);
        Assert.Contains("{ThemeResource ListViewItemBackgroundSelected}", block, StringComparison.Ordinal);
        Assert.Contains("{ThemeResource DawnAccentTextBrush}", block, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveIndicator", block, StringComparison.Ordinal);
        Assert.DoesNotContain("FontWeight", block, StringComparison.Ordinal);

        // 상태 충돌 금지(2026-10-03 사용자 재보고 — 클릭 후 하이라이트 소실): 호버·체크·누름이
        // 같은 프로퍼티(Root.Background)를 지정하면 상태 종료 시 스냅샷 복원이 체크 틴트를
        // 덮어쓴다. 배경은 전용 오버레이 요소의 Opacity로만 제어한다.
        Assert.DoesNotContain("Target=\"Root.Background\"", block, StringComparison.Ordinal);
        Assert.Contains("Target=\"HoverBackground.Opacity\"", block, StringComparison.Ordinal);
        Assert.Contains("Target=\"CheckedBackground.Opacity\"", block, StringComparison.Ordinal);

        // 1~2차 커스텀 캡션 시도의 죽은 스타일(사용처 0)은 재유입 금지.
        Assert.DoesNotContain("x:Key=\"CaptionButtonStyle\"", theme, StringComparison.Ordinal);

        // 상단바 탭은 카드 언어 스타일을 참조한다(밑줄 스타일 역참조 금지).
        var xaml = ReadMainWindowXaml();
        Assert.DoesNotContain("Style=\"{StaticResource EoleNavTabStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource TitleBarTabStyle}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsGear_MarkerResets_WhenLeavingSettingsSurface()
    {
        // PT1-03 위치 마커(설정 중 액센트 톱니)는 설정 표면을 벗어나면 반드시 회복된다. 탭 클릭
        // 경로는 ContentFrame 탐색을 일으키지 않아(Navigated 미발화) 톱니가 주황으로 남는 결함
        // (2026-10-03 사용자 보고)의 재발 방지 — 두 경로가 모두 마커 갱신을 호출해야 한다.
        var source = ReadMainWindowSource();
        Assert.Contains("private void UpdateSettingsGearMarker()", source, StringComparison.Ordinal);
        var calls = Regex.Count(source, @"UpdateSettingsGearMarker\(\);");
        Assert.True(calls >= 2, "gear marker must refresh on both enter (OnContentFrameNavigated) " +
            "and leave (ApplyNavigationState) paths");
        Assert.Contains("ContentFrame.Navigated += OnContentFrameNavigated", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SheddingThresholds_ArePinned_AndWiredToWindowSizeChanged()
    {
        var xaml = ReadMainWindowXaml();

        // 단계 숨김은 Window.SizeChanged(창 논리 폭)에만 연결된다. TitleBar 컨트롤 자체의
        // SizeChanged는 좁은 창에서 보고 폭이 예측적이지 않았다(2026-10-03 4차 실측) — XAML
        // 연결 방식의 재발 금지.
        Assert.DoesNotContain("SizeChanged=\"OnTitleBarSizeChanged\"", xaml, StringComparison.Ordinal);

        var source = ReadMainWindowSource();
        Assert.Contains("SizeChanged += OnTitleBarSizeChanged", source, StringComparison.Ordinal);
        Assert.Contains("TrackVisibleMinWidth = 840", source);
        Assert.Contains("BrandVisibleMinWidth = 740", source);
        Assert.Contains("TitleBarTrack.Visibility = width >= TrackVisibleMinWidth", source);
        Assert.Contains("AppBrandText.Visibility = width >= BrandVisibleMinWidth", source);

        // 변형 B(2026-10-05): 상태 텍스트 상한은 창 폭에서 유도된다 — 오른쪽 예약(캡션 144 +
        // 톱니 32 + 여유 32 = 208 논리 px)과 배치된 시작 X를 뺀다. TitleBar 템플릿이 LeftHeader
        // 측정에 캡션 폭을 반영하지 않아 논리 960px 창에서 긴 제목 꼬리가 최소화 버튼과 24px
        // 겹쳤던(2026-10-05 UIA 실측) 재발 방지. XAML MaxWidth=320만으로는 부족하다.
        Assert.Contains("UpdateTitleBarTrackWidthCap();", source, StringComparison.Ordinal);
        // Window.SizeChanged는 초기 배치에서 발화하지 않아 첫 계산이 배치 전 ActualWidth 0을
        // 읽었다(2026-10-05 실측) — 1차 트리거는 RootGrid.SizeChanged(첫 배치에서도 발화)다.
        Assert.Contains("RootGrid.SizeChanged += (_, _) => UpdateTitleBarTrackWidthCap();", source, StringComparison.Ordinal);
        Assert.Contains("private const double TitleBarRightReserve = 208;", source, StringComparison.Ordinal);
        Assert.Contains("Math.Min(320, available)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_MinWidth_CoversNarrowStateContent()
    {
        // WinUI's Window has no MinWidth in XAML and the preferred minimum lives on the
        // OverlappedPresenter, not AppWindow — contract is the code-behind assignments.
        var source = ReadMainWindowSource();

        var m = Regex.Match(source, @"PreferredMinimumWidth = (\d+);");
        Assert.True(m.Success, "MainWindow must set OverlappedPresenter.PreferredMinimumWidth — below it the nav " +
            "tabs slide under the caption buttons no matter how the bar sheds content");
        Assert.True(int.Parse(m.Groups[1].Value) >= 620,
            "Minimum below 620 leaves the ko-localized nav (≈352px) + captions (140) + menu/gear without room");

        // Mini mode legally shrinks the same window to 500px with the title bar hidden, so
        // it must lift the minimum — and restore it on exit.
        Assert.Contains("presenter.PreferredMinimumWidth = 0;", source);
        Assert.Contains("presenter.PreferredMinimumWidth = 620;", source);
    }
}
