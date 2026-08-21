using System;

namespace DawnPlayer.App.Shortcuts;

/// <summary>
/// The rebinding surface of the shortcut service, split out from <c>IShortcutService</c> so the
/// settings ViewModel — and the test project, which has no WinUI — can depend on it without
/// pulling in <c>UIElement</c>.
/// </summary>
public interface IShortcutBindingStore
{
    /// <summary>The live command-to-chord mapping. Mutate it only through the methods below.</summary>
    ShortcutMap Map { get; }

    /// <summary>Raised after any binding change, so tooltips and shortcut hints can refresh.</summary>
    event Action? ShortcutsChanged;

    /// <summary>Assigns a chord, refusing (without changing anything) when another command holds it.</summary>
    ShortcutAssignResult TryAssign(ShortcutCommand command, KeyChord chord, out ShortcutCommand conflicting);

    /// <summary>Assigns a chord, unbinding whichever command held it.</summary>
    void ForceAssign(ShortcutCommand command, KeyChord chord);

    /// <summary>Leaves the command with no shortcut at all.</summary>
    void Clear(ShortcutCommand command);

    void ResetToDefault(ShortcutCommand command);

    void ResetAll();
}
