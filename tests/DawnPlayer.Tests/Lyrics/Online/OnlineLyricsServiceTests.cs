using DawnPlayer.Core.Lyrics.Online;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Plugins;
using LrclibLyricsPlugin;
using Xunit;

namespace DawnPlayer.Tests.Lyrics.Online;

/// <summary>Fake plugins with fixed behavior, one class per plugin id (attribute is per-type).</summary>
[LyricsPlugin("alpha", "Alpha 가사", "1.2.3", "테스트")]
internal sealed class FakeAlphaPlugin : ILyricsPlugin
{
    public List<LyricsSearchResult> Results { get; set; } = new();
    public Exception? SearchError { get; set; }
    public LyricsContent? Content { get; set; }
    public int SearchCalls { get; private set; }
    public int GetCalls { get; private set; }

    public Task<IReadOnlyList<LyricsSearchResult>> SearchAsync(LyricsSearchQuery query, CancellationToken cancellationToken)
    {
        SearchCalls++;
        if (SearchError != null) throw SearchError;
        return Task.FromResult<IReadOnlyList<LyricsSearchResult>>(Results);
    }

    public Task<LyricsContent?> GetAsync(LyricsSearchResult result, CancellationToken cancellationToken)
    {
        GetCalls++;
        return Task.FromResult(Content);
    }
}

[LyricsPlugin("beta", "Beta 가사", "1.0.0", "테스트")]
internal sealed class FakeBetaPlugin : ILyricsPlugin
{
    public List<LyricsSearchResult> Results { get; set; } = new();
    public Exception? SearchError { get; set; }
    public LyricsContent? Content { get; set; }
    public int SearchCalls { get; private set; }

    public Task<IReadOnlyList<LyricsSearchResult>> SearchAsync(LyricsSearchQuery query, CancellationToken cancellationToken)
    {
        SearchCalls++;
        if (SearchError != null) throw SearchError;
        return Task.FromResult<IReadOnlyList<LyricsSearchResult>>(Results);
    }

    public Task<LyricsContent?> GetAsync(LyricsSearchResult result, CancellationToken cancellationToken) =>
        Task.FromResult(Content);
}

public class OnlineLyricsServiceTests
{
    private static AppSettings SettingsWith(Action<LyricsOnlineSettings>? mutate = null)
    {
        var settings = AppSettings.CreateDefault();
        mutate?.Invoke(settings.LyricsOnline);
        return settings;
    }

    private static Track Track(string title = "Song", string artist = "Artist", string album = "Album", int durationMs = 200_000) => new()
    {
        Path = @"C:\Music\song.flac",
        Title = title,
        Artist = artist,
        Album = album,
        DurationMs = durationMs
    };

    private static LyricsSearchResult Result(string title, string artist, int durationMs = 200_000, bool synced = true) => new()
    {
        ResultId = "r1",
        Title = title,
        Artist = artist,
        DurationMs = durationMs,
        IsSynced = synced
    };

    // ---------------- pure scoring ----------------

    [Fact]
    public void PickBest_ExactTitleAndArtistWithDuration_Wins()
    {
        var query = new LyricsSearchQuery { Title = "Song", Artist = "Artist", DurationMs = 200_000 };
        var results = new[]
        {
            Result("Song (Inst.)", "Artist", 200_100),
            Result("Song", "Artist", 200_000),
            Result("Another", "Other", 200_000)
        };

        var best = OnlineLyricsService.PickBest(query, results, preferSynced: true);
        Assert.NotNull(best);
        Assert.Equal("Song", best!.Title);
    }

    [Fact]
    public void PickBest_DifferentSongDurationPenalty_Rejects()
    {
        var query = new LyricsSearchQuery { Title = "Song", Artist = "Artist", DurationMs = 200_000 };
        var results = new[] { Result("Song", "Artist", 260_000) };   // +60s: another release

        Assert.Null(OnlineLyricsService.PickBest(query, results, preferSynced: false));
    }

    [Fact]
    public void PickBest_PartialTitleOnly_IsAccepted()
    {
        var query = new LyricsSearchQuery { Title = "Ditto" };
        var results = new[] { Result("Ditto (NewJeans)", "") };

        var best = OnlineLyricsService.PickBest(query, results, preferSynced: false);
        Assert.NotNull(best);
    }

    [Fact]
    public void PickBest_EmptyResults_ReturnsNull()
    {
        Assert.Null(OnlineLyricsService.PickBest(new LyricsSearchQuery { Title = "x" }, Array.Empty<LyricsSearchResult>(), false));
    }

    [Fact]
    public void Score_PreferSynced_AddsBonus()
    {
        var query = new LyricsSearchQuery { Title = "Song", Artist = "Artist" };
        var synced = Result("Song", "Artist", 0, synced: true);
        var plain = Result("Song", "Artist", 0, synced: false);

        int withSynced = OnlineLyricsService.Score(query, synced, preferSynced: true);
        int withoutSynced = OnlineLyricsService.Score(query, plain, preferSynced: true);

        Assert.Equal(1, withSynced - withoutSynced);
    }

