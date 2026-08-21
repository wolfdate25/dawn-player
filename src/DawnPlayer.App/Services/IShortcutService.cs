using DawnPlayer.App.Shortcuts;
using Microsoft.UI.Xaml;

namespace DawnPlayer.App.Services;

/// <summary>
/// Owns the effective keyboard shortcut map, keeps the window root's
/// <see cref="UIElement.KeyboardAccelerators"/> in sync with it, and persists changes.
/// </summary>
public interface IShortcutService : IShortcutBindingStore
{
    /// <summary>
    /// Takes over the accelerator collection on <paramref name="root"/> and rebuilds it from the
    /// map. Called once with the window root; later rebuilds happen automatically on change.
    /// </summary>
    void AttachTo(UIElement root);
}
