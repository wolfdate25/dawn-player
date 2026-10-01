using DawnPlayer.App.Helpers;
using DawnPlayer.App.Services;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Review-pass gates (2026-10-01): failure scenarios for the two pure remediation pieces the
/// audit waves added — the notification slot's severity guard (PT4-19) and the restored-window
/// clamp (PT1-06). The window may never come back unreachable just because the monitor layout
/// changed between save and restore.
/// </summary>
public class ReviewHardeningTests
{
    // ---------------- PT4-19: transient must not bury an unacknowledged problem ----------------

    [Fact]
    public void Warning_IsNotReplaced_ByTransientNotification()
    {
        var presenter = new NotificationPresenter();
        presenter.Show("재생 실패", UiSeverity.Warning);
        presenter.Show("저장했습니다", UiSeverity.Success);
        Assert.Equal("재생 실패", presenter.Message);
        Assert.Equal(UiSeverity.Warning, presenter.Severity);
        Assert.False(presenter.WillAutoClose);
    }

    [Fact]
    public void Error_IsNotReplaced_ByInformational()
    {
        var presenter = new NotificationPresenter();
        presenter.Show("디코딩 오류", UiSeverity.Error);
        presenter.Show("시작했습니다", UiSeverity.Informational);
        Assert.Equal("디코딩 오류", presenter.Message);
    }

    [Fact]
    public void HigherSeverity_Still_Replaces()
    {
        var presenter = new NotificationPresenter();
        presenter.Show("저장했습니다", UiSeverity.Success);
        presenter.Show("재생 실패", UiSeverity.Warning);
        Assert.Equal("재생 실패", presenter.Message);
        presenter.Show("치명적 오류", UiSeverity.Error);
        Assert.Equal("치명적 오류", presenter.Message);
    }

    [Fact]
    public void Transient_FlowsNormally_WhenSlotClosedOrTransient()
    {
        var presenter = new NotificationPresenter();
        presenter.Show("첫 번째", UiSeverity.Success);
        presenter.Show("두 번째", UiSeverity.Informational); // transient → transient replaces
        Assert.Equal("두 번째", presenter.Message);
        presenter.Dismiss();
        presenter.Show("세 번째", UiSeverity.Success); // closed slot accepts anything
        Assert.Equal("세 번째", presenter.Message);
    }

    // ---------------- PT1-06: restored windows stay reachable ----------------

    [Fact]
    public void SavedPosition_OnPrimaryScreen_Unchanged()
    {
        var (x, y) = WindowPlacementMath.ClampToVirtualScreen(100, 100, 1200, 800, 0, 0, 3840, 2160);
        Assert.Equal((100, 100), (x, y));
    }

    [Fact]
    public void PositionFarRight_OfDisconnectedMonitor_ClampsBack()
    {
        // Saved on a second monitor at x=5000; the desktop is now one 1920 screen. The window
        // must come back inside it with a grabbable sliver — not sit at 5000 invisible.
        var (x, y) = WindowPlacementMath.ClampToVirtualScreen(5000, 100, 1200, 800, 0, 0, 1920, 1080);
        Assert.Equal(1920 - 160, x);
        Assert.Equal(100, y);
    }

    [Fact]
    public void PositionFarBelow_ClampsBack()
    {
        var (x, y) = WindowPlacementMath.ClampToVirtualScreen(100, 4000, 1200, 800, 0, 0, 1920, 1080);
        Assert.Equal(100, x);
        Assert.Equal(1080 - 60, y);
    }

    [Fact]
    public void PositionNegative_ClampsToSliver()
    {
        var (x, y) = WindowPlacementMath.ClampToVirtualScreen(-5000, -3000, 1200, 800, 0, 0, 1920, 1080);
        Assert.Equal(0 - 1200 + 160, x);
        Assert.Equal(0 - 800 + 60, y);
    }

    [Fact]
    public void Clamp_IsIdempotent()
    {
        var first = WindowPlacementMath.ClampToVirtualScreen(5000, 4000, 1200, 800, 0, 0, 1920, 1080);
        var second = WindowPlacementMath.ClampToVirtualScreen(first.X, first.Y, 1200, 800, 0, 0, 1920, 1080);
        Assert.Equal(first, second);
    }
}
