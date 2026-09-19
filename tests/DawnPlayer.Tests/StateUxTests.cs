using DawnPlayer.App.Controls;
using DawnPlayer.App.Services;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// U2 state-UX rules: the InfoBar presenter's auto-close policy (transient auto-closes,
/// problems stay, single-slot replacement) and the loading-visibility gate (no flash under the
/// threshold, latch once shown, re-arm after End).
/// </summary>
public class StateUxTests
{
    // ---------- NotificationPresenter ----------

    [Fact]
    public void Presenter_TransientSeverities_AutoClose_ProblemsDoNot()
    {
        Assert.True(NotificationPresenter.IsTransient(UiSeverity.Informational));
        Assert.True(NotificationPresenter.IsTransient(UiSeverity.Success));
        Assert.False(NotificationPresenter.IsTransient(UiSeverity.Warning));
        Assert.False(NotificationPresenter.IsTransient(UiSeverity.Error));
    }

    [Fact]
    public void Presenter_Show_ReflectsWillAutoClose()
    {
        var p = new NotificationPresenter();
        p.Show("가져오기 완료", UiSeverity.Success);
        Assert.True(p.IsOpen);
        Assert.True(p.WillAutoClose);

        p.Show("스캔 실패", UiSeverity.Warning);
        Assert.True(p.IsOpen);
        Assert.False(p.WillAutoClose); // single slot: replaced, and warnings stay
    }

    [Fact]
    public void Presenter_Dismiss_Clears_And_ReRaisedChanged()
    {
        var p = new NotificationPresenter();
        var changes = 0;
        p.Changed += () => changes++;

        p.Show("알림", UiSeverity.Informational);
        Assert.Equal(1, changes);
        p.Dismiss();
        Assert.False(p.IsOpen);
        Assert.False(p.WillAutoClose);
        Assert.Equal(2, changes);
        p.Dismiss(); // already closed: no extra event
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Presenter_EmptyMessage_IsIgnored()
    {
        var p = new NotificationPresenter();
        p.Show("", UiSeverity.Informational);
        Assert.False(p.IsOpen);
    }

    // ---------- LoadingGate ----------

    [Fact]
    public void Gate_FastWork_NeverShows()
    {
        var gate = new LoadingGate(); // 300ms default
        gate.Begin(1000);
        Assert.False(gate.ShouldShow(1100));
        Assert.False(gate.ShouldShow(1299));
        gate.End();
        Assert.False(gate.ShouldShow(2000));
    }

    [Fact]
    public void Gate_SlowWork_ShowsAtThreshold_AndLatches()
    {
        var gate = new LoadingGate();
        gate.Begin(1000);
        Assert.False(gate.ShouldShow(1299));
        Assert.True(gate.ShouldShow(1300)); // exactly at threshold
        Assert.True(gate.ShouldShow(1301)); // latch: never flickers back off
        gate.End();
        Assert.False(gate.ShouldShow(1400));
        Assert.False(gate.IsPending);
    }

    [Fact]
    public void Gate_RepeatedBegin_KeepsOriginalStart()
    {
        var gate = new LoadingGate(300);
        gate.Begin(0);
        // A stream of progress events calling Begin again must not postpone the threshold.
        gate.Begin(250);
        gate.Begin(400);
        Assert.True(gate.ShouldShow(300));
    }

    [Fact]
    public void Gate_CustomThreshold_And_NegativeRejected()
    {
        var gate = new LoadingGate(50);
        gate.Begin(0);
        Assert.False(gate.ShouldShow(49));
        Assert.True(gate.ShouldShow(50));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoadingGate(-1));
    }
}
