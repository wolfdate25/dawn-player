using System.Globalization;
using System.Text;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Lyrics;

/// <summary>
/// Expands track tokens (%filename% etc.) in lyric file name templates. Shared by the offline
/// candidate builder and the online save-path resolver so both accept the same vocabulary.
/// </summary>
public static class LyricsTokenExpander
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Expands %filename% %title% %artist% %albumartist% %album% %year% %trackno%.
    /// Values are sanitized (invalid file name chars → '_'); %filename% is the audio file's
    /// base name and is passed through as-is.
    /// </summary>
    public static string Expand(string template, Track track)
    {
        var baseName = Path.GetFileNameWithoutExtension(track.Path);
        var albumArtist = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist;

        return template
            .Replace("%filename%", baseName, StringComparison.OrdinalIgnoreCase)
            .Replace("%title%", Sanitize(track.Title), StringComparison.OrdinalIgnoreCase)
            .Replace("%artist%", Sanitize(track.Artist), StringComparison.OrdinalIgnoreCase)
            .Replace("%albumartist%", Sanitize(albumArtist), StringComparison.OrdinalIgnoreCase)
            .Replace("%album%", Sanitize(track.Album), StringComparison.OrdinalIgnoreCase)
            .Replace("%year%", track.Year > 0 ? track.Year.ToString(CultureInfo.InvariantCulture) : "", StringComparison.OrdinalIgnoreCase)
            .Replace("%trackno%", track.TrackNo > 0 ? track.TrackNo.ToString(CultureInfo.InvariantCulture) : "", StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim())
            sb.Append(InvalidChars.Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
