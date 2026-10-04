using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// L16 Network 헤더 통일 게이트 (2026-10-04). Radio·YouTube·DLNA 섹션 헤더가 하나의 3존 골격을
/// 공유하는지 소스 스캔으로 고정한다(OutlineUnityGateTests와 같은 방식 — 페이지 XAML은 테스트
/// 프로젝트에 링크될 수 없다):
///   * 골격 — 헤더 행은 필러 `*` 컬럼을 가진 Grid다. 아이콘(14px accent)+SectionHeaderText
///     (Margin 8,0,0,0)가 좌측, 주 액션 버튼(Padding 10,6·R4)이 마지막 컬럼의 우측 끝 앵커다.
///     액션이 제목 바로 옆에 붙는 인라인 StackPanel 헤더(구 YouTube)와 헤더 밖 툴바 행(구 DLNA)
///     의 재유입 금지.
///   * 메타 — 카운트·상태·배지 같은 상태 메타는 우측 클러스터(액션 직전, 12px 간격)에 산다.
///   * 위계 — 섹션 제목(14 SemiBold Primary)과 그룹 라벨(12 SemiBold Secondary)은 다른 단계다.
/// </summary>
public class NetworkHeaderUnityGateTests
{
    private static readonly (string File, string ActionMarker, bool ActionIsUid)[] Sections =
    {
        ("Views/Network/RadioSection.xaml", "AddStationButton", false),
        ("Views/Network/DlnaSection.xaml", "Network_Dlna_Refresh", true),
        ("Views/Network/YouTubeSection.xaml", "PathsButton", false),
    };

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

    /// <summary>헤더 Grid 여는 태그 — 세 섹션 모두 파일에서 첫 Grid가 헤더 행이다. 실패 시 null.</summary>
    private static Match? HeaderStart(string text) =>
        Regex.Match(text, "<Grid Grid\\.Row=\"0\">", RegexOptions.Singleline) is { Success: true } m ? m : null;

    [Fact]
    public void SectionHeaders_ShareOneThreeZoneSkeleton()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; header gates need a source checkout");

