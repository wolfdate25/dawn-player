namespace DawnPlayer.App.Controls;

/// <summary>
/// Combines the ICY station name and the live StreamTitle into the secondary now-playing line.
/// WinUI-free so it links into the test project.
/// </summary>
public static class RadioSubtitleFormatter
{
    /// <summary>Either part alone is shown as-is; both empty yields empty (caller keeps whatever
    /// it had). The separator matches the shell's "Artist — Title" punctuation family.</summary>
    public static string Format(string? stationName, string? streamTitle)
    {
        var station = (stationName ?? "").Trim();
        var title = (streamTitle ?? "").Trim();
        if (station.Length == 0) return title;
        if (title.Length == 0) return station;
        return station + " · " + title;
    }
}
