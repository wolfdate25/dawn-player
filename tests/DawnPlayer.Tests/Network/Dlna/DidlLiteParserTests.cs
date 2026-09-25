using DawnPlayer.Core.Network.Dlna;
using Xunit;

namespace DawnPlayer.Tests.Network.Dlna;

/// <summary>
/// DIDL-Lite parsing against the fixture shapes real servers emit: mixed containers/items,
/// alternate namespace prefixes, malformed rows that must be skipped, multi-res items, and the
/// UPnP duration literal.
/// </summary>
public sealed class DidlLiteParserTests
{
    private const string MixedDidl =
        """
        <DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"
                   xmlns:dc="http://purl.org/dc/elements/1.1/"
                   xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">
          <container id="1" parentID="0" childCount="12" restricted="1">
            <dc:title>음악</dc:title>
            <upnp:class>object.container.storageFolder</upnp:class>
          </container>
          <item id="42" parentID="1" restricted="1">
            <dc:title>Rubia</dc:title>
            <upnp:class>object.item.audioItem.musicTrack</upnp:class>
            <upnp:artist>Zhou Shen</upnp:artist>
            <upnp:album>Rubia</upnp:album>
            <upnp:genre>Pop</upnp:genre>
            <upnp:albumArtURI>http://192.168.0.10:8200/AlbumArt/42</upnp:albumArtURI>
            <res protocolInfo="http-get:*:audio/flac:DLNA.ORG_PN=FLAC_2_44100_16" size="22170628" duration="0:03:56.500" bitrate="1411000">http://192.168.0.10:8200/Media/42.flac</res>
            <res protocolInfo="http-get:*:audio/mpeg:DLNA.ORG_PN=MP3" size="8663624" duration="0:03:56.500">http://192.168.0.10:8200/Media/42.mp3</res>
          </item>
        </DIDL-Lite>
        """;

    [Fact]
    public void MixedDocument_ParsesContainersAndItems()
    {
        var entries = DidlLiteParser.Parse(MixedDidl);

        Assert.Equal(2, entries.Count);

        var folder = Assert.IsType<DidlContainerEntry>(entries[0]);
        Assert.Equal("1", folder.Id);
        Assert.Equal("0", folder.ParentId);
        Assert.Equal("음악", folder.Title);
        Assert.Equal(12, folder.ChildCount);

        var item = Assert.IsType<DidlItemEntry>(entries[1]);
        Assert.Equal("42", item.Id);
        Assert.Equal("Rubia", item.Title);
        Assert.Equal("Zhou Shen", item.Artist);
        Assert.Equal("Rubia", item.Album);
        Assert.Equal("Pop", item.Genre);
        Assert.NotNull(item.AlbumArtUri);
        Assert.Equal(2, item.Resources.Count);
        var flac = item.Resources[0];
        Assert.Equal("http://192.168.0.10:8200/Media/42.flac", flac.Uri.AbsoluteUri);
        Assert.Equal(22_170_628, flac.SizeBytes);
        Assert.Equal(TimeSpan.FromSeconds(236.5), item.Duration);
    }

    [Fact]
    public void AlternatePrefixes_StillResolve()
    {
        var xml = MixedDidl
            .Replace("<DIDL-Lite", "<d:didl xmlns:d=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:x=\"http://purl.org/dc/elements/1.1/\" xmlns:y=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"")
            .Replace("</DIDL-Lite>", "</d:didl>")
            .Replace("<dc:title>", "<x:title>").Replace("</dc:title>", "</x:title>")
            .Replace("<upnp:artist>", "<y:artist>").Replace("</upnp:artist>", "</y:artist>")
            .Replace("<container", "<d:container").Replace("</container>", "</d:container>");

        var entries = DidlLiteParser.Parse(xml);

        Assert.Equal(2, entries.Count);
        Assert.Equal("음악", entries[0].Title);
        Assert.Equal("Zhou Shen", Assert.IsType<DidlItemEntry>(entries[1]).Artist);
    }

    [Fact]
    public void MalformedObject_IsSkipped_ParsingContinues()
    {
        var xml = """
            <DIDL-Lite xmlns:dc="http://purl.org/dc/elements/1.1/">
              <item id="broken" parentID="0">
                <dc:title>has no res and that is fine</dc:title>
              </item>
              <container parentID="0">
              </container>
              <item id="ok" parentID="0">
                <dc:title>Fine</dc:title>
                <res protocolInfo="http-get:*:audio/mpeg:*">http://h/a.mp3</res>
              </item>
            </DIDL-Lite>
            """;
        // The middle container has no id — malformed by our contract.

        var entries = DidlLiteParser.Parse(xml);

        Assert.Equal(2, entries.Count);
        Assert.Equal("broken", entries[0].Id);
        Assert.Equal("ok", entries[1].Id);
    }

    [Fact]
    public void EmptyDidl_AndGarbage_YieldNoEntries()
    {
        Assert.Empty(DidlLiteParser.Parse("""<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"/>"""));
        Assert.Empty(DidlLiteParser.Parse("not xml"));
        Assert.Empty(DidlLiteParser.Parse(""));
    }

    [Theory]
    [InlineData("0:03:45.500", 225.5)]
    [InlineData("1:02:03", 3723)]
    [InlineData("00:00:10", 10)]
    [InlineData("2:00:00.250", 7200.25)]
    public void DurationLiterals_Parse(string literal, double expectedSeconds)
    {
        Assert.True(DidlDuration.TryParse(literal, out var value));
        Assert.Equal(expectedSeconds, value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3:45")]            // missing hours field
    [InlineData("0:99:00")]         // minutes out of range
    [InlineData("0:00:61")]         // seconds out of range
    [InlineData("0:xx:00")]
    [InlineData("garbage")]
    public void InvalidDurationLiterals_Rejected(string literal)
    {
        Assert.False(DidlDuration.TryParse(literal, out _));
    }
}
