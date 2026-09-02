using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Localization;
using DawnPlayer.Core.Models;

namespace DawnPlayer.App.Views;

/// <summary>
/// Tree grouping modes for the left library navigation pane.
/// </summary>
public enum TreeGroupMode
{
    ArtistAlbum = 0,
    Artist = 1,
    GenreArtist = 2,
    GenreArtistAlbum = 3,
    Album = 4,
    Genre = 5,
    Folder = 6
}

/// <summary>
/// Builds the library tree as plain <see cref="LibraryTreeNode"/> hierarchies for all seven
/// grouping modes.
/// </summary>
/// <remarks>
/// Free of WinUI types so the grouping algorithms can be linked into the test project and covered
/// directly; <see cref="LibraryTreeBuilder"/> is the thin adapter that wraps the result in
/// TreeView nodes.
/// </remarks>
public static class LibraryTreeModelBuilder
{
    private const string UnknownAlbumTitle = "(Single / Unknown)";

    /// <summary>
    /// Builds the root ("전체") node plus the hierarchy for <paramref name="mode"/> into
    /// <paramref name="roots"/>, and returns the root node.
    /// </summary>
    public static LibraryTreeNode BuildTree(IReadOnlyList<Track> tracks, TreeGroupMode mode, IList<LibraryTreeNode> roots)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(roots);

        roots.Clear();

        var allNode = new LibraryTreeNode
        {
            Title = AppStrings.Get("Library_AllNode", "전체"),
            Glyph = "\uE8B9",
            FilterType = "All",
            Count = tracks.Count,
            DefaultExpanded = true
        };
        roots.Add(allNode);

        switch (mode)
        {
            case TreeGroupMode.ArtistAlbum:
                BuildArtistAlbumTree(tracks, roots);
                break;
            case TreeGroupMode.Artist:
                BuildArtistTree(tracks, roots);
                break;
            case TreeGroupMode.GenreArtist:
                BuildGenreArtistTree(tracks, roots);
                break;
            case TreeGroupMode.GenreArtistAlbum:
                BuildGenreArtistAlbumTree(tracks, roots);
                break;
            case TreeGroupMode.Album:
                BuildAlbumTree(tracks, roots);
                break;
            case TreeGroupMode.Genre:
                BuildGenreTree(tracks, roots);
                break;
            case TreeGroupMode.Folder:
                BuildFolderTree(tracks, roots);
                break;
        }

