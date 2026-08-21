using System.Collections.Generic;

namespace DawnPlayer.App.Shortcuts;

/// <summary>
/// What kind of element holds keyboard focus, as far as shortcut dispatch is concerned. Only the
/// distinction matters, not the exact control type: whether the pressed key has an owner that needs
/// it more than the shortcut does.
/// </summary>
public enum ShortcutFocusContext
{
    /// <summary>Nothing focused, or a focused element that owns no keys (a panel, a text block).</summary>
    Unknown,

    /// <summary>A text field. The user is typing; none of its keys are ever taken.</summary>
    TextInput,

    /// <summary>A ListView, GridView, ListBox or TreeView, or an item container inside one.</summary>
    ItemsList,

    /// <summary>Any other control that consumes keys itself — a button, a slider, a combo box.</summary>
    Other
}

/// <summary>
/// Decides when a shortcut may run <em>ahead of</em> the focused element instead of after it.
/// <para>
/// WinUI raises <c>KeyboardAccelerator.Invoked</c> only for a key the focused element left unhandled,
/// so a focused list silently kills every shortcut it consumes. Measured against the running app with
/// a ListView focused by a real click: <c>Space</c> never fired (the list takes it for item
/// activation) and every <c>Home</c> variant was swallowed too, while <c>Ctrl+J</c>, <c>Ctrl+B</c> and
/// a bare <c>Right</c> fired normally. A list holds focus for most of this app's lifetime, which made
/// the conventional <c>Space</c> play/pause a dead key — and it had been dead since long before
/// shortcuts became rebindable, because the old hard-coded accelerator worked the same way.
/// </para>
/// <para>
/// The window therefore tunnels ahead of the focused element and takes the key first, but only in the
/// narrow set of cases where taking it cannot steal a key its owner genuinely needs. Everything else
/// falls through to normal routing with the accelerator behind it, so a chord is still dispatched
/// exactly once.
/// </para>
/// </summary>
public static class ShortcutDispatchPolicy
{
    private static readonly string[] ListNavigationTokens =
    {
        "Enter",    // invoke the focused item
        "PageUp",
        "PageDown",
        "End",
        "Home",
        "Left",     // previous item, or collapse a TreeView node
        "Up",
        "Right",    // next item, or expand a TreeView node
        "Down"
    };

    private static readonly HashSet<int> ListNavigationKeys = BuildKeyCodes();

    /// <summary>The reserved key codes, exposed so the mapping from token to code stays under test.</summary>
    public static IReadOnlyCollection<int> ListNavigationKeyCodes => ListNavigationKeys;

    /// <summary>
    /// True for a chord a focused list needs for its own navigation. Reserved unmodified and with
    /// Shift alone, which extends the selection; with Ctrl, Alt or Win the chord is the shortcut's,
    /// because a rebindable shortcut that silently does nothing is worse than losing the list's
    /// obscure "move focus without selecting" variants.
    /// </summary>
    public static bool IsListNavigationChord(KeyChord chord) =>
        (chord.Modifiers & ~ShortcutModifiers.Shift) == ShortcutModifiers.None
        && ListNavigationKeys.Contains(chord.KeyCode);

    /// <summary>
    /// True when the window should dispatch <paramref name="chord"/> itself and mark the key handled,
    /// instead of letting it reach the focused element and relying on the accelerator behind it.
    /// </summary>
    public static bool ShouldPreemptFocusedElement(ShortcutFocusContext context, KeyChord chord) =>
        context == ShortcutFocusContext.ItemsList
        && chord.IsValid
        && !IsListNavigationChord(chord);

    private static HashSet<int> BuildKeyCodes()
    {
        var codes = new HashSet<int>();
        foreach (var token in ListNavigationTokens)
        {
            if (ShortcutKeyNames.TryGetKeyCode(token, out var code)) codes.Add(code);
        }

        return codes;
    }
}
