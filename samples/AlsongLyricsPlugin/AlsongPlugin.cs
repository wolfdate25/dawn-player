using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DawnPlayer.Plugins;

[assembly: InternalsVisibleTo("DawnPlayer.Tests")]

namespace AlsongLyricsPlugin;

/// <summary>
/// Lyrics plugin for the Alsong server (lyrics.alsong.co.kr), ported from
/// wolfdate25/alsong-lyrics-searcher (a foobar2000 ESLyrics script) to the Dawn
/// <see cref="ILyricsPlugin"/> contract. Only the title/artist search operation
/// (GetResembleLyric2) is ported: it is the only operation that still works without
/// a dead third-party helper. The legacy file-hash operation (GetLyric7) is out of scope.
/// </summary>
[LyricsPlugin("alsong", "Alsong", "1.0.0", "Dawn Player Samples")]
public sealed class AlsongPlugin : ILyricsPlugin
{
    internal const string EndpointUrl = "http://lyrics.alsong.co.kr/alsongwebservice/service1.asmx";
    internal const string SoapAction = "AlsongWebServer/GetResembleLyric2";
    internal const string SourceUrl = "https://www.alsong.co.kr/";

    // Protocol key sent with every request. This is a functional constant shared by
    // every public Alsong client (same value as in alsong-lyrics-searcher), not a secret.
    internal const string EncData = "8582df6473c019a3186a2974aa1e034ae1b2bbb2e7c99575aadc475fcddd997d74bbc1ce3d50b9900282903ee9eb60ae8c5bbf27484441bacb41ecf9128402696641655ff38c2cbbf3c81396034a883af2d82e0545ec32170bddc7c141208e7255e367e5b5ebd81750226856f5405ec3ad7b6f8600c32c2718c4c525bfe34666";

    private const string UserAgent = "gSOAP/2.7";
    private const int MaxCacheEntries = 200;

    // One client per process: socket reuse, and the host may call the plugin concurrently.
    private static readonly HttpClient SharedHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly Regex BrRegex = new Regex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimestampRegex = new Regex(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]", RegexOptions.Compiled);

    private readonly HttpClient _http;

    // The search response already carries the full lyric text, so SearchAsync caches
    // records here and GetAsync serves them without a second round trip. The cache is
    // bounded; on a miss GetAsync re-runs the embedded query (ResultId stays valid
    // across restarts and evictions).
    private readonly ConcurrentDictionary<string, AlsongRecord> _recordsByInfoId = new ConcurrentDictionary<string, AlsongRecord>();
    private readonly Queue<string> _cacheOrder = new Queue<string>();
    private readonly object _cacheLock = new object();

    public AlsongPlugin()
        : this(SharedHttp)
    {
    }

    internal AlsongPlugin(HttpClient http)
    {
        if (http == null) throw new ArgumentNullException(nameof(http));
        _http = http;
    }

    public async Task<IReadOnlyList<LyricsSearchResult>> SearchAsync(LyricsSearchQuery query, CancellationToken cancellationToken)
    {
        if (query == null) throw new ArgumentNullException(nameof(query));

        // Alsong has no album-search parameter: without title and artist there is
        // nothing to send, so report "nothing found" without touching the network.
        var title = query.Title != null ? query.Title.Trim() : "";
        var artist = query.Artist != null ? query.Artist.Trim() : "";
        if (title.Length == 0 && artist.Length == 0)
            return Array.Empty<LyricsSearchResult>();

        var matches = await SearchCoreAsync(title, artist, cancellationToken).ConfigureAwait(false);

        var results = new List<LyricsSearchResult>(matches.Count);
        foreach (var match in matches)
        {
            AddToCache(match);
            results.Add(new LyricsSearchResult
            {
                ResultId = EncodeResultId(match.InfoId, title, artist),
                Title = match.Title,
                Artist = match.Artist,
                Album = match.Album,
                DurationMs = 0,
                IsSynced = match.IsSynced,
                SourceUrl = SourceUrl
            });
        }

        return results;
    }

