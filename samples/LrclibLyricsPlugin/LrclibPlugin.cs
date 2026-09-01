using System.Text.Json;
using System.Text.Json.Serialization;
using DawnPlayer.Plugins;

namespace LrclibLyricsPlugin;

/// <summary>
/// Reference lyrics plugin for the free LRCLIB API (https://lrclib.net). Copy this project as a
/// starting point for a new site plugin: the attribute carries what the host displays, and the
/// two methods below are the whole contract.
/// </summary>
[LyricsPlugin("lrclib", "LRCLIB", "1.0.0", "Dawn Player Samples")]
public sealed class LrclibPlugin : ILyricsPlugin
{
    private const string BaseUrl = "https://lrclib.net/api";

    // One client per process: socket reuse, and the host may call the plugin concurrently.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<LyricsSearchResult>> SearchAsync(LyricsSearchQuery query, CancellationToken cancellationToken)
    {
        var url = BuildSearchUrl(query);
        var json = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var records = JsonSerializer.Deserialize<List<LrclibRecord>>(json, JsonOptions) ?? new List<LrclibRecord>();

        return records
            .Where(r => !r.Instrumental)
            .Select(ToResult)
            .ToList();
    }

    public async Task<LyricsContent?> GetAsync(LyricsSearchResult result, CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync($"{BaseUrl}/get/{Uri.EscapeDataString(result.ResultId)}", cancellationToken).ConfigureAwait(false);
        var record = JsonSerializer.Deserialize<LrclibRecord>(json, JsonOptions);
        if (record is null) return null;

        return new LyricsContent
        {
            SyncedLrc = string.IsNullOrWhiteSpace(record.SyncedLyrics) ? null : record.SyncedLyrics,
            PlainText = string.IsNullOrWhiteSpace(record.PlainLyrics) ? null : record.PlainLyrics
        };
    }

    /// <summary>
    /// LRCLIB's precise filters (track_name/artist_name) beat its free-text query; the q= search
    /// is the fallback for album-only lookups, which the filters cannot express.
    /// </summary>
    private static string BuildSearchUrl(LyricsSearchQuery query)
    {
        bool hasTitle = !string.IsNullOrWhiteSpace(query.Title);
        bool hasArtist = !string.IsNullOrWhiteSpace(query.Artist);
        bool hasAlbum = !string.IsNullOrWhiteSpace(query.Album);

        if (hasTitle || hasArtist)
        {
            var parameters = new List<string>();
            if (hasTitle) parameters.Add($"track_name={Uri.EscapeDataString(query.Title!)}");
            if (hasArtist) parameters.Add($"artist_name={Uri.EscapeDataString(query.Artist!)}");
            return $"{BaseUrl}/search?{string.Join("&", parameters)}";
        }

        var term = hasAlbum ? query.Album! : "";
        return $"{BaseUrl}/search?q={Uri.EscapeDataString(term)}";
    }

    private static async Task<string> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        // GetStringAsync gained a CancellationToken overload after netstandard2.0; plain
        // GetAsync + ReadAsStringAsync keeps the plugin portable.
        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static LyricsSearchResult ToResult(LrclibRecord record) => new()
    {
        ResultId = record.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Title = record.TrackName,
        Artist = record.ArtistName,
        Album = record.AlbumName,
        DurationMs = record.Duration is double seconds && seconds > 0 ? (int)Math.Round(seconds * 1000.0) : 0,
        IsSynced = !string.IsNullOrWhiteSpace(record.SyncedLyrics),
        SourceUrl = "https://lrclib.net"
    };

    private sealed class LrclibRecord
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("albumName")] public string? AlbumName { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    }
}