        return allNode;
    }

    public static void BuildArtistAlbumTree(IReadOnlyList<Track> tracks, IList<LibraryTreeNode> parent)
    {
        var artistGroups = tracks
            .GroupBy(t => t.SortArtist)
            .Where(g => g.Key.Length > 0)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var ag in artistGroups)
        {
            var artistNode = new LibraryTreeNode
            {
                Title = ag.Key,
                Glyph = "\uE77B",
                FilterType = "Artist",
                FilterValue = ag.Key,
                Count = ag.Count()
            };

            var albumGroups = ag
                .GroupBy(t => t.Album)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var alb in albumGroups)
            {
                artistNode.Children.Add(new LibraryTreeNode
                {
                    Title = string.IsNullOrEmpty(alb.Key) ? UnknownAlbumTitle : alb.Key,
                    Glyph = "\uE93C",
                    FilterType = "ArtistAlbum",
                    FilterValue = alb.Key,
                    FilterExtra = ag.Key,
                    Count = alb.Count()
                });
            }

            parent.Add(artistNode);
        }
    }

    public static void BuildArtistTree(IReadOnlyList<Track> tracks, IList<LibraryTreeNode> parent)
    {
        var artistGroups = tracks
            .GroupBy(t => t.SortArtist)
            .Where(g => g.Key.Length > 0)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var ag in artistGroups)
        {
            parent.Add(new LibraryTreeNode
            {
                Title = ag.Key,
                Glyph = "\uE77B",
                FilterType = "Artist",
                FilterValue = ag.Key,
                Count = ag.Count()
            });
        }
    }

    public static void BuildGenreArtistTree(IReadOnlyList<Track> tracks, IList<LibraryTreeNode> parent)
    {
        foreach (var gg in GenreGroups(tracks))
        {
            var genreNode = new LibraryTreeNode
            {
                Title = gg.Key,
                Glyph = "\uE8D6",
                FilterType = "Genre",
                FilterValue = gg.Key,
                Count = gg.Count()
            };

            var artistGroups = gg
                .GroupBy(t => t.SortArtist)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var ag in artistGroups)
            {
                genreNode.Children.Add(new LibraryTreeNode
                {
                    Title = ag.Key,
                    Glyph = "\uE77B",
                    FilterType = "GenreArtist",
                    FilterValue = ag.Key,
                    FilterExtra = gg.Key,
                    Count = ag.Count()
                });
            }

            parent.Add(genreNode);
        }
    }

    public static void BuildGenreArtistAlbumTree(IReadOnlyList<Track> tracks, IList<LibraryTreeNode> parent)
    {
        foreach (var gg in GenreGroups(tracks))
        {
            var genreNode = new LibraryTreeNode
            {
                Title = gg.Key,
                Glyph = "\uE8D6",
                FilterType = "Genre",
                FilterValue = gg.Key,
                Count = gg.Count()
            };

            var artistGroups = gg
                .GroupBy(t => t.SortArtist)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var ag in artistGroups)
            {
                var artistNode = new LibraryTreeNode
                {
                    Title = ag.Key,
                    Glyph = "\uE77B",
                    FilterType = "GenreArtist",
                    FilterValue = ag.Key,
                    FilterExtra = gg.Key,
                    Count = ag.Count()
                };

                var albumGroups = ag
                    .GroupBy(t => t.Album)
                    .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

                foreach (var alb in albumGroups)
                {
                    artistNode.Children.Add(new LibraryTreeNode
                    {
                        Title = string.IsNullOrEmpty(alb.Key) ? UnknownAlbumTitle : alb.Key,
                        Glyph = "\uE93C",
                        FilterType = "GenreArtistAlbum",
                        FilterValue = alb.Key,
                        FilterExtra = ag.Key,
                        FilterExtra2 = gg.Key,
                        Count = alb.Count()
                    });
                }

                genreNode.Children.Add(artistNode);
            }

            parent.Add(genreNode);
        }
    }

    public static void BuildAlbumTree(IReadOnlyList<Track> tracks, IList<LibraryTreeNode> parent)
    {
        var albumGroups = tracks
            .GroupBy(t => t.AlbumKey)
            .Where(g => g.Key.Trim('\u0001').Length > 0 && g.First().Album.Length > 0)
            .OrderBy(g => g.First().Album, StringComparer.CurrentCultureIgnoreCase);

        foreach (var alb in albumGroups)
        {
            var first = alb.First();
            parent.Add(new LibraryTreeNode
            {
                Title = $"{first.Album} — {first.SortArtist}",
                Glyph = "\uE93C",
                FilterType = "Album",
                FilterValue = alb.Key,
                Count = alb.Count()
            });
        }
    }

    public static void BuildGenreTree(IReadOnlyList<Track> tracks, IList<LibraryTreeNode> parent)
    {
        foreach (var gg in GenreGroups(tracks))
        {
            parent.Add(new LibraryTreeNode
            {
                Title = gg.Key,
                Glyph = "\uE8D6",
                FilterType = "Genre",
                FilterValue = gg.Key,
                Count = gg.Count()
            });
        }
    }

    private static IEnumerable<IGrouping<string, Track>> GenreGroups(IReadOnlyList<Track> tracks) =>
        tracks
            .Where(t => t.Genre.Length > 0)
            .GroupBy(t => t.Genre)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

    private sealed class FolderDirNode
    {
        public string FullPath { get; set; } = "";
        public string Name { get; set; } = "";
        public Dictionary<string, FolderDirNode> Subfolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int DirectCount { get; set; }
        public int TotalCount { get; set; }
    }

    public static void BuildFolderTree(IReadOnlyList<Track> tracks, IList<LibraryTreeNode> parent)
    {
        if (tracks.Count == 0) return;

        var rootMap = new Dictionary<string, FolderDirNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            var dir = System.IO.Path.GetDirectoryName(track.Path);
            if (string.IsNullOrEmpty(dir)) continue;

            dir = System.IO.Path.GetFullPath(dir);
            var root = System.IO.Path.GetPathRoot(dir);
            if (string.IsNullOrEmpty(root)) root = dir;

            if (!rootMap.TryGetValue(root, out var rootNode))
            {
                rootNode = new FolderDirNode
                {
                    FullPath = root,
                    Name = root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                };
                if (string.IsNullOrEmpty(rootNode.Name)) rootNode.Name = root;
                rootMap[root] = rootNode;
            }

            var relative = dir.Length > root.Length
                ? dir.Substring(root.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                : "";
            var curr = rootNode;

            if (!string.IsNullOrEmpty(relative))
            {
                var parts = relative.Split(new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                var accumulated = root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

                foreach (var part in parts)
                {
                    accumulated = System.IO.Path.Combine(accumulated, part);
                    if (!curr.Subfolders.TryGetValue(part, out var childNode))
                    {
                        childNode = new FolderDirNode
                        {
                            FullPath = accumulated,
                            Name = part
                        };
                        curr.Subfolders[part] = childNode;
                    }
                    curr = childNode;
                }
            }

            curr.DirectCount++;
        }

        static int ComputeTotal(FolderDirNode node)
        {
            int sum = node.DirectCount;
            foreach (var sub in node.Subfolders.Values)
            {
                sum += ComputeTotal(sub);
            }
            node.TotalCount = sum;
            return sum;
        }

        foreach (var r in rootMap.Values)
        {
            ComputeTotal(r);
        }

        // A drive whose tracks all live far below the root would otherwise render as a chain of
        // single-child folders; start at the first level that actually branches or holds tracks.
        static FolderDirNode SimplifyRoot(FolderDirNode node)
        {
            var curr = node;
            while (curr.DirectCount == 0 && curr.Subfolders.Count == 1)
            {
                curr = curr.Subfolders.Values.First();
            }
            return curr;
        }

        static LibraryTreeNode CreateNode(FolderDirNode dirNode, bool isRoot)
        {
            var node = new LibraryTreeNode
            {
                Title = isRoot ? dirNode.FullPath : dirNode.Name,
                Glyph = "\uE838",
                FilterType = "Folder",
                FilterValue = dirNode.FullPath,
                Count = dirNode.TotalCount,
                DefaultExpanded = isRoot
            };

            foreach (var sub in dirNode.Subfolders.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                node.Children.Add(CreateNode(sub, isRoot: false));
            }

            return node;
        }

        foreach (var r in rootMap.Values.OrderBy(r => r.FullPath, StringComparer.CurrentCultureIgnoreCase))
        {
            parent.Add(CreateNode(SimplifyRoot(r), isRoot: true));
        }
    }

    /// <summary>
    /// Searches the hierarchy for the first node matching the filter criteria. A null
    /// <paramref name="val"/> or <paramref name="extra"/> matches any value.
    /// </summary>
    public static LibraryTreeNode? FindNodeRecursive(IEnumerable<LibraryTreeNode> nodes, string type, string? val, string? extra)
    {
        if (nodes == null) return null;

        foreach (var n in nodes)
        {
            if (n.FilterType == type && (val == null || n.FilterValue == val) && (extra == null || n.FilterExtra == extra))
                return n;

            var child = FindNodeRecursive(n.Children, type, val, extra);
            if (child != null) return child;
        }
        return null;
    }
}
