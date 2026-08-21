using System.Linq;
using DawnPlayer.App.Helpers;
using DawnPlayer.App.Shortcuts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DawnPlayer.App.Views;

/// <summary>
/// Single-shot key capture: the first allow-listed key press becomes <see cref="CapturedChord"/> and
/// closes the dialog, Esc cancels. Built in code rather than XAML to match the other dialogs on the
/// settings page.
/// </summary>
internal sealed class ShortcutCaptureDialog : ContentDialog
{
    // Pressing only a modifier is not a chord yet, so those presses are ignored rather than rejected.
    private static readonly int[] BareModifierKeys =
    {
        (int)VirtualKey.Shift, (int)VirtualKey.Control, (int)VirtualKey.Menu, (int)VirtualKey.CapitalLock,
        (int)VirtualKey.LeftWindows, (int)VirtualKey.RightWindows,
        (int)VirtualKey.LeftShift, (int)VirtualKey.RightShift,
        (int)VirtualKey.LeftControl, (int)VirtualKey.RightControl,
        (int)VirtualKey.LeftMenu, (int)VirtualKey.RightMenu
    };

    private readonly TextBlock _hint;

    public ShortcutCaptureDialog(string commandDisplayName)
    {
        Title = $"단축키 지정 — {commandDisplayName}";
        CloseButtonText = "취소";
        DefaultButton = ContentDialogButton.None;

        _hint = new TextBlock
        {
            Text = "Esc를 누르면 취소됩니다. Tab, Esc, Backspace 등 일부 키는 단축키로 쓸 수 없습니다.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "새 키 조합을 누르세요.", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                _hint
            }
        };

        // PreviewKeyDown tunnels ahead of both the dialog buttons and the window accelerators, so a
        // captured Space or Enter neither activates a button nor triggers play/pause. It only routes
        // through this dialog while focus is somewhere inside it — normally the Close button — so the
        // dialog also makes itself focusable as a fallback for a content with no focusable child.
        // The window's own preempting handler cannot get in ahead of this one either: it sits on the
        // window root, which is not an ancestor of this dialog's popup, and it only ever takes keys
        // from a focused list.
        IsTabStop = true;
        PreviewKeyDown += OnPreviewKeyDown;
        Opened += (_, _) => Focus(FocusState.Programmatic);
    }

    /// <summary>The captured chord, or null when the dialog was cancelled.</summary>
    public KeyChord? CapturedChord { get; private set; }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var code = (int)e.Key;
        if (BareModifierKeys.Contains(code)) return;

        if (!ShortcutKeyNames.IsAllowedKey(code))
        {
            // Left unhandled on purpose: Esc must still close the dialog and Tab must still move focus.
            _hint.Text = "이 키는 단축키로 사용할 수 없습니다. 다른 키를 눌러 주세요.";
            return;
        }

        e.Handled = true;
        CapturedChord = new KeyChord(KeyboardHelper.ReadModifiers(), code);
        Hide();
    }
}
