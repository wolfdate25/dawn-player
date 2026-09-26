using DawnPlayer.Core.Network.Dlna;
using Xunit;

namespace DawnPlayer.Tests.Network.Dlna;

/// <summary>
/// Device-description parsing against fixture documents shaped like the real ones (MinimServer,
/// Windows streaming, nested device trees). The control-URL resolution rules and the
/// "no ContentDirectory → not browsable" contract live here.
/// </summary>
public sealed class DlnaDeviceDescriptionParserTests
{
    private static readonly Uri DescriptionAt = new("http://192.168.0.10:8200/rootDesc.xml");

    private const string MinimalServer =
        """"
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <device>
            <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
            <friendlyName>MinimServer</friendlyName>
            <UDN>uuid:2fac1234-31f8-11b4-a222-08002b34c003</UDN>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId>
                <controlURL>/Control/L16ContentDirectory</controlURL>
                <eventSubURL>/Event/L16ContentDirectory</eventSubURL>
                <SCPDURL>/L16ContentDirectory.xml</SCPDURL>
              </service>
              <service>
                <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
                <controlURL>/Control/ConnectionManager</controlURL>
              </service>
            </serviceList>
          </device>
        </root>
        """";

    [Fact]
    public void MinimalServer_PicksContentDirectory_WithServiceTypeForSoapAction()
    {
        var server = DlnaDeviceDescriptionParser.TryParse(MinimalServer, DescriptionAt);

        Assert.NotNull(server);
        Assert.Equal("MinimServer", server!.FriendlyName);
        Assert.StartsWith("uuid:", server.Udn);
        Assert.Equal("http://192.168.0.10:8200/Control/L16ContentDirectory", server.ControlUrl.AbsoluteUri);
        Assert.Equal("urn:schemas-upnp-org:service:ContentDirectory:1", server.ServiceType);
    }

    [Fact]
    public void AbsoluteControlUrl_IsKeptVerbatim()
    {
        var xml = MinimalServer.Replace("/Control/L16ContentDirectory", "http://other.host:9100/cd");
        var server = DlnaDeviceDescriptionParser.TryParse(xml, DescriptionAt);

        Assert.NotNull(server);
        Assert.Equal("http://other.host:9100/cd", server!.ControlUrl.AbsoluteUri);
    }

    [Fact]
    public void DisplayName_AppendsHost_SoSameNameServersStayDistinct()
    {
        var server = DlnaDeviceDescriptionParser.TryParse(MinimalServer, DescriptionAt);

        // Two servers on the LAN advertising the same friendly name must stay distinguishable.
        Assert.Equal("MinimServer (192.168.0.10)", server!.DisplayName);
    }

    [Fact]
    public void UrlBase_Element_OverridesDescriptionUrlAsBase()
    {
        var xml = MinimalServer.Replace(
            "<device>",
            "<URLBase>http://192.168.0.10:8201/base/</URLBase>\n          <device>");

        var server = DlnaDeviceDescriptionParser.TryParse(xml, DescriptionAt);

        Assert.NotNull(server);
        // RFC 3986 resolution: a path-absolute reference ("/Control/…") swaps the base's path,
        // so URLBase contributes authority+port here — matching how real servers behave.
        Assert.Equal("http://192.168.0.10:8201/Control/L16ContentDirectory", server!.ControlUrl.AbsoluteUri);
    }

    [Fact]
    public void NestedDeviceTree_FindsContentDirectoryInEmbeddedDevice()
    {
        var xml = """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
                <friendlyName>MinimServer</friendlyName>
                <UDN>uuid:2fac1234-31f8-11b4-a222-08002b34c003</UDN>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
                    <controlURL>/Control/ConnectionManager</controlURL>
                  </service>
                </serviceList>
                <deviceList>
                  <device>
                    <deviceType>urn:schemas-upnp-org:device:MediaServer:3</deviceType>
                    <friendlyName>Inner Server</friendlyName>
                    <UDN>uuid:inner-device</UDN>
                    <serviceList>
                      <service>
                        <serviceType>urn:schemas-upnp-org:service:ContentDirectory:3</serviceType>
                        <controlURL>/inner/cd</controlURL>
                      </service>
                    </serviceList>
                  </device>
                </deviceList>
              </device>
            </root>
            """;

        var server = DlnaDeviceDescriptionParser.TryParse(xml, DescriptionAt);

        Assert.NotNull(server);
        // Identity comes from the top-level device; the control endpoint from the embedded one.
        Assert.Equal("MinimServer", server!.FriendlyName);
        Assert.Equal("http://192.168.0.10:8200/inner/cd", server.ControlUrl.AbsoluteUri);
        Assert.Equal("urn:schemas-upnp-org:service:ContentDirectory:3", server.ServiceType);
    }

    [Fact]
    public void RendererOnlyDevice_YieldsNull()
    {
        var xml = MinimalServer.Replace("ContentDirectory:1", "AVTransport:1");

        Assert.Null(DlnaDeviceDescriptionParser.TryParse(xml, DescriptionAt));
    }

    [Fact]
    public void AlternateNamespacePrefixes_StillResolve()
    {
        var xml = MinimalServer.Replace("<root xmlns=", "<up:root xmlns:up=").Replace("</root>", "</up:root>")
            .Replace("<device>", "<up:device>").Replace("</device>", "</up:device>")
            .Replace("<friendlyName>", "<up:friendlyName>").Replace("</friendlyName>", "</up:friendlyName>")
            .Replace("<controlURL>", "<up:controlURL>").Replace("</controlURL>", "</up:controlURL>")
            .Replace("<serviceType>", "<up:serviceType>").Replace("</serviceType>", "</up:serviceType>");

        var server = DlnaDeviceDescriptionParser.TryParse(xml, DescriptionAt);

        Assert.NotNull(server);
        Assert.Equal("MinimServer", server!.FriendlyName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<root/>")]
    public void MalformedOrEmptyDocuments_YieldNull(string xml)
    {
        Assert.Null(DlnaDeviceDescriptionParser.TryParse(xml, DescriptionAt));
    }
}
