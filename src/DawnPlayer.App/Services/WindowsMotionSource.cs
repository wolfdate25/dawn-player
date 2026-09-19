using Windows.UI.ViewManagement;

namespace DawnPlayer.App.Services;

/// <summary>OS motion preference via WinRT <c>UISettings</c>. A failure to query (rare host
/// combinations, test harnesses) falls OPEN to true — a probe error must never disable motion
/// for everyone; the app-level toggle remains the user's explicit off switch.</summary>
public sealed class WindowsMotionSource : IMotionPreferenceSource
{
    private readonly UISettings _settings = new();

    public bool AnimationsEnabled
    {
        get
        {
            try
            {
                return _settings.AnimationsEnabled;
            }
            catch
            {
                return true;
            }
        }
    }
}
