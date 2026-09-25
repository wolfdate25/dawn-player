using DawnPlayer.Core.Network.Dlna;
using Xunit;

namespace DawnPlayer.Tests.Network.Dlna;

/// <summary>
/// The SSDP datagram parser decides which responses become device candidates; anything malformed
/// must be dropped here rather than surfacing as a broken server entry.
/// </summary>
public sealed class SsdpResponseParserTests
{
    private static readonly string Valid =
        "HTTP/1.1 200 OK\r\n" +
        "CACHE-CONTROL: max-age=1800\r\n" +
        "EXT:\r\n" +
        "LOCATION: http://192.168.0.10:8200/rootDesc.xml\r\n" +
        "SERVER: Linux UPnP/1.0 MiniUPnPd/2.3.1\r\n" +
        "ST: urn:schemas-upnp-org:device:MediaServer:1\r\n" +
        "USN: uuid:2fac1234-31f8-11b4-a222-08002b34c003::urn:schemas-upnp-org:device:MediaServer\r\n";

    [Fact]
    public void ValidResponse_ParsesAllFields()
    {
        var hit = SsdpResponseParser.TryParse(Valid);

        Assert.NotNull(hit);
        Assert.Equal("http://192.168.0.10:8200/rootDesc.xml", hit!.Location.AbsoluteUri);
        Assert.StartsWith("uuid:2fac1234", hit.Usn);
        Assert.Contains("MediaServer:1", hit.St);
        Assert.Contains("MiniUPnPd", hit.ServerHeader);
    }

    [Fact]
    public void HeaderNamesAndValues_TolerateCaseAndLfOnlyLines()
    {
        var lfOnly = Valid.Replace("\r\n", "\n")
            .Replace("LOCATION:", "location:")
            .Replace("USN:", "usn:");

        var hit = SsdpResponseParser.TryParse(lfOnly);

        Assert.NotNull(hit);
        Assert.Equal("http://192.168.0.10:8200/rootDesc.xml", hit!.Location.AbsoluteUri);
    }

    [Theory]
    [InlineData("NOTIFY * HTTP/1.1\r\nNT: some:device\r\n\r\n")]   // advertisement, not a search response
    [InlineData("HTTP/1.1 404 Not Found\r\n\r\n")]                  // error status
    [InlineData("HTTP/1.1 200 OK\r\nUSN: uuid:x\r\n\r\n")]          // LOCATION missing
    [InlineData("HTTP/1.1 200 OK\r\nLOCATION: http://a/b.xml\r\n\r\n")] // USN missing
    [InlineData("HTTP/1.1 200 OK\r\nLOCATION: not a uri\r\nUSN: u\r\n\r\n")] // unparseable LOCATION
    [InlineData("")]
    [InlineData("   ")]
    public void MalformedDatagrams_AreDropped(string datagram)
    {
        Assert.Null(SsdpResponseParser.TryParse(datagram));
    }
}
