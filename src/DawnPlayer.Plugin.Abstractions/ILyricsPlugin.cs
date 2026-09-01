namespace DawnPlayer.Plugins;

/// <summary>
/// A lyrics source plugin. Implementations talk to one site/API: search turns a user query into
/// candidate results, and <see cref="GetAsync"/> downloads the lyrics for one result.
/// </summary>
/// <remarks>
/// Implementations may be called concurrently (automatic lookup and the search window can both
/// run) and must be stateless apart from immutable configuration read at construction. Throw on
/// failure rather than returning empty lists for errors — the host reports and moves on to the
/// next plugin in priority order.
/// </remarks>
public interface ILyricsPlugin
{
    /// <summary>Searches the site for lyrics matching <paramref name="query"/>.</summary>
    /// <returns>Zero or more candidates, best first. An empty list means "nothing found".</returns>
    Task<IReadOnlyList<LyricsSearchResult>> SearchAsync(LyricsSearchQuery query, CancellationToken cancellationToken);

    /// <summary>Downloads the lyrics referenced by <paramref name="result"/>.</summary>
    /// <returns>The synced and/or plain lyrics, or null when the result is no longer available.</returns>
    Task<LyricsContent?> GetAsync(LyricsSearchResult result, CancellationToken cancellationToken);
}
