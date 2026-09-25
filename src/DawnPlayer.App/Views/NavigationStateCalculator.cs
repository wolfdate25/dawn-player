namespace DawnPlayer.App.Views;

/// <summary>
/// Which shell surfaces are shown for one navigation target: the three tab toggles, the content
/// hosts, and which content page owns the lyrics pane.
/// </summary>
public readonly record struct NavigationViewState(
    bool TabLibraryChecked,
    bool TabPlaylistsChecked,
    bool TabNetworkChecked,
    bool LibraryVisible,
    bool PlaylistsVisible,
    bool NetworkVisible,
    bool SettingsVisible,
    bool LibraryLyricsVisible,
    bool PlaylistLyricsVisible);

/// <summary>
/// The shell's navigation rules, as a pure function of the target and the lyrics preference.
/// </summary>
/// <remarks>
/// Kept free of WinUI types so it can be linked into the test project (MainWindow itself cannot
/// be), and so the rules are stated once instead of being spread across event handlers that
/// each set a handful of properties.
/// </remarks>
public static class NavigationStateCalculator
{
    public const string LibraryTab = "Library";
    public const string PlaylistsTab = "Playlists";
    public const string NetworkTab = "Network";

    /// <summary>Anything that is neither playlists nor network is the library tab.</summary>
    public static string NormalizeTab(string? tab) =>
        string.Equals(tab, PlaylistsTab, System.StringComparison.OrdinalIgnoreCase) ? PlaylistsTab
        : string.Equals(tab, NetworkTab, System.StringComparison.OrdinalIgnoreCase) ? NetworkTab
        : LibraryTab;

    /// <summary>State for a content tab. Only library and playlists own a lyrics pane.</summary>
    public static NavigationViewState ForTab(string? tabName, bool showLyricsPane)
    {
        string tab = NormalizeTab(tabName);
        bool library = tab == LibraryTab;
        bool playlists = tab == PlaylistsTab;

        return new NavigationViewState(
            TabLibraryChecked: library,
            TabPlaylistsChecked: playlists,
            TabNetworkChecked: !library && !playlists,
            LibraryVisible: library,
            PlaylistsVisible: playlists,
            NetworkVisible: !library && !playlists,
            SettingsVisible: false,
            LibraryLyricsVisible: library && showLyricsPane,
            PlaylistLyricsVisible: playlists && showLyricsPane);
    }

    /// <summary>
    /// State for the settings page: no tab is checked, and no lyrics pane exists there — the
    /// preference is retained and re-applied when a content page comes back.
    /// </summary>
    public static NavigationViewState ForSettings() =>
        new(TabLibraryChecked: false,
            TabPlaylistsChecked: false,
            TabNetworkChecked: false,
            LibraryVisible: false,
            PlaylistsVisible: false,
            NetworkVisible: false,
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
