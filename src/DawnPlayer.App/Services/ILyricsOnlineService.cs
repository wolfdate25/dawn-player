using DawnPlayer.Core.Lyrics.Online;
using DawnPlayer.Core.Models;
using DawnPlayer.Plugins;

namespace DawnPlayer.App.Services;

/// <summary>
/// Online lyrics: plugin discovery, automatic per-track lookup, manual search and offline
/// saving. All members are safe to call from the UI thread; network work is asynchronous.
/// </summary>
public interface ILyricsOnlineService
{
    /// <summary>Every loaded plugin (enabled or not), unordered.</summary>
    IReadOnlyList<LyricsPluginInfo> Plugins { get; }

    /// <summary>Human-readable failures from the last <see cref="ReloadPlugins"/> scan.</summary>
    IReadOnlyList<string> LoadErrors { get; }

    /// <summary>Re-scans the plugins folder (new installs). Already-loaded plugins stay resident.</summary>
    void ReloadPlugins();

    /// <summary>Lyrics fetched online this session for a track, if any (does not touch the network).</summary>
    OnlineLyricsResult? GetSessionLyrics(string trackPath);

    /// <summary>Manual search across every enabled plugin, in parallel. Errors come back per plugin.</summary>
    Task<IReadOnlyList<PluginSearchOutcome>> SearchAsync(LyricsSearchQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads one search result into the session cache (displayed on the next pane refresh).
    /// Raises no events — pair with <see cref="ApplyResult"/> when the user actually picks it.
    /// </summary>
    Task<OnlineLyricsResult?> FetchAsync(LyricsPluginInfo plugin, LyricsSearchResult result, Track track, CancellationToken cancellationToken);

    /// <summary>Makes an already-fetched result the displayed lyrics for the track.</summary>
    void ApplyResult(OnlineLyricsResult result, Track track);

    /// <summary>Writes an online result to disk using the configured save location/template.</summary>
    LyricsSaveOutcome SaveResult(OnlineLyricsResult result, Track track);
}
