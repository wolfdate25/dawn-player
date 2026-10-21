using System;
using System.Collections.Generic;
using DawnPlayer.Core.Models;

namespace DawnPlayer.App.Views;

/// <summary>
/// Pure logic engine for in-playlist search (Find &amp; Jump).
/// Evaluates match queries against track metadata and computes sequential navigation targets.
/// </summary>
public static class PlaylistSearchEngine
{
    /// <summary>
    /// Searches <paramref name="items"/> for tracks matching all whitespace-separated tokens in <paramref name="query"/>
    /// across Title, Artist, Album, and Path. Returns the 0-based matching indices.
    /// </summary>
    public static IReadOnlyList<int> FindMatches(IReadOnlyList<PlaylistItem>? items, string? query)
    {
        if (items == null || items.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<int>();
        }

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return Array.Empty<int>();
        }

        var matches = new List<int>();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var track = item?.Track;
            if (track == null) continue;

            string title = track.Title ?? string.Empty;
            string artist = track.Artist ?? string.Empty;
            string album = track.Album ?? string.Empty;
            string path = track.Path ?? string.Empty;

            bool allTokensMatch = true;
            for (int t = 0; t < tokens.Length; t++)
            {
                var token = tokens[t];
                bool tokenMatched = title.Contains(token, StringComparison.OrdinalIgnoreCase)
                    || artist.Contains(token, StringComparison.OrdinalIgnoreCase)
                    || album.Contains(token, StringComparison.OrdinalIgnoreCase)
                    || path.Contains(token, StringComparison.OrdinalIgnoreCase);

                if (!tokenMatched)
                {
                    allTokensMatch = false;
                    break;
                }
            }

            if (allTokensMatch)
            {
                matches.Add(i);
            }
        }

        return matches;
    }

    /// <summary>
    /// Computes the next match index after <paramref name="currentIndex"/>, wrapping around to the first match.
    /// Returns -1 if <paramref name="matches"/> is null or empty.
    /// </summary>
    public static int GetNextMatchIndex(IReadOnlyList<int>? matches, int currentIndex)
    {
        if (matches == null || matches.Count == 0) return -1;

        for (int i = 0; i < matches.Count; i++)
        {
            if (matches[i] > currentIndex)
            {
                return matches[i];
            }
        }

        // Wrap around to first match
        return matches[0];
    }

    /// <summary>
    /// Computes the previous match index before <paramref name="currentIndex"/>, wrapping around to the last match.
    /// Returns -1 if <paramref name="matches"/> is null or empty.
    /// </summary>
    public static int GetPrevMatchIndex(IReadOnlyList<int>? matches, int currentIndex)
    {
        if (matches == null || matches.Count == 0) return -1;

        for (int i = matches.Count - 1; i >= 0; i--)
        {
            if (matches[i] < currentIndex)
            {
                return matches[i];
            }
        }

        // Wrap around to last match
        return matches[^1];
    }
}
