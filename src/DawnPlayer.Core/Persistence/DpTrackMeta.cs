using System.Text.Json;

namespace DawnPlayer.Core.Persistence;

/// <summary>
/// The typed payload of the M3U8 <c>#DPTRACK:</c> directive, which persists remote tracks
/// (radio/DLNA/YouTube) with their display metadata across restarts. Encoded as compact JSON,
/// URI-escaped onto one comment line — foreign players ignore unknown <c>#</c> directives, and a
/// hand-edited or directive-less file simply falls back to the plain-URL radio path on load.
/// </summary>
public sealed record DpTrackMeta(
    int SourceKind,
    string? Title,
    string? Artist,
    string? Album,
    double? DurationSeconds,
    string? ArtUrl)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Single-line, round-trippable encoding for the directive line.</summary>
    public string Encode() => Uri.EscapeDataString(JsonSerializer.Serialize(this, Options));

    /// <summary>Decodes a directive payload; null (never throws) when it is corrupt, foreign, or
    /// missing required shape — the entry then degrades to its plain path line.</summary>
    public static DpTrackMeta? TryDecode(string? directive)
    {
        if (string.IsNullOrWhiteSpace(directive)) return null;
        try
        {
            var json = Uri.UnescapeDataString(directive.Trim());
            var meta = JsonSerializer.Deserialize<DpTrackMeta>(json, Options);
            return meta is { SourceKind: > 0 } ? meta : null;
        }
        catch
        {
            return null;
        }
    }
}
