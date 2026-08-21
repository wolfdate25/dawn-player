using System;
using System.Collections.Generic;
using System.Linq;

namespace DawnPlayer.App.Shortcuts;

/// <summary>
/// The allow-list of bindable keys, with the canonical token used in settings and the label shown
/// in the UI. Codes are <c>Windows.System.VirtualKey</c> numeric values.
/// <para>
/// The list is an allow-list rather than a block-list on purpose. Tab, Escape, Backspace and the
/// bare modifier keys are load-bearing for focus movement and dialog dismissal, and a chord token
/// never contains a literal <c>+</c> — punctuation keys are spelled out ("Minus", "Equals") — so
/// parsing a token can split on <c>+</c> without ambiguity.
/// </para>
/// </summary>
public static class ShortcutKeyNames
{
    private static readonly (int Code, string Token, string Display)[] Table = BuildTable();

    private static readonly Dictionary<int, (string Token, string Display)> ByCode =
        Table.ToDictionary(e => e.Code, e => (e.Token, e.Display));

    private static readonly Dictionary<string, int> ByToken =
        Table.ToDictionary(e => e.Token, e => e.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>All bindable keys in menu order.</summary>
    public static IReadOnlyList<(int Code, string Token, string Display)> All => Table;

    public static bool IsAllowedKey(int keyCode) => ByCode.ContainsKey(keyCode);

    /// <summary>Canonical settings token for a key, or null when the key is not bindable.</summary>
    public static string? GetToken(int keyCode) => ByCode.TryGetValue(keyCode, out var e) ? e.Token : null;

    /// <summary>Human-facing label for a key, or null when the key is not bindable.</summary>
    public static string? GetDisplay(int keyCode) => ByCode.TryGetValue(keyCode, out var e) ? e.Display : null;

    public static bool TryGetKeyCode(string? token, out int keyCode)
    {
        keyCode = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;
        return ByToken.TryGetValue(token.Trim(), out keyCode);
    }

    private static (int, string, string)[] BuildTable()
    {
        var list = new List<(int, string, string)>
        {
            (32, "Space", "Space"),
            (13, "Enter", "Enter"),
            (37, "Left", "←"),
            (38, "Up", "↑"),
            (39, "Right", "→"),
            (40, "Down", "↓"),
            (36, "Home", "Home"),
            (35, "End", "End"),
            (33, "PageUp", "PageUp"),
            (34, "PageDown", "PageDown"),
            (45, "Insert", "Insert"),
            (46, "Delete", "Delete")
        };

        for (int c = 'A'; c <= 'Z'; c++) list.Add((c, ((char)c).ToString(), ((char)c).ToString()));
        for (int c = '0'; c <= '9'; c++) list.Add((c, ((char)c).ToString(), ((char)c).ToString()));
        for (int i = 1; i <= 24; i++) list.Add((111 + i, "F" + i, "F" + i));
        for (int i = 0; i <= 9; i++) list.Add((96 + i, "NumPad" + i, "NumPad " + i));

        list.Add((106, "NumPadMultiply", "NumPad *"));
        list.Add((107, "NumPadAdd", "NumPad +"));
        list.Add((109, "NumPadSubtract", "NumPad -"));
        list.Add((110, "NumPadDecimal", "NumPad ."));
        list.Add((111, "NumPadDivide", "NumPad /"));

        list.Add((186, "Semicolon", ";"));
        list.Add((187, "Equals", "="));
        list.Add((188, "Comma", ","));
        list.Add((189, "Minus", "-"));
        list.Add((190, "Period", "."));
        list.Add((191, "Slash", "/"));
        list.Add((192, "Backquote", "`"));
        list.Add((219, "LeftBracket", "["));
        list.Add((220, "Backslash", "\\"));
        list.Add((221, "RightBracket", "]"));
        list.Add((222, "Quote", "'"));

        return list.ToArray();
    }
}
