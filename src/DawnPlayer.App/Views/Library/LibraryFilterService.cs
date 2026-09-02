using DawnPlayer.App.Localization;
using DawnPlayer.Core.Models;

namespace DawnPlayer.App.Views;

/// <summary>
/// Column identifiers for track sorting in the library table view.
/// </summary>
public enum SortColumn
{
    None = 0,
    TrackNo = 1,
    Title = 2,
    Artist = 3,
    Album = 4,
    Duration = 5
}

/// <summary>
/// WinUI-free grouping result for one album card. <see cref="LibraryFilterService.BuildAlbumCards"/>
/// maps this onto the bindable <c>AlbumCard</c>, which cannot leave the App project because it
/// holds a BitmapImage.
/// </summary>
public sealed record AlbumCardModel(
    string Key,
    string Album,
    string Artist,
    int Year,
    string? ArtPath,
    IReadOnlyList<Track> Tracks);

/// <summary>
/// Service providing track filtering, search keyword querying, 5-column sorting, and album card collection construction.
/// </summary>
public static class LibraryFilterService
{
    /// <summary>
    /// Applies active tree node filter, search keyword, and column sorting to a list of tracks.
    /// </summary>
    public static List<Track> FilterAndSort(
        IReadOnlyList<Track> allTracks,
        LibraryTreeNode? selectedNode,
        string search,
        SortColumn sortColumn,
        bool sortAscending)
    {
        IEnumerable<Track> query = allTracks;

        if (selectedNode != null && selectedNode.FilterType != "All")
        {
            query = selectedNode.FilterType switch
            {
                "Artist" => query.Where(t => t.SortArtist == selectedNode.FilterValue),
                "Album" => query.Where(t => t.AlbumKey == selectedNode.FilterValue),
                "ArtistAlbum" => query.Where(t => t.SortArtist == selectedNode.FilterExtra && t.Album == selectedNode.FilterValue),
                "Genre" => query.Where(t => t.Genre == selectedNode.FilterValue),
                "GenreArtist" => query.Where(t => t.Genre == selectedNode.FilterExtra && t.SortArtist == selectedNode.FilterValue),
                "GenreArtistAlbum" => query.Where(t => t.Genre == selectedNode.FilterExtra2 && t.SortArtist == selectedNode.FilterExtra && t.Album == selectedNode.FilterValue),
                "Folder" => !string.IsNullOrEmpty(selectedNode.FilterValue)
                    ? query.Where(t => IsPathInsideFolder(t.Path, selectedNode.FilterValue))
                    : query,
                _ => query
            };
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            string s = search.Trim();
            query = query.Where(t =>
                t.Title.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                t.Album.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        query = sortColumn switch
        {
            SortColumn.TrackNo => sortAscending ? query.OrderBy(t => t.TrackNo) : query.OrderByDescending(t => t.TrackNo),
            SortColumn.Title => sortAscending ? query.OrderBy(t => t.Title) : query.OrderByDescending(t => t.Title),
            SortColumn.Artist => sortAscending ? query.OrderBy(t => t.SortArtist) : query.OrderByDescending(t => t.SortArtist),
            SortColumn.Album => sortAscending ? query.OrderBy(t => t.Album).ThenBy(t => t.TrackNo) : query.OrderByDescending(t => t.Album).ThenByDescending(t => t.TrackNo),
            SortColumn.Duration => sortAscending ? query.OrderBy(t => t.DurationMs) : query.OrderByDescending(t => t.DurationMs),
            _ => query
        };

        return query.ToList();
    }

    /// <summary>
    /// Groups visible tracks by album key, applying display fallbacks for missing album/artist tags.
    /// </summary>
    public static List<AlbumCardModel> BuildAlbumCardModels(IReadOnlyList<Track> visibleTracks)
    {
        var result = new List<AlbumCardModel>();

        foreach (var g in visibleTracks.GroupBy(t => t.AlbumKey))
        {
            var first = g.First();
            result.Add(new AlbumCardModel(
                g.Key,
                string.IsNullOrEmpty(first.Album) ? AppStrings.Get("Library_NoAlbum", "(앨범 없음)") : first.Album,
                string.IsNullOrEmpty(first.Artist) ? AppStrings.Get("Library_NoArtist", "(아티스트 없음)") : first.Artist,
                first.Year,
                first.ArtPath,
                g.ToList()));
        }

        return result;
    }

#if !TEST_PROJECT
    /// <summary>
    /// Groups visible tracks by album key and constructs a list of AlbumCard models.
    /// </summary>
    public static List<AlbumCard> BuildAlbumCards(IReadOnlyList<Track> visibleTracks, double cardWidth)
    {
        var result = new List<AlbumCard>();

        foreach (var m in BuildAlbumCardModels(visibleTracks))
        {
            var card = new AlbumCard
            {
                Key = m.Key,
                Album = m.Album,
                Artist = m.Artist,
                Year = m.Year,
                ArtPath = m.ArtPath,
                CardWidth = cardWidth > 0 ? cardWidth : 144
            };
            card.Tracks.AddRange(m.Tracks);
            result.Add(card);
        }

        return result;
    }
#endif

    /// <summary>
    /// Checks whether a track's file path resides within or is equal to the given folder path.
    /// </summary>
    public static bool IsPathInsideFolder(string? filePath, string? folderPath)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(folderPath)) return false;

        string normFolder = folderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        string? trackDir = System.IO.Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(trackDir)) return false;

        string normTrackDir = trackDir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (string.Equals(normTrackDir, normFolder, StringComparison.OrdinalIgnoreCase)) return true;

        string prefix = normFolder + System.IO.Path.DirectorySeparatorChar;
        return filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
