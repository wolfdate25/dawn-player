using System.Net;
using DawnPlayer.Core.Network.Dlna;
using Xunit;

namespace DawnPlayer.Tests.Network.Dlna;

/// <summary>
/// The art cache is best-effort infrastructure with hard budget rules: URL-addressed reuse,
/// oldest-first eviction, and every failure mode resolving to null instead of an exception.
/// </summary>
public sealed class DlnaArtCacheTests : IDisposable
{
    private readonly string _dir;
    private readonly FakeHandler _handler = new();
    private readonly DlnaArtCache _cache;

    public DlnaArtCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DawnPlayerArtCacheTests_" + Guid.NewGuid().ToString("N"));
        _cache = new DlnaArtCache(new HttpClient(_handler), _dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] Jpeg => [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];

    [Fact]
    public async Task DownloadsOnce_ThenServesFromDisk()
    {
        _handler.QueueRespond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) });

        var first = await _cache.GetOrDownloadAsync(new Uri("http://art.example/cover.jpg"));
        var second = await _cache.GetOrDownloadAsync(new Uri("http://art.example/cover.jpg"));

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, _handler.RequestCount); // the second call never hit the network
        Assert.True(File.Exists(first));
    }

    [Fact]
    public async Task NonImageBody_YieldsNull_AndNothingIsWritten()
    {
        _handler.QueueRespond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("not an image"u8.ToArray()) });

        var path = await _cache.GetOrDownloadAsync(new Uri("http://art.example/cover.png"));

        Assert.Null(path);
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : []);
    }

    [Fact]
    public async Task ServerError_YieldsNull()
    {
        _handler.QueueRespond(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await _cache.GetOrDownloadAsync(new Uri("http://art.example/x.jpg")));
    }

    [Fact]
    public async Task UnknownExtension_DefaultsToJpgFileName()
    {
        _handler.QueueRespond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) });

        var path = await _cache.GetOrDownloadAsync(new Uri("http://art.example/getCover?id=7"));

        Assert.NotNull(path);
        Assert.EndsWith(".jpg", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BudgetCap_DeletesOldestDlnaFilesFirst()
    {
        // Pre-seed the cache beyond the budget with an "old" file and a "recent" file.
        Directory.CreateDirectory(_dir);
        var oldFile = Path.Combine(_dir, "dlna-old.jpg");
        var newFile = Path.Combine(_dir, "dlna-new.jpg");
        var big = new byte[DlnaArtCache.MaxTotalBytes / 2 + 1024];
        big[0] = 0xFF; big[1] = 0xD8;
        File.WriteAllBytes(oldFile, big);
        File.WriteAllBytes(newFile, big);
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow);

        _handler.QueueRespond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) });

        var fresh = await _cache.GetOrDownloadAsync(new Uri("http://art.example/fresh.jpg"));

        // The sweep keeps total under the cap: the oldest seeded file is gone, the recent survives.
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
        Assert.True(File.Exists(fresh));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = [];

        public int RequestCount { get; private set; }

        public void QueueRespond(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responders.Enqueue(responder);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var responder = _responders.Count > 0 ? _responders.Dequeue() : null;
            return Task.FromResult(responder != null
                ? responder(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
