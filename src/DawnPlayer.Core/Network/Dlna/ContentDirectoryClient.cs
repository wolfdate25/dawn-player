using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace DawnPlayer.Core.Network.Dlna;

/// <summary>Browse failed at the protocol level (transport error, SOAP fault, malformed body).
/// The message is a short technical detail; the UI wraps it in a localized line.</summary>
public sealed class DlnaException : Exception
{
    public DlnaException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>One page of a ContentDirectory Browse result.</summary>
public sealed record DlnaBrowsePage(
    IReadOnlyList<DidlEntry> Entries,
    int NumberReturned,
    int TotalMatches);

/// <summary>
/// Talks to one server's ContentDirectory control endpoint. The HttpClient is injectable so tests
/// drive the full request/response handling from a fake handler without sockets.
/// </summary>
public sealed class ContentDirectoryClient
{
    private readonly HttpClient _http;

    public ContentDirectoryClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<DlnaBrowsePage> BrowseAsync(
        DlnaServer server,
        string objectId = "0",
        int startingIndex = 0,
        int requestedCount = 500,
        CancellationToken cancellationToken = default)
    {
        var body = SoapBrowseMessage.BuildBrowseRequest(objectId, startingIndex, requestedCount);
        using var request = new HttpRequestMessage(HttpMethod.Post, server.ControlUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
        // The SOAPACTION header carries the exact service version the description advertised.
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{server.ServiceType}#Browse\"");

        string soapXml;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            soapXml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DlnaException($"browse failed: {server.ControlUrl}", ex);
        }

        if (!SoapBrowseMessage.TryParseBrowseResponse(soapXml, out var didl, out var returned, out var total))
            throw new DlnaException($"unusable browse response from {server.FriendlyName}");

        var entries = didl != null ? DidlLiteParser.Parse(didl) : [];
        return new DlnaBrowsePage(entries, returned, total);
    }
}
