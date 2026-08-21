using System;
using System.Collections.Generic;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Playlists;

/// <summary>
/// Builder responsible for clustering playlist items into album groups for CollectionViewSource.
/// </summary>
public static class PlaylistGroupBuilder
{
    /// <summary>
    /// Clusters consecutive tracks in the playlist with matching album keys into AlbumGroup instances using an immutable snapshot.
    /// </summary>
    public static List<AlbumGroup> BuildGroups(Playlist? pl) =>
        BuildGroupsFromSnapshot(pl?.GetSnapshot() ?? Array.Empty<PlaylistItem>());

    /// <summary>
    /// Clusters consecutive tracks from an item collection into AlbumGroup instances with immutable snapshot iteration.
    /// </summary>
    public static List<AlbumGroup> BuildGroupsFromItems(IEnumerable<PlaylistItem>? items) =>
        BuildGroupsFromSnapshot(CollectionSnapshot.Capture(items));

    private static List<AlbumGroup> BuildGroupsFromSnapshot(PlaylistItem[] snapshot)
    {
        var groups = new List<AlbumGroup>();
        if (snapshot.Length == 0) return groups;

        AlbumGroup? current = null;

        for (int i = 0; i < snapshot.Length; i++)
        {
            var item = snapshot[i];
            if (item == null) continue;
            var t = item.Track;
            var key = t?.AlbumKey ?? string.Empty;

            if (current == null || current.Key != key)
            {
                current = new AlbumGroup
                {
                    Key = key,
                    Album = (t != null && !string.IsNullOrEmpty(t.Album)) ? t.Album : "(앨범 없음)",
                    Artist = (t != null && !string.IsNullOrEmpty(t.SortArtist)) ? t.SortArtist : "(아티스트 없음)",
                    Year = t?.Year ?? 0,
                    ArtPath = t?.ArtPath
                };
                groups.Add(current);
            }
            current.AddItem(item);
        }

        return groups;
    }
}
