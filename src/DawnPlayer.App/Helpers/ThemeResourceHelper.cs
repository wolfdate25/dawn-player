using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DawnPlayer.App.Helpers;

/// <summary>
/// Safely resolves XAML Theme resources without throwing KeyNotFoundException or crashing WinUI.
/// </summary>
public static class ThemeResourceHelper
{
    private static readonly SolidColorBrush DefaultAccent = new(Windows.UI.Color.FromArgb(255, 232, 163, 61));
    private static readonly SolidColorBrush DefaultPrimaryText = new(Windows.UI.Color.FromArgb(255, 243, 243, 246));
    private static readonly SolidColorBrush DefaultSecondaryText = new(Windows.UI.Color.FromArgb(255, 174, 174, 188));
    private static readonly SolidColorBrush DefaultTertiaryText = new(Windows.UI.Color.FromArgb(255, 120, 120, 136));

    public static Brush GetBrush(string key, Brush? fallback = null)
    {
        try
        {
            if (Application.Current?.Resources != null)
            {
                if (Application.Current.Resources.TryGetValue(key, out var res) && res is Brush b)
                    return b;
            }
        }
        catch { }

        return fallback ?? key switch
        {
            "DawnAccentBrush" => DefaultAccent,
            "TextPrimaryBrush" => DefaultPrimaryText,
            "TextSecondaryBrush" => DefaultSecondaryText,
            "TextTertiaryBrush" => DefaultTertiaryText,
            _ => DefaultPrimaryText
        };
    }
}
