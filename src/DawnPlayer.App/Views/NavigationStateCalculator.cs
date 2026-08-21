namespace DawnPlayer.App.Views;

/// <summary>
/// Which shell surfaces are shown for one navigation target: the two tab toggles, the three
/// content hosts, and which content page owns the lyrics pane.
/// </summary>
public readonly record struct NavigationViewState(
    bool TabLibraryChecked,
    bool TabPlaylistsChecked,
    bool LibraryVisible,
    bool PlaylistsVisible,
    bool SettingsVisible,
    bool LibraryLyricsVisible,
    bool PlaylistLyricsVisible);

/// <summary>
/// The shell's navigation rules, as a pure function of the target and the lyrics preference.
/// </summary>
/// <remarks>
/// Kept free of WinUI types so it can be linked into the test project (MainWindow itself cannot
/// be), and so the rules are stated once instead of being spread across four event handlers that
/// each set six properties.
/// </remarks>
public static class NavigationStateCalculator
{
    public const string LibraryTab = "Library";
    public const string PlaylistsTab = "Playlists";

    /// <summary>Anything that is not the playlists tab is the library tab.</summary>
    public static string NormalizeTab(string? tab) =>
        string.Equals(tab, PlaylistsTab, System.StringComparison.OrdinalIgnoreCase) ? PlaylistsTab : LibraryTab;

    /// <summary>State for a content tab. Only the visible page shows a lyrics pane.</summary>
    public static NavigationViewState ForTab(string? tabName, bool showLyricsPane)
    {
        bool playlists = NormalizeTab(tabName) == PlaylistsTab;

        return new NavigationViewState(
            TabLibraryChecked: !playlists,
            TabPlaylistsChecked: playlists,
            LibraryVisible: !playlists,
            PlaylistsVisible: playlists,
            SettingsVisible: false,
            LibraryLyricsVisible: !playlists && showLyricsPane,
            PlaylistLyricsVisible: playlists && showLyricsPane);
    }

    /// <summary>
    /// State for the settings page: neither tab is checked, and no lyrics pane exists there — the
    /// preference is retained and re-applied when a content page comes back.
    /// </summary>
    public static NavigationViewState ForSettings() =>
        new(TabLibraryChecked: false,
            TabPlaylistsChecked: false,
            LibraryVisible: false,
            PlaylistsVisible: false,
            SettingsVisible: true,
            LibraryLyricsVisible: false,
            PlaylistLyricsVisible: false);

    /// <summary>
    /// Applies a lyrics-pane toggle to an existing state. Which page shows the pane follows
    /// whichever content page is currently visible.
    /// </summary>
    public static NavigationViewState ForLyricsToggle(NavigationViewState current, bool showLyricsPane) =>
        current with
        {
            LibraryLyricsVisible = current.LibraryVisible && showLyricsPane,
            PlaylistLyricsVisible = current.PlaylistsVisible && showLyricsPane
        };
}
