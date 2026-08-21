using DawnPlayer.App.Views;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.Layout;

/// <summary>
/// Shell navigation rules, exercised against the real <see cref="NavigationStateCalculator"/> the
/// window uses. A hand-written copy of these rules lived here before, which meant the tests could
/// stay green while MainWindow's own logic drifted.
/// </summary>
[Collection("SettingsStoreCollection")]
public sealed class LayoutAndNavigationTransitionTests
{
    [Theory]
    [InlineData("Playlists", "Playlists")]
    [InlineData("playlists", "Playlists")]
    [InlineData("Library", "Library")]
    [InlineData("library", "Library")]
    [InlineData("Nonsense", "Library")]
    [InlineData("", "Library")]
    [InlineData(null, "Library")]
    public void NormalizeTab_AnythingButPlaylists_IsLibrary(string? input, string expected)
    {
        Assert.Equal(expected, NavigationStateCalculator.NormalizeTab(input));
    }

    [Fact]
    public void LibraryTab_ShowsOnlyTheLibraryPageAndItsLyricsPane()
    {
        var state = NavigationStateCalculator.ForTab("Library", showLyricsPane: true);

        Assert.True(state.TabLibraryChecked);
        Assert.False(state.TabPlaylistsChecked);
        Assert.True(state.LibraryVisible);
        Assert.False(state.PlaylistsVisible);
        Assert.False(state.SettingsVisible);
        Assert.True(state.LibraryLyricsVisible);
        Assert.False(state.PlaylistLyricsVisible);
    }

    [Fact]
    public void PlaylistsTab_ShowsOnlyThePlaylistPageAndItsLyricsPane()
    {
        var state = NavigationStateCalculator.ForTab("Playlists", showLyricsPane: true);

        Assert.False(state.TabLibraryChecked);
        Assert.True(state.TabPlaylistsChecked);
        Assert.False(state.LibraryVisible);
        Assert.True(state.PlaylistsVisible);
        Assert.False(state.SettingsVisible);
        Assert.False(state.LibraryLyricsVisible);
        Assert.True(state.PlaylistLyricsVisible);
    }

    [Fact]
    public void LyricsPreferenceOff_LeavesBothPanesHidden()
    {
        var library = NavigationStateCalculator.ForTab("Library", showLyricsPane: false);
        var playlists = NavigationStateCalculator.ForTab("Playlists", showLyricsPane: false);

        Assert.False(library.LibraryLyricsVisible);
        Assert.False(library.PlaylistLyricsVisible);
        Assert.False(playlists.LibraryLyricsVisible);
        Assert.False(playlists.PlaylistLyricsVisible);
    }

    [Fact]
    public void SettingsPage_ClearsBothTabsAndShowsNoLyricsPane()
    {
        var state = NavigationStateCalculator.ForSettings();

        Assert.False(state.TabLibraryChecked);
        Assert.False(state.TabPlaylistsChecked);
        Assert.False(state.LibraryVisible);
        Assert.False(state.PlaylistsVisible);
        Assert.True(state.SettingsVisible);
        Assert.False(state.LibraryLyricsVisible);
        Assert.False(state.PlaylistLyricsVisible);
    }

    [Fact]
    public void LyricsToggle_AppliesToWhicheverContentPageIsVisible()
    {
        var onLibrary = NavigationStateCalculator.ForLyricsToggle(
            NavigationStateCalculator.ForTab("Library", showLyricsPane: false), showLyricsPane: true);
        Assert.True(onLibrary.LibraryLyricsVisible);
        Assert.False(onLibrary.PlaylistLyricsVisible);

        var onPlaylists = NavigationStateCalculator.ForLyricsToggle(
            NavigationStateCalculator.ForTab("Playlists", showLyricsPane: false), showLyricsPane: true);
        Assert.False(onPlaylists.LibraryLyricsVisible);
        Assert.True(onPlaylists.PlaylistLyricsVisible);
    }

    [Fact]
    public void LyricsToggle_OnSettingsPage_HasNoPaneToShow()
    {
        // The preference is still recorded by the caller; there is simply nothing to reveal here.
        var state = NavigationStateCalculator.ForLyricsToggle(
            NavigationStateCalculator.ForSettings(), showLyricsPane: true);

        Assert.False(state.LibraryLyricsVisible);
        Assert.False(state.PlaylistLyricsVisible);
        Assert.True(state.SettingsVisible);
    }

    [Fact]
    public void Navigation_LibraryToPlaylistsToSettingsAndBack_PersistsTheLastTab()
    {
        var settings = AppSettings.CreateDefault();
        settings.Ui.LastNavTab = "Library";
        settings.Ui.ShowLyricsPane = true;

        // Mirrors what MainWindow.NavigateToTab records alongside applying the state.
        void Navigate(string tab) => settings.Ui.LastNavTab = NavigationStateCalculator.NormalizeTab(tab);

        var restored = NavigationStateCalculator.ForTab(
            NavigationStateCalculator.NormalizeTab(settings.Ui.LastNavTab), settings.Ui.ShowLyricsPane);
        Assert.True(restored.LibraryVisible);
        Assert.Equal("Library", settings.Ui.LastNavTab);

        Navigate("Playlists");
        var playlists = NavigationStateCalculator.ForTab(settings.Ui.LastNavTab, settings.Ui.ShowLyricsPane);
        Assert.True(playlists.PlaylistsVisible);
        Assert.True(playlists.PlaylistLyricsVisible);
        Assert.Equal("Playlists", settings.Ui.LastNavTab);

        var onSettings = NavigationStateCalculator.ForSettings();
        Assert.True(onSettings.SettingsVisible);
        // Visiting settings must not lose which content tab to come back to.
        Assert.Equal("Playlists", settings.Ui.LastNavTab);

        Navigate("Library");
        var back = NavigationStateCalculator.ForTab(settings.Ui.LastNavTab, settings.Ui.ShowLyricsPane);
        Assert.True(back.LibraryVisible);
        Assert.True(back.LibraryLyricsVisible);
        Assert.Equal("Library", settings.Ui.LastNavTab);
    }
}