    public async Task<LyricsContent?> GetAsync(LyricsSearchResult result, CancellationToken cancellationToken)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));
        if (!TryDecodeResultId(result.ResultId, out var infoId, out var title, out var artist))
            return null;

        AlsongRecord? record;
        if (!_recordsByInfoId.TryGetValue(infoId, out record))
        {
            var matches = await SearchCoreAsync(title, artist, cancellationToken).ConfigureAwait(false);
            record = matches.FirstOrDefault(m => m.InfoId == infoId);
            if (record == null)
                return null;
            AddToCache(record);
        }

        return ToContent(record);
    }

    private async Task<IReadOnlyList<AlsongRecord>> SearchCoreAsync(string title, string artist, CancellationToken cancellationToken)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl))
        {
            request.Headers.Add("SOAPAction", SoapAction);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Content = new StringContent(BuildRequestEnvelope(title, artist), Encoding.UTF8, "application/soap+xml");

            using (var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseResponse(xml);
            }
        }
    }

    private void AddToCache(AlsongRecord record)
    {
        if (!_recordsByInfoId.TryAdd(record.InfoId, record))
            return;

        lock (_cacheLock)
        {
            _cacheOrder.Enqueue(record.InfoId);
            while (_cacheOrder.Count > MaxCacheEntries)
            {
                var oldest = _cacheOrder.Dequeue();
                _recordsByInfoId.TryRemove(oldest, out _);
            }
        }
    }

    private static LyricsContent ToContent(AlsongRecord record)
    {
        if (!record.IsSynced)
            return new LyricsContent { PlainText = record.LyricText };

        var plain = StripTimestamps(record.LyricText);
        return new LyricsContent
        {
            SyncedLrc = record.LyricText,
            PlainText = plain.Length == 0 ? null : plain
        };
    }

    internal static string BuildRequestEnvelope(string title, string artist)
    {
        var body = "<ns1:GetResembleLyric2>"
            + "<ns1:encData>" + EncData + "</ns1:encData>"
            + "<ns1:stQuery>"
            + "<ns1:strTitle>" + EscapeXml(title) + "</ns1:strTitle>"
            + "<ns1:strArtistName>" + EscapeXml(artist) + "</ns1:strArtistName>"
            + "<ns1:nCurPage>0</ns1:nCurPage>"
            + "</ns1:stQuery></ns1:GetResembleLyric2>";

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<SOAP-ENV:Envelope xmlns:SOAP-ENV=\"http://www.w3.org/2003/05/soap-envelope\" "
            + "xmlns:SOAP-ENC=\"http://www.w3.org/2003/05/soap-encoding\" "
            + "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" "
            + "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" "
            + "xmlns:ns2=\"ALSongWebServer/Service1Soap\" "
            + "xmlns:ns1=\"ALSongWebServer\" "
            + "xmlns:ns3=\"ALSongWebServer/Service1Soap12\">"
            + "<SOAP-ENV:Body>" + body + "</SOAP-ENV:Body></SOAP-ENV:Envelope>";
    }

    internal static string EscapeXml(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    internal static IReadOnlyList<AlsongRecord> ParseResponse(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var records = new List<AlsongRecord>();

        foreach (var element in doc.Descendants())
        {
            if (!string.Equals(element.Name.LocalName, "ST_GET_RESEMBLELYRIC2_RETURN", StringComparison.Ordinal))
                continue;

            var infoId = ChildText(element, "strInfoID");
            if (infoId.Length == 0)
                continue;

            var text = CleanLyricText(ChildText(element, "strLyric"));
            if (text.Length == 0)
                continue;

            records.Add(new AlsongRecord(
                infoId,
                NullIfEmpty(ChildText(element, "strTitle")),
                NullIfEmpty(ChildText(element, "strArtistName")),
                NullIfEmpty(ChildText(element, "strAlbumName")),
                text,
                TimestampRegex.IsMatch(text)));
        }

        return records;
    }

    internal static string CleanLyricText(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";

        // The server HTML-escapes the lyric (&lt;br&gt; separators), so decode first
        // and then split on real <br> in all its spellings.
        var decoded = WebUtility.HtmlDecode(raw).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = BrRegex.Split(decoded);
        var builder = new StringBuilder(decoded.Length);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(trimmed);
        }

        return builder.ToString();
    }

    internal static string StripTimestamps(string syncedText)
    {
        if (string.IsNullOrEmpty(syncedText))
            return "";

        var builder = new StringBuilder(syncedText.Length);
        foreach (var line in syncedText.Split('\n'))
        {
            var stripped = TimestampRegex.Replace(line, "").Trim();
            if (stripped.Length == 0)
                continue;
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(stripped);
        }

        return builder.ToString();
    }

    internal static string EncodeResultId(string infoId, string title, string artist)
    {
        var payload = infoId + "\n" + SanitizeField(title) + "\n" + SanitizeField(artist);
        return ToBase64Url(Encoding.UTF8.GetBytes(payload));
    }

    internal static bool TryDecodeResultId(string resultId, out string infoId, out string title, out string artist)
    {
        infoId = "";
        title = "";
        artist = "";
        if (string.IsNullOrEmpty(resultId))
            return false;

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(FromBase64Url(resultId));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = payload.Split('\n');
        if (parts.Length != 3 || parts[0].Length == 0)
            return false;

        infoId = parts[0];
        title = parts[1];
        artist = parts[2];
        return true;
    }

    private static string SanitizeField(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value.Replace("\r", " ").Replace("\n", " ");
    }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }

        return Convert.FromBase64String(base64);
    }

    private static string ChildText(XElement parent, string localName)
    {
        foreach (var child in parent.Elements())
        {
            if (string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal))
                return child.Value != null ? child.Value.Trim() : "";
        }

        return "";
    }

    private static string? NullIfEmpty(string value)
    {
        return value.Length == 0 ? null : value;
    }
}
