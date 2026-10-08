using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Follow System crash gate (2026-10-05). Assigning <c>ElementTheme.Default</c> to the root of a
/// live, rendered window (theme switched to "Follow System" while running) access-violates inside
/// Microsoft.UI.Xaml.dll — 0xC0000005 at offset 0x65f458 (v1.6.1 release) / 0x65f45b (Debug, same
/// function), reproduced 3/3. The contrast experiment pinned the cause: Dark→System crashed every
/// time while Dark→Light — the identical pipeline (palette + backdrop re-assignment + title bar)
/// minus the Default assignment — survived and persisted settings, and launching with
/// Theme=System (single Default assignment on a fresh tree) was also fine. Fix: ApplyTheme pins
/// the OS-resolved concrete theme (Light/Dark from the live UISettings watcher) and the HC branch
/// does the same; OS flips reach Follow System through the watcher's re-apply instead of Default
/// resolution. Like MainWindowTitleBarLayoutTests, this is a source-scan contract: reintroducing
/// a Default assignment into the theme pipeline is a regression even though it compiles.
/// </summary>
public class ThemeServiceThemePinGateTests
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

    [Fact]
    public void ThemeService_NeverAssignsElementThemeDefault()
    {
        // 크래시 재유입 금지: System 분기와 고대비 분기 모두 구체값(Light/Dark)만 대입한다.
        // Default "대입문"은 살아있는 트리에서 네이티브 AV를 일으킨다(클래스 요약의 실험
        // 매트릭스). 주석의 토큰 언급은 위험하지 않으므로 대입 패턴만 검사한다.
        var source = ReadRepoFile("src", "DawnPlayer.App", "Services", "ThemeService.cs");
        var assignment = new Regex(@"RequestedTheme\s*=\s*ElementTheme\.Default", RegexOptions.Multiline);
        Assert.True(!assignment.IsMatch(source),
            "ThemeService must never assign ElementTheme.Default to a live tree — it AVs inside " +
            "Microsoft.UI.Xaml.dll (0xC0000005, 2026-10-05 follow-system crash). Pin a concrete " +
            "Light/Dark resolved from IsOsLightTheme() instead.");
    }

    [Fact]
    public void FollowSystem_PinsConcreteTheme_FromLiveOsSignal()
    {
        var theme = ReadRepoFile("src", "DawnPlayer.App", "Services", "ThemeService.cs");

        // System 핀은 살아있는 OS 신호에서 온다 — Application.RequestedTheme는 기동 시 1회만
        // 해석되므로 라이브 추적이 불가능하고, 와처가 그 역할을 대신한다.
        Assert.Contains("_ => IsOsLightTheme() ? ElementTheme.Light : ElementTheme.Dark", theme, StringComparison.Ordinal);
        Assert.Contains("ColorValuesChanged", theme, StringComparison.Ordinal);
        Assert.Contains("GetColorValue(Windows.UI.ViewManagement.UIColorType.Background)", theme, StringComparison.Ordinal);

        // OS 전환 재적용은 System 모드에서만 — 명시 모드의 핀을 깨지 않는다(구 핸들러와 동일 가드).
        var handler = theme[theme.IndexOf("watch.ColorValuesChanged", StringComparison.Ordinal)..];
        Assert.Contains("ThemeMode.System", handler);
    }

    [Fact]
    public void HighContrastBranch_AlsoPinsConcreteTheme()
    {
        var theme = ReadRepoFile("src", "DawnPlayer.App", "Services", "ThemeService.cs");
        Assert.Contains("hcElement.RequestedTheme = IsOsLightTheme() ? ElementTheme.Light : ElementTheme.Dark;", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_DoesNotRelyOnActualThemeChangedForOsFollow()
    {
        // 구 OS 추적 경로(루트 ActualThemeChanged 구독)는 루트가 핀으로 고정된 뒤 발화하지 않는
        // 죽은 메커니즘 — 재유입은 "OS 따르기가 가끔만 동작하는" 정확한 버그가 된다. 주석 언급은
        // 위험하지 않으므로 구독(+=) 배선만 금지한다.
        var source = ReadRepoFile("src", "DawnPlayer.App", "MainWindow.xaml.cs");
        var wiring = new Regex(@"ActualThemeChanged\s*\+=", RegexOptions.Multiline);
        Assert.True(!wiring.IsMatch(source),
            "The root is pinned to a concrete theme, so ActualThemeChanged never signals OS flips — " +
            "ThemeService's UISettings watcher owns Follow System tracking (2026-10-05 crash fix).");
    }
}
