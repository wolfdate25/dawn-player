using DawnPlayer.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace DawnPlayer.App.Views;

/// <summary>
/// Wraps the hierarchy produced by <see cref="LibraryTreeModelBuilder"/> in WinUI TreeView nodes.
/// The grouping algorithms themselves live in the model builder, which carries no WinUI dependency
/// and is therefore directly testable.
/// </summary>
public static class LibraryTreeBuilder
{
    /// <summary>
    /// Builds the root node and hierarchical children for the given grouping mode.
    /// </summary>
    public static TreeViewNode BuildTree(IReadOnlyList<Track> tracks, TreeGroupMode mode, IList<TreeViewNode> rootNodes)
    {
        rootNodes.Clear();

        var models = new List<LibraryTreeNode>();
        var allModel = LibraryTreeModelBuilder.BuildTree(tracks, mode, models);

        TreeViewNode? allTvNode = null;
        foreach (var model in models)
        {
            var tvNode = ToTreeViewNode(model);
            if (ReferenceEquals(model, allModel)) allTvNode = tvNode;
            rootNodes.Add(tvNode);
        }

        return allTvNode ?? new TreeViewNode { Content = allModel, IsExpanded = allModel.DefaultExpanded };
    }

    private static TreeViewNode ToTreeViewNode(LibraryTreeNode model)
    {
        var node = new TreeViewNode
        {
            Content = model,
            IsExpanded = model.DefaultExpanded
        };

        foreach (var child in model.Children)
        {
            node.Children.Add(ToTreeViewNode(child));
        }

        return node;
    }

    /// <summary>
    /// Searches the tree hierarchy recursively for a node matching the filter criteria.
    /// </summary>
    public static TreeViewNode? FindNodeRecursive(IList<TreeViewNode> nodes, string type, string? val, string? extra)
    {
        foreach (var n in nodes)
        {
            if (n.Content is LibraryTreeNode ln)
            {
                if (ln.FilterType == type && (val == null || ln.FilterValue == val) && (extra == null || ln.FilterExtra == extra))
                    return n;
            }
            var child = FindNodeRecursive(n.Children, type, val, extra);
            if (child != null) return child;
        }
        return null;
    }

    /// <summary>
    /// Expands all ancestor nodes of the given TreeViewNode so that it becomes visible in the tree.
    /// </summary>
    public static void ExpandAncestors(TreeViewNode node)
    {
        var cur = node.Parent;
        while (cur != null)
        {
            cur.IsExpanded = true;
            cur = cur.Parent;
        }
    }
}
