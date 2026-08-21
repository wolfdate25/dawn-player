using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Shortcuts;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>
/// Presentation state for the shortcut settings section. Depends on
/// <see cref="IShortcutBindingStore"/> rather than the full service so it stays free of WinUI types.
/// </summary>
public sealed class ShortcutSettingsViewModel : ViewModelBase
{
    private readonly IShortcutBindingStore _store;
    private readonly List<ShortcutBindingViewModel> _rows;

    public ShortcutSettingsViewModel(IShortcutBindingStore store)
    {
        _store = store;
        _rows = ShortcutCommandCatalog.All.Select(info => new ShortcutBindingViewModel(info)).ToList();

        Groups = _rows
            .GroupBy(row => row.Info.Category)
            .Select(g => new ShortcutGroupViewModel(g.Key, g.ToList()))
            .ToList();

        RefreshAll();
    }

    public IReadOnlyList<ShortcutGroupViewModel> Groups { get; }

    public IReadOnlyList<ShortcutBindingViewModel> Rows => _rows;

    private Action? _storeChangedHandler;

    /// <summary>
    /// Subscribes to out-of-band store changes so the rows never go stale. The store is
    /// app-lifetime while this VM is page-lifetime, so the page must call
    /// <see cref="DetachFromStore"/> on unload or every settings visit leaks a subscriber.
    /// </summary>
    public void AttachToStore()
    {
        if (_storeChangedHandler != null) return;
        _storeChangedHandler = RefreshAll;
        _store.ShortcutsChanged += _storeChangedHandler;
        RefreshAll();
    }

    public void DetachFromStore()
    {
        if (_storeChangedHandler == null) return;
        _store.ShortcutsChanged -= _storeChangedHandler;
        _storeChangedHandler = null;
    }

    public void RefreshAll()
    {
        foreach (var row in _rows)
        {
            row.Refresh(_store.Map);
        }
    }

    /// <summary>
    /// Attempts an assignment. On <see cref="ShortcutAssignResult.Conflict"/> nothing changes, so
    /// the caller can show which command holds the chord and offer <see cref="ForceAssign"/>.
    /// </summary>
    public ShortcutAssignResult TryAssign(ShortcutCommand command, KeyChord chord, out ShortcutCommand conflicting)
    {
        var result = _store.TryAssign(command, chord, out conflicting);
        if (result == ShortcutAssignResult.Assigned) RefreshAll();
        return result;
    }

    public void ForceAssign(ShortcutCommand command, KeyChord chord)
    {
        _store.ForceAssign(command, chord);
        RefreshAll();
    }

    public void Clear(ShortcutCommand command)
    {
        _store.Clear(command);
        RefreshAll();
    }

    public void ResetToDefault(ShortcutCommand command)
    {
        _store.ResetToDefault(command);
        RefreshAll();
    }

    public void ResetAll()
    {
        _store.ResetAll();
        RefreshAll();
    }

    public static string GetCommandDisplayName(ShortcutCommand command) =>
        ShortcutCommandCatalog.Get(command).DisplayName;
}
