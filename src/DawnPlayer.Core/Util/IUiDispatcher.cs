namespace DawnPlayer.Core.Util;

/// <summary>
/// The one UI-framework seam Core is allowed to know about: posting an action onto the UI
/// thread. Core types (PlaylistManager) hold this behind a nullable property — null means
/// headless (tests, CLI), where actions run inline on the caller. This keeps ObservableCollection
/// and INPC types servicable from Core without importing a UI framework.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread. Implementations should run it
    /// synchronously on that thread when already there (the App adapter does).</summary>
    void Post(Action action);
}

/// <summary>Wraps a delegate (e.g. the App's <c>RunOnUi</c>) as an <see cref="IUiDispatcher"/>.</summary>
public sealed class DelegateUiDispatcher : IUiDispatcher
{
    private readonly Action<Action> _post;

    public DelegateUiDispatcher(Action<Action> post) =>
        _post = post ?? throw new ArgumentNullException(nameof(post));

    public void Post(Action action) => _post(action);
}

/// <summary>Headless dispatcher: runs actions inline. The default when no UI thread exists.</summary>
public sealed class InlineUiDispatcher : IUiDispatcher
{
    public static readonly InlineUiDispatcher Instance = new();

    public void Post(Action action) => action();
}
