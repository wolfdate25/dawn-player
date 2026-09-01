using DawnPlayer.App.Services;
using DawnPlayer.App.ViewModels.Settings;
using DawnPlayer.Core.Lyrics.Online;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Plugins;
using Xunit;

namespace DawnPlayer.Tests.ViewModels;

/// <summary>In-memory ILyricsOnlineService double: no plugins folder, no network.</summary>
public sealed class FakeLyricsOnlineService : ILyricsOnlineService
{
    public List<LyricsPluginInfo> PluginInfos { get; } = new();
    public List<string> Errors { get; } = new();
    public int RescanCalls { get; private set; }

    public IReadOnlyList<LyricsPluginInfo> Plugins => PluginInfos;
    public IReadOnlyList<string> LoadErrors => Errors;

    public void ReloadPlugins() => RescanCalls++;

    public OnlineLyricsResult? GetSessionLyrics(string trackPath) => null;

    public Task<IReadOnlyList<PluginSearchOutcome>> SearchAsync(LyricsSearchQuery query, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PluginSearchOutcome>>(Array.Empty<PluginSearchOutcome>());

    public Task<OnlineLyricsResult?> FetchAsync(LyricsPluginInfo plugin, LyricsSearchResult result, Track track, CancellationToken cancellationToken)
        => Task.FromResult<OnlineLyricsResult?>(null);

    public void ApplyResult(OnlineLyricsResult result, Track track) { }

    public LyricsSaveOutcome SaveResult(OnlineLyricsResult result, Track track)
        => LyricsSaveOutcome.Fail("not in tests");
}

public class LyricsOnlineSettingsViewModelTests
{
    [Fact]
    public void RefreshPlugins_ListsPluginsInSavedPriorityOrder()
    {
        var online = new FakeLyricsOnlineService();
        online.PluginInfos.Add(new LyricsPluginInfo("alpha", "Alpha", "1.0.0", "a", IsExternal: true));
        online.PluginInfos.Add(new LyricsPluginInfo("beta", "Beta", "1.0.0", "b", IsExternal: true));
        var (vm, settings) = CreateWithPlugins(online, s => s.LyricsOnline.PluginOrder = new List<string> { "beta", "alpha" });

        vm.RefreshPlugins();

        Assert.Equal(new[] { "beta", "alpha" }, vm.Plugins.Select(p => p.Id));
    }

    [Fact]
    public void RefreshPlugins_DisabledFlagDefaultsToEnabled()
    {
        var online = new FakeLyricsOnlineService();
        online.PluginInfos.Add(new LyricsPluginInfo("alpha", "Alpha", "1.0.0", "a", false));
        var (vm, settings) = CreateWithPlugins(online, s => s.LyricsOnline.PluginEnabled["alpha"] = false);

        vm.RefreshPlugins();

        Assert.False(vm.Plugins.Single().IsEnabled);
        Assert.True(vm.HasPlugins);
    }

    [Fact]
    public void SetPluginEnabled_PersistsIntoSettings()
    {
        var online = new FakeLyricsOnlineService();
        online.PluginInfos.Add(new LyricsPluginInfo("alpha", "Alpha", "1.0.0", "a", false));
        var (vm, settings) = CreateWithPlugins(online);
        vm.RefreshPlugins();

        vm.Plugins.Single().IsEnabled = false;
        vm.SetPluginEnabled(vm.Plugins.Single());

        Assert.False(settings.LyricsOnline.PluginEnabled["alpha"]);
    }

    [Fact]
    public void MovePluginUp_RewritesOrderAndKeepsUnknownIds()
    {
        var online = new FakeLyricsOnlineService();
        online.PluginInfos.Add(new LyricsPluginInfo("alpha", "Alpha", "1.0.0", "a", false));
        online.PluginInfos.Add(new LyricsPluginInfo("beta", "Beta", "1.0.0", "b", false));
        var (vm, settings) = CreateWithPlugins(online, s =>
            s.LyricsOnline.PluginOrder = new List<string> { "uninstalled", "alpha", "beta" });
        vm.RefreshPlugins();

        vm.MovePluginUp(vm.Plugins[1]);   // beta above alpha

        Assert.Equal(new[] { "beta", "alpha" }, vm.Plugins.Select(p => p.Id));
        // Uninstalled plugin ids survive so reinstalling restores the order.
        Assert.Equal(new List<string> { "beta", "alpha", "uninstalled" }, settings.LyricsOnline.PluginOrder);
    }

