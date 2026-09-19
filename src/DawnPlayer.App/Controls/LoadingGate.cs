namespace DawnPlayer.App.Controls;

/// <summary>
/// U2 loading-visibility gate: async work shorter than the threshold never shows a progress
/// indicator at all (the skill's loading rule — "avoid flashing for near-instant work"), while
/// longer work shows one from the threshold moment onward. Timestamps are injected by the
/// caller (millisecond ticks), keeping this testable without a clock.
/// </summary>
public sealed class LoadingGate
{
    public const int DefaultThresholdMs = 300;

    private readonly int _thresholdMs;
    private long _startTick;
    private bool _pending;
    private bool _shown;

    public LoadingGate(int thresholdMs = DefaultThresholdMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdMs);
        _thresholdMs = thresholdMs;
    }

    /// <summary>Work started. Repeated calls while already pending keep the original start time,
    /// so a stream of progress events does not postpone the threshold forever.</summary>
    public void Begin(long nowMs)
    {
        if (_pending) return;
        _pending = true;
        _startTick = nowMs;
        _shown = false;
    }

    /// <summary>Whether the indicator should be visible at <paramref name="nowMs"/>. First true
    /// latches until <see cref="End"/> — a slow frame never makes the indicator flicker off.</summary>
    public bool ShouldShow(long nowMs)
    {
        if (!_pending) return false;
        if (!_shown && nowMs - _startTick >= _thresholdMs) _shown = true;
        return _shown;
    }

    /// <summary>Work finished (or failed): the indicator goes away and the gate re-arms.</summary>
    public void End()
    {
        _pending = false;
        _shown = false;
    }

    /// <summary>Whether work is in flight (regardless of visibility).</summary>
    public bool IsPending => _pending;
}
