using System.Collections.Generic;
using DawnPlayer.App.ViewModels.Playlist;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests.ViewModels;

/// <summary>
/// M5 view-model extraction gate: the playlist/library page decision logic (sort and rate
/// guards, dead-item sweep messaging, stop-after-current menu mirror, search debounce,
/// zoom/width clamps, persisted grouping) lives in view models that run without a WinUI
/// surface. These tests pin the decisions.
/// </summary>
public sealed class PlaylistAndLibraryViewModelTests
{
    [Fact]
    public void SetGrouped_PersistsAndSignalsRebuild_OnlyOnChange()
    {
        var settings = new AppSettings();
        var vm = new PlaylistViewModel(settings);

        Assert.True(vm.Grouped); // default from settings

        Assert.True(vm.SetGrouped(false), "flip must request a rebuild");
        Assert.False(vm.Grouped);
        Assert.False(settings.Ui.PlaylistGroupedView);

        Assert.False(vm.SetGrouped(false), "same value must not rebuild or rewrite settings");
    }

    [Fact]
    public void CanSort_RequiresMoreThanOneItem()
    {
        var vm = new PlaylistViewModel(new AppSettings());
        var pl = new Playlist("pl");

        Assert.False(vm.CanSort(pl));

        pl.Items.Add(new PlaylistItem(new Track { Path = "a" }));
        pl.Items.Add(new PlaylistItem(new Track { Path = "b" }));
        Assert.True(vm.CanSort(pl));
        Assert.False(vm.CanSort(null));
    }

    [Fact]
    public void CanRate_RequiresSelection()
    {
        var vm = new PlaylistViewModel(new AppSettings());
        Assert.False(vm.CanRate(new List<PlaylistItem>()));
        Assert.True(vm.CanRate(new List<PlaylistItem> { new(new Track { Path = "a" }) }));
    }

    [Fact]
    public void DeadItemSweep_PicksTheRightMessage()
    {
        var vm = new PlaylistViewModel(new AppSettings());

        var removed = vm.DescribeDeadItemSweepOutcome(3);
        Assert.Equal("Msg_RemovedMissingFiles", removed!.Value.Key);
        Assert.Equal(3, removed.Value.Args[0]);

        var none = vm.DescribeDeadItemSweepOutcome(0);
        Assert.Equal("Msg_NoMissingFiles", none!.Value.Key);
    }

    [Fact]
    public void StopAfterCurrentMirror_FollowsTheController()
    {
        var vm = new PlaylistViewModel(new AppSettings());
        Assert.True(vm.SyncStopAfterCurrentMenu(controllerFlag: true, menuChecked: false));
        Assert.True(vm.SyncStopAfterCurrentMenu(controllerFlag: true, menuChecked: true));
        Assert.False(vm.SyncStopAfterCurrentMenu(controllerFlag: false, menuChecked: false));
    }

    [Fact]
    public void SearchEdit_DebouncesAndNormalizes()
    {
        var vm = new LibraryViewModel(new AppSettings());

        Assert.True(vm.OnSearchEdited("  beatles ", nowTickMs: 0));
        Assert.Equal("beatles", vm.SearchText);
        Assert.True(vm.IsSearchPending(""));

        // Within the debounce window: no new trigger.
        Assert.False(vm.OnSearchEdited("beatles ", nowTickMs: 100));

        // Past the window: fires again.
        Assert.True(vm.OnSearchEdited("beatles live", nowTickMs: LibraryViewModel.SearchDebounceMs + 101));
    }

    [Fact]
    public void ZoomAndWidth_Clamp()
    {
        var vm = new LibraryViewModel(new AppSettings());
        Assert.Equal(80, vm.ClampCoverZoom(10));
        Assert.Equal(260, vm.ClampCoverZoom(9999));
        Assert.Equal(150, vm.ClampSidebarWidth(10, min: 150, max: 400));

        // SaveSidebarWidth: below the meaningful-delta threshold writes nothing.
        Assert.False(vm.SaveSidebarWidth(200.4, current: 200, min: 150, max: 400));
        Assert.True(vm.SaveSidebarWidth(320, current: 200, min: 150, max: 400));
    }
}
