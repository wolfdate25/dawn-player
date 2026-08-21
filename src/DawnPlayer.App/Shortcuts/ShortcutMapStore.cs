using System;

namespace DawnPlayer.App.Shortcuts;

/// <summary>
/// The mutation rules for a <see cref="ShortcutMap"/>: every change funnels through
/// <see cref="Commit"/>, which runs the owner's callback (persist, rebuild accelerators) and then
/// notifies listeners. <c>ShortcutService</c> composes this rather than reimplementing it, so there
/// is one place that decides what counts as a change.
/// </summary>
public sealed class ShortcutMapStore : IShortcutBindingStore
{
    private readonly Action? _onChanged;

    /// <param name="map">The map to own. A fresh default map when omitted.</param>
    /// <param name="onChanged">
    /// Runs before <see cref="ShortcutsChanged"/> on every change. Omit for an in-memory store that
    /// does not persist — useful in tests and as a harmless stand-in before services are wired up.
    /// </param>
    public ShortcutMapStore(ShortcutMap? map = null, Action? onChanged = null)
    {
        Map = map ?? new ShortcutMap();
        _onChanged = onChanged;
    }

    public ShortcutMap Map { get; }

    public event Action? ShortcutsChanged;

    public ShortcutAssignResult TryAssign(ShortcutCommand command, KeyChord chord, out ShortcutCommand conflicting)
    {
        var result = Map.TryAssign(command, chord, out conflicting);
        if (result == ShortcutAssignResult.Assigned) Commit();
        return result;
    }

    public void ForceAssign(ShortcutCommand command, KeyChord chord)
    {
        Map.ForceAssign(command, chord);
        Commit();
    }

    public void Clear(ShortcutCommand command)
    {
        Map.Clear(command);
        Commit();
    }

    public void ResetToDefault(ShortcutCommand command)
    {
        Map.ResetToDefault(command);
        Commit();
    }

    public void ResetAll()
    {
        Map.ResetAll();
        Commit();
    }

    private void Commit()
    {
        _onChanged?.Invoke();
        ShortcutsChanged?.Invoke();
    }
}
