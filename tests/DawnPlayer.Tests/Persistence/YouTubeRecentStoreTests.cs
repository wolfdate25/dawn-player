using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.Persistence;

/// <summary>
/// The YouTube recent grid's store: dedup by page URL with move-to-front (the grid reads
/// newest-first), the 20-entry cap, removal, and the atomic-file persistence recipe it shares
/// with the radio-station store.
/// </summary>
public sealed class YouTubeRecentStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _filePath;

    public YouTubeRecentStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DawnPlayerYtRecentTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _filePath = Path.Combine(_dir, "youtube-recent.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static YouTubeRecentEntry Entry(string url, string title = "T", long durationMs = 1000) =>
        new(url, title, "Uploader", durationMs, null, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    [Fact]
    public void Add_MovesDuplicateUrlToFront_WithFreshMetadata()
    {
        var store = new YouTubeRecentStore(_filePath);
        store.Add(Entry("https://www.youtube.com/watch?v=a", "First"));
        store.Add(Entry("https://www.youtube.com/watch?v=b", "Second"));
        store.Add(Entry("https://www.youtube.com/watch?v=a", "First-renamed"));

        var entries = store.Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal("https://www.youtube.com/watch?v=a", entries[0].PageUrl);
        Assert.Equal("First-renamed", entries[0].Title);
    }

    [Fact]
    public void Add_BeyondCap_EvictsTheOldest()
    {
        var store = new YouTubeRecentStore(_filePath);
        for (var i = 0; i < YouTubeRecentStore.MaxRecent + 5; i++)
        {
            store.Add(Entry($"https://www.youtube.com/watch?v=v{i}"));
        }

        var entries = store.Entries;
        Assert.Equal(YouTubeRecentStore.MaxRecent, entries.Count);
        Assert.Equal($"https://www.youtube.com/watch?v=v{YouTubeRecentStore.MaxRecent + 4}", entries[0].PageUrl);
        Assert.DoesNotContain(entries, e => e.PageUrl.EndsWith("v0", StringComparison.Ordinal));
    }

    [Fact]
    public void Remove_ByExactUrl_LeavesTheRest()
    {
        var store = new YouTubeRecentStore(_filePath);
        store.Add(Entry("https://www.youtube.com/watch?v=a"));
        store.Add(Entry("https://www.youtube.com/watch?v=b"));

        Assert.True(store.Remove("https://www.youtube.com/watch?v=a"));

        var entry = Assert.Single(store.Entries);
        Assert.Equal("https://www.youtube.com/watch?v=b", entry.PageUrl);
        Assert.False(store.Remove("https://www.youtube.com/watch?v=a"));
    }

    [Fact]
    public void SaveAndReload_PreservesEntries()
    {
        var store = new YouTubeRecentStore(_filePath);
        store.Add(Entry("https://www.youtube.com/watch?v=keep", "Keep", 213_000));
        store.Save();

        var reloaded = new YouTubeRecentStore(_filePath);

        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal("https://www.youtube.com/watch?v=keep", entry.PageUrl);
        Assert.Equal("Keep", entry.Title);
        Assert.Equal(213_000, entry.DurationMs);
    }

    [Fact]
    public void CorruptedFile_FallsBackToEmptyInsteadOfThrowing()
    {
        File.WriteAllText(_filePath, "{ this is not valid json");

        var store = new YouTubeRecentStore(_filePath);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public void MissingFile_LoadsEmpty()
    {
        var store = new YouTubeRecentStore(Path.Combine(_dir, "does-not-exist.json"));
        Assert.Empty(store.Entries);
    }
}
