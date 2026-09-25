using System.Net;
using System.Text;
using AlsongLyricsPlugin;
using DawnPlayer.Plugins;

namespace DawnPlayer.Tests.Lyrics.Online;

/// <summary>Alsong lyrics plugin tests. All fixtures are synthetic; no test touches the network.</summary>
public class AlsongPluginTests
{
    private const string TwoResultEnvelope =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\">"
        + "<soap:Body><GetResembleLyric2Response xmlns=\"ALSongWebServer\"><GetResembleLyric2Result>"
        + "<ST_GET_RESEMBLELYRIC2_RETURN>"
        + "<strInfoID>1001</strInfoID>"
        + "<strTitle>테스트 노래</strTitle><strArtistName>테스트 가수</strArtistName><strAlbumName>테스트 앨범</strAlbumName>"
        + "<strLyric>[00:01.00]첫 줄&lt;br&gt;[00:05.00]둘째 줄 &amp; 특수문자</strLyric>"
        + "</ST_GET_RESEMBLELYRIC2_RETURN>"
        + "<ST_GET_RESEMBLELYRIC2_RETURN>"
        + "<strInfoID>1002</strInfoID>"
        + "<strTitle>plain song</strTitle><strArtistName>plain artist</strArtistName><strAlbumName></strAlbumName>"
        + "<strLyric>line one&lt;BR/&gt;line two&lt;br /&gt;line three</strLyric>"
        + "</ST_GET_RESEMBLELYRIC2_RETURN>"
        + "</GetResembleLyric2Result></GetResembleLyric2Response></soap:Body></soap:Envelope>";

    private const string EmptyEnvelope =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\">"
        + "<soap:Body><GetResembleLyric2Response xmlns=\"ALSongWebServer\"><GetResembleLyric2Result />"
        + "</GetResembleLyric2Response></soap:Body></soap:Envelope>";

