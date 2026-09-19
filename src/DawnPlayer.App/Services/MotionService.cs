namespace DawnPlayer.App.Services;

/// <summary>Source of the OS-level "show animations in Windows" preference. Abstracted so the
/// motion decision logic stays testable headlessly; the Windows implementation lives in
/// <c>WindowsMotionSource</c> (App project only — instantiating WinRT <c>UISettings</c> is not
/// meaningful in a unit-test process).</summary>
public interface IMotionPreferenceSource
{
    bool AnimationsEnabled { get; }
}

/// <summary>
/// U1 motion gate: non-essential UI motion runs only when the user has not disabled it in the
/// app AND Windows animations are on. Both inputs are read on every query, so flipping the
/// Windows setting or the app toggle takes effect without any subscription plumbing.
/// Durations live in <see cref="DawnPlayer.App.Styles.DesignTokenValues.Motion"/>.
/// </summary>
public sealed class MotionService
{
    private readonly IMotionPreferenceSource _osSource;
    private readonly Func<bool> _userMotionEnabled;

    public MotionService(IMotionPreferenceSource osSource, Func<bool> userMotionEnabled)
    {
        _osSource = osSource ?? throw new ArgumentNullException(nameof(osSource));
        _userMotionEnabled = userMotionEnabled ?? throw new ArgumentNullException(nameof(userMotionEnabled));
    }

    /// <summary>False ⇒ callers must skip non-essential animation and jump to the end state.</summary>
    public bool MotionEnabled => _userMotionEnabled() && _osSource.AnimationsEnabled;
}
