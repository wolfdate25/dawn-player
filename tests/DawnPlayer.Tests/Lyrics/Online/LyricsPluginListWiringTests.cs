using DawnPlayer.App.Services;
using DawnPlayer.App.ViewModels.Settings;
using DawnPlayer.Core.Lyrics.Online;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Plugins;

namespace DawnPlayer.Tests.Lyrics.Online;

/// <summary>
/// Regression tests for the settings plugin list wiring: the list must reflect the
/// <see cref="ILyricsOnlineService"/> snapshot. (A null service once reached the settings
/// page because the shared instance was never published, leaving the list permanently
/// empty while the host had plugins loaded.)
/// </summary>
public class LyricsPluginListWiringTests
{
    private sealed class FakeOnlineService : ILyricsOnlineService
    {
        public List<LyricsPluginInfo> PluginsList { get; } = new();
        public IReadOnlyList<LyricsPluginInfo> Plugins => PluginsList;
        public IReadOnlyList<string> LoadErrors => Array.Empty<string>();
        public int ReloadCalls { get; private set; }

        public void ReloadPlugins() => ReloadCalls++;
        public Task<IReadOnlyList<PluginSearchOutcome>> SearchAsync(LyricsSearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PluginSearchOutcome>>(Array.Empty<PluginSearchOutcome>());
        public OnlineLyricsResult? GetSessionLyrics(string trackPath) => null;
        public OnlineLyricsResult? GetAppliedResult(string trackPath) => null;
        public void ClearAppliedResult(string trackPath) { }
        public Task<OnlineLyricsResult?> FetchAsync(LyricsPluginInfo plugin, LyricsSearchResult result, Track track, CancellationToken cancellationToken) =>
            Task.FromResult<OnlineLyricsResult?>(null);
        public void ApplyResult(OnlineLyricsResult result, Track track) { }
        public LyricsSaveOutcome SaveResult(OnlineLyricsResult result, Track track) => throw new NotImplementedException();
        public LyricsSaveOutcome SaveResult(OnlineLyricsResult result, Track track, bool overwriteOnce) => throw new NotImplementedException();
    }

    private static LyricsOnlineSettingsViewModel CreateViewModel(FakeOnlineService? service, AppSettings settings) =>
        new LyricsOnlineSettingsViewModel(settings, service, null, _ => { });

    [Fact]
    public void Constructor_WithLoadedPlugin_ListsIt()
    {
        var service = new FakeOnlineService();
        service.PluginsList.Add(new LyricsPluginInfo("alsong", "Alsong", "1.0.0", "Dawn Player Samples", IsExternal: true));

        var vm = CreateViewModel(service, AppSettings.CreateDefault());

        Assert.True(vm.HasPlugins);
        var item = Assert.Single(vm.Plugins);
        Assert.Equal("alsong", item.Id);
        Assert.Equal("Alsong", item.Name);
        Assert.True(item.IsExternal);
    }

    [Fact]
    public void Rescan_ReloadsAndRefreshesList()
    {
        var service = new FakeOnlineService();
        var vm = CreateViewModel(service, AppSettings.CreateDefault());
        Assert.False(vm.HasPlugins);

        service.PluginsList.Add(new LyricsPluginInfo("alsong", "Alsong", "1.0.0", "Dawn Player Samples", IsExternal: true));
        vm.Rescan();

        Assert.Equal(1, service.ReloadCalls);
        Assert.True(vm.HasPlugins);
        Assert.Equal("alsong", Assert.Single(vm.Plugins).Id);
    }

    [Fact]
    public void Constructor_NullService_ShowsEmptyWithoutThrowing()
    {
        var vm = CreateViewModel(null, AppSettings.CreateDefault());

        Assert.False(vm.HasPlugins);
        Assert.Empty(vm.Plugins);
    }
}
