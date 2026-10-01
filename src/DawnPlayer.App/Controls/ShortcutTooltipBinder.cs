using DawnPlayer.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DawnPlayer.App.Controls;

/// <summary>
/// Appends the live shortcut chord to a control's tooltip ("무작위 재생" → "무작위 재생 (Ctrl+H)").
/// Audit PT5-09: chords used to be visible only on the settings page. The chord is read from
/// the live map at render time, so a rebinding is honored on the next tooltip refresh. Not
/// linked into the test project (WinUI types).
/// </summary>
public static class ShortcutTooltipBinder
{
    public static string WithShortcutSuffix(string baseText, Shortcuts.ShortcutCommand command)
    {
        var chord = Services.AppServices.Shortcuts?.Map.GetChord(command);
        if (chord is not { } c) return baseText;
        return AppStrings.Format("Tooltip_WithShortcut", "{0} ({1})", baseText, c.ToDisplayString());
    }

    /// <summary>Reads the element's current tooltip and re-sets it with the chord suffix.
    /// No-op when the element has no tooltip yet.</summary>
    public static void BindShortcutTooltip(FrameworkElement element, Shortcuts.ShortcutCommand command)
    {
        var existing = ToolTipService.GetToolTip(element) as string;
        if (string.IsNullOrEmpty(existing)) return;
        ToolTipService.SetToolTip(element, WithShortcutSuffix(existing, command));
    }
}
