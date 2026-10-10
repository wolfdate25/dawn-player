using System.Text.RegularExpressions;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Fullscreen Now Playing contract gate (2026-10-08 "전체 화면 플레이어 확인" — same treatment
/// the mini player got). The runtime smoke test exposed one completeness defect and the review
/// three smaller ones, each pinned here:
/// <list type="bullet">
/// <item>W1 — the played-progress <c>Canvas.Clip</c> on the wave canvas clipped HIT TESTING
///       too: clicks/drags/hover ahead of the playhead — the normal "seek forward" gesture —
///       silently landed outside the hit region (proved by the clip-inside vs clip-outside
///       click experiment). The clip now lives on the <c>WavePlayed</c> polygon only and the
///       canvas itself must never carry a Clip again.</item>
/// <item>F2 — every title-menu re-entry constructed ANOTHER fullscreen window with its own
///       10 Hz frame timer; Escape closed only the top one, which read as "Escape broken".
///       The window is a single live instance now (ShowOrActivate).</item>
/// <item>F4 — a remote (M3U8) track's cover resolves after LoadCover already ran; fullscreen
///       kept the placeholder until the next track change unless it mirrors
///       AppServices.RemoteArtResolved like the bar does.</item>
/// <item>F5 — the code-created spectrum bars snapshot the accent brush at construction; a
///       theme flip while fullscreen is open left them stale without an
///       ActualThemeChanged re-tint.</item>
/// </list>
/// Verified fine at runtime (no gate needed): fresh-open Escape/Space/arrow keys reach the
/// root (WinUI grants the root pane initial focus), and Space with the play button focused
/// toggles exactly once — the root KeyDown handler wins and the button activation is
/// suppressed.
/// </summary>
public class FullscreenNowPlayingGateTests
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

    private static string FsXaml() => ReadRepoFile("src", "DawnPlayer.App", "Views", "FullscreenNowPlayingWindow.xaml");

    private static string FsSource() => ReadRepoFile("src", "DawnPlayer.App", "Views", "FullscreenNowPlayingWindow.xaml.cs");

    private static string MainWindowSource() => ReadRepoFile("src", "DawnPlayer.App", "MainWindow.xaml.cs");

    [Fact]
    public void WaveProgressClip_LivesOnPlayedPolygon_NeverOnTheCanvas()
    {
        var xaml = FsXaml();

        // W1 재발 방지: Canvas.Clip은 히트테스트도 절단한다 — 재생 위치 앞쪽 클릭·드래그(정상적인
        // "앞으로 시크")가 전부 무반응이었다(2026-10-08 실측). 클립은 WavePlayed 폴리곤 소유.
        var canvasStart = xaml.IndexOf("<Canvas x:Name=\"WaveCanvas\"", StringComparison.Ordinal);
        Assert.True(canvasStart >= 0, "WaveCanvas not found");
        var canvasEnd = xaml.IndexOf("</Canvas>", canvasStart, StringComparison.Ordinal);
        var canvasBlock = xaml[canvasStart..canvasEnd];
        Assert.DoesNotContain("<Canvas.Clip>", canvasBlock, StringComparison.Ordinal);

        Assert.Contains("x:Name=\"WavePlayed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Polygon.Clip>", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WavePlayedClip\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void WaveProgressClip_CodeResizesPlayedPolygonClip()
    {
        var source = FsSource();

        // 코드 측 클립 갱신도 WavePlayedClip으로 — 구 WaveClip 참조의 재유입 금지.
        Assert.Contains("WavePlayedClip.Rect", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WaveClip.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"WaveClip\"", FsXaml(), StringComparison.Ordinal);
    }

    [Fact]
    public void Fullscreen_IsSingleLiveInstance_ReentryActivates()
    {
        var source = FsSource();
        Assert.Contains("private static FullscreenNowPlayingWindow? _current;", source, StringComparison.Ordinal);
        Assert.Contains("public static void ShowOrActivate()", source, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"if \(_current is \{ \} existing\)"), source);
        Assert.Contains("if (_current == this) _current = null;", source, StringComparison.Ordinal);

        // 진입 경로는 항상 ShowOrActivate — new …().Activate() 직접 호출 재유입 금지(F2).
        Assert.Contains("Views.FullscreenNowPlayingWindow.ShowOrActivate();", MainWindowSource(), StringComparison.Ordinal);
        Assert.DoesNotContain("new Views.FullscreenNowPlayingWindow().Activate()", MainWindowSource(), StringComparison.Ordinal);
    }

    [Fact]
    public void Fullscreen_MirrorsRemoteArtResolution()
    {
        // F4: 스트림 커버는 LoadCover 이후에 도착한다 — 바와 같이 RemoteArtResolved를 미러링하지
        // 않으면 다음 트랙 전환까지 플레이스홀더가 남는다.
        var source = FsSource();
        Assert.Contains("AppServices.RemoteArtResolved += OnRemoteArt;", source, StringComparison.Ordinal);
        Assert.Contains("AppServices.RemoteArtResolved -= OnRemoteArt;", source, StringComparison.Ordinal);
        Assert.Contains("private void OnRemoteArt(Core.Models.Track track)", source, StringComparison.Ordinal);

        // 구독 해제가 Closed 경로에 있는지(누수 방지).
        var closed = source.IndexOf("Closed += (_, _) =>", StringComparison.Ordinal);
        Assert.True(closed >= 0, "Closed handler not found");
        var closedBody = source[closed..source.IndexOf("};", closed, StringComparison.Ordinal)];
        Assert.Contains("AppServices.RemoteArtResolved -= OnRemoteArt;", closedBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SpectrumBars_RetintOnThemeFlip()
    {
        // F5: 코드 생성 스펙트럼 바는 생성 시점 브러시 스냅샷 — 테마 전환 재적용이 없으면
        // 전체 화면이 열린 동안 테마를 바꿨을 때 액센트가 낡은 색으로 남는다.
        var source = FsSource();
        Assert.Contains("Root.ActualThemeChanged += (_, _) => RetintSpectrumBars();", source, StringComparison.Ordinal);
        Assert.Contains("private void RetintSpectrumBars()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave_RendersRmsBodyWithPeakCaps()
    {
        // raw max-abs 파형은 리미티드 마스터에서 벽처럼 보인다(모든 버킷이 풀스케일 근처의
        // 트랜지언트를 하나씩 갖는다) — RMS 바디 + 반투명 피크 캡 2레이어가 표준 렌더링.
        // 캡은 재생 구역 전용: 미재생 쪽 캡은 회색 풀스케일 벽으로 RMS 바디를 덮는다
        // (2026-10-08 실측 — WaveUnplayedPeak 재유입 금지).
        var xaml = FsXaml();
        Assert.Contains("x:Name=\"WavePlayedPeak\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WavePlayedPeakClip\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"WaveUnplayedPeak\"", xaml, StringComparison.Ordinal);

        var source = FsSource();
        Assert.Contains("_envelope.Rms", source, StringComparison.Ordinal);
        Assert.Contains("WaveformPeaks.GetOrScanEnvelope(", source, StringComparison.Ordinal);
        Assert.Contains("WavePlayedPeakClip.Rect", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WaveUnplayedPeak", source, StringComparison.Ordinal);
    }
}
