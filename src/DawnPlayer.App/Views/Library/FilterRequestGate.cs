namespace DawnPlayer.App.Views;

/// <summary>
/// Generation gate for the async library filter pipeline (2026-09-30 audit PT2-08).
/// <see cref="LibraryFilterService.FilterAndSort"/> runs off the UI thread; while one compute is
/// in flight the user can retype the search, re-sort, or pick another tree node. A result may
/// only touch the UI while its token is still the newest, so an out-of-order completion can
/// never repaint the screen with stale data. Same discipline as the DLNA browse-generation
/// guard in DlnaSection. UI-thread-only by invariant: <see cref="BeginRequest"/> and
/// <see cref="CanApply"/> both run on the dispatcher thread.
/// </summary>
public sealed class FilterRequestGate
{
    private int _latest;

    /// <summary>Issues the token for a new request and invalidates every in-flight one.</summary>
    public int BeginRequest() => ++_latest;

    /// <summary>Whether a compute result may still be applied. Only the newest token passes;
    /// stale tokens are rejected forever, and the current token passes idempotently.</summary>
    public bool CanApply(int token) => token == _latest;
}