    private sealed class FakeAlsongHandler : HttpMessageHandler
    {
        public string ResponseXml { get; set; } = TwoResultEnvelope;
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Behave like a real handler: observe cancellation instead of ignoring it.
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastRequest = request;
            LastBody = request.Content?.ReadAsStringAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(ResponseXml, Encoding.UTF8, "application/soap+xml")
            });
        }
    }

    private static AlsongPlugin CreatePlugin(FakeAlsongHandler handler) =>
        new AlsongPlugin(new HttpClient(handler));

    private static LyricsSearchQuery Query(string? title, string? artist) =>
        new LyricsSearchQuery { Title = title, Artist = artist };

    [Fact]
    public async Task SearchAsync_ReturnsParsedCandidates()
    {
        var handler = new FakeAlsongHandler();
        var results = await CreatePlugin(handler).SearchAsync(Query("테스트 노래", "테스트 가수"), CancellationToken.None);

        Assert.Equal(2, results.Count);

        Assert.Equal("테스트 노래", results[0].Title);
        Assert.Equal("테스트 가수", results[0].Artist);
        Assert.Equal("테스트 앨범", results[0].Album);
        Assert.True(results[0].IsSynced);
        Assert.Equal(0, results[0].DurationMs);
        Assert.Equal(AlsongPlugin.SourceUrl, results[0].SourceUrl);
        Assert.False(string.IsNullOrEmpty(results[0].ResultId));

        Assert.Equal("plain song", results[1].Title);
        Assert.Null(results[1].Album);
        Assert.False(results[1].IsSynced);
    }

    [Fact]
    public async Task SearchAsync_SendsExpectedSoapRequest()
    {
        var handler = new FakeAlsongHandler();
        await CreatePlugin(handler).SearchAsync(Query("밤편지", "아이유"), CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(AlsongPlugin.EndpointUrl, handler.LastRequest.RequestUri!.ToString());
        Assert.True(handler.LastRequest.Headers.Contains("SOAPAction"));
        Assert.Equal(AlsongPlugin.SoapAction, string.Join("", handler.LastRequest.Headers.GetValues("SOAPAction")));
        Assert.Contains("gSOAP", handler.LastRequest.Headers.UserAgent.ToString());
        Assert.Contains("<ns1:strTitle>밤편지</ns1:strTitle>", handler.LastBody);
        Assert.Contains("<ns1:strArtistName>아이유</ns1:strArtistName>", handler.LastBody);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "\t")]
    public async Task SearchAsync_BlankQuery_ReturnsEmptyWithoutHttp(string? title, string? artist)
    {
        var handler = new FakeAlsongHandler();
        var results = await CreatePlugin(handler).SearchAsync(Query(title, artist), CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SearchAsync_EmptyResult_ReturnsEmpty()
    {
        var handler = new FakeAlsongHandler { ResponseXml = EmptyEnvelope };
        var results = await CreatePlugin(handler).SearchAsync(Query("없는 곡", "없는 가수"), CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ServerError_ThrowsSoHostMovesToNextPlugin()
    {
        var handler = new FakeAlsongHandler { StatusCode = HttpStatusCode.InternalServerError, ResponseXml = "" };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreatePlugin(handler).SearchAsync(Query("제목", "가수"), CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_MalformedXml_Throws()
    {
        var handler = new FakeAlsongHandler { ResponseXml = "<not xml" };
        await Assert.ThrowsAsync<System.Xml.XmlException>(() =>
            CreatePlugin(handler).SearchAsync(Query("제목", "가수"), CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_CancelledToken_PropagatesCancellation()
    {
        var handler = new FakeAlsongHandler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // ThrowsAny: HttpClient surfaces cancellation as TaskCanceledException, which
        // derives from OperationCanceledException — the type the host rethrows on.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreatePlugin(handler).SearchAsync(Query("제목", "가수"), cts.Token));
    }

    [Fact]
    public async Task GetAsync_ServesSyncedLyricsFromSearchCacheWithoutSecondRoundTrip()
    {
        var handler = new FakeAlsongHandler();
        var plugin = CreatePlugin(handler);
        var results = await plugin.SearchAsync(Query("테스트 노래", "테스트 가수"), CancellationToken.None);

        var content = await plugin.GetAsync(results[0], CancellationToken.None);

        Assert.NotNull(content);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("[00:01.00]첫 줄\n[00:05.00]둘째 줄 & 특수문자", content!.SyncedLrc);
        Assert.Equal("첫 줄\n둘째 줄 & 특수문자", content.PlainText);
        Assert.True(content.HasContent);
    }

    [Fact]
    public async Task GetAsync_ServesPlainLyricsWithoutSyncedPart()
    {
        var handler = new FakeAlsongHandler();
        var plugin = CreatePlugin(handler);
        var results = await plugin.SearchAsync(Query("테스트 노래", "테스트 가수"), CancellationToken.None);

        var content = await plugin.GetAsync(results[1], CancellationToken.None);

        Assert.NotNull(content);
        Assert.Null(content!.SyncedLrc);
        Assert.Equal("line one\nline two\nline three", content.PlainText);
    }

    [Fact]
    public async Task GetAsync_CacheMiss_ResearchesAndMatchesByInfoId()
    {
        var handler = new FakeAlsongHandler();
        var seeding = CreatePlugin(handler);
        var results = await seeding.SearchAsync(Query("테스트 노래", "테스트 가수"), CancellationToken.None);

        // A fresh instance has an empty cache: the embedded query must re-find the record.
        var fresh = CreatePlugin(handler);
        var content = await fresh.GetAsync(results[0], CancellationToken.None);

        Assert.NotNull(content);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("첫 줄", content!.SyncedLrc);
    }

    [Fact]
    public async Task GetAsync_UnknownInfoId_ReturnsNull()
    {
        var handler = new FakeAlsongHandler();
        var plugin = CreatePlugin(handler);
        var forged = new LyricsSearchResult
        {
            ResultId = AlsongPlugin.EncodeResultId("9999", "테스트 노래", "테스트 가수"),
            Title = "테스트 노래"
        };

        var content = await plugin.GetAsync(forged, CancellationToken.None);

        Assert.Null(content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!!not-base64!!!")]
    [InlineData("bm8tbmV3bGluZXMtaGVyZQ==")] // valid base64, wrong shape
    public async Task GetAsync_TamperedResultId_ReturnsNull(string resultId)
    {
        var handler = new FakeAlsongHandler();
        var content = await CreatePlugin(handler).GetAsync(
            new LyricsSearchResult { ResultId = resultId }, CancellationToken.None);

        Assert.Null(content);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void BuildRequestEnvelope_EscapesXmlSpecialsAndKeepsKorean()
    {
        var envelope = AlsongPlugin.BuildRequestEnvelope("a&b<c>\"d'e", "아이유");

        Assert.Contains("a&amp;b&lt;c&gt;&quot;d&apos;e", envelope);
        Assert.Contains("아이유", envelope);

        // The escaped envelope must parse back to the original query strings.
        var doc = System.Xml.Linq.XDocument.Parse(envelope);
        var text = doc.ToString();
        Assert.Contains("a&b<c>\"d'e", System.Net.WebUtility.HtmlDecode(text));
    }

    [Theory]
    [InlineData("a&lt;br&gt;b", "a\nb")]
    [InlineData("a<BR>b", "a\nb")]
    [InlineData("a<br/>b", "a\nb")]
    [InlineData("a<br />b", "a\nb")]
    [InlineData("[00:01.00]x&lt;br&gt;&amp; &quot;q&quot;", "[00:01.00]x\n& \"q\"")]
    [InlineData("", "")]
    public void CleanLyricText_SplitsBreaksAndDecodesEntities(string raw, string expected)
    {
        Assert.Equal(expected, AlsongPlugin.CleanLyricText(raw));
    }

    [Fact]
    public void ResultId_RoundTripsQuery()
    {
        var encoded = AlsongPlugin.EncodeResultId("8125935", "밤편지", "아이유");

        Assert.True(AlsongPlugin.TryDecodeResultId(encoded, out var infoId, out var title, out var artist));
        Assert.Equal("8125935", infoId);
        Assert.Equal("밤편지", title);
        Assert.Equal("아이유", artist);
    }
}
