using System;
using System.Collections.Generic;
using System.Linq;

namespace DawnPlayer.App.Shortcuts;

/// <summary>Outcome of an assignment attempt.</summary>
public enum ShortcutAssignResult
{
    Assigned,
    InvalidChord,
    Conflict
}

/// <summary>
/// The effective command-to-chord mapping: catalog defaults with the persisted overrides applied.
/// Kept a bijection at all times — no two commands may hold the same chord — because the accelerator
/// builder would otherwise register a duplicate and one of the two would silently never fire.
/// </summary>
public sealed class ShortcutMap
{
    private readonly Dictionary<ShortcutCommand, KeyChord?> _effective = new();

    public ShortcutMap() : this(null) { }

    public ShortcutMap(IReadOnlyDictionary<string, string>? overrides) => Load(overrides);

    /// <summary>Rebuilds the map from catalog defaults plus <paramref name="overrides"/>.</summary>
    public void Load(IReadOnlyDictionary<string, string>? overrides)
    {
        _effective.Clear();
        foreach (var info in ShortcutCommandCatalog.All)
        {
            _effective[info.Command] = info.DefaultChord;
        }

        if (overrides == null || overrides.Count == 0) return;

        // Sort by catalog order so a settings file that binds two commands to the same chord always
        // resolves the same way, whatever order the JSON happened to list them in.
        var order = ShortcutCommandCatalog.All
            .Select((info, index) => (info.Command, index))
            .ToDictionary(e => e.Command, e => e.index);

        var parsed = new List<(ShortcutCommand Command, KeyChord? Chord)>();
        foreach (var pair in overrides)
        {
            // Unknown command ids and unparsable tokens are dropped rather than thrown on: a
            // hand-edited or downgraded settings file must still let the app start.
            if (!ShortcutCommandCatalog.TryGetCommand(pair.Key, out var command)) continue;

            if (string.IsNullOrEmpty(pair.Value))
            {
                parsed.Add((command, null));
                continue;
            }

            if (!KeyChord.TryParse(pair.Value, out var chord) || !chord.IsValid) continue;
            parsed.Add((command, chord));
        }

        foreach (var (command, chord) in parsed.OrderBy(e => order[e.Command]))
        {
            if (chord == null)
            {
                _effective[command] = null;
                continue;
            }

            ForceAssign(command, chord.Value);
        }
    }

    public IReadOnlyDictionary<ShortcutCommand, KeyChord?> Effective => _effective;

    public KeyChord? GetChord(ShortcutCommand command) =>
        _effective.TryGetValue(command, out var chord) ? chord : null;

    public bool IsUnassigned(ShortcutCommand command) => GetChord(command) == null;

    public bool IsDefault(ShortcutCommand command) =>
        GetChord(command) == ShortcutCommandCatalog.Get(command).DefaultChord;

    /// <summary>Reverse lookup used for conflict reporting and for dispatching a pressed chord.</summary>
    public bool TryFindCommand(KeyChord chord, out ShortcutCommand command)
    {
        foreach (var pair in _effective)
        {
            if (pair.Value == chord)
            {
                command = pair.Key;
                return true;
            }
        }

        command = default;
        return false;
    }

    /// <summary>
    /// Assigns <paramref name="chord"/> without disturbing anything else. Returns
    /// <see cref="ShortcutAssignResult.Conflict"/> (with <paramref name="conflicting"/> set) when
    /// another command already holds it, leaving the map untouched so the caller can offer to steal.
    /// </summary>
    public ShortcutAssignResult TryAssign(ShortcutCommand command, KeyChord chord, out ShortcutCommand conflicting)
    {
        conflicting = default;
        if (!chord.IsValid) return ShortcutAssignResult.InvalidChord;

        if (TryFindCommand(chord, out var holder) && holder != command)
        {
            conflicting = holder;
            return ShortcutAssignResult.Conflict;
        }

        _effective[command] = chord;
        return ShortcutAssignResult.Assigned;
    }

    /// <summary>Assigns <paramref name="chord"/>, unbinding whatever other command held it.</summary>
    public void ForceAssign(ShortcutCommand command, KeyChord chord)
    {
        if (!chord.IsValid) return;

        if (TryFindCommand(chord, out var holder) && holder != command)
        {
            _effective[holder] = null;
        }

        _effective[command] = chord;
    }

    /// <summary>Leaves the command deliberately unbound. Persisted as an empty token.</summary>
    public void Clear(ShortcutCommand command) => _effective[command] = null;

    public void ResetToDefault(ShortcutCommand command)
    {
        var def = ShortcutCommandCatalog.Get(command).DefaultChord;
        if (def == null)
        {
            _effective[command] = null;
            return;
        }

        ForceAssign(command, def.Value);
    }

    public void ResetAll()
    {
        _effective.Clear();
        foreach (var info in ShortcutCommandCatalog.All)
        {
            _effective[info.Command] = info.DefaultChord;
        }
    }

    /// <summary>
    /// Serializes only the deltas against the catalog defaults, so an untouched install writes an
    /// empty dictionary and a later change of default reaches everyone who never overrode it.
    /// </summary>
    public Dictionary<string, string> ToBindingsDictionary()
    {
        var result = new Dictionary<string, string>();
        foreach (var info in ShortcutCommandCatalog.All)
        {
            var effective = GetChord(info.Command);
            if (effective == info.DefaultChord) continue;
            result[info.Command.ToString()] = effective?.ToToken() ?? string.Empty;
        }

        return result;
    }
}
