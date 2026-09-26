using System.Xml.Linq;

namespace DawnPlayer.Core.Network.Dlna;

/// <summary>A UPnP media server we can actually browse: identity plus its ContentDirectory control
/// endpoint. <see cref="ServiceType"/> is the full URN and drives the SOAPACTION header.
/// <see cref="DisplayName"/> appends the host so two servers advertising the same friendly name
/// stay distinguishable in the picker.</summary>
public sealed record DlnaServer(
    Uri DescriptionUrl,
    string FriendlyName,
    string Udn,
    Uri ControlUrl,
    string ServiceType)
{
    public string DisplayName => $"{FriendlyName} ({DescriptionUrl.Host})";
}

/// <summary>
/// Extracts the ContentDirectory service from a UPnP device description document. Local-name
/// matching throughout — servers ship assorted namespace prefixes — and a missing
/// ContentDirectory yields null (renderer-only devices are not browsable).
/// </summary>
public static class DlnaDeviceDescriptionParser
{
    public static DlnaServer? TryParse(string xml, Uri descriptionUrl)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return null; }

        var rootDevice = FirstElement(doc.Root, "device");
        var friendlyName = TextOf(rootDevice, "friendlyName") ?? descriptionUrl.Host;
        var udn = TextOf(rootDevice, "UDN") ?? "";

        // URLBase overrides the description URL as the base for relative service URLs.
        var baseUrlText = TextOf(doc.Root, "URLBase");
        Uri? baseUrl = null;
        if (!string.IsNullOrWhiteSpace(baseUrlText))
            Uri.TryCreate(baseUrlText, UriKind.Absolute, out baseUrl);
        baseUrl ??= descriptionUrl;

        foreach (var service in doc.Root!.Descendants())
        {
            if (service.Name.LocalName != "service") continue;

            var serviceType = TextOf(service, "serviceType");
            if (serviceType == null || !serviceType.Contains("ContentDirectory", StringComparison.OrdinalIgnoreCase))
                continue;

            var controlUrlText = TextOf(service, "controlURL");
            if (string.IsNullOrWhiteSpace(controlUrlText)) continue;
            if (!Uri.TryCreate(baseUrl, controlUrlText, out var controlUrl) || !controlUrl.IsAbsoluteUri)
                continue;

            return new DlnaServer(descriptionUrl, friendlyName, udn, controlUrl, serviceType);
        }

        return null;
    }

    private static XElement? FirstElement(XElement? of, string localName) =>
        of?.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? TextOf(XElement? of, string localName)
    {
        var element = of?.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == localName);
        var value = element?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
