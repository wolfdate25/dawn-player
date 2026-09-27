using System;
using System.Collections.Generic;

namespace DawnPlayer.Core.Network.YouTube;

/// <summary>
/// Short-lived cache of -J resolve results keyed by page URL. The section resolves metadata up
/// front (to fill the recent list with real titles before playback), and the reader's Open then
/// reuses the cached result instead of spawning a second yt-dlp resolve — the measured cost of
/// one resolve is ~2.5 s, so removing the duplicate halves the open latency. Metadata (title,
/// duration) does not expire the way media URLs do; the TTL only bounds staleness.
/// Bounded: overflowing the cap clears the cache (the next open simply re-resolves).
/// </summary>
public static class YouTubeResolveCache
{
    /// <summary>How long a resolved metadata stays trusted for reuse.</summary>
    public static TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(15);

    private const int Cap = 32;

    private static readonly object _gate = new();
    private static readonly Dictionary<string, (YouTubeTrackMeta Meta, DateTime ResolvedAt)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public static void Put(string pageUrl, YouTubeTrackMeta meta, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(pageUrl) || meta is null) return;
        lock (_gate)
        {
            if (_entries.Count >= Cap) _entries.Clear();
            _entries[pageUrl.Trim()] = (meta, now ?? DateTime.UtcNow);
        }
    }

    /// <summary>Returns cached metadata for this exact page URL, or null when absent/stale.</summary>
    public static YouTubeTrackMeta? Get(string pageUrl, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;
        var at = now ?? DateTime.UtcNow;
        lock (_gate)
        {
            if (!_entries.TryGetValue(pageUrl.Trim(), out var hit)) return null;
            if (at - hit.ResolvedAt > MaxAge)
            {
                _entries.Remove(pageUrl.Trim());
                return null;
            }
            return hit.Meta;
        }
    }

    public static void Clear()
    {
        lock (_gate) _entries.Clear();
    }
}
