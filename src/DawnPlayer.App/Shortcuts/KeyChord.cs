using System;
using System.Collections.Generic;
using System.Text;

namespace DawnPlayer.App.Shortcuts;

/// <summary>
/// A modifier + key combination. <see cref="KeyCode"/> holds the numeric
/// <c>Windows.System.VirtualKey</c> value so this type can be shared with the test project, which
/// has no WinRT projection; the App layer casts it back.
/// </summary>
public readonly record struct KeyChord(ShortcutModifiers Modifiers, int KeyCode)
{
    private const ShortcutModifiers KnownModifiers =
        ShortcutModifiers.Control | ShortcutModifiers.Menu | ShortcutModifiers.Shift | ShortcutModifiers.Windows;

    // Emitted and accepted in this order so that "Shift+Ctrl+S" and "Ctrl+Shift+S" normalize to
    // the same token, which is what makes conflict detection a plain dictionary lookup.
    private static readonly (ShortcutModifiers Flag, string Token, string Display)[] ModifierOrder =
    {
        (ShortcutModifiers.Control, "Ctrl", "Ctrl"),
        (ShortcutModifiers.Menu, "Alt", "Alt"),
        (ShortcutModifiers.Shift, "Shift", "Shift"),
        (ShortcutModifiers.Windows, "Win", "Win")
    };

    private static readonly Dictionary<string, ShortcutModifiers> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = ShortcutModifiers.Control,
        ["Control"] = ShortcutModifiers.Control,
        ["Alt"] = ShortcutModifiers.Menu,
        ["Menu"] = ShortcutModifiers.Menu,
        ["Shift"] = ShortcutModifiers.Shift,
        ["Win"] = ShortcutModifiers.Windows,
        ["Windows"] = ShortcutModifiers.Windows
    };

    /// <summary>True when the key is on the allow-list and no unknown modifier bits are set.</summary>
    public bool IsValid => ShortcutKeyNames.IsAllowedKey(KeyCode) && (Modifiers & ~KnownModifiers) == 0;

    /// <summary>Canonical settings token, e.g. "Ctrl+Shift+S". Empty when the chord is not valid.</summary>
    public string ToToken() => Format(useDisplayLabels: false);

    /// <summary>Label for the UI, e.g. "Ctrl+→". Empty when the chord is not valid.</summary>
    public string ToDisplayString() => Format(useDisplayLabels: true);

    public override string ToString() => ToDisplayString();

    private string Format(bool useDisplayLabels)
    {
        if (!IsValid) return string.Empty;

        var sb = new StringBuilder();
        foreach (var (flag, token, display) in ModifierOrder)
        {
            if ((Modifiers & flag) == 0) continue;
            sb.Append(useDisplayLabels ? display : token).Append('+');
        }

        sb.Append(useDisplayLabels
            ? ShortcutKeyNames.GetDisplay(KeyCode)
            : ShortcutKeyNames.GetToken(KeyCode));
        return sb.ToString();
    }

    /// <summary>
    /// Parses a canonical token such as "Ctrl+Shift+S". Modifier order and casing are free-form and
    /// aliases ("Control", "Alt", "Windows") are accepted, but the key must be the final segment and
    /// must be on the allow-list. Anything else — including a duplicated modifier or a bare modifier
    /// with no key — fails.
    /// </summary>
    public static bool TryParse(string? token, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var modifiers = ShortcutModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!ModifierAliases.TryGetValue(parts[i], out var flag)) return false;
            if ((modifiers & flag) != 0) return false;
            modifiers |= flag;
        }

        if (!ShortcutKeyNames.TryGetKeyCode(parts[^1], out var keyCode)) return false;

        chord = new KeyChord(modifiers, keyCode);
        return true;
    }
}
