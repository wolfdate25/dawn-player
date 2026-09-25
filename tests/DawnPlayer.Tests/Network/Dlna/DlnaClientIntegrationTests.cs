using System.Net;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Network.Dlna;
using Xunit;

namespace DawnPlayer.Tests.Network.Dlna;

/// <summary>
/// End-to-end over the Core stack with a fake HTTP transport: device description → server →
/// Browse (request shape checked on the server side) → DIDL parse → playable Track. The audio
/// spooling/playback half of that Track is already covered by the N1 integration tests.
/// </summary>
public sealed class DlnaClientIntegrationTests
{
    private const string DeviceXml =
        """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <device>
            <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
            <friendlyName>Living Room NAS</friendlyName>
            <UDN>uuid:9f38b7a0-c000-11ee-9a17-08002b34c003</UDN>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType>
                <controlURL>/ctl/cds</controlURL>
              </service>
            </serviceList>
          </device>
        </root>
        """;

    private const string BrowseResponseSoap =
        """"
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <u:BrowseResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
              <Result>&lt;DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/"&gt;&lt;item id="42" parentID="7" restricted="1"&gt;&lt;dc:title&gt;Rubia&lt;/dc:title&gt;&lt;upnp:class&gt;object.item.audioItem.musicTrack&lt;/upnp:class&gt;&lt;upnp:artist&gt;Zhou Shen&lt;/upnp:artist&gt;&lt;upnp:album&gt;Rubia&lt;/upnp:album&gt;&lt;res protocolInfo="http-get:*:audio/flac:DLNA.ORG_PN=FLAC" size="22170628" duration="0:03:56.500"&gt;http://192.168.0.10:8200/Media/42.flac&lt;/res&gt;&lt;/item&gt;&lt;/DIDL-Lite&gt;</Result>
              <NumberReturned>1</NumberReturned>
              <TotalMatches>1</TotalMatches>
              <UpdateID>1</UpdateID>
            </u:BrowseResponse>
          </s:Body>
        </s:Envelope>
        """";

    [Fact]
    public async Task DescriptionToTrack_WholeChainWorks_WithCorrectSoapAction()
    {
        string? seenAction = null;
        string? seenBody = null;
        using var handler = new RoutingHandler(request =>
        {
            seenAction = request.Headers.TryGetValues("SOAPACTION", out var values) ? values.FirstOrDefault() : null;
            seenBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BrowseResponseSoap, System.Text.Encoding.UTF8, "text/xml"),
            };
        });

        var server = DlnaDeviceDescriptionParser.TryParse(DeviceXml, new Uri("http://192.168.0.10:8200/rootDesc.xml"));
        Assert.NotNull(server);

        var client = new ContentDirectoryClient(new HttpClient(handler));
        var page = await client.BrowseAsync(server!, objectId: "7");

        // The request carried the advertised service version and a well-formed Browse envelope.
        Assert.Equal("\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"", seenAction);
        Assert.NotNull(seenBody);
        Assert.Contains("BrowseDirectChildren", seenBody);
        Assert.Contains("<ObjectID>7</ObjectID>", seenBody);

        // And the response became a playable Dlna track.
        Assert.Equal(1, page.NumberReturned);
        var item = Assert.IsType<DidlItemEntry>(Assert.Single(page.Entries));
        var track = DlnaTrackFactory.TryCreate(item, server!.DescriptionUrl);

        Assert.NotNull(track);
        Assert.Equal("http://192.168.0.10:8200/Media/42.flac", track!.Path);
        Assert.Equal(TrackSourceKind.Dlna, track.SourceKind);
        Assert.Equal("FLAC", track.Codec);
        Assert.Equal("Rubia", track.Title);
        Assert.Equal("Zhou Shen", track.Artist);
        Assert.Equal(236_500, track.DurationMs);
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
