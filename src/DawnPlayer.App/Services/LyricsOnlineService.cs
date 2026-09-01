using DawnPlayer.Core.Lyrics;
using DawnPlayer.Core.Lyrics.Online;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Plugins;

namespace DawnPlayer.App.Services;

/// <summary>
/// App-facing online lyrics pipeline. Owns the plugin host and the core orchestration service,
/// and drives the automatic flow: when the current track changes and has no offline lyrics,
/// fetch by plugin priority in the background, optionally auto-save, then refresh the panes via
/// <see cref="AppServices.RaiseLyricsChanged"/>. Panes read the fetched document from the
/// session cache during that refresh.
/// </summary>
public sealed class LyricsOnlineService : ILyricsOnlineService
{
    private readonly LyricsPluginHost _host;
    private readonly OnlineLyricsService _core;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string> _log;

    private int _fetchGeneration;
    private CancellationTokenSource? _fetchCts;

    public LyricsOnlineService(Func<AppSettings> settings, Action<string> log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _host = new LyricsPluginHost(settings, log);
        _core = new OnlineLyricsService(_host, settings, log);
    }

    /// <summary>Called from AppServices.Initialize after settings exist: loads plugins and starts auto-lookup.</summary>
    public void Initialize()
    {
        _host.Reload();
        AppServices.CurrentTrackChanged += OnCurrentTrackChanged;
    }

    public IReadOnlyList<LyricsPluginInfo> Plugins => _host.Plugins.Select(p => p.Info).ToList();

    public IReadOnlyList<string> LoadErrors => _host.LoadErrors;

    public void ReloadPlugins()
    {
        _host.Reload();
        foreach (var error in _host.LoadErrors)
            _log($"[lyrics-online] {error}");
    }

    public OnlineLyricsResult? GetSessionLyrics(string trackPath) => _core.GetSessionLyrics(trackPath);

    public Task<IReadOnlyList<PluginSearchOutcome>> SearchAsync(LyricsSearchQuery query, CancellationToken cancellationToken) =>
        _core.SearchAllAsync(query, cancellationToken);

    public async Task<OnlineLyricsResult?> FetchAsync(LyricsPluginInfo plugin, LyricsSearchResult result, Track track, CancellationToken cancellationToken)
    {
        var loaded = _host.Plugins.FirstOrDefault(p => p.Info.Id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase));
        if (loaded is null) return null;

        return await _core.FetchAsync(loaded, result, track.Path, cancellationToken).ConfigureAwait(false);
    }

    public void ApplyResult(OnlineLyricsResult result, Track track) =>
        AppServices.RaiseLyricsChanged(track);

    public LyricsSaveOutcome SaveResult(OnlineLyricsResult result, Track track)
    {
        var outcome = LyricsSavePathResolver.Save(track, result.Document, _settings().LyricsOnline);
        if (outcome.Result == LyricsSaveResult.Saved)
            // The file now exists offline; make panes reload from disk.
            AppServices.RaiseLyricsChanged(track);
        return outcome;
    }

    private void OnCurrentTrackChanged(PlaylistItem? item)
    {
        // A newer track invalidates any in-flight fetch for the previous one.
        var generation = ++_fetchGeneration;
        var previous = Interlocked.Exchange(ref _fetchCts, null);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }

        var track = item?.Track;
        var settings = _settings();
        if (track is null || !settings.LyricsOnline.EnableOnline || !settings.LyricsOnline.AutoFetchOnPlay)
            return;
        if (_core.GetEnabledOrderedPlugins().Count == 0)
            return;

        var cts = new CancellationTokenSource();
        _fetchCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAutoFetchAsync(track, settings, generation, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log($"[lyrics-online] 자동 검색 실패: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_fetchCts, cts))
                    Interlocked.CompareExchange(ref _fetchCts, null, cts);
                cts.Dispose();
            }
        });
    }

    private async Task RunAutoFetchAsync(Track track, AppSettings settings, int generation, CancellationToken token)
    {
        // Offline wins: embedded tags and .lrc files keep priority over the network.
        var offline = LyricsFinder.LoadLyrics(track, settings);
        if (offline is { HasLines: true })
            return;

        if (Volatile.Read(ref _fetchGeneration) != generation || token.IsCancellationRequested)
            return;

        var result = await _core.FetchBestAsync(track, token).ConfigureAwait(false);
        if (result is null || Volatile.Read(ref _fetchGeneration) != generation)
            return;

        if (settings.LyricsOnline.AutoSave)
        {
            var outcome = LyricsSavePathResolver.Save(track, result.Document, settings.LyricsOnline);
            if (outcome.Result == LyricsSaveResult.Saved)
                _log($"[lyrics-online] 자동 저장: {outcome.Path}");
            else if (outcome.Result == LyricsSaveResult.Failed)
                _log($"[lyrics-online] 자동 저장 실패: {outcome.Error}");
        }

        // When saved, the file exists now and the reload finds it; otherwise the pane falls
        // back to the session cache this fetch just populated.
        AppServices.RaiseLyricsChanged(track);
    }
}
