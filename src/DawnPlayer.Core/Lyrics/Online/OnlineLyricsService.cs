using System.Collections.Concurrent;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Plugins;

namespace DawnPlayer.Core.Lyrics.Online;

/// <summary>Lyrics that came from a plugin, kept for display and optional offline saving.</summary>
public sealed record OnlineLyricsResult(
    LyricsDocument Document,
    string PluginId,
    string PluginName,
    bool IsSynced,
    LyricsSearchResult Match);

/// <summary>One plugin's answer for the search window: results, or the error that replaced them.</summary>
public sealed record PluginSearchOutcome(LyricsPluginInfo Plugin, IReadOnlyList<LyricsSearchResult> Results, string? Error = null);

/// <summary>
/// Coordinates plugins for automatic lookup and manual search: walks them in the user's priority
/// order, scores candidates against the track, downloads the best one, and keeps the result as a
/// per-session cache keyed by track path so panes can redisplay it without refetching.
/// </summary>
public sealed class OnlineLyricsService
{
    private readonly LyricsPluginHost _host;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, OnlineLyricsResult> _sessionLyrics = new(StringComparer.OrdinalIgnoreCase);

    public OnlineLyricsService(LyricsPluginHost host, Func<AppSettings> settings, Action<string>? log = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log;
    }

