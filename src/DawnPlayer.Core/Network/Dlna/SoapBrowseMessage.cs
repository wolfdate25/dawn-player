using System.Globalization;
using System.Xml.Linq;

namespace DawnPlayer.Core.Network.Dlna;

/// <summary>
/// Builds ContentDirectory Browse requests and reads their responses. Both halves are pure — the
/// wire format (envelope shape, XML-escaped arguments, the doubly-escaped DIDL payload inside
/// Result) is pinned by tests so the HTTP layer stays a dumb transport.
/// </summary>
public static class SoapBrowseMessage
{
    /// <summary>BrowseDirectChildren request body for one page of a container. Built as text in
    /// the conventional UPnP shape (s: envelope, unqualified argument elements) rather than via
    /// an XML object model, whose serialization emits xmlns="" resets some servers choke on.</summary>
    public static string BuildBrowseRequest(string objectId, int startingIndex, int requestedCount) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
        "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
        "<s:Body>" +
        "<u:Browse xmlns:u=\"urn:schemas-upnp-org:service:ContentDirectory:1\">" +
        $"<ObjectID>{XmlEscape(objectId)}</ObjectID>" +
        "<BrowseFlag>BrowseDirectChildren</BrowseFlag>" +
        "<Filter>*</Filter>" +
        $"<StartingIndex>{startingIndex.ToString(CultureInfo.InvariantCulture)}</StartingIndex>" +
        $"<RequestedCount>{requestedCount.ToString(CultureInfo.InvariantCulture)}</RequestedCount>" +
        "<SortCriteria></SortCriteria>" +
        "</u:Browse>" +
        "</s:Body>" +
        "</s:Envelope>";

    /// <summary>
    /// Extracts the Browse response fields. <paramref name="didlResult"/> is the raw DIDL document
    /// (already unescaped once by the XML parser — it sits in the SOAP body as text content).
    /// Returns false for SOAP faults or missing fields, never throws.
    /// </summary>
    public static bool TryParseBrowseResponse(string soapXml, out string? didlResult, out int numberReturned, out int totalMatches)
    {
        didlResult = null;
        numberReturned = 0;
        totalMatches = 0;

        XDocument doc;
        try { doc = XDocument.Parse(soapXml); }
        catch { return false; }

        if (doc.Root?.Descendants().Any(e => e.Name.LocalName == "Fault") == true) return false;

        var response = doc.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "BrowseResponse");
        if (response == null) return false;

        didlResult = TextOf(response, "Result");
        return int.TryParse(TextOf(response, "NumberReturned"), out numberReturned)
            && int.TryParse(TextOf(response, "TotalMatches"), out totalMatches);
    }

    private static string? TextOf(XElement of, string localName)
    {
        var element = of.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == localName);
        return element?.Value;
    }

    private static string XmlEscape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
