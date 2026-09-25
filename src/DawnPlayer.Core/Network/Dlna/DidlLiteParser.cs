using System.Globalization;
using System.Xml.Linq;

namespace DawnPlayer.Core.Network.Dlna;

/// <summary>One playable resource (a res element): where the bytes are and what they are.
/// <see cref="ProtocolInfo"/> is the full UPnP token ("http-get:*:audio/flac:*").</summary>
public sealed record DidlResource(Uri Uri, string ProtocolInfo, long? SizeBytes, TimeSpan? Duration, long? Bitrate);

/// <summary>Base of the DIDL object model: identity plus class.</summary>
public abstract record DidlEntry(string Id, string? ParentId, string Title, string Class);

/// <summary>A browsable folder ("object.container.*").</summary>
public sealed record DidlContainerEntry(
    string Id, string? ParentId, string Title, string Class, int? ChildCount) : DidlEntry(Id, ParentId, Title, Class);

/// <summary>A playable object ("object.item.*") with its metadata and candidate resources.</summary>
public sealed record DidlItemEntry(
    string Id,
    string? ParentId,
    string Title,
    string Class,
    string? Artist,
    string? Album,
    string? Genre,
    TimeSpan? Duration,
    Uri? AlbumArtUri,
    IReadOnlyList<DidlResource> Resources) : DidlEntry(Id, ParentId, Title, Class);

/// <summary>
/// Parses DIDL-Lite documents. Servers disagree on namespace prefixes and occasionally emit one
/// malformed object among hundreds, so: local-name matching throughout, per-object try/skip, and
/// the document parse itself never throws — a useless payload yields an empty list.
/// </summary>
public static class DidlLiteParser
{
    public static IReadOnlyList<DidlEntry> Parse(string didlXml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(didlXml); }
        catch { return []; }

        var entries = new List<DidlEntry>();
        foreach (var element in doc.Root?.Elements() ?? [])
        {
            DidlEntry? entry = null;
            try
            {
                if (element.Name.LocalName == "container") entry = ParseContainer(element);
                else if (element.Name.LocalName == "item") entry = ParseItem(element);
            }
            catch
            {
                // One bad row must not take the browsing session down.
            }
            if (entry != null) entries.Add(entry);
        }
        return entries;
    }

    private static DidlContainerEntry ParseContainer(XElement element)
    {
        var title = TextOf(element, "title") ?? "";
        var @class = TextOf(element, "class") ?? "";
        if (element.Attribute("id")?.Value is not { Length: > 0 } id) return null!;
        return new DidlContainerEntry(
            id,
            Attribute(element, "parentID"),
            title,
            @class,
            int.TryParse(Attribute(element, "childCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var children) ? children : null);
    }

    private static DidlItemEntry ParseItem(XElement element)
    {
        var title = TextOf(element, "title") ?? "";
        var @class = TextOf(element, "class") ?? "";
        if (element.Attribute("id")?.Value is not { Length: > 0 } id) return null!;

        Uri? artUri = null;
        var artText = TextOf(element, "albumArtURI");
        if (!string.IsNullOrWhiteSpace(artText)) Uri.TryCreate(artText, UriKind.Absolute, out artUri);

        var resources = new List<DidlResource>();
        foreach (var res in element.Elements())
        {
            if (res.Name.LocalName != "res") continue;

            var uriText = res.Value.Trim();
            if (uriText.Length == 0 || !Uri.TryCreate(uriText, UriKind.Absolute, out var resUri)) continue;

            TimeSpan? duration = null;
            var durationText = Attribute(res, "duration");
            if (durationText != null && DidlDuration.TryParse(durationText, out var parsedDuration)) duration = parsedDuration;

            resources.Add(new DidlResource(
                resUri,
                Attribute(res, "protocolInfo") ?? "",
                long.TryParse(Attribute(res, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : null,
                duration,
                long.TryParse(Attribute(res, "bitrate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitrate) ? bitrate : null));
        }

        TimeSpan? itemDuration = resources.FirstOrDefault(r => r.Duration != null)?.Duration;

        return new DidlItemEntry(
            id,
            Attribute(element, "parentID"),
            title,
            @class,
            TextOf(element, "artist"),
            TextOf(element, "album"),
            TextOf(element, "genre"),
            itemDuration,
            artUri,
            resources);
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attribute(name)?.Value is { Length: > 0 } value ? value : null;

    /// <summary>First descendant-or-self text with this local name (dc:title, upnp:artist…).</summary>
    private static string? TextOf(XElement element, string localName)
    {
        var value = element.DescendantsAndSelf()
            .FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

/// <summary>UPnP duration literals: "H:MM:SS", "HH:MM:SS.frac". Public for tests.</summary>
public static class DidlDuration
{
    public static bool TryParse(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes > 59) return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds >= 60) return false;

        value = new TimeSpan((long)(TimeSpan.TicksPerHour * hours
            + TimeSpan.TicksPerMinute * minutes
            + TimeSpan.TicksPerSecond * seconds));
        return true;
    }
}
