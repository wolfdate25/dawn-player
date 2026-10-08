using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Mini-player behavior gate (2026-10-08 "미니 플레이어 완성도" 수리). The mode shipped with a
/// cluster of completeness defects, each pinned here so a revert re-introducing one fails the
/// suite:
/// <list type="bullet">
/// <item>M1 — the caption-drag ancestor walk stopped at any Control/UserControl, and the
///       NowPlayingBar shell IS a UserControl covering 100% of the mini window: no drag
///       surface existed at all and the window was immovable.</item>
/// <item>M2 — the mini resize used hard-coded physical pixels (500×104); at 150% scale the
///       bar laid out at 333×69 logical and the controls clipped off the window. Sizes are
///       now logical constants scaled by live DPI (MiniPlayerPlacement).</item>
/// <item>M3 — ExtendsContentIntoTitleBar keeps the system caption buttons (Standard 32px)
///       drawn at the top-right even with the TitleBar control collapsed; with row 0 at
///       zero height they floated on top of the seek row's remaining-time text. A 32px
///       caption gutter row now hosts them over empty surface.</item>
/// <item>M4 — Escape was the only exit and it rides RootGrid.KeyDown, which dies with lost
///       focus (entering via the menu strands the user). The root now receives focus on
///       entry, plus a right-click context menu (restore / always-on-top / fullscreen /
///       exit) and double-click-to-restore — the WCAG 2.2 single-pointer alternative to
///       dragging.</item>
/// <item>M5 — the mini window width always lands in the Compact trigger, which removed the
///       volume slider entirely (mute-only mini) and kept a lyrics toggle whose pane is
///       unreachable in mini. A triggerless MiniVolume state re-shows the slider and hides
///       the dead toggle; the bar re-asserts it after every AdaptiveTrigger pass.</item>
/// <item>M6 — entering mini from a maximized window never left the zoomed state before
///       resizing.</item>
/// <item>M7 — the 620 PreferredMinimumWidth lifted AFTER the resize call, racing the shell's
///       clamp.</item>
/// <item>M8 — shutting down while in mini persisted the 600×128 mini client as the restored
///       window size; the next start crushed the full UI into it.</item>
/// <item>M10 — the InfoBar lived in a zero-height row in mini: warnings had no surface.</item>
/// </list>
/// Like <see cref="MiniPlayerLayoutTests"/>, these are contract gates: changing one of these
/// behaviors without updating its gate is a regression.
/// </summary>
public class MiniModeBehaviorTests
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

    private static string MainWindowSource() => ReadRepoFile("src", "DawnPlayer.App", "MainWindow.xaml.cs");

    private static string MainWindowXaml() => ReadRepoFile("src", "DawnPlayer.App", "MainWindow.xaml");

    private static string BarXaml() => ReadRepoFile("src", "DawnPlayer.App", "Controls", "NowPlayingBar.xaml");

    private static string BarSource() => ReadRepoFile("src", "DawnPlayer.App", "Controls", "NowPlayingBar.xaml.cs");

    /// <summary>The ToggleMiniMode method body (enter + exit branches).</summary>
    private static string ToggleMiniModeBody()
    {
        var source = MainWindowSource();
        var start = source.IndexOf("public void ToggleMiniMode()", StringComparison.Ordinal);
        Assert.True(start >= 0, "ToggleMiniMode not found");
        var end = source.IndexOf("private Microsoft.UI.Windowing.AppWindow? GetAppWindow()", start, StringComparison.Ordinal);
        Assert.True(end > start, "ToggleMiniMode body end marker not found");
        return source[start..end];
    }

    // ---------------- M1: caption drag ----------------

    [Fact]
    public void MiniDrag_UsesNativeNonClientCaptionRegion()
    {
        var source = MainWindowSource();

        // 드래그는 XAML 포인터 이벤트(수동 추적·WM_NCLBUTTONDOWN 모달 루프 모두 이 창의 NC
        // 입력 싱크에 press 후 스트림이 끊겨 신뢰 불가 — 2026-10-08 실측)가 아니라
        // InputNonClientPointerSource Caption 영역(시스템 네이티브 드래그)으로 시스템에 맡긴다.
        Assert.Contains("InputNonClientPointerSource.GetForWindowId", source, StringComparison.Ordinal);
        Assert.Contains("NonClientRegionKind.Caption", source, StringComparison.Ordinal);
        Assert.Contains("NonClientRegionKind.Passthrough", source, StringComparison.Ordinal);
        Assert.Contains("CollectPassthroughRects(", source, StringComparison.Ordinal);

        // 컨트롤 구멍은 트리 순회로 계산한다 — UserControl 셸은 표면이고, 컨트롤 사각형이
        // 템플릿 자식을 커버하므로 컨트롤 안으로는 재귀하지 않는다.
        var walk = source.IndexOf("private static void CollectPassthroughRects", StringComparison.Ordinal);
        Assert.True(walk >= 0, "CollectPassthroughRects helper not found");
        var walkEnd = source.IndexOf("private void OnRootDoubleTapped", walk, StringComparison.Ordinal);
        Assert.True(walkEnd > walk, "CollectPassthroughRects body end not found");
        var body = source[walk..walkEnd];
        Assert.Contains("is Microsoft.UI.Xaml.Controls.Control", body, StringComparison.Ordinal);
        Assert.Contains("is Microsoft.UI.Xaml.Controls.UserControl", body, StringComparison.Ordinal);
        Assert.Contains("continue; // 컨트롤 내부는 재귀하지 않는다", body, StringComparison.Ordinal);

        // 복귀 시 Caption 해제가 ToggleMiniMode 안에 있어야 한다 — 남으면 일반 창 전체가
        // 시스템 캡션이 된다.
        var body2 = ToggleMiniModeBody();
        Assert.Contains("ApplyMiniNonClientRegions();", body2, StringComparison.Ordinal);
        Assert.True(body2.IndexOf("ApplyMiniNonClientRegions();", StringComparison.Ordinal) >= 0);

        // 구 수동 추적·모달 루프의 재유입 금지.
        Assert.DoesNotContain("0xA1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CapturePointer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerMoved=\"OnRootPointerMoved\"", MainWindowXaml(), StringComparison.Ordinal);
    }

    [Fact]
    public void MiniRestoreButton_VisibleOnlyInMiniState_WiredToToggle()
    {
        // 미니의 발견 가능한 포인터 탈출구 — 표면이 시스템 캡션이라 컨텍스트 메뉴가 닿지 않는
        // 자리를 이 버튼이 담당한다(2026-10-08).
        var xaml = BarXaml();
        var btn = xaml.IndexOf("x:Name=\"MiniRestoreButton\"", StringComparison.Ordinal);
        Assert.True(btn >= 0, "MiniRestoreButton missing from NowPlayingBar.xaml");

        var miniAt = xaml.IndexOf("<VisualState x:Name=\"MiniVolume\"", StringComparison.Ordinal);
        Assert.True(miniAt > 0, "MiniVolume state missing");
        var miniEnd = xaml.IndexOf("</VisualState>", miniAt, StringComparison.Ordinal);
        Assert.Contains("Target=\"MiniRestoreButton.Visibility\"", xaml[miniAt..miniEnd], StringComparison.Ordinal);

        // 기본 상태(비미니)에서는 숨김 — Compact/Wide 세터가 없고 요소 기본값이 Collapsed.
        var elemStart = xaml.IndexOf("<Button x:Uid=\"NowPlaying_MiniRestore\"", StringComparison.Ordinal);
        var elemEnd = xaml.IndexOf('>', elemStart);
        var elemDecl = xaml.Substring(elemStart, elemEnd - elemStart);
        Assert.Contains("Visibility=\"Collapsed\"", elemDecl, StringComparison.Ordinal);

        var source = BarSource();
        Assert.Contains("MiniRestoreRequested", source, StringComparison.Ordinal);

        var main = MainWindowSource();
        Assert.Contains("PlayerBar.MiniRestoreRequested += (_, _) => ToggleMiniMode();", main, StringComparison.Ordinal);
    }

    [Fact]
    public void MiniDrag_LeftButtonOnly_BarShellIsSurface_InteractiveControlsExempt()
    {
        var source = MainWindowSource();

        // 표면 우클릭은 시스템 캡션 영역이 우선하므로 남는 XAML 우클릭 경로(컨트롤 구멍)는
        // 좌클릭 가드의 반대인 인터랙티브 판정만 남는다 — 기하 히트테스트 필수(위조 사슬 방어).
        var geo = source.IndexOf("FindElementsInHostCoordinates(point, RootGrid)", StringComparison.Ordinal);
        Assert.True(geo >= 0, "surface input eligibility must use geometric hit-testing — the ancestor walk " +
            "is fooled by the NC input sink's aliased chain");
        var shell = source.IndexOf("is Microsoft.UI.Xaml.Controls.UserControl", StringComparison.Ordinal);
        var control = source.IndexOf("is Microsoft.UI.Xaml.Controls.Control", StringComparison.Ordinal);
        Assert.True(shell >= 0 && control > shell,
            "the check must pass the UserControl shell before the Control check — treating the " +
            "bar shell as an owner leaves the mini window without any drag surface (M1)");
    }

    // ---------------- M2/M3/M6/M7/M10: enter/exit invariants ----------------

    [Fact]
    public void MiniEntry_OrderInvariants()
    {
        var body = ToggleMiniModeBody();

        var restored = body.IndexOf("presenter.Restore();", StringComparison.Ordinal);
        var lift = body.IndexOf("PreferredMinimumWidth = 0;", StringComparison.Ordinal);
        var resize = body.IndexOf("ResizeClient(", StringComparison.Ordinal);
        Assert.True(restored >= 0 && restored < resize,
            "maximized must leave the zoomed state before the resize (M6) — a zoomed window " +
            "keeps its shell bounds and ignores the mini size (OverlappedPresenter.State is " +
            "read-only in this SDK projection, so Restore() is the mechanism)");
        Assert.True(lift >= 0 && lift < resize,
            "PreferredMinimumWidth must lift BEFORE the resize (M7) — applied after, the shell " +
            "can clamp the mini size right back to 620");

        // M2: 물리 픽셀 하드코딩 금지 — 논리 설계 × 실시간 DPI 스케일.
        Assert.Contains("MiniPlayerPlacement.PhysicalSize(", body, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(body, @"ResizeClient\(new (Windows\.Graphics\.)?SizeInt32\(\d"),
            "mini size must come from MiniPlayerPlacement.PhysicalSize(scale), not hard-coded " +
            "physical pixels (M2 — 150% scale laid the bar out at 2/3 size)");

        // M3: 거터 32 — 0으로 묻으면 시스템 캡션이(ECTB 아래 계속 그려진다) 바를 덮는다.
        Assert.Contains("MiniPlayerPlacement.CaptionGutterHeight", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RowDefinitions[0].Height = new GridLength(0);", body, StringComparison.Ordinal);

        // M10: InfoBar가 0높이 행에 갇히지 않게 오버레이로 펼치고 복귀 시 되돌린다.
        Assert.Contains("SetRowSpan(NotifyBar, 3)", body, StringComparison.Ordinal);
        Assert.Contains("SetRowSpan(NotifyBar, 1)", body, StringComparison.Ordinal);

        // Standard(32) 캡션 전환은 ResizeClient보다 앞서야 한다 — 뒤에서 바꾸면 캡션 높이만큼
        // 클라이언트가 다시 늘어나 하단에 죽은 공간이 생긴다(2026-10-08 실측 +40물리 px).
        var standard = body.IndexOf("TitleBarHeightOption.Standard", StringComparison.Ordinal);
        Assert.True(standard >= 0 && standard < resize,
            "PreferredHeightOption.Standard must be applied BEFORE the client resize — after it, " +
            "the option change re-grows the client by one caption strip");

        // 메뉴 경로 진입 시 플라이아웃 고아 방지: 앵커(타이틀바)가 무너진 뒤 열려 남은 메뉴의
        // 투명 라이트 디스미스 장벽이 창 전체 press를 삼킨다(2026-10-08 실측 — 드래그·클릭 전멸).
        var hideFlyout = body.IndexOf("TitleMenuFlyout.Hide();", StringComparison.Ordinal);
        Assert.True(hideFlyout >= 0 && hideFlyout < resize,
            "ToggleMiniMode must close the title menu flyout before reshaping the window — an " +
            "orphaned flyout barrier eats every subsequent press");
    }

    [Fact]
    public void MiniEntry_ParksFocusForEscapeAndShortcuts()
    {
        // M4: Escape·전역 단축키는 RootGrid.KeyDown을 탄다 — 메뉴로 진입하면 포커스가 죽은
        // 요소에 남아 이벤트가 루트에 도달하지 않았다. 루트를 탭 스톱으로 세워 포커스를 준다.
        var source = MainWindowSource();
        Assert.Contains("private void EnterMiniFocus()", source, StringComparison.Ordinal);
        Assert.Contains("RootGrid.IsTabStop = true;", source, StringComparison.Ordinal);
        Assert.Contains("RootGrid.Focus(FocusState.Programmatic)", source, StringComparison.Ordinal);

        var body = ToggleMiniModeBody();
        Assert.Contains("EnterMiniFocus", body, StringComparison.Ordinal);

        // 복귀 시 탭 순서 오염을 되돌린다.
        Assert.Contains("RootGrid.IsTabStop = false;", source, StringComparison.Ordinal);
    }

    // ---------------- M4: pointer-only escape hatches ----------------

    [Fact]
    public void RootGrid_WiresContextMenu_DoubleClickRestore_Escape()
    {
        var xaml = MainWindowXaml();
        Assert.Contains("RightTapped=\"OnRootRightTapped\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DoubleTapped=\"OnRootDoubleTapped\"", xaml, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"OnRootKeyDown\"", xaml, StringComparison.Ordinal);
        // 포인터 드래그 배선은 의도적으로 없다 — 표면은 NC Caption 영역(시스템 소유)이고
        // XAML 포인터 파이프라인은 이 창에서 신뢰되지 않았다(2026-10-08 실측).
        Assert.DoesNotContain("PointerPressed=\"OnRootPointerPressed\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MiniContextMenu_OffersRestore_AlwaysOnTop_Fullscreen_Exit()
    {
        // 드래그만으로 창을 옮기게 강요하는 것(WCAG 2.2 Dragging Movements 위반)과 Escape
        // 의존(M4)의 대체 수단 — 포인터만으로 도달 가능한 메뉴가 네 기능을 모두 제공해야 한다.
        var source = MainWindowSource();
        Assert.Contains("Mini_Restore.Text", source, StringComparison.Ordinal);
        Assert.Contains("Mini_AlwaysOnTop.Text", source, StringComparison.Ordinal);
        Assert.Contains("MainWindow_Menu_Fullscreen.Text", source, StringComparison.Ordinal);
        Assert.Contains("MainWindow_Menu_Exit.Text", source, StringComparison.Ordinal);
        Assert.Contains("IsAlwaysOnTop", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleClickRestore_IgnoresInteractiveControls()
    {
        // 재생 버튼 연타가 복원으로 이어지지 않게 — 이중 탭 가드도 IsInteractivePress를 쓴다.
        // (표면 대부분은 Caption 영역이라 시스템이 우선하지만, 도달하는 경로는 성실하게 처리.)
        var source = MainWindowSource();
        var start = source.IndexOf("private void OnRootDoubleTapped", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnRootDoubleTapped not found");
        var body = source[start..source.IndexOf("private void OnRootRightTapped", start, StringComparison.Ordinal)];
        Assert.Contains("IsInteractivePress", body, StringComparison.Ordinal);
        Assert.Contains("ToggleMiniMode()", body, StringComparison.Ordinal);
    }

    // ---------------- M5: MiniVolume state ----------------

    [Fact]
    public void MiniVolumeState_CarriesFullCompactShed_AndVolumeWithoutLyrics()
    {
        var xaml = BarXaml();
        var compactAt = xaml.IndexOf("<VisualState x:Name=\"Compact\"", StringComparison.Ordinal);
        var miniAt = xaml.IndexOf("<VisualState x:Name=\"MiniVolume\"", StringComparison.Ordinal);
        Assert.True(compactAt >= 0, "Compact state missing (MiniPlayerLayoutTests owns it)");
        Assert.True(miniAt > compactAt,
            "MiniVolume must be declared after Compact — it is the code-driven override of the " +
            "trigger-selected Compact state, not a sibling breakpoint");

        var end = xaml.IndexOf("</VisualState>", miniAt, StringComparison.Ordinal);
        var block = xaml[miniAt..end];

        // GoToState(MiniVolume)는 직전 상태의 세터를 통째로 복원한다 — 세딩 없는 부분
        // 오버라이드는 Wide 나머지를 600px에 찌그러뜨린다(2026-10-08 실측). Compact 세딩을
        // 미니 상태가 직접 소유해야 한다.
        Assert.Contains("Target=\"FormatBadge.Visibility\"", block, StringComparison.Ordinal);
        Assert.Contains("Target=\"OutputBadge.Visibility\"", block, StringComparison.Ordinal);
        Assert.Contains("Target=\"TrackArtist.Visibility\"", block, StringComparison.Ordinal);
        Assert.Contains("Target=\"ArtButton.Width\"", block, StringComparison.Ordinal);
        Assert.Contains("Target=\"ArtButton.Height\"", block, StringComparison.Ordinal);
        Assert.Contains("Target=\"RootLayout.Padding\"", block, StringComparison.Ordinal);
        Assert.Contains("Target=\"TransportPanel.Spacing\"", block, StringComparison.Ordinal);
        Assert.Contains("Target=\"ToolsPanel.Spacing\"", block, StringComparison.Ordinal);

        // 미니 차이: 슬라이더는 되살리고(뮤트 전용 미니 금지), 미니에서 도달 불가능한 가사
        // 팬 토글은 숨긴다.
        Assert.Contains("Target=\"VolumeSliderHost.Visibility\"", block, StringComparison.Ordinal);
        Assert.Contains("Value=\"Visible\"", block, StringComparison.Ordinal);
        Assert.Contains("Target=\"LyricsButton.Visibility\"", block, StringComparison.Ordinal);
        Assert.Contains("Value=\"Collapsed\"", block, StringComparison.Ordinal);

        // 트리거 없음 — 폭 트리거를 달면 좁은 일반 창(620–730)까지 오염된다. 코드 GoToState 전용.
        Assert.DoesNotContain("StateTriggers", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Bar_AppliesAndReAssertsMiniContext_OverTriggerPasses()
    {
        var source = BarSource();
        Assert.Contains("public void ApplyMiniContext(bool", source, StringComparison.Ordinal);
        Assert.Contains("\"MiniVolume\"", source, StringComparison.Ordinal);

        // AdaptiveTrigger 재평가가 미니 문맥을 덮어쓰므로 폭이 바뀔 때마다(OnRootSizeChanged)
        // 코드 측 볼륨 표시도 함께 재단언해야 한다.
        var sizeChanged = source.IndexOf("private void OnRootSizeChanged", StringComparison.Ordinal);
        Assert.True(sizeChanged >= 0, "OnRootSizeChanged not found");
        var body = source[sizeChanged..source.IndexOf("\n    }", sizeChanged, StringComparison.Ordinal)];
        Assert.Contains("_miniContext", body, StringComparison.Ordinal);
    }

    // ---------------- M8: shutdown placement ----------------

    [Fact]
    public void Shutdown_RestoresNormalPlacement_BeforeSaving()
    {
        // 미니 클라이언트(600×128)를 WindowWidth/Height로 굳히면 재시작 시 풀 UI가 최소폭
        // 620에 으깨진다 — 저장 전에 미니 배치를 복원한다.
        var source = MainWindowSource();
        var start = source.IndexOf("private void ShutdownForReal()", StringComparison.Ordinal);
        Assert.True(start >= 0, "ShutdownForReal not found");
        var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];
        var exitMini = body.IndexOf("if (_isMiniMode) ToggleMiniMode();", StringComparison.Ordinal);
        var save = body.IndexOf("SessionManager.Shutdown(", StringComparison.Ordinal);
        Assert.True(exitMini >= 0 && save > exitMini,
            "ShutdownForReal must exit mini mode BEFORE SessionManager.Shutdown persists placement (M8)");
    }

    // ---------------- pure geometry (M2/M3 invariants) ----------------

    [Theory]
    [InlineData(1.0, 600, 128)]
    [InlineData(1.25, 750, 160)]
    [InlineData(1.5, 900, 192)]
    [InlineData(2.0, 1200, 256)]
    public void PhysicalSize_ScalesLogicalDesign_AtCommonScales(double scale, int expectedW, int expectedH)
    {
        var (w, h) = DawnPlayer.App.Helpers.MiniPlayerPlacement.PhysicalSize(scale);
        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);

        // 불변식: 반올림 오차는 0.5 물리 px 이하 — 고DPI에서 2/3로 찌그러지던 M2의 재발 금지.
        Assert.True(Math.Abs(w / scale - DawnPlayer.App.Helpers.MiniPlayerPlacement.LogicalWidth) <= 0.5);
        Assert.True(Math.Abs(h / scale - DawnPlayer.App.Helpers.MiniPlayerPlacement.LogicalHeight) <= 0.5);
    }

    [Fact]
    public void MiniGeometry_CaptionNeverOverlapsBar()
    {
        // M3 불변식: 창 높이 = 캡션 거터 + 바 — 캡션 버튼이 바 콘텐츠 위에 떠 있을 수 없다.
        Assert.Equal(
            DawnPlayer.App.Helpers.MiniPlayerPlacement.CaptionGutterHeight + DawnPlayer.App.Helpers.MiniPlayerPlacement.BarLogicalHeight,
            DawnPlayer.App.Helpers.MiniPlayerPlacement.LogicalHeight);
        Assert.Equal(96, DawnPlayer.App.Helpers.MiniPlayerPlacement.BarLogicalHeight); // NowPlayingBar MinHeight 계약
    }

    [Fact]
    public void MiniGeometry_WidthFitsCompactBarWithVolumeSlider()
    {
        // Compact 바 자연 최소폭(패딩 24 + 아트열 52 + 트랜스포트 7버튼 276 + 도구[뮤트+슬라이더
        // +큐] 176 ≈ 528) + 제목 여유 — 이보다 좁으면 컨트롤이 잘린다(M5/M9).
        Assert.True(DawnPlayer.App.Helpers.MiniPlayerPlacement.LogicalWidth >= 560,
            $"mini width {DawnPlayer.App.Helpers.MiniPlayerPlacement.LogicalWidth} clips the compact bar with the volume slider");
    }

    [Fact]
    public void MiniMinimumWidth_StaysBelowNormalMinimum()
    {
        // 미니가 정상 최소폭(620) 이상으로 클램프되면 미니가 아니다.
        Assert.True(DawnPlayer.App.Helpers.MiniPlayerPlacement.MiniPreferredMinWidth < 620);
    }
}
