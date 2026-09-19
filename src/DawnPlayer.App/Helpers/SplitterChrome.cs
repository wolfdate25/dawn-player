using System.Linq;
using DawnPlayer.Core.Audio;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DawnPlayer.App.Helpers;

/// <summary>
/// Shared splitter hover chrome (cursor + accent highlight) for the interactive layout
/// splitters. LibraryPage and PlaylistPage used to carry two diverging copies — the playlist
/// copy was missing the drag guard, so the cursor could snap back to an arrow mid-drag.
/// </summary>
public static class SplitterChrome
{
    /// <summary>Applies or clears the hover state: west-east cursor while any splitter is
    /// interacted with, accent-tinted splitter line while hovered.</summary>
    public static void SetHover(FrameworkElement page, Action<InputCursor?> setCursor, Border? splitter,
        bool hovered, params SplitterResizer?[] resizers)
    {
        bool dragging = resizers.Any(r => r?.IsDragging == true);
        setCursor(hovered || dragging ? InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast) : null);
        SetHighlight(splitter, hovered || dragging);
    }

    /// <summary>Clears the cursor on exit unless another splitter is mid-drag.</summary>
    public static void ClearHover(FrameworkElement page, Action<InputCursor?> setCursor, Border? splitter,
        params SplitterResizer?[] resizers)
    {
        if (resizers.Any(r => r?.IsDragging == true)) return;
        setCursor(null);
        SetHighlight(splitter, false);
    }

    private static void SetHighlight(Border? splitter, bool on)
    {
        if (splitter?.Child is not Rectangle line) return;
        line.Fill = on
            ? ThemeResourceHelper.GetBrush("DawnAccentBrush")
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
