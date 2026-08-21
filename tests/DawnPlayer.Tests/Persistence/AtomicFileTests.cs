using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.Tests.Persistence;

/// <summary>
/// Covers the durability guarantees callers depend on: the target is either the old content or the
/// new content but never a partial write, and one previous generation survives as a .bak so a
/// reader that hits unparseable content has something to fall back to.
/// </summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _dir;

    public AtomicFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DawnPlayer_AtomicFileTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public void WriteAllText_NewFile_CreatesTargetWithoutBackup()
    {
        var target = PathFor("settings.json");

        AtomicFile.WriteAllText(target, "{\"a\":1}");

        Assert.Equal("{\"a\":1}", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".bak"));
    }

    [Fact]
    public void WriteAllText_ExistingFile_KeepsPreviousGenerationAsBak()
    {
        var target = PathFor("settings.json");
        AtomicFile.WriteAllText(target, "first");

        AtomicFile.WriteAllText(target, "second");

        Assert.Equal("second", File.ReadAllText(target));
        Assert.Equal("first", File.ReadAllText(target + ".bak"));
    }

    [Fact]
    public void WriteAllText_ThirdWrite_BakHoldsOnlyTheImmediatelyPreviousGeneration()
    {
        var target = PathFor("settings.json");
        AtomicFile.WriteAllText(target, "first");
        AtomicFile.WriteAllText(target, "second");

        AtomicFile.WriteAllText(target, "third");

        Assert.Equal("third", File.ReadAllText(target));
        Assert.Equal("second", File.ReadAllText(target + ".bak"));
    }

    [Fact]
    public void WriteAllText_KeepBackupFalse_DoesNotCreateBak()
    {
        var target = PathFor("cache.bin");
        AtomicFile.WriteAllText(target, "first", keepBackup: false);

        AtomicFile.WriteAllText(target, "second", keepBackup: false);

        Assert.Equal("second", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".bak"));
    }

    [Fact]
    public void WriteAllText_UsesUtf8WithoutBom()
    {
        var target = PathFor("korean.json");

        AtomicFile.WriteAllText(target, "한글");

        var bytes = File.ReadAllBytes(target);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("한글", File.ReadAllText(target));
    }

    [Fact]
    public void Write_OnSuccess_LeavesNoTempFileBehind()
    {
        var target = PathFor("settings.json");

        AtomicFile.WriteAllText(target, "content");

        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp.*"));
    }

    [Fact]
    public void Write_WhenWriterThrows_LeavesTargetUntouchedAndCleansUpTemp()
    {
        var target = PathFor("settings.json");
        AtomicFile.WriteAllText(target, "original");

        Assert.Throws<InvalidOperationException>(() =>
            AtomicFile.Write(target, _ => throw new InvalidOperationException("writer failed")));

        Assert.Equal("original", File.ReadAllText(target));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp.*"));
    }

    [Fact]
    public void Write_IntoMissingDirectory_CreatesItFirst()
    {
        var target = Path.Combine(_dir, "nested", "deeper", "settings.json");

        AtomicFile.WriteAllText(target, "content");

        Assert.Equal("content", File.ReadAllText(target));
    }

    [Fact]
    public void WriteAllBytes_RoundTripsExactly()
    {
        var target = PathFor("cover.jpg");
        var payload = Enumerable.Range(0, 512).Select(i => (byte)(i % 256)).ToArray();

        AtomicFile.WriteAllBytes(target, payload);

        Assert.Equal(payload, File.ReadAllBytes(target));
    }

    [Fact]
    public void CleanupStaleTempFiles_RemovesOnlyThisTargetsLeftovers()
    {
        var target = PathFor("settings.json");
        File.WriteAllText(target, "live");
        File.WriteAllText(target + ".tmp.abc123", "crashed");
        File.WriteAllText(PathFor("other.json.tmp.def456"), "someone else's");

        AtomicFile.CleanupStaleTempFiles(target);

        Assert.False(File.Exists(target + ".tmp.abc123"));
        Assert.True(File.Exists(PathFor("other.json.tmp.def456")));
        Assert.Equal("live", File.ReadAllText(target));
    }

    [Fact]
    public async Task Write_ConcurrentWriters_TargetAlwaysHoldsOneCompleteVersion()
    {
        var target = PathFor("settings.json");
        AtomicFile.WriteAllText(target, "v0");

        var writers = Enumerable.Range(0, 8).Select(id => Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                AtomicFile.WriteAllText(target, $"writer{id}-iteration{i}");
            }
        }));

        var reader = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    var text = File.ReadAllText(target);
                    // Any observed content must be a whole value, never a truncated one.
                    Assert.True(text == "v0" || (text.StartsWith("writer", StringComparison.Ordinal) &&
                                                 text.Contains("-iteration", StringComparison.Ordinal)),
                        $"Reader observed a partial write: '{text}'");
                }
                catch (IOException)
                {
                    // The rename briefly denies sharing; retrying is the caller's business.
                }
            }
        });

        await Task.WhenAll(writers.Append(reader));

        Assert.StartsWith("writer", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp.*"));
    }
}
