using System.Text.Json;

namespace DawnPlayer.Core.Network.YouTube;

/// <summary>Page-URL validation for YouTube sources. Playlists persist the page URL — the media
/// URLs yt-dlp yields expire within hours and are bound to the fetching IP — so every playback
/// starts from the page. Accepts the shapes we can actually play, normalizes them to a canonical
/// watch URL, and rejects everything else so a typo can never become an unplayable track.</summary>
public static class YouTubePageUrl
{
    /// <summary>Accepts youtube.com/watch?v=…, youtu.be/&lt;id&gt;, music.youtube.com/watch?v=…,
    /// and /shorts/&lt;id&gt; or /live/&lt;id&gt; paths; drops playlist/timestamp parameters that
    /// do not change what should play. Returns the canonical
    /// <c>https://www.youtube.com/watch?v=&lt;id&gt;</c>, or null when the input is not a video page.</summary>
    public static string? TryNormalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;

        string? videoId = null;
        var host = uri.Host.ToLowerInvariant();
        if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            if (uri.AbsolutePath == "/watch")
            {
                videoId = QueryValue(uri.Query, "v");
            }
            else if (uri.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal) &&
                     uri.AbsolutePath.Length > "/shorts/".Length)
            {
                videoId = uri.AbsolutePath["/shorts/".Length..];
            }
            else if (uri.AbsolutePath.StartsWith("/live/", StringComparison.Ordinal) &&
                     uri.AbsolutePath.Length > "/live/".Length)
            {
                videoId = uri.AbsolutePath["/live/".Length..];
            }
        }
        else if (host == "youtu.be")
        {
            var id = uri.AbsolutePath.TrimStart('/');
            if (id.Length > 0) videoId = id;
        }

        if (string.IsNullOrWhiteSpace(videoId) || videoId.Contains('/')) return null;
        return "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(videoId);
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair[..eq].Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var raw = pair[(eq + 1)..];
            var value = Uri.UnescapeDataString(raw.Replace('+', ' '));
            return value.Length == 0 ? null : value;
        }
        return null;
    }
}

/// <summary>Display metadata for a resolved video, taken from yt-dlp's -J output. A zero
/// <see cref="DurationMs"/> means live/unknown length — the reader then behaves like a live
/// stream (no seek, no natural end, A-B marking refused by the controller's gate).</summary>
/// <param name="Title">Video title; empty when absent (the playlist row keeps the URL).</param>
/// <param name="Uploader">Channel name; empty when absent.</param>
/// <param name="DurationMs">Duration in milliseconds; 0 for live/unknown.</param>
/// <param name="ThumbnailUrl">Best-quality thumbnail, when the payload carries one.</param>
public sealed record YouTubeTrackMeta(string Title, string Uploader, long DurationMs, string? ThumbnailUrl);

/// <summary>Parses yt-dlp -J JSON. Never throws: malformed or empty payloads yield null and the
/// caller reports a resolve failure with guidance.</summary>
public static class YouTubeJsonParser
{
    public static YouTubeTrackMeta? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var title = GetString(root, "title");
            var uploader = GetString(root, "uploader");
            var thumb = GetStringOrNull(root, "thumbnail");

            long durationMs = 0;
            if (root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number)
            {
                var seconds = duration.GetDouble();
                if (double.IsFinite(seconds) && seconds > 0) durationMs = (long)(seconds * 1000);
            }

            return new YouTubeTrackMeta(title, uploader, durationMs, thumb);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetString(JsonElement root, string name)
    {
        var text = GetStringOrNull(root, name);
        return string.IsNullOrEmpty(text) ? "" : text;
    }

    private static string? GetStringOrNull(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        return string.IsNullOrEmpty(value.GetString()) ? null : value.GetString();
    }
}

/// <summary>
/// Resolve-once helper shared by the section UI (fill recent rows with real titles) and the
/// provider's Open (reuse the fresh metadata instead of resolving a second time). The result is
/// cached by page URL with a TTL; resolve failures propagate to the caller.
/// </summary>
public static class YouTubeResolve
{
    public static YouTubeTrackMeta ResolveMeta(IYouTubeProcessRunner runner, string pageUrl)
    {
        var cached = YouTubeResolveCache.Get(pageUrl);
        if (cached != null) return cached;

        var json = runner.ResolveJson(pageUrl, TimeSpan.FromSeconds(10), CancellationToken.None);
        var meta = YouTubeJsonParser.Parse(json)
            ?? throw new YouTubeProcessException($"unparsable yt-dlp JSON for {pageUrl}");
        YouTubeResolveCache.Put(pageUrl, meta);
        return meta;
    }
}
