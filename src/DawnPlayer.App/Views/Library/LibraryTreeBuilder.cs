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
    /// Folder mode defers child UI nodes until first expansion (P0 lazy expansion):
    /// the model hierarchy stays complete, so counts and filters are unaffected.
    /// User expansion state survives the rebuild (PT2-05): a rescan or tag edit used to
    /// collapse every manually expanded node back to its DefaultExpanded default.
    /// </summary>
    public static TreeViewNode BuildTree(IReadOnlyList<Track> tracks, TreeGroupMode mode, IList<TreeViewNode> rootNodes)
    {
        var expandedKeys = CollectExpandedKeys(rootNodes);

        rootNodes.Clear();

        var models = new List<LibraryTreeNode>();
        var allModel = LibraryTreeModelBuilder.BuildTree(tracks, mode, models);

        bool lazy = mode == TreeGroupMode.Folder;
        TreeViewNode? allTvNode = null;
        foreach (var model in models)
        {
            var tvNode = ToTreeViewNode(model, lazy, expandedKeys);
            if (ReferenceEquals(model, allModel)) allTvNode = tvNode;
            rootNodes.Add(tvNode);
        }

        return allTvNode ?? new TreeViewNode { Content = allModel, IsExpanded = allModel.DefaultExpanded };
    }

    /// <summary>Stable identity of a node across rebuilds: its filter coordinates. Two nodes
    /// with the same coordinates are the same node for expansion-state purposes.</summary>
    public static string ExpansionKey(LibraryTreeNode model) =>
        $"{model.FilterType}|{model.FilterValue}|{model.FilterExtra}|{model.FilterExtra2}";

    private static HashSet<string> CollectExpandedKeys(IList<TreeViewNode> roots)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Walk(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.IsExpanded && n.Content is LibraryTreeNode model)
                {
                    keys.Add(ExpansionKey(model));
                }
                Walk(n.Children);
            }
        }
        Walk(roots);
        return keys;
    }

    /// <summary>Marks an unexpanded node whose children are not materialized yet.</summary>
    private static readonly object DeferredSentinel = new();

    private static TreeViewNode ToTreeViewNode(LibraryTreeNode model, bool lazy, HashSet<string>? expandedKeys = null)
    {
        bool expanded = model.DefaultExpanded
            || (expandedKeys?.Contains(ExpansionKey(model)) ?? false);
        // Content MUST carry the model: the item template binds Content.Title/Glyph/CountText,
        // and every lookup (filter match, deferred expansion, selection restore) pattern-matches
        // node.Content as LibraryTreeNode. Dropping this assignment (as the PT2-05 refactor
        // briefly did) turns the whole tree into blank rows — pinned by a source-scan gate.
        var node = new TreeViewNode { Content = model, IsExpanded = expanded };

        if (lazy && model.DeferChildren)
        {
            if (expanded)
            {
                // Restored expansion materializes immediately: a sentinel child under an
                // already-expanded node depends on Expanding firing for a node the TreeView
                // never saw the user expand — materializing here keeps the restore honest.
                foreach (var child in model.Children)
                {
                    node.Children.Add(ToTreeViewNode(child, lazy, expandedKeys));
                }
            }
            else
            {
                node.Children.Add(new TreeViewNode { Content = DeferredSentinel });
            }
        }
        else
        {
            foreach (var child in model.Children)
            {
                node.Children.Add(ToTreeViewNode(child, lazy, expandedKeys));
            }
        }

        return node;
    }

    /// <summary>
    /// Replaces the deferred placeholder with the real children. Returns true when
    /// materialization happened; safe to call on already-loaded or childless nodes.
    /// Grandchildren of deferred nodes stay deferred themselves.
    /// </summary>
    public static bool EnsureChildrenLoaded(TreeViewNode node)
    {
        if (node.Children.Count != 1
            || !ReferenceEquals(node.Children[0].Content, DeferredSentinel))
            return false;

        node.Children.Clear();
        if (node.Content is LibraryTreeNode model)
        {
            foreach (var child in model.Children)
            {
                node.Children.Add(ToTreeViewNode(child, lazy: true));
            }
        }
        return true;
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
    /// Like <see cref="FindNodeRecursive"/>, but materializes deferred children while
    /// descending so selection restore keeps working on lazily built trees.
    /// </summary>
    public static TreeViewNode? FindNodeRecursiveMaterialized(IList<TreeViewNode> nodes, string type, string? val, string? extra)
    {
        foreach (var n in nodes)
        {
            EnsureChildrenLoaded(n);
            if (n.Content is LibraryTreeNode ln)
            {
                if (ln.FilterType == type && (val == null || ln.FilterValue == val) && (extra == null || ln.FilterExtra == extra))
                    return n;
            }
            var child = FindNodeRecursiveMaterialized(n.Children, type, val, extra);
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
