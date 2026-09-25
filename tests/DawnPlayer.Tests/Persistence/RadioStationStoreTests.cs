using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.Persistence;

/// <summary>
/// The radio-station store owns a user file that is edited only through the UI but can still be
/// hand-edited, half-written, or duplicated — these tests pin the failure behaviors the UI relies on.
/// </summary>
public sealed class RadioStationStoreTests : IDisposable
{
    private readonly string _dir;

    public RadioStationStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DawnPlayerStationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort cleanup */ }
    }

    private string StorePath(string name = "stations.json") => Path.Combine(_dir, name);

    private static RadioStation Station(string name, string url, long addedTicks = 1_000) =>
        new(name, url, null, addedTicks, null);

    [Fact]
    public void RoundTrip_SaveThenReopen_PreservesStationsInAddedOrder()
    {
        var path = StorePath();
        var first = new RadioStationStore(path);
        first.AddOrReplace(Station("Jazz", "http://radio.example/jazz", 200));
        first.AddOrReplace(Station("Rock", "http://radio.example/rock", 100));
        first.Save();

        var reopened = new RadioStationStore(path);
        Assert.Equal(2, reopened.Stations.Count);
        // Added order is normalized to the AddedUtcTicks sequence, not the call sequence.
        Assert.Equal("Rock", reopened.Stations[0].Name);
        Assert.Equal("Jazz", reopened.Stations[1].Name);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToBackup()
    {
        var path = StorePath();
        File.WriteAllText(path, "{ not valid json at all");
        File.WriteAllText(path + ".bak",
            "[{ \"Name\": \"Bak\", \"Url\": \"http://radio.example/bak\", \"Genre\": null, \"AddedUtcTicks\": 5, \"LastPlayedUtcTicks\": null }]");

        var store = new RadioStationStore(path);
        var station = Assert.Single(store.Stations);
        Assert.Equal("Bak", station.Name);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyList()
    {
        var store = new RadioStationStore(StorePath("never-written.json"));
        Assert.Empty(store.Stations);
    }

    [Fact]
    public void Normalize_DropsInvalidUrls_BlankNamesBecomeUrl_LastDuplicateWins()
    {
        var stations = new List<RadioStation>
        {
            Station("", "http://radio.example/a"),
            Station("Bad", "ftp://not-supported"),
            null!,
            Station("A-old", "http://radio.example/a", 50),
            Station("Keep", "http://radio.example/keep"),
        };

        var normalized = RadioStationStore.Normalize(stations);

        Assert.Equal(2, normalized.Count);
        Assert.All(normalized, s => Assert.StartsWith("http://", s.Url));
        // The later duplicate wins the name; it sorts by its own added ticks, not the slot it replaced.
        Assert.Equal("A-old", normalized[0].Name);
        Assert.Equal("Keep", normalized[1].Name);
    }

    [Fact]
    public void Normalize_ClampsToNewestMaxStations()
    {
        var stations = Enumerable.Range(0, RadioStationStore.MaxStations + 40)
            .Select(i => Station($"S{i}", $"http://radio.example/{i}", addedTicks: i))
            .ToList();

        var normalized = RadioStationStore.Normalize(stations);

        Assert.Equal(RadioStationStore.MaxStations, normalized.Count);
        // 540 entries minus the 500 cap: the oldest 40 are dropped.
        Assert.Equal("S40", normalized[0].Name);
        Assert.Equal($"S{RadioStationStore.MaxStations + 39}", normalized[^1].Name);
    }

    [Fact]
    public void AddOrReplace_SameUrl_ReplacesAndKeepsOriginalAddedTicks()
    {
        var store = new RadioStationStore(StorePath());
        store.AddOrReplace(Station("Old name", "http://radio.example/x", 111));
        store.AddOrReplace(Station("Renamed", "http://radio.example/X", 999));

        var station = Assert.Single(store.Stations);
        Assert.Equal("Renamed", station.Name);
        Assert.Equal(111, station.AddedUtcTicks);
    }

    [Fact]
    public void AddOrReplace_InvalidUrlOrFullCap_ReturnsFalse()
    {
        var store = new RadioStationStore(StorePath());
        Assert.False(store.AddOrReplace(Station("X", "ftp://nope")));
        Assert.False(store.AddOrReplace(null!));

        for (int i = 0; i < RadioStationStore.MaxStations; i++)
            Assert.True(store.AddOrReplace(Station($"S{i}", $"http://radio.example/{i}")));
        Assert.False(store.AddOrReplace(Station("Overflow", "http://radio.example/overflow")));
    }

    [Fact]
    public void Remove_ExistingUrl_RemovesIt_MissingUrl_ReturnsFalse()
    {
        var store = new RadioStationStore(StorePath());
        store.AddOrReplace(Station("A", "http://radio.example/a"));

        Assert.False(store.Remove("http://radio.example/missing"));
        Assert.True(store.Remove("HTTP://RADIO.EXAMPLE/A"));
        Assert.Empty(store.Stations);
    }

    [Fact]
    public async Task ConcurrentAddAndSave_ConsistentResultOnReload()
    {
        var path = StorePath();
        var store = new RadioStationStore(path);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            await Task.Yield();
            for (int j = 0; j < 10; j++)
            {
                store.AddOrReplace(Station($"S{i}-{j}", $"http://radio.example/{i}-{j}"));
                if (j % 3 == 0) store.Save();
            }
        }));
        store.Save();

        var reopened = new RadioStationStore(path);
        Assert.Equal(160, reopened.Stations.Count);
    }
}
