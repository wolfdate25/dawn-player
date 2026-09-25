using DawnPlayer.Core.Network.Dlna;
using Xunit;

namespace DawnPlayer.Tests.Network.Dlna;

/// <summary>
/// The SOAP wire format: request shape (so ObjectIDs with markup cannot inject XML) and response
/// extraction (the DIDL payload is text content of Result, escaped once by the SOAP layer).
/// </summary>
public sealed class SoapBrowseMessageTests
{
    [Fact]
    public void Request_ContainsArguments_AndEscapesMarkupInObjectIds()
    {
        var request = SoapBrowseMessage.BuildBrowseRequest("Music > A & B<folder>", 40, 500);

        Assert.Contains(":Envelope", request);
        Assert.Contains(":Body", request);
        Assert.Contains("BrowseDirectChildren", request);
        Assert.Contains("StartingIndex>40<", request);
        Assert.Contains("RequestedCount>500<", request);
        // Escaped, so it can never break out of the element.
        Assert.Contains("Music &gt; A &amp; B&lt;folder&gt;", request);
        Assert.DoesNotContain("Music > A & B<folder>", request);
    }

    [Fact]
    public void Response_ExtractsFields_AndUnescapesTheDidlPayload()
    {
        string soap =
            """"
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:BrowseResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                  <Result>&lt;DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"&gt;&lt;item id="i1"&gt;&lt;dc:title&gt;A &amp;amp; B&lt;/dc:title&gt;&lt;/item&gt;&lt;/DIDL-Lite&gt;</Result>
                  <NumberReturned>1</NumberReturned>
                  <TotalMatches>42</TotalMatches>
                  <UpdateID>7</UpdateID>
                </u:BrowseResponse>
              </s:Body>
            </s:Envelope>
            """";

        Assert.True(SoapBrowseMessage.TryParseBrowseResponse(soap, out var result, out var returned, out var total));
        Assert.Equal(1, returned);
        Assert.Equal(42, total);
        Assert.NotNull(result);
        // Exactly one round of unescaping: the SOAP parser resolved the payload, so &amp; in the
        // title is back to &amp; in the DIDL string (which the DIDL parser unescapes again).
        Assert.Contains("A &amp; B", result);
    }

    [Fact]
    public void SoapFault_FailsParsing()
    {
        string fault =
            """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <s:Fault>
                  <faultcode>s:Client</faultcode>
                  <faultstring>UPnPError</faultstring>
                  <detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>401</errorCode></UPnPError></detail>
                </s:Fault>
              </s:Body>
            </s:Envelope>
            """;

        Assert.False(SoapBrowseMessage.TryParseBrowseResponse(fault, out _, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body/></s:Envelope>")]
    public void MissingBrowseResponseOrFields_FailParsing(string soap)
    {
        Assert.False(SoapBrowseMessage.TryParseBrowseResponse(soap, out _, out _, out _));
    }
}
