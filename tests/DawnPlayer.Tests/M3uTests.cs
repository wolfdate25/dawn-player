using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests;

public class M3uTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dawnplayer-tests-" + Guid.NewGuid().ToString("N"));

    public M3uTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void WritesAndReadsRoundTrip()
    {
        var file = Path.Combine(_dir, "pl.m3u8");
        var items = new List<PlaylistItem>
        {
            new(new Track { Path = @"C:\music\a.mp3", Title = "A", Artist = "X", DurationMs = 1000 }),
            new(new Track { Path = @"C:\music\b.flac", Title = "B", Artist = "Y", DurationMs = 61000 }),
        };
        M3u.Write(file, items, "테스트");

        var read = M3u.Read(file);
        Assert.Equal(2, read.Count);
        Assert.Equal(@"C:\music\a.mp3", read[0].Path);
        Assert.Equal(1.0, read[0].DurationSeconds);
        Assert.Equal("X - A", read[0].Title);
        Assert.Equal(61, read[1].DurationSeconds!.Value, 1);
    }

    [Fact]
    public void ResolvesRelativePaths()
    {
        var m3u = Path.Combine(_dir, "rel.m3u8");
        File.WriteAllText(m3u, "#EXTM3U\n#EXTINF:10,Song\nsub/song.mp3\n");
        var read = M3u.Read(m3u);
        Assert.Single(read);
        Assert.True(Path.IsPathRooted(read[0].Path));
        Assert.EndsWith("sub/song.mp3", read[0].Path.Replace('\\', '/'));
    }

    [Fact]
    public void SkipsCommentsAndBlankLines()
    {
        var m3u = Path.Combine(_dir, "comments.m3u8");
        File.WriteAllText(m3u, "#EXTM3U\n#PLAYLIST:name\n\n#some comment\nC:/x/y.mp3\n");
        var read = M3u.Read(m3u);
        Assert.Single(read);
    }
}
