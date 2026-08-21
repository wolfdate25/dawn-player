using System;
using DawnPlayer.App.Helpers;
using DawnPlayer.App.Shortcuts;
using DawnPlayer.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DawnPlayer.App.Services;

/// <inheritdoc cref="IShortcutService"/>
public sealed class ShortcutService : IShortcutService
{
    private readonly AppSettings _settings;
    private readonly ShortcutMapStore _store;
    private UIElement? _root;

    public ShortcutService(AppSettings settings)
    {
        _settings = settings;
        _store = new ShortcutMapStore(new ShortcutMap(settings.Shortcuts.Bindings), PersistAndRebuild);
        _store.ShortcutsChanged += () => ShortcutsChanged?.Invoke();
    }

    public ShortcutMap Map => _store.Map;

    public event Action? ShortcutsChanged;

    public void AttachTo(UIElement root)
    {
        if (_root != null) _root.PreviewKeyDown -= OnRootPreviewKeyDown;

        _root = root;
        root.PreviewKeyDown += OnRootPreviewKeyDown;
        Rebuild();
    }

    public ShortcutAssignResult TryAssign(ShortcutCommand command, KeyChord chord, out ShortcutCommand conflicting) =>
        _store.TryAssign(command, chord, out conflicting);

    public void ForceAssign(ShortcutCommand command, KeyChord chord) => _store.ForceAssign(command, chord);

    public void Clear(ShortcutCommand command) => _store.Clear(command);

    public void ResetToDefault(ShortcutCommand command) => _store.ResetToDefault(command);

    public void ResetAll() => _store.ResetAll();

    private void PersistAndRebuild()
    {
        _settings.Shortcuts.Bindings = Map.ToBindingsDictionary();
        SettingsWriter.Schedule(_settings);
        Rebuild();
    }

    /// <summary>
    /// Runs a shortcut ahead of the focused element for the keys that element would otherwise swallow.
    /// <para>
    /// Accelerators alone are not enough: WinUI raises <c>Invoked</c> only for a key the focused
    /// element left unhandled, so every shortcut a focused list consumes was dead — <c>Space</c>
    /// play/pause above all, and a list holds focus for most of this app's lifetime. Tunnelling from
    /// the window root reaches the key before the list does; <see cref="ShortcutDispatchPolicy"/>
    /// decides the narrow set of cases where taking it is safe. Everything it declines falls through
    /// to normal routing with the accelerator behind it, so a chord is dispatched exactly once either
    /// way, and taking the key here rather than reacting to it afterwards means the list's own action
    /// — moving the selection on Space — never runs at all.
    /// </para>
    /// </summary>
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled) return;

        var chord = new KeyChord(KeyboardHelper.ReadModifiers(), (int)args.Key);
        var context = KeyboardHelper.ClassifyFocus(_root?.XamlRoot);
        if (!ShortcutDispatchPolicy.ShouldPreemptFocusedElement(context, chord)) return;

        if (!Map.TryFindCommand(chord, out var command)) return;

        // Execute still owns the decision to decline, so the text-input guard has exactly one home
        // even though this path is already unreachable while a text input holds focus.
        args.Handled = ShortcutCommandExecutor.Execute(command, _root?.XamlRoot);
    }

    /// <summary>
    /// Replaces the root element's accelerators wholesale. Rebuilding rather than patching keeps the
    /// collection an exact mirror of the map, so an unbound command leaves no stale accelerator
    /// behind and a rebound one cannot end up registered twice.
    /// </summary>
    private void Rebuild()
    {
        if (_root == null) return;

        _root.KeyboardAccelerators.Clear();

        foreach (var info in ShortcutCommandCatalog.All)
        {
            var chord = Map.GetChord(info.Command);
            if (chord == null || !chord.Value.IsValid) continue;

            var command = info.Command;
            var accelerator = new KeyboardAccelerator
            {
                Key = (VirtualKey)chord.Value.KeyCode,
                Modifiers = (VirtualKeyModifiers)(int)chord.Value.Modifiers
            };

            // Returning false from the executor (focus is in a text input) must leave the key
            // unhandled so the character still reaches the TextBox.
            accelerator.Invoked += (_, args) =>
                args.Handled = ShortcutCommandExecutor.Execute(command, _root?.XamlRoot);

            _root.KeyboardAccelerators.Add(accelerator);
        }
    }
}
