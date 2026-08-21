using DawnPlayer.App.Shortcuts;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace DawnPlayer.App.Helpers;

/// <summary>
/// Helper for keyboard interaction and input focus detection.
/// </summary>
public static class KeyboardHelper
{
    /// <summary>
    /// Checks whether the currently focused UI element is a text input field (TextBox, AutoSuggestBox, PasswordBox, RichEditBox).
    /// </summary>
    public static bool FocusIsInTextInput(XamlRoot? xamlRoot)
    {
        try
        {
            var focused = FocusManager.GetFocusedElement(xamlRoot);
            return IsTextInput(focused);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Classifies the focused element for <see cref="ShortcutDispatchPolicy"/>: which kind of owner
    /// the pressed key would reach if the window did not take it first.
    /// <para>
    /// The walk stops at the innermost element that actually owns keys, which is what keeps a Button
    /// inside a list row — an album card, a row's inline play button — reporting
    /// <see cref="ShortcutFocusContext.Other"/> and holding on to its Space. Everything in between is
    /// walked straight through, including the ScrollViewer inside a ListView's own template: it is a
    /// Control, but it is not the key's owner here.
    /// </para>
    /// </summary>
    public static ShortcutFocusContext ClassifyFocus(XamlRoot? xamlRoot)
    {
        try
        {
            if (FocusManager.GetFocusedElement(xamlRoot) is not DependencyObject focused)
            {
                return ShortcutFocusContext.Unknown;
            }

            var found = ShortcutFocusContext.Unknown;

            for (var node = focused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                // A text input anywhere up the chain vetoes whatever was found below it: an
                // AutoSuggestBox hosts its suggestions in a ListView, and a key the edit box wants is
                // never the shortcut's.
                if (IsTextInput(node)) return ShortcutFocusContext.TextInput;

                if (found != ShortcutFocusContext.Unknown) continue;

                found = node switch
                {
                    ListViewBase or ListBox or TreeView or ItemsView => ShortcutFocusContext.ItemsList,
                    ButtonBase or Slider or ComboBox or ToggleSwitch or RatingControl or CalendarView
                        or MenuFlyoutPresenter or NumberBox => ShortcutFocusContext.Other,
                    _ => ShortcutFocusContext.Unknown
                };
            }

            return found;
        }
        catch
        {
            return ShortcutFocusContext.Unknown;
        }
    }

    /// <summary>Reads the modifier keys that are physically down right now.</summary>
    public static ShortcutModifiers ReadModifiers()
    {
        var modifiers = ShortcutModifiers.None;
        if (IsKeyDown(VirtualKey.Control)) modifiers |= ShortcutModifiers.Control;
        if (IsKeyDown(VirtualKey.Menu)) modifiers |= ShortcutModifiers.Menu;
        if (IsKeyDown(VirtualKey.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) modifiers |= ShortcutModifiers.Windows;
        return modifiers;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsTextInput(object? node) =>
        node is TextBox or AutoSuggestBox or PasswordBox or RichEditBox;
}
