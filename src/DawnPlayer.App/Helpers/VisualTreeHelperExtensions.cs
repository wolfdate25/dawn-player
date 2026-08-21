using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DawnPlayer.App.Helpers;

/// <summary>
/// Extension methods for VisualTreeHelper navigation and ancestor search.
/// </summary>
public static class VisualTreeHelperExtensions
{
    /// <summary>
    /// Traverses up the visual tree to find an ancestor of type <typeparamref name="T"/>.
    /// </summary>
    public static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        var current = element;
        while (current != null)
        {
            if (current is T match) return match;
            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                break;
            }
        }
        return null;
    }

    /// <summary>
    /// Traverses up the visual tree to find an element whose DataContext or Content is of type <typeparamref name="T"/>.
    /// </summary>
    public static T? FindAncestorDataContext<T>(DependencyObject? element) where T : class
    {
        var current = element;
        while (current != null)
        {
            if (current is T direct) return direct;
            if (current is FrameworkElement fe && fe.DataContext is T match) return match;
            if (current is Microsoft.UI.Xaml.Controls.ContentControl cc && cc.Content is T matchContent) return matchContent;
            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                break;
            }
        }
        return null;
    }

    /// <summary>
    /// Traverses down the visual tree (breadth-first) to find a descendant of type <typeparamref name="T"/>.
    /// </summary>
    public static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) return null;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            int count = 0;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(current);
            }
            catch
            {
                continue;
            }

            for (int i = 0; i < count; i++)
            {
                DependencyObject? child = null;
                try
                {
                    child = VisualTreeHelper.GetChild(current, i);
                }
                catch
                {
                    continue;
                }
                if (child is T match) return match;
                if (child != null) queue.Enqueue(child);
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts a data context item of type <typeparamref name="T"/> from a routed event source,
    /// falling back to <paramref name="fallback"/> if not found.
    /// </summary>
    public static T? ResolveItem<T>(RoutedEventArgs? e, T? fallback = null) where T : class
    {
        if (e?.OriginalSource is T directSource) return directSource;
        if (e?.OriginalSource is DependencyObject d)
        {
            var match = FindAncestorDataContext<T>(d);
            if (match != null) return match;
        }
        return fallback;
    }

    /// <summary>
    /// Extracts a data context item of type <typeparamref name="T"/> from a double-tap event source,
    /// falling back to <paramref name="fallback"/> if not found.
    /// </summary>
    public static T? ResolveItem<T>(Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs? e, T? fallback = null) where T : class
    {
        if (e?.OriginalSource is T directSource) return directSource;
        if (e?.OriginalSource is DependencyObject d)
        {
            var match = FindAncestorDataContext<T>(d);
            if (match != null) return match;
        }
        return fallback;
    }
}
