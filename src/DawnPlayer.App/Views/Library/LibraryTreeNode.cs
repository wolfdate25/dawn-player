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
    /// Whether the view may defer materializing this node's children until first expansion
    /// (P0 lazy expansion). The model hierarchy itself is always complete; only UI node
    /// creation is deferred. Currently set for Folder-mode non-leaf nodes only.
    /// </summary>
    public bool DeferChildren { get; set; }

    /// <summary>
    /// Whether this node comes from Folder mode. The row icon is redundant there
    /// (every node is a folder; the expander already marks parents), so the view
    /// hides it to return width to the title.
    /// </summary>
    public bool IsFolder => string.Equals(FilterType, "Folder", StringComparison.Ordinal);

    /// <summary>
    /// The tree's item template draws the title, but a TreeViewItem's accessible name comes from the
    /// bound object. Without this every node announced itself as
    /// "DawnPlayer.App.Views.LibraryTreeNode", so the whole library tree was unreadable to screen
    /// readers and to UI automation.
    /// </summary>
    public override string ToString() =>
        Count > 0 ? $"{Title} ({Count})" : Title;
}