    /// <summary>Enabled plugins in the user's priority order (unordered ones last, by name).</summary>
    public IReadOnlyList<LoadedLyricsPlugin> GetEnabledOrderedPlugins()
    {
        var order = _settings().LyricsOnline.PluginOrder;

        return _host.Plugins
            .Where(p => !IsDisabled(p.Info.Id))
            .OrderBy(PriorityOf)
            .ThenBy(p => p.Info.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int PriorityOf(LoadedLyricsPlugin plugin)
        {
            var index = order.FindIndex(id => id.Equals(plugin.Info.Id, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }
    }

    private bool IsDisabled(string pluginId) =>
        _settings().LyricsOnline.PluginEnabled.TryGetValue(pluginId, out var enabled) && !enabled;

    /// <summary>
    /// Automatic lookup: tries plugins in priority order, returns the first result that scores
    /// well enough and downloads successfully. Null when nothing matched (or no plugins).
    /// </summary>
    public async Task<OnlineLyricsResult?> FetchBestAsync(Track track, CancellationToken cancellationToken)
    {
        var online = _settings().LyricsOnline;
        var query = new LyricsSearchQuery
        {
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
            DurationMs = track.DurationMs > int.MaxValue ? int.MaxValue : (int)track.DurationMs
        };

        foreach (var plugin in GetEnabledOrderedPlugins())
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<LyricsSearchResult> results;
            try
            {
                results = await plugin.Plugin.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[lyrics-online] {plugin.Info.Id} 검색 실패: {ex.Message}");
                continue;
            }

            var best = PickBest(query, results, online.PreferSynced);
            if (best is null) continue;

            var fetched = await FetchAsync(plugin, best, track.Path, cancellationToken).ConfigureAwait(false);
            if (fetched != null) return fetched;
        }

        return null;
    }

    /// <summary>Searches every enabled plugin in parallel for the search window. Errors become outcomes, never throw.</summary>
    public async Task<IReadOnlyList<PluginSearchOutcome>> SearchAllAsync(LyricsSearchQuery query, CancellationToken cancellationToken)
    {
        var plugins = GetEnabledOrderedPlugins();
        var outcomes = await Task.WhenAll(plugins.Select(async plugin =>
        {
            try
            {
                var results = await plugin.Plugin.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                return new PluginSearchOutcome(plugin.Info, results.ToList());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new PluginSearchOutcome(plugin.Info, Array.Empty<LyricsSearchResult>(), ex.Message);
            }
        })).ConfigureAwait(false);

        return outcomes.ToList();
    }

    /// <summary>Downloads one picked search result, parses it, and stores it as the session lyrics for the track.</summary>
    public async Task<OnlineLyricsResult?> FetchAsync(
        LoadedLyricsPlugin plugin, LyricsSearchResult match, string trackPath, CancellationToken cancellationToken)
    {
        LyricsContent? content;
        try
        {
            content = await plugin.Plugin.GetAsync(match, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[lyrics-online] {plugin.Info.Id} 다운로드 실패: {ex.Message}");
            return null;
        }

        if (content is null || !content.HasContent) return null;

        var source = !string.IsNullOrWhiteSpace(match.SourceUrl) ? match.SourceUrl! : $"online:{plugin.Info.Id}";
        var document = ParseContent(content, source);
        if (document is null) return null;

        var result = new OnlineLyricsResult(document, plugin.Info.Id, plugin.Info.Name,
            IsSynced: !string.IsNullOrWhiteSpace(content.SyncedLrc), Match: match);
        _sessionLyrics[trackPath] = result;
        return result;
    }

    public OnlineLyricsResult? GetSessionLyrics(string trackPath) =>
        _sessionLyrics.TryGetValue(trackPath, out var result) ? result : null;

    public void StoreSessionLyrics(string trackPath, OnlineLyricsResult result) =>
        _sessionLyrics[trackPath] = result;

    /// <summary>
    /// Pure candidate scoring. Title agreement dominates; artist/album/duration refine; a synced
    /// candidate gets a nudge when the user prefers time-synced lyrics. Returns the best
    /// candidate when it clears <see cref="MinimumScore"/>, else null.
    /// </summary>
    public const int MinimumScore = 2;

    public static LyricsSearchResult? PickBest(LyricsSearchQuery query, IEnumerable<LyricsSearchResult> results, bool preferSynced)
    {
        LyricsSearchResult? best = null;
        var bestScore = int.MinValue;

        foreach (var result in results)
        {
            var score = Score(query, result, preferSynced);
            if (score > bestScore)
            {
                bestScore = score;
                best = result;
            }
        }

        return bestScore >= MinimumScore ? best : null;
    }

    /// <summary>
    /// Exact text match +4 / partial containment +2 (either direction: "Ditto" ↔ "Ditto (Inst.)").
    /// Title disagreement is a strong negative; artist disagreement a mild one; duration beyond
    /// 15 s is close to disqualifying — the API found a different release of the song.
    /// </summary>
    public static int Score(LyricsSearchQuery query, LyricsSearchResult result, bool preferSynced)
    {
        var score = 0;

        score += TextScore(query.Title, result.Title, exact: 4, partial: 2, mismatch: -3);
        score += TextScore(query.Artist, result.Artist, exact: 2, partial: 1, mismatch: -2);
        score += TextScore(query.Album, result.Album, exact: 1, partial: 1, mismatch: 0);

        if (query.DurationMs > 0 && result.DurationMs > 0)
        {
            var delta = Math.Abs(query.DurationMs - result.DurationMs);
            if (delta <= 3000) score += 2;
            else if (delta <= 10000) score += 1;
            // Beyond 15 s the site found another release of the song: heavy enough to
            // outweigh even an exact title+artist match (4 + 2).
            else if (delta > 15000) score -= 6;
        }

        if (preferSynced && result.IsSynced) score += 1;

        return score;
    }

    private static int TextScore(string? queryText, string? resultText, int exact, int partial, int mismatch)
    {
        if (string.IsNullOrWhiteSpace(queryText) || string.IsNullOrWhiteSpace(resultText))
            return 0;

        var q = Normalize(queryText);
        var r = Normalize(resultText);
        if (q.Length == 0 || r.Length == 0) return 0;

        if (string.Equals(q, r, StringComparison.Ordinal)) return exact;
        if (r.Contains(q, StringComparison.Ordinal) || q.Contains(r, StringComparison.Ordinal)) return partial;
        return mismatch;
    }

    private static string Normalize(string s) =>
        string.Join(' ', s.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static LyricsDocument? ParseContent(LyricsContent content, string sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(content.SyncedLrc))
        {
            var doc = LrcParser.Parse(content.SyncedLrc!, sourcePath);
            if (doc.HasLines) return doc;
        }
        if (!string.IsNullOrWhiteSpace(content.PlainText))
        {
            var doc = LrcParser.Parse(content.PlainText!, sourcePath);
            if (doc.HasLines) return doc;
        }
        return null;
    }
}