        foreach (var (file, actionMarker, actionIsUid) in Sections)
        {
            var text = ReadApp(root!, file);

            // 1) 헤더 뿌리는 Grid + 컬럼 정의(= 인라인 StackPanel 헤더의 반대).
            var header = HeaderStart(text);
            Assert.True(header != null, $"{file}: header grid <Grid Grid.Row=\"0\"> not found");
            var headerStart = header!.Index;
            var defs = Regex.Match(text[headerStart..], "<Grid\\.ColumnDefinitions>.*?</Grid\\.ColumnDefinitions>",
                RegexOptions.Singleline);
            Assert.True(defs.Success, $"{file}: header needs explicit ColumnDefinitions");
            Assert.Contains("<ColumnDefinition Width=\"*\"/>", defs.Value, StringComparison.Ordinal);

            // 2) 아이콘(14px accent)이 제목보다 먼저, 둘 다 헤더 안에.
            var icon = Regex.Match(text, "<FontIcon Glyph=\"[^\"]+\" FontSize=\"14\" Foreground=\"\\{ThemeResource DawnAccentTextBrush\\}\"");
            Assert.True(icon.Success && icon.Index > headerStart, $"{file}: 14px accent header icon not found");
            var title = Regex.Match(text, "<TextBlock[^>]*SectionHeaderText[^>]*/>", RegexOptions.Singleline);
            Assert.True(title.Success, $"{file}: SectionHeaderText title not found");
            Assert.True(title.Index > icon.Index, $"{file}: title must follow the icon");
            Assert.Contains("Margin=\"8,0,0,0\"", title.Value, StringComparison.Ordinal);

            // 3) 주 액션은 마지막 컬럼(우측 끝 앵커)의 작은 버튼 계약(10,6·R4).
            var actionPattern = actionIsUid
                ? $"<Button x:Uid=\"{actionMarker}\"[^>]*>"
                : $"<Button[^>]*x:Name=\"{actionMarker}\"[^>]*>";
            var action = Regex.Match(text, actionPattern, RegexOptions.Singleline);
            Assert.True(action.Success && action.Index > title.Index, $"{file}: header action '{actionMarker}' not found after title");
            Assert.Contains("Padding=\"10,6\"", action.Value, StringComparison.Ordinal);
            Assert.Contains("CornerRadius=\"4\"", action.Value, StringComparison.Ordinal);

            var columnCount = Regex.Count(defs.Value, "<ColumnDefinition");
            var lastColumn = columnCount - 1;
            Assert.True(lastColumn > 0, $"{file}: header needs more than one column");
            Assert.Contains($"Grid.Column=\"{lastColumn}\"", action.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HeaderMeta_ClustersBeforeAction_WithActionGap()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        // Radio: 방송국 수, YouTube: 의존성 배지 — 같은 종류의 상태 메타는 같은 자리(액션 직전,
        // 12px 간격)에. 제목 바로 옆 인라인 배치(구 YouTube)의 재유입 금지.
        var cases = new (string File, string MetaMarker, string ActionMarker)[]
        {
            ("Views/Network/RadioSection.xaml", "StationCountText", "AddStationButton"),
            ("Views/Network/YouTubeSection.xaml", "DependencyBadge", "PathsButton"),
        };
        foreach (var (file, metaMarker, actionMarker) in cases)
        {
            var text = ReadApp(root!, file);
            var meta = Regex.Match(text, $"<TextBlock[^>]*x:Name=\"{metaMarker}\"[^>]*/?>", RegexOptions.Singleline);
            Assert.True(meta.Success, $"{file}: header meta '{metaMarker}' not found");
            Assert.Contains("Margin=\"0,0,12,0\"", meta.Value, StringComparison.Ordinal);

            var title = Regex.Match(text, "<TextBlock[^>]*SectionHeaderText[^>]*/>", RegexOptions.Singleline);
            var action = text.IndexOf(actionMarker, StringComparison.Ordinal);
            Assert.True(title.Success && action >= 0);
            Assert.True(meta.Index > title.Index && meta.Index < action,
                $"{file}: header meta must sit in the right cluster (after title, before action)");
        }
    }

    [Fact]
    public void GroupLabelScale_IsDefinedBelowSectionScale()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        // 위계 사다리: SectionHeaderText(14 SemiBold Primary) > GroupHeaderText(12 SemiBold
        // Secondary) > ColumnHeaderText(11.5 SemiBold Tertiary, 테이블 컬럼).
        var theme = ReadApp(root!, "DawnTheme.xaml");
        var sectionStart = theme.IndexOf("x:Key=\"SectionHeaderText\"", StringComparison.Ordinal);
        var groupStart = theme.IndexOf("x:Key=\"GroupHeaderText\"", StringComparison.Ordinal);
        Assert.True(sectionStart >= 0, "SectionHeaderText missing from DawnTheme.xaml");
        Assert.True(groupStart > sectionStart, "GroupHeaderText must be defined next to SectionHeaderText");

        var styleBlock = theme[groupStart..theme.IndexOf("</Style>", groupStart, StringComparison.Ordinal)];
        Assert.Contains("Property=\"FontSize\" Value=\"12\"", styleBlock, StringComparison.Ordinal);
        Assert.Contains("Property=\"FontWeight\" Value=\"SemiBold\"", styleBlock, StringComparison.Ordinal);
        Assert.Contains("Property=\"Foreground\" Value=\"{ThemeResource TextSecondaryBrush}\"", styleBlock, StringComparison.Ordinal);

        // 창시 사례 고정: YouTube "최근 항목"은 그룹 라벨 단계를 쓴다(섹션 제목과 동급 금지).
        var youtube = ReadApp(root!, "Views/Network/YouTubeSection.xaml");
        var recentLine = youtube[(youtube.IndexOf("Network_YouTube_RecentHeader", StringComparison.Ordinal))..];
        recentLine = recentLine[..recentLine.IndexOf('\n')];
        Assert.Contains("GroupHeaderText", recentLine, StringComparison.Ordinal);
    }
}
