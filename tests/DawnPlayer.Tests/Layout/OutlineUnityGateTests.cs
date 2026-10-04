using System.Text.RegularExpressions;
using DawnPlayer.App.Styles;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// L13 아웃라인 통일 게이트 (2026-10-04, 변형 A + Playlist Italic 제거 승인). XAML 페이지는
/// 테스트 프로젝트에 링크될 수 없으므로 PaletteConsistencyTests/LibraryTreeContentGateTests와
/// 같은 소스 스캔 방식으로 세 페이지+Network 섹션의 아웃라인 계약을 고정한다:
///   * 반경 스케일 — 리터럴 CornerRadius(속성·Setter 양형)는 0/1/2/4/5만 허용. 3(구 드로어·
///     하이라이트·세그먼트)과 6(구 Network 박스·썸네일)의 재유입 금지. 캐노니컬 값의 소유자는
///     DesignTokenValues.Radius이며 이 게이트가 그 값을 참조한다.
///   * Network 구조 — 섹션 탭(EoleNavTabStyle)·페이지 타이틀(Network_PageHeader) 퇴출, 데이터
///     소스 사이드바(SourceList) 유지. Radio/DLNA 리스트는 플랫(박스 속성 금지) + 미디어 행 44.
///   * 타이포 — 텍스트 요소의 FontSize는 토큰 참조만 허용(글리프 메트릭스인 FontIcon과 고정
///     Width·Height 글리프 상자는 예외). 섹션 헤더는 SectionHeaderText(Italic 없음)로 수렴하고,
///     섹션 안의 그룹 라벨은 한 단계 아래 GroupHeaderText로 강등된다(L16 — NetworkHeaderUnity
///     게이트 참조).
///   * 구분선 역할 — 1px 헤어라인은 SeparatorSubtleBrush만. BorderSubtle은 박스·이미지 외곽 전용.
/// </summary>
public class OutlineUnityGateTests
{
    private static readonly string[] ScopedFiles =
    {
        "Views/LibraryPage.xaml",
        "Views/PlaylistPage.xaml",
        "Views/NetworkPage.xaml",
        "Views/Network/RadioSection.xaml",
        "Views/Network/DlnaSection.xaml",
        "Views/Network/YouTubeSection.xaml",
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

    private static int LineOf(string text, int index) => text.Substring(0, index).Count(c => c == '\n') + 1;

    private static readonly Regex TagRegex = new("<[A-Za-z]+[^>]*>", RegexOptions.Singleline);
    private static readonly Regex CornerRadiusAttr = new("CornerRadius=\"([0-9.,]+)\"");
    private static readonly Regex CornerRadiusSetter = new("Property=\"CornerRadius\"\\s+Value=\"([0-9.,]+)\"");

    private static bool IsAllowedRadius(string raw, out string offender)
    {
        offender = raw;
        foreach (var part in raw.Split(','))
        {
            if (!double.TryParse(part, out var v) || v is not (0 or 1 or 2 or 4 or 5)) return false;
        }
        return true;
    }

    [Fact]
    public void CornerRadiusLiterals_StayOnRadiusScale()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; outline gates need a source checkout");

        var offenders = new List<string>();
        foreach (var relative in ScopedFiles)
        {
            var text = ReadApp(root!, relative);
            foreach (Match m in CornerRadiusAttr.Matches(text))
            {
                if (!IsAllowedRadius(m.Groups[1].Value, out _))
                    offenders.Add($"{relative}:{LineOf(text, m.Index)} CornerRadius=\"{m.Groups[1].Value}\"");
            }
            foreach (Match m in CornerRadiusSetter.Matches(text))
            {
                if (!IsAllowedRadius(m.Groups[1].Value, out _))
                    offenders.Add($"{relative}:{LineOf(text, m.Index)} Setter CornerRadius={m.Groups[1].Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Off-scale corner radii (allowed: 0 flush / 1-2 micro-pills / 4 rows·boxes·segments / 5 chips; " +
            "3과 6은 L13에서 퇴출됐다 — DesignTokenValues.Radius 참조):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NetworkPage_UsesSidebarSkeleton_TabsAndTitleRetired()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        var page = ReadApp(root!, "Views/NetworkPage.xaml");

        // 데이터 소스 사이드바(공통 구조): 헤더 + 3개 소스 행 + 선택 목록.
        Assert.Contains("Network_SidebarHeader", page);
        Assert.Contains("x:Name=\"SourceList\"", page);
        Assert.Contains("Network_Section_Radio", page);
        Assert.Contains("Network_Section_Dlna", page);
        Assert.Contains("Network_Section_YouTube", page);

        // 섹션 탭·페이지 타이틀 퇴출 — 사이드바가 구조 역할을 흡수했다(L13 구조 결정).
        Assert.DoesNotContain("EoleNavTabStyle", page);
        Assert.DoesNotContain("Network_PageHeader", page);

        // 스타일 원천: EoleNavTabStyle 정의 자체가 삭제돼야 죽은 스타일 재유입이 막긴다.
        var theme = ReadApp(root!, "DawnTheme.xaml");
        Assert.DoesNotContain("x:Key=\"EoleNavTabStyle\"", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkSectionLists_AreFlatMediaRows()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        // 변형 A(전면 플랫): Network 리스트는 Library/Playlist처럼 백플레이트 없는 플랫 행.
        foreach (var (file, listName) in new[] { ("Views/Network/RadioSection.xaml", "StationList"), ("Views/Network/DlnaSection.xaml", "EntryList") })
        {
            var text = ReadApp(root!, file);
            var openTag = Regex.Match(text, $"<ListView[^>]*x:Name=\"{listName}\"[^>]*>", RegexOptions.Singleline);
            Assert.True(openTag.Success, $"{file}: '{listName}' ListView not found");
            var tag = openTag.Value;
            foreach (var banned in new[] { "Background=", "BorderBrush=", "BorderThickness=", "CornerRadius=" })
            {
                Assert.True(!tag.Contains(banned, StringComparison.Ordinal),
                    $"{file}:{LineOf(text, openTag.Index)} — flat-list contract: '{listName}' must not carry '{banned}' (박스는 변형 A에서 퇴출)");
            }

            // 미디어 행 기하: 44(MinHeight)·4(CornerRadius) — 값의 소유자는 DesignTokenValues.
            var styleBlock = Regex.Match(text, "<ListView\\.ItemContainerStyle>.*?</ListView\\.ItemContainerStyle>", RegexOptions.Singleline);
            Assert.True(styleBlock.Success, $"{file}: '{listName}' needs an explicit ItemContainerStyle");
            var style = styleBlock.Value;
            Assert.Contains($"Value=\"{DesignTokenValues.Rows.Media}\"", style, StringComparison.Ordinal);
            Assert.Contains($"Value=\"{DesignTokenValues.Radius.Row}\"", style, StringComparison.Ordinal);
        }

        // Radio 재생 배지 전경 = OnAccent (TextPrimary는 액센트 위 대비 규약 위반).
        var radio = ReadApp(root!, "Views/Network/RadioSection.xaml");
        Match? badgeTag = null;
        foreach (Match t in TagRegex.Matches(radio))
        {
            if (t.Value.Contains("Network_Radio_PlayingBadge", StringComparison.Ordinal)) { badgeTag = t; break; }
        }
        Assert.True(badgeTag != null, "Radio playing badge not found");
        Assert.Contains("OnAccentBrush", badgeTag!.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("TextPrimaryBrush", badgeTag.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TextFontSizes_UseTokens_AndHeadersUseSectionHeaderText()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        // 텍스트 요소의 FontSize는 토큰 참조만. 예외: FontIcon(글리프 메트릭스)과 Width·Height가
        // 고정된 글리프 상자(트리 셰브런류 — 아이콘 크기이지 타이포 스케일이 아니다).
        var offenders = new List<string>();
        foreach (var relative in ScopedFiles)
        {
            var text = ReadApp(root!, relative);
            foreach (Match tag in TagRegex.Matches(text))
            {
                var fs = Regex.Match(tag.Value, "\\bFontSize=\"([0-9.]+)\"");
                if (!fs.Success) continue;
                var isIcon = tag.Value.StartsWith("<FontIcon", StringComparison.Ordinal);
                var fixedBox = tag.Value.Contains("Width=") && tag.Value.Contains("Height=");
                if (!isIcon && !fixedBox)
                    offenders.Add($"{relative}:{LineOf(text, tag.Index)} <{tag.Value[1..tag.Value.IndexOf(' ')]}> FontSize={fs.Groups[1].Value}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Text FontSize must reference Font* tokens (FontIcon·고정 글리프 상자 제외):\n  " + string.Join("\n  ", offenders));

        // 섹션 헤더 단일 스타일: 14 SemiBold — Italic 없음(2026-10-04 사용자 결정).
        var theme = ReadApp(root!, "DawnTheme.xaml");
        var styleStart = theme.IndexOf("x:Key=\"SectionHeaderText\"", StringComparison.Ordinal);
        Assert.True(styleStart >= 0, "SectionHeaderText style missing from DawnTheme.xaml");
        var styleBlock = theme[styleStart..theme.IndexOf("</Style>", styleStart, StringComparison.Ordinal)];
        Assert.Contains("Property=\"FontSize\" Value=\"14\"", styleBlock, StringComparison.Ordinal);
        Assert.Contains("Property=\"FontWeight\" Value=\"SemiBold\"", styleBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Italic", styleBlock, StringComparison.Ordinal);

        // Playlist 헤더 3곳(사이드바·제목·앨범 그룹)이 수렴한다.
        var playlist = ReadApp(root!, "Views/PlaylistPage.xaml");
        Assert.True(Regex.Count(playlist, "Style=\"\\{StaticResource SectionHeaderText\\}\"") >= 3,
            "Playlist headers (sidebar/title/group) must use SectionHeaderText");
        var sidebarHeaderLine = playlist[(playlist.IndexOf("Playlist_SidebarHeader", StringComparison.Ordinal))..];
        sidebarHeaderLine = sidebarHeaderLine[..sidebarHeaderLine.IndexOf('\n')];
        Assert.DoesNotContain("Italic", sidebarHeaderLine, StringComparison.Ordinal);
        var titleLine = playlist[(playlist.IndexOf("PlaylistTitleText", StringComparison.Ordinal))..];
        titleLine = titleLine[..titleLine.IndexOf('\n')];
        Assert.DoesNotContain("Italic", titleLine, StringComparison.Ordinal);

        // L16: YouTube는 섹션 제목 1곳만 SectionHeaderText다. "최근 항목"은 섹션 제목과 동급이면
        // 한 화면에 같은 크기 헤더 2개 — 위계 붕괴라 그룹 라벨 단계(GroupHeaderText)로 강등했다.
        var youtube = ReadApp(root!, "Views/Network/YouTubeSection.xaml");
        Assert.True(Regex.Count(youtube, "Style=\"\\{StaticResource SectionHeaderText\\}\"") == 1,
            "YouTube section header must use SectionHeaderText (recent header demoted to GroupHeaderText, L16)");
        Assert.Contains("Network_YouTube_RecentHeader", youtube);
        var recentLine = youtube[(youtube.IndexOf("Network_YouTube_RecentHeader", StringComparison.Ordinal))..];
        recentLine = recentLine[..recentLine.IndexOf('\n')];
        Assert.Contains("GroupHeaderText", recentLine, StringComparison.Ordinal);
        var dlna = ReadApp(root!, "Views/Network/DlnaSection.xaml");
        Assert.Contains("Network_Dlna_Title", dlna); // 헤더 부재 해소(구조 결정 ③)
    }

    [Fact]
    public void HairlineDividers_UseSeparatorSubtle()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        // 구분선 역할 분리(L13 ①): 1px 헤어라인의 채움은 SeparatorSubtleBrush 단일.
        // BorderSubtleBrush는 박스·이미지 외곽선 전용(Height=1이 아니므로 이 게이트와 무관).
        var offenders = new List<string>();
        foreach (var relative in ScopedFiles)
        {
            var text = ReadApp(root!, relative);
            foreach (Match border in Regex.Matches(text, "<Border[^>]*>", RegexOptions.Singleline))
            {
                if (!Regex.IsMatch(border.Value, "\\bHeight=\"1\"")) continue;
                if (!border.Value.Contains("SeparatorSubtleBrush", StringComparison.Ordinal))
                    offenders.Add($"{relative}:{LineOf(text, border.Index)} hairline without SeparatorSubtleBrush");
            }
        }
        Assert.True(offenders.Count == 0,
            "1px hairlines must use SeparatorSubtleBrush:\n  " + string.Join("\n  ", offenders));
    }
}
