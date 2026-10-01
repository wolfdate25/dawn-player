using DawnPlayer.App.Views;
using Xunit;

namespace DawnPlayer.Tests.Views;

/// <summary>
/// Failure-scenario gates for the async library filter pipeline (PT2-08). The gate is the
/// only thing standing between an out-of-order background compute and the visible library:
/// a slower older result must never overwrite a newer one, and the newest result must apply
/// no matter how many requests were issued after it started.
/// </summary>
public class FilterRequestGateTests
{
    [Fact]
    public void FreshToken_Applies_StaleTokenIsRejectedForever()
    {
        var gate = new FilterRequestGate();
        var first = gate.BeginRequest();
        Assert.True(gate.CanApply(first));

        gate.BeginRequest(); // newer request issued while the first was in flight
        Assert.False(gate.CanApply(first), "stale token must be rejected");
        // Rejection is permanent, not a one-shot.
        Assert.False(gate.CanApply(first));
    }

    [Fact]
    public void OutOfOrderCompletion_OlderResultNeverApplies_NewestAlwaysDoes()
    {
        var gate = new FilterRequestGate();
        var slowOld = gate.BeginRequest();   // e.g. big "All tracks" compute
        gate.BeginRequest();                 // user retyped → fast narrow compute
        var newest = gate.BeginRequest();    // user picked a node → latest

        // The old compute lands last: rejected.
        Assert.False(gate.CanApply(slowOld));
        // The newest token still applies even though two newer requests existed.
        Assert.True(gate.CanApply(newest));
    }

    [Fact]
    public void CurrentToken_IsIdempotent()
    {
        var gate = new FilterRequestGate();
        var token = gate.BeginRequest();
        Assert.True(gate.CanApply(token));
        Assert.True(gate.CanApply(token), "applying the current result twice must stay allowed");
    }

    [Fact]
    public void RapidBurst_OnlyLastTokenSurvives()
    {
        var gate = new FilterRequestGate();
        var tokens = Enumerable.Range(0, 50).Select(_ => gate.BeginRequest()).ToList();
        Assert.All(tokens.Take(49), t => Assert.False(gate.CanApply(t)));
        Assert.True(gate.CanApply(tokens[^1]));
    }

    [Fact]
    public void TokensAreStrictlyIncreasing()
    {
        var gate = new FilterRequestGate();
        var a = gate.BeginRequest();
        var b = gate.BeginRequest();
        Assert.True(b > a);
    }
}
