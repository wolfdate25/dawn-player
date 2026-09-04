using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Services;

/// <summary>One queued scrobble: the track facts plus when it started playing (unix seconds).</summary>
public sealed record Scrobble(string Artist, string Track, string Album, int DurationSeconds, long StartUnix, string Path);

/// <summary>
/// Last.fm 2.0 API client: web-auth token/session exchange, now-playing updates and scrobbles.
/// Requests are signed per the Last.fm spec (md5 of sorted key=value pairs + the api secret).
/// All network calls run on the caller's context — the owning service decides threading.
/// </summary>
public sealed class LastfmClient : IDisposable
{
    private const string Endpoint = "https://ws.audioscrobbler.com/2.0/";
    private const string AuthUrl = "https://www.last.fm/api/auth/";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Func<LastfmSettings> _settings;

    public LastfmClient(Func<LastfmSettings> settings)
    {
        _settings = settings;
    }

    public void Dispose() => _http.Dispose();

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings().ApiKey) && !string.IsNullOrWhiteSpace(_settings().ApiSecret);

    public bool IsAuthenticated => IsConfigured && !string.IsNullOrWhiteSpace(_settings().SessionKey);

    /// <summary>The browser URL the user must open and approve to authorize a token.</summary>
    public static string BuildAuthPageUrl(string apiKey, string token) =>
        $"{AuthUrl}?api_key={Uri.EscapeDataString(apiKey)}&token={Uri.EscapeDataString(token)}";

    /// <summary>Requests a fresh auth token (valid ~1 h, must be approved in the browser).</summary>
    public async Task<string> GetTokenAsync()
    {
        var s = _settings();
        var json = await CallAsync(new Dictionary<string, string>
        {
            ["method"] = "auth.getToken",
            ["api_key"] = s.ApiKey,
        }, signed: true, sessionKey: null).ConfigureAwait(false);
        return RequireField(json, "token");
    }

    /// <summary>Exchanges an approved token for a session key + username, persisted by the caller.</summary>
    public async Task<(string SessionKey, string Username)> GetSessionAsync(string token)
    {
        var s = _settings();
        var json = await CallAsync(new Dictionary<string, string>
        {
            ["method"] = "auth.getSession",
            ["api_key"] = s.ApiKey,
            ["token"] = token,
        }, signed: true, sessionKey: null).ConfigureAwait(false);

        string key = RequireField(json, "key");
        string name = FindString(json, "name") ?? "";
        return (key, name);
    }

    public async Task UpdateNowPlayingAsync(string artist, string track, string album, int durationSeconds)
    {
        if (!IsAuthenticated) return;
        var s = _settings();
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "track.updateNowPlaying",
            ["artist"] = artist,
            ["track"] = track,
            ["api_key"] = s.ApiKey,
            ["sk"] = s.SessionKey,
        };
        if (!string.IsNullOrEmpty(album)) parameters["album"] = album;
        if (durationSeconds > 0) parameters["duration"] = durationSeconds.ToString(CultureInfo.InvariantCulture);

        await CallAsync(parameters, signed: true, sessionKey: s.SessionKey).ConfigureAwait(false);
    }

    /// <summary>Posts up to 50 scrobbles in one call. Returns true when the API accepted them.</summary>
    public async Task<bool> ScrobbleAsync(IReadOnlyList<Scrobble> scrobbles)
    {
        if (!IsAuthenticated || scrobbles.Count == 0) return false;
        var s = _settings();

        var parameters = new Dictionary<string, string>
        {
            ["method"] = "track.scrobble",
            ["api_key"] = s.ApiKey,
            ["sk"] = s.SessionKey,
        };
        for (int i = 0; i < Math.Min(scrobbles.Count, 50); i++)
        {
            var sc = scrobbles[i];
            parameters[$"artist[{i}]"] = sc.Artist;
            parameters[$"track[{i}]"] = sc.Track;
            if (!string.IsNullOrEmpty(sc.Album)) parameters[$"album[{i}]"] = sc.Album;
            if (sc.DurationSeconds > 0) parameters[$"duration[{i}]"] = sc.DurationSeconds.ToString(CultureInfo.InvariantCulture);
            parameters[$"timestamp[{i}]"] = sc.StartUnix.ToString(CultureInfo.InvariantCulture);
        }

        var json = await CallAsync(parameters, signed: true, sessionKey: s.SessionKey).ConfigureAwait(false);
        return json.Contains("\"scrobbles\"", StringComparison.Ordinal)
            || !json.Contains("\"error\"", StringComparison.Ordinal);
    }

    // ---------------- plumbing ----------------

    /// <summary>Last.fm signature: md5 of the alphabetically sorted key=value pairs (sk excluded)
    /// concatenated with the api secret. MD5 is the protocol's fixed choice — not a strength claim.
    /// </summary>
    public static string Sign(Dictionary<string, string> parameters, string apiSecret)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(key).Append('=').Append(value);
        }
        sb.Append(apiSecret);
#pragma warning disable CA5351 // MD5 is mandated by the Last.fm auth spec
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
#pragma warning restore CA5351
    }

    private async Task<string> CallAsync(Dictionary<string, string> parameters, bool signed, string? sessionKey)
    {
        if (signed)
        {
            parameters["api_sig"] = Sign(new Dictionary<string, string>(parameters), _settings().ApiSecret);
        }
        parameters["format"] = "json";

        using var content = new FormUrlEncodedContent(parameters);
        using var response = await _http.PostAsync(Endpoint, content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Last.fm API {response.StatusCode}: {Truncate(body)}");
        }
        if (body.Contains("\"error\"", StringComparison.Ordinal))
        {
            throw new HttpRequestException($"Last.fm API error: {Truncate(body)}");
        }
        return body;
    }

    private static string RequireField(string json, string field)
    {
        var value = FindString(json, field);
        return value ?? throw new HttpRequestException($"Last.fm response has no '{field}': {Truncate(json)}");
    }

    /// <summary>First string-valued occurrence of a top-level-or-nested field. The auth responses
    /// are small and known-shaped; a depth-first walk is simpler than mapping DTOs.</summary>
    private static string? FindString(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Walk(doc.RootElement, field);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Walk(JsonElement element, string field)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, field, StringComparison.Ordinal) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
                var nested = Walk(property.Value, field);
                if (nested != null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = Walk(item, field);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];
}
