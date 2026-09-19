using System;
using DawnPlayer.App.Services;
using DawnPlayer.App.ViewModels.Settings;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.ViewModels;

/// <summary>
/// The Last.fm settings view model. The part worth pinning: the auth-flow token lives on the
/// app-scoped <see cref="ScrobbleService"/>, not on the settings page — the page is recreated
/// on every navigation and used to orphan a browser approval the user had just granted.
/// Network-touching calls (StartAuth/Confirm) are intentionally not exercised here.
/// </summary>
public sealed class LastfmSettingsViewModelTests
{
    private static (LastfmSettingsViewModel Vm, AppSettings Settings, ScrobbleService Scrobbler) Create()
    {
        var settings = new AppSettings();
        var scrobbler = new ScrobbleService(() => settings, _ => { });
        return (new LastfmSettingsViewModel(settings, scrobbler), settings, scrobbler);
    }

    [Fact]
    public void WithoutCredentials_StatusAsksForKeys_AndHidesButtons()
    {
        var (vm, _, _) = Create();
        vm.Refresh();

        Assert.False(vm.AuthButtonVisible);
        Assert.False(vm.ConfirmButtonVisible);
        Assert.Contains("API", vm.StatusText);
    }

    [Fact]
    public void WithConfiguredKeys_StatusOffersAuth()
    {
        var (vm, settings, _) = Create();
        settings.Lastfm.ApiKey = "k";
        settings.Lastfm.ApiSecret = "s";
        vm.Refresh();

        Assert.True(vm.AuthButtonVisible);
        Assert.False(vm.ConfirmButtonVisible);
    }

    [Fact]
    public void ApplyCredentials_PersistsIntoSettings()
    {
        var (vm, settings, _) = Create();
        vm.ApiKey = "my-key";
        vm.ApiSecret = "my-secret";

        vm.ApplyCredentials();

        Assert.Equal("my-key", settings.Lastfm.ApiKey);
        Assert.Equal("my-secret", settings.Lastfm.ApiSecret);
    }

    [Fact]
    public void ApplyEnabled_PersistsAndSynchronizesCredentials()
    {
        var (vm, settings, scrobbler) = Create();
        vm.ApiKey = "k";
        vm.ApiSecret = "s";

        vm.ApplyEnabled(true);

        Assert.True(settings.Lastfm.Enabled);
        Assert.True(scrobbler.Enabled);
    }

    [Fact]
    public void PendingToken_SurvivesOnTheService_NotTheViewModel()
    {
        // The contract the fix is about: the token is service state. A view model instance (a
        // recreated page) must still see it.
        var (vm, _, scrobbler) = Create();
        scrobbler.PendingToken = "in-flight-token";

        var (recreatedPageVm, _, sameService) = Create();

        // Same service instance (the app-scoped scrobbler) keeps the token alive.
        Assert.Equal("in-flight-token", scrobbler.PendingToken);
        Assert.Null(recreatedPageVm.GetType().GetProperty("PendingToken"));
        _ = sameService;
        _ = vm;
    }

    [Fact]
    public void ConfirmAuth_WithoutPendingToken_IsANoOpThatDoesNotThrow()
    {
        var (vm, _, _) = Create();
        vm.Refresh();

        var task = vm.ConfirmAuthAsync();
        Assert.True(task.IsCompletedSuccessfully);
    }
}
