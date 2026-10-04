using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// L15 하단바 별점 위치 게이트 (2026-10-04 변형 A 승인 — 제목 인라인). NowPlayingBar.xaml은 테스트
/// 프로젝트에 링크될 수 없으므로 소스 스캔으로 계약을 고정한다:
///   * 제목 행은 [제목 Auto][별 Auto][여백 *] 3열 — 별(TrackRatingButton)이 제목 텍스트 바로 뒤에
///     붙는다(구 구조의 [제목 *][별 Auto]는 별을 정보 블록과 트랜스포트 경계에 떠 있게 했다).
///   * 긴 제목 보호 — 제목 MaxWidth를 코드비하인드가 갱신해야 한다(Auto 열은 트리밍이
///     작동하지 않아, 보호가 없으면 긴 제목이 별을 행 밖으로 밀어낸다).
///   * 기존 계약 유지 — 스트림 숨김(RatingCommands.IsRateable)과 플라이아웃(RatingControl).
/// </summary>
public class NowPlayingRatingInlineGateTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DawnPlayer.slnx"))) return dir;
        }
        return null;
    }

    private static string ReadControl(DirectoryInfo root, string relative) =>
        File.ReadAllText(Path.Combine(root.FullName, "src", "DawnPlayer.App", "Controls", relative));

    [Fact]
    public void TitleRow_IsTitleThenStarThenSpacer()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repository root not found; rating gates need a source checkout");

        var xaml = ReadControl(root!, "NowPlayingBar.xaml");
        var rowStart = xaml.IndexOf("<Grid x:Name=\"TitleRow\"", StringComparison.Ordinal);
        Assert.True(rowStart >= 0, "TitleRow grid not found in NowPlayingBar.xaml");
        var block = xaml[rowStart..xaml.IndexOf("</Grid>", rowStart, StringComparison.Ordinal)];

        // 열 순서 = [제목 Auto][별 Auto][여백 *] — 별이 제목 바로 뒤에서 끝난다.
        var defs = block[block.IndexOf("<Grid.ColumnDefinitions>", StringComparison.Ordinal)..];
        defs = defs[..(defs.IndexOf("</Grid.ColumnDefinitions>", StringComparison.Ordinal))];
        var widths = Regex.Matches(defs, "Width=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "Auto", "Auto", "*" }, widths);

        // 요소 배치: 제목 col 0, 별 col 1.
        var titleTag = Regex.Match(block, "<TextBlock[^>]*x:Name=\"TrackTitle\"[^>]*>", RegexOptions.Singleline);
        Assert.True(titleTag.Success, "TrackTitle not found in TitleRow");
        Assert.Contains("Grid.Column=\"0\"", titleTag.Value, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", titleTag.Value, StringComparison.Ordinal);

        var buttonTag = Regex.Match(block, "<Button[^>]*x:Name=\"TrackRatingButton\"[^>]*>", RegexOptions.Singleline);
        Assert.True(buttonTag.Success, "TrackRatingButton not found in TitleRow");
        Assert.Contains("Grid.Column=\"1\"", buttonTag.Value, StringComparison.Ordinal);
        // 구 구조([제목 *][별 Auto])의 재유입 금지 — 제목 열이 *면 별은 다시 경계로 밀려난다.
        Assert.DoesNotContain("Width=\"*\"", defs.Split('\n')[0], StringComparison.Ordinal);
    }

    [Fact]
    public void LongTitle_Protection_WiredInCodeBehind()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        var cs = ReadControl(root!, "NowPlayingBar.xaml.cs");

        // Auto 열은 TextTrimming이 작동하지 않으므로 제목 MaxWidth 갱신이 계약이다.
        Assert.Contains("TrackTitle.MaxWidth", cs, StringComparison.Ordinal);
        Assert.Contains("TitleRow.SizeChanged", cs, StringComparison.Ordinal);

        // 갱신은 별 가시성을 반영해야 한다(Collapsed 요소의 ActualWidth는 마지막 값이 남는다).
        var update = cs.IndexOf("private void UpdateTitleMaxWidth", StringComparison.Ordinal);
        Assert.True(update >= 0, "UpdateTitleMaxWidth not found");
        var body = cs[update..cs.IndexOf("\n    }", update, StringComparison.Ordinal)];
        Assert.Contains("TrackRatingButton.Visibility", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RatingTarget_FallsBackToDisplayedTrack()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        // L15 사용자 보고("평점 설정이 갱신 안 됨")의 원인: 세션 복원 후 재생 전에는
        // Playback.CurrentItem이 null이라 별 클릭이 무음 no-op이었다. 별점 대상은 표시 중인
        // 트랙(_displayedTrack)으로 폴백해야 한다 — 개방·커밋·적용 반영 3곳 모두.
        var cs = ReadControl(root!, "NowPlayingBar.xaml.cs");
        Assert.True(Regex.Count(cs, Regex.Escape("?? _displayedTrack")) >= 3,
            "rating target must fall back to _displayedTrack (flyout opening / value changed / ratings applied)");
        Assert.Contains("_displayedTrack = item?.Track;", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamHide_AndFlyout_ContractsSurvive()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        var cs = ReadControl(root!, "NowPlayingBar.xaml.cs");
        Assert.Contains("RatingCommands.IsRateable", cs, StringComparison.Ordinal);

        var xaml = ReadControl(root!, "NowPlayingBar.xaml");
        var ratingStart = xaml.IndexOf("x:Name=\"TrackRatingButton\"", StringComparison.Ordinal);
        Assert.True(ratingStart >= 0);
        var flyout = xaml.IndexOf("x:Name=\"TrackRatingFlyout\"", ratingStart, StringComparison.Ordinal);
        Assert.True(flyout > ratingStart, "rating flyout must remain inside TrackRatingButton");

        // L15 후속: WinUI RatingControl은 미평점(0) 렌더링이 불가(최소 1 채움 강제) — 앱이
        // 별 5개 버튼을 직접 렌더링하는 커스텀 행으로 대체됐다(사용자 요구: 미평점 = 모두 빈 별).
        var flyoutBlock = xaml[flyout..xaml.IndexOf("</Flyout>", flyout, StringComparison.Ordinal)];
        for (var n = 1; n <= 5; n++)
            Assert.Contains($"x:Name=\"FlyoutStar{n}\"", flyoutBlock, StringComparison.Ordinal);
        Assert.Contains("FlyoutStarRow", flyoutBlock, StringComparison.Ordinal);
        Assert.Contains("OnFlyoutStarClick", flyoutBlock, StringComparison.Ordinal);

        var clickHandler = cs.IndexOf("private void OnFlyoutStarClick", StringComparison.Ordinal);
        Assert.True(clickHandler >= 0, "OnFlyoutStarClick handler not found");
        var handlerBody = cs[clickHandler..cs.IndexOf("\n    }", clickHandler, StringComparison.Ordinal)];
        Assert.Contains("RateTracks", handlerBody, StringComparison.Ordinal);
        // 같은 별 개수 재클릭 → 지우기(IsClearEnabled 계약 유지)
        Assert.Contains("next = stars == current ? 0 : stars", handlerBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RatedTrack_ShowsFilledStarPerRatingPoint()
    {
        var root = FindRepoRoot();
        Assert.True(root != null);

        // 2026-10-04 사용자 결정: 평점 부여 곡은 단일 아이콘이 아니라 평점 수만큼 채운 별
        // (3점 → E735×3 — MDL2 기준 채움 글리프). 별 컨테이너는 코드비하인드가 채우는
        // StackPanel이고, 구 단일 아이콘(TrackRatingIcon)의 재유입 금지.
        var xaml = ReadControl(root!, "NowPlayingBar.xaml");
        Assert.Contains("x:Name=\"TrackRatingStars\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"TrackRatingIcon\"", xaml, StringComparison.Ordinal);

        var cs = ReadControl(root!, "NowPlayingBar.xaml.cs");
        Assert.DoesNotContain("TrackRatingIcon", cs, StringComparison.Ordinal);
        var update = cs.IndexOf("private void UpdateTrackRatingCell", StringComparison.Ordinal);
        Assert.True(update >= 0, "UpdateTrackRatingCell not found");
        var body = cs[update..cs.IndexOf("\n    ///", update, StringComparison.Ordinal)];
        Assert.Contains("TrackRatingStars.Children", body, StringComparison.Ordinal);
        // 글리프 매핑 고정(2026-10-04 스왑): 채움=E735, 외곽=E734(MDL2) —
        // 뒤집힌 채 렌더링됐던 사용자 보고의 재발 방지.
        Assert.Contains("rating > 0 ? \"\\uE735\" : \"\\uE734\"", body, StringComparison.Ordinal);
    }
}
