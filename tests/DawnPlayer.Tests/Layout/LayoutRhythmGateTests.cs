using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// L14 레이아웃 리듬 게이트 (2026-10-04 "전부 승인"). L13 사이드바 전환에서 Network 콘텐츠
/// 인셋이 소실된 것(F1, P0)을 시발점으로 한 간격 일관성 감사 — 6개 XAML 파일의 리터럴 164건
/// 전수 조사 결과(implementation_plan.md L14)의 재발 방지 계약:
///   * 의미 인셋 토큰 — PageContentPadding(20,14,20,16: Network 콘텐츠 열),
///     PageHeaderMargin(20,14,20,6: Playlist 헤더 원점)이 DesignTokens에 정의되고 실제로
///     소비돼야 한다(토큰 정의만 있고 소비가 없으면 죽은 토큰).
///   * 버튼 패딩 패밀리 — "10,5"/"14,5"(세로 5 = 오프그리드) 퇴출, 아이콘-라벨 Spacing 7 퇴출.
///   * 사이드바 헤더 원점 통일 — Library 트리 헤더도 16,14로(Playlist/Network와 동일).
///   * 스플리터 히트존 통일 — 10px/-5 오프셋 패턴 퇴출(8px/-4로).
/// </summary>
public class LayoutRhythmGateTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) return dir;
        }
        return null;
    }

    private static string ReadApp(DirectoryInfo root, string relative) =>
        File.ReadAllText(Path.Combine(root.FullName, "src", "DawnPlayer.App", relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void PageInsetTokens_AreDefinedAndConsumed()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; rhythm gates need a source checkout");

        var tokens = ReadApp(root!, "Styles/DesignTokens.xaml");
        Assert.Contains("x:Key=\"PageContentPadding\"", tokens, StringComparison.Ordinal);
        Assert.Contains(">20,14,20,16<", tokens, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"PageHeaderMargin\"", tokens, StringComparison.Ordinal);
        Assert.Contains(">20,14,20,6<", tokens, StringComparison.Ordinal);

        // 정의만 있고 소비가 없으면 죽은 토큰 — 두 소비 지점을 고정한다.
        var network = ReadApp(root!, "Views/NetworkPage.xaml");
        Assert.Contains("Padding=\"{ThemeResource PageContentPadding}\"", network, StringComparison.Ordinal);
        var playlist = ReadApp(root!, "Views/PlaylistPage.xaml");
        Assert.Contains("Margin=\"{ThemeResource PageHeaderMargin}\"", playlist, StringComparison.Ordinal);

        // F1 재발 방지: Network 콘텐츠에 오프-토큰 하드코딩 인셋이 돌아오지 않는다.
        Assert.DoesNotContain("Padding=\"24,16,24,12\"", network, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistHeader_LeavesOffGrid22()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);
        var playlist = ReadApp(root!, "Views/PlaylistPage.xaml");
        // F8: 헤더 22 → PageHeaderMargin(20) 정규화. 오프그리드 22 헤더의 재유입 금지.
        Assert.DoesNotContain("Margin=\"22,14,22,6\"", playlist, StringComparison.Ordinal);
    }

    [Fact]
    public void SmallButtons_UseVertical6_AndIconSpacing8()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        // F13: "10,5"/"14,5"(세로 5 = 오프그리드)와 아이콘-라벨 Spacing 7의 재유입 금지.
        var offenders = new List<string>();
        foreach (var relative in new[]
        {
            "Views/LibraryPage.xaml", "Views/PlaylistPage.xaml", "Views/NetworkPage.xaml",
            "Views/Network/RadioSection.xaml", "Views/Network/DlnaSection.xaml", "Views/Network/YouTubeSection.xaml",
        })
        {
            var text = ReadApp(root!, relative);
            if (text.Contains("Padding=\"10,5\"", StringComparison.Ordinal))
                offenders.Add($"{relative}: Padding=\"10,5\"");
            if (text.Contains("Padding=\"14,5\"", StringComparison.Ordinal))
                offenders.Add($"{relative}: Padding=\"14,5\"");
            if (text.Contains("Spacing=\"7\"", StringComparison.Ordinal))
                offenders.Add($"{relative}: Spacing=\"7\"");
        }
        Assert.True(offenders.Count == 0,
            "작은 버튼 세로 패딩은 6(10,6·14,6), 아이콘-라벨 간격은 8로:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void LibrarySidebarHeader_AlignsOnSidebarRhythm()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);
        var library = ReadApp(root!, "Views/LibraryPage.xaml");

        // F9: Library 트리 헤더도 Playlist/Network 사이드바 헤더 원점(16,14)으로.
        Assert.DoesNotContain("Margin=\"12,10,12,6\"", library, StringComparison.Ordinal);
        Assert.Contains("Margin=\"16,14,14,8\"", library, StringComparison.Ordinal);

        // F11: 스플리터 히트존 10px/-5 오프셋 패턴 퇴출(8px/-4로 통일). Width="10" 자체는
        // 트리 셰브런 템플릿 등 정당한 용례가 있어 스플리터 결합 패턴만 민다.
        Assert.DoesNotContain("Width=\"10\" Margin=\"0,0,-5,0\"", library, StringComparison.Ordinal);
    }
}
