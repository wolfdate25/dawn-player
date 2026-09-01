using System.Collections.ObjectModel;
using System.Globalization;

namespace DawnPlayer.App.Views;

/// <summary>
/// Data model representing a node in the hierarchical library tree.
/// </summary>
public sealed class LibraryTreeNode
{
    public string Title { get; set; } = "";
    public string Glyph { get; set; } = "\uE8B9";
    public string FilterType { get; set; } = "All"; // All, Artist, Album, ArtistAlbum, Genre, GenreArtist, GenreArtistAlbum, Folder
    public string FilterValue { get; set; } = "";
    public string FilterExtra { get; set; } = "";
    public string FilterExtra2 { get; set; } = "";
    public int Count { get; set; }
    public string CountText => Count > 0 ? Count.ToString("N0", CultureInfo.InvariantCulture) : "";
    public ObservableCollection<LibraryTreeNode> Children { get; } = new();

    /// <summary>Whether the view should render this node already expanded.</summary>
    public bool DefaultExpanded { get; set; }

    /// <summary>
    /// The tree's item template draws the title, but a TreeViewItem's accessible name comes from the
    /// bound object. Without this every node announced itself as
    /// "DawnPlayer.App.Views.LibraryTreeNode", so the whole library tree was unreadable to screen
    /// readers and to UI automation.
    /// </summary>
    public override string ToString() =>
        Count > 0 ? $"{Title} ({Count})" : Title;
}