    // ---------------- ordering & enable ----------------

    [Fact]
    public void GetEnabledOrderedPlugins_RespectsOrderAndDisabledFlags()
    {
        var settings = SettingsWith(s =>
        {
            s.PluginOrder = new List<string> { "beta", "alpha" };
            s.PluginEnabled["alpha"] = true;
            s.PluginEnabled["beta"] = false;
        });
        var host = new LyricsPluginHost(() => settings);
        host.RegisterInstance(new FakeAlphaPlugin());
        host.RegisterInstance(new FakeBetaPlugin());
        var service = new OnlineLyricsService(host, () => settings);

        var ordered = service.GetEnabledOrderedPlugins();

        Assert.Equal(new[] { "alpha" }, ordered.Select(p => p.Info.Id));
    }

    [Fact]
    public void GetEnabledOrderedPlugins_UnlistedPluginsComeAfterListed()
    {
        var settings = SettingsWith(s => s.PluginOrder = new List<string> { "beta" });
        var host = new LyricsPluginHost(() => settings);
        host.RegisterInstance(new FakeAlphaPlugin());
        host.RegisterInstance(new FakeBetaPlugin());
        var service = new OnlineLyricsService(host, () => settings);

        var ordered = service.GetEnabledOrderedPlugins();

        Assert.Equal(new[] { "beta", "alpha" }, ordered.Select(p => p.Info.Id));
    }

    // ---------------- fetch pipeline ----------------

