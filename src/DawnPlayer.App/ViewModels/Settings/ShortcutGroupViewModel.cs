using System.Collections.Generic;
using DawnPlayer.App.Shortcuts;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>
/// A category of shortcut rows. WinUI list grouping needs a CollectionViewSource and a code-behind
/// to drive it; a plain group-of-rows shape renders with two nested ItemsControls and no extra state.
/// </summary>
public sealed class ShortcutGroupViewModel
{
    public ShortcutGroupViewModel(ShortcutCategory category, IReadOnlyList<ShortcutBindingViewModel> items)
    {
        Category = category;
        Name = ShortcutCommandCatalog.GetCategoryName(category);
        Items = items;
    }

    public ShortcutCategory Category { get; }

    public string Name { get; }

    public IReadOnlyList<ShortcutBindingViewModel> Items { get; }
}