    [Fact]
    public void MovePluginDown_AtBottom_IsNoOp()
    {
        var online = new FakeLyricsOnlineService();
        online.PluginInfos.Add(new LyricsPluginInfo("alpha", "Alpha", "1.0.0", "a", false));
        online.PluginInfos.Add(new LyricsPluginInfo("beta", "Beta", "1.0.0", "b", false));
        var (vm, settings) = CreateWithPlugins(online);
        vm.RefreshPlugins();

        vm.MovePluginDown(vm.Plugins[1]);

        Assert.Equal(new[] { "alpha", "beta" }, vm.Plugins.Select(p => p.Id));
        // A no-op move must not write an order (the default PluginOrder stays empty).
        Assert.Empty(settings.LyricsOnline.PluginOrder);
    }

    [Fact]
    public void Rescan_ReloadsFromService()
    {
        var online = new FakeLyricsOnlineService();
        var (vm, settings) = CreateWithPlugins(online);

        vm.Rescan();

        Assert.Equal(1, online.RescanCalls);
    }

    [Fact]
    public void SaveFileNameTemplate_TrimsAndSaves()
    {
        var (vm, settings, saves) = CreateFull();
        vm.SaveFileNameTemplate = "  %title%.lrc  ";

        Assert.Equal("%title%.lrc", settings.LyricsOnline.SaveFileNameTemplate);
        Assert.Contains(settings, saves);
    }

    [Fact]
    public void SaveLocationIndex_TogglesCustomFolderFlag()
    {
        var (vm, settings, saves) = CreateFull();
        Assert.False(vm.IsCustomFolderSelected);

        vm.SaveLocationIndex = 1;

        Assert.True(vm.IsCustomFolderSelected);
        Assert.Equal(LyricsSaveLocation.CustomFolder, settings.LyricsOnline.SaveLocation);
    }

    [Fact]
    public void SetCustomSaveFolder_UpdatesLabel()
    {
        var (vm, settings, saves) = CreateFull();
        Assert.Contains("지정 안 됨", vm.CustomSaveFolderLabel);

        vm.SetCustomSaveFolder(@"D:\Lyrics");

        Assert.Equal(@"D:\Lyrics", vm.CustomSaveFolderLabel);
        Assert.Equal(@"D:\Lyrics", settings.LyricsOnline.CustomSaveFolder);
    }

    [Fact]
    public void EnableOnline_SavesAndNotifies()
    {
        var settings = AppSettings.CreateDefault();
        var notified = 0;
        var saves = new List<AppSettings>();
        var vm = new LyricsOnlineSettingsViewModel(settings, online: null, () => notified++, s => saves.Add(s));

        vm.EnableOnline = false;

        Assert.False(settings.LyricsOnline.EnableOnline);
        Assert.Equal(1, notified);
        Assert.Contains(settings, saves);
    }

    private static (LyricsOnlineSettingsViewModel vm, AppSettings settings) CreateWithPlugins(
        FakeLyricsOnlineService online, Action<AppSettings>? mutate = null)
    {
        var settings = AppSettings.CreateDefault();
        mutate?.Invoke(settings);
        var vm = new LyricsOnlineSettingsViewModel(settings, online, null, _ => { });
        return (vm, settings);
    }

    private static (LyricsOnlineSettingsViewModel vm, AppSettings settings, List<AppSettings> saves) CreateFull()
    {
        var settings = AppSettings.CreateDefault();
        var saves = new List<AppSettings>();
        var vm = new LyricsOnlineSettingsViewModel(settings, online: null, null, s => saves.Add(s));
        return (vm, settings, saves);
    }
}