    [Fact]
    public async Task FetchBestAsync_PluginError_FallsThroughToNextPlugin()
    {
        var settings = SettingsWith(s => s.PluginOrder = new List<string> { "beta", "alpha" });
        var host = new LyricsPluginHost(() => settings);
        var beta = new FakeBetaPlugin { SearchError = new InvalidOperationException("boom") };
        var alpha = new FakeAlphaPlugin
        {
            Results = { Result("Song", "Artist") },
            Content = new LyricsContent { SyncedLrc = "[00:01.00]hello" }
        };
        host.RegisterInstance(alpha);
        host.RegisterInstance(beta);
        var service = new OnlineLyricsService(host, () => settings);

        var result = await service.FetchBestAsync(Track(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("alpha", result!.PluginId);
        Assert.Equal(1, beta.SearchCalls);
        Assert.Equal(1, alpha.GetCalls);
        Assert.True(result.IsSynced);
    }

    [Fact]
    public async Task FetchBestAsync_PlainOnlyContent_IsNotMarkedSynced()
    {
        var settings = SettingsWith();
        var host = new LyricsPluginHost(() => settings);
        host.RegisterInstance(new FakeAlphaPlugin
        {
            Results = { Result("Song", "Artist") },
            Content = new LyricsContent { PlainText = "first line\nsecond line" }
        });
        var service = new OnlineLyricsService(host, () => settings);

        var result = await service.FetchBestAsync(Track(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.IsSynced);
        Assert.True(result.Document.HasLines);
    }

    [Fact]
    public async Task FetchBestAsync_NoCandidateAboveThreshold_ReturnsNullAndSkipsDownload()
    {
        var settings = SettingsWith();
        var host = new LyricsPluginHost(() => settings);
        var alpha = new FakeAlphaPlugin
        {
            Results = { Result("Completely Different", "Someone Else", 300_000) },
            Content = new LyricsContent { SyncedLrc = "[00:01.00]x" }
        };
        host.RegisterInstance(alpha);
        var service = new OnlineLyricsService(host, () => settings);

        var result = await service.FetchBestAsync(Track(), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, alpha.GetCalls);
    }

    [Fact]
    public async Task FetchBestAsync_StoresSessionCacheByTrackPath()
    {
        var settings = SettingsWith();
        var host = new LyricsPluginHost(() => settings);
        host.RegisterInstance(new FakeAlphaPlugin
        {
            Results = { Result("Song", "Artist") },
            Content = new LyricsContent { SyncedLrc = "[00:01.00]hello" }
        });
        var service = new OnlineLyricsService(host, () => settings);

        await service.FetchBestAsync(Track(), CancellationToken.None);

        var cached = service.GetSessionLyrics(@"C:\Music\song.flac");
        Assert.NotNull(cached);
        Assert.Equal("alpha", cached!.PluginId);
        Assert.Null(service.GetSessionLyrics(@"C:\Music\other.flac"));
    }

    [Fact]
    public async Task SearchAllAsync_IsolatesPluginErrors()
    {
        var settings = SettingsWith();
        var host = new LyricsPluginHost(() => settings);
        host.RegisterInstance(new FakeAlphaPlugin { Results = { Result("Song", "Artist") } });
        host.RegisterInstance(new FakeBetaPlugin { SearchError = new InvalidOperationException("network down") });
        var service = new OnlineLyricsService(host, () => settings);

        var outcomes = await service.SearchAllAsync(new LyricsSearchQuery { Title = "Song" }, CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes.Where(o => o.Error == null), o => Assert.Single(o.Results));
        Assert.Contains(outcomes, o => o.Error == "network down");
    }

    // ---------------- settings persistence ----------------

    // ---------------- settings persistence ----------------

    private static readonly System.Text.Json.JsonSerializerOptions RoundTripOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void LyricsOnlineSettings_RoundTripsThroughSettingsJson()
    {
        var settings = SettingsWith(s =>
        {
            s.EnableOnline = false;
            s.PluginOrder = new List<string> { "beta", "alpha" };
            s.PluginEnabled["beta"] = false;
            s.PluginOptions["alpha"] = new Dictionary<string, string> { ["ApiKey"] = "secret" };
            s.AutoSave = true;
            s.SaveLocation = LyricsSaveLocation.CustomFolder;
            s.CustomSaveFolder = @"D:\Lyrics";
            s.SaveFileNameTemplate = @"%album%\%trackno%. %title%.lrc";
            s.OverwriteExisting = true;
        });

        var json = SettingsStore.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, RoundTripOptions)!;

        var online = restored.LyricsOnline;
        Assert.False(online.EnableOnline);
        Assert.Equal(new List<string> { "beta", "alpha" }, online.PluginOrder);
        Assert.False(online.PluginEnabled["beta"]);
        Assert.Equal("secret", online.PluginOptions["alpha"]["ApiKey"]);
        Assert.True(online.AutoSave);
        Assert.Equal(LyricsSaveLocation.CustomFolder, online.SaveLocation);
        Assert.Equal(@"D:\Lyrics", online.CustomSaveFolder);
        Assert.Equal(@"%album%\%trackno%. %title%.lrc", online.SaveFileNameTemplate);
        Assert.True(online.OverwriteExisting);
    }

    // ---------------- plugin host ----------------

    [Fact]
    public void PluginHost_LoadFromAssembly_FindsSamplePluginWithMetadata()
    {
        var host = new LyricsPluginHost(() => AppSettings.CreateDefault());

        int found = host.LoadFromAssembly(typeof(LrclibPlugin).Assembly);

        Assert.Equal(1, found);
        var plugin = Assert.Single(host.Plugins);
        Assert.Equal("lrclib", plugin.Info.Id);
        Assert.Equal("LRCLIB", plugin.Info.Name);
        Assert.Equal("1.0.0", plugin.Info.Version);
        Assert.False(plugin.Info.IsExternal);
    }

    [Fact]
    public void PluginHost_DuplicateId_IsRejectedAndReported()
    {
        var host = new LyricsPluginHost(() => AppSettings.CreateDefault());
        host.RegisterInstance(new FakeAlphaPlugin());
        host.RegisterInstance(new FakeAlphaPlugin());

        Assert.Single(host.Plugins);
        Assert.Contains(host.LoadErrors, e => e.Contains("alpha"));
    }

    [Fact]
    public void PluginHost_LoadFromAssembly_InjectsContextIntoPluginConstructor()
    {
        var settings = SettingsWith(s => s.PluginOptions["ctxprobe"] = new Dictionary<string, string> { ["key"] = "value" });
        var host = new LyricsPluginHost(() => settings);

        // Scanning the test assembly exercises the instantiation path (constructor selection),
        // unlike RegisterInstance which takes an already-built plugin.
        host.LoadFromAssembly(typeof(ContextProbePlugin).Assembly);
        var probe = host.Plugins.Select(p => p.Plugin).OfType<ContextProbePlugin>().Single();

        Assert.NotNull(probe.Context);
        Assert.Equal("value", probe.Context!.GetSetting("key"));
        Assert.Null(probe.Context.GetSetting("missing"));
        Assert.Contains("plugins-data", probe.Context.DataFolder.Replace('/', Path.DirectorySeparatorChar));
    }

    [LyricsPlugin("ctxprobe", "컨텍스트 프로브", "1.0.0", "테스트")]
    private sealed class ContextProbePlugin : ILyricsPlugin
    {
        public ILyricsPluginContext? Context { get; private set; }

        public ContextProbePlugin(ILyricsPluginContext context) => Context = context;
        public ContextProbePlugin() { }

        public Task<IReadOnlyList<LyricsSearchResult>> SearchAsync(LyricsSearchQuery query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LyricsSearchResult>>(Array.Empty<LyricsSearchResult>());

        public Task<LyricsContent?> GetAsync(LyricsSearchResult result, CancellationToken cancellationToken)
            => Task.FromResult<LyricsContent?>(null);
    }
}
