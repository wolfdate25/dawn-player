using System.Text.RegularExpressions;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests.Util;

/// <summary>In-memory sink for tests. Thread-safe because the facade may hand it entries
/// from parallel writers.</summary>
public sealed class MemoryLogSink : ILogSink
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public LogLevel MinimumLevel { get; set; } = LogLevel.Trace;

    public void Write(LogLevel level, string message)
    {
        lock (_gate) _entries.Add((level, message));
    }

    public IReadOnlyList<(LogLevel Level, string Message)> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    public void Dispose() { }
}

/// <summary>
/// The logging facade is the one static every Core subsystem touches, so its failure modes are
/// process-wide: a throwing sink must not take playback down, a swapped sink must never be
/// observed half-installed, and a disabled facade must stay free. These tests pin those
/// contracts. All facade tests live in one class: they share the static sink slot, and xUnit
/// serializes tests within a class but parallelizes across classes.
/// </summary>
public sealed class LogFacadeTests : IDisposable
{
    public void Dispose() => Log.SetSink(null);

    [Fact]
    public void WithoutSink_WritesAreNoOps()
    {
        Log.Info("nobody listens");
        Assert.False(Log.IsEnabled(LogLevel.Error));
    }

    [Fact]
    public void Entries_BelowMinimumLevel_AreFiltered()
    {
        var sink = new MemoryLogSink { MinimumLevel = LogLevel.Warning };
        Log.SetSink(sink);

        Log.Trace("t");
        Log.Debug("d");
        Log.Info("i");
        Log.Warn("w");
        Log.Error("e");

        var entries = sink.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal(LogLevel.Warning, entries[0].Level);
        Assert.Equal("w", entries[0].Message);
        Assert.Equal(LogLevel.Error, entries[1].Level);
    }

    [Fact]
    public void ThrowingSink_IsSwallowed()
    {
        Log.SetSink(new ThrowingSink());
        Log.Error("must not throw");
        Assert.True(true, "reached: the facade swallowed the sink failure");
    }

    private sealed class ThrowingSink : ILogSink
    {
        public LogLevel MinimumLevel => LogLevel.Trace;
        public void Write(LogLevel level, string message) => throw new InvalidOperationException("sink is broken");
        public void Dispose() { }
    }

    [Fact]
    public async Task ConcurrentWrites_DuringSinkSwap_NeverThrowAndNeverTear()
    {
        var a = new MemoryLogSink();
        var b = new MemoryLogSink();
        Log.SetSink(a);

        var writers = Enumerable.Range(0, 4).Select(async w =>
        {
            for (int i = 0; i < 500; i++)
            {
                Log.Info($"w{w}-{i}");
                await Task.Yield();
            }
        });
        var swapper = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                Log.SetSink(i % 2 == 0 ? b : a);
                await Task.Delay(1);
            }
        });

        await Task.WhenAll(writers.Append(swapper));

        // The facade sink is process-wide and xUnit runs test classes in parallel, so entries
        // from unrelated tests can land here too. Scope the assertions to this test's messages:
        // all 2000 must have reached one of the two sinks, intact and correctly leveled (no
        // entry vanishes in a swap — MemoryLogSink.Dispose is a no-op, so a sink swapped out
        // still accepts and records whatever was handed to it mid-swap).
        var mine = a.Snapshot().Concat(b.Snapshot()).Where(e => e.Message.StartsWith("w", StringComparison.Ordinal)).ToList();
        Assert.Equal(2000, mine.Count);
        Assert.All(mine, e =>
        {
            Assert.Equal(LogLevel.Info, e.Level);
            Assert.Matches(@"^w\d+-\d+$", e.Message);
        });
    }

    [Fact]
    public void SetNull_DisablesLogging()
    {
        var sink = new MemoryLogSink();
        Log.SetSink(sink);
        Log.Info("captured");
        Log.SetSink(null);
        Log.Info("dropped");

        var entries = sink.Snapshot();
        Assert.Single(entries);
        Assert.False(Log.IsEnabled(LogLevel.Error));
    }
}

/// <summary>
/// The rolling file sink owns the log file format users are told to open ("자세한 내용:
/// dawnplayer.log"). Pinned here: line shape, the size-based rollover chain, the cap on kept
/// files, flush-based determinism for tests, and that concurrent producers never interleave a
/// line. Driven directly (not through the static facade) so it stays independent of the
/// facade's process-wide sink slot.
/// </summary>
public sealed class RollingFileLogSinkTests : IDisposable
{
    private readonly string _dir;

    public RollingFileLogSinkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DawnLogSinkTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string LogPath => Path.Combine(_dir, "test.log");

    [Fact]
    public void WrittenLines_CarryTimestampAndLevelTag()
    {
        using (var sink = new RollingFileLogSink(LogPath))
        {
            sink.Write(LogLevel.Warning, "disk almost full");
            Assert.True(sink.Flush(TimeSpan.FromSeconds(5)), "flush timed out");
        }

        var lines = File.ReadAllLines(LogPath);
        Assert.Single(lines);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3} \[WARN\] disk almost full$", lines[0]);
    }

    [Fact]
    public void CrossingMaxBytes_RollsToSuffixOne_AndKeepsCap()
    {
        // "HH:mm:ss.fff [INFO] " (20) + "line-0000-" + 20 x's (30) + newline = 51 bytes per
        // line, so a 1 KB cap holds 20 lines and 60 lines roll exactly twice.
        using (var sink = new RollingFileLogSink(LogPath, maxBytes: 1024, maxFiles: 3))
        {
            for (int i = 0; i < 60; i++)
            {
                sink.Write(LogLevel.Info, $"line-{i:D4}-" + new string('x', 20));
            }
            Assert.True(sink.Flush(TimeSpan.FromSeconds(5)), "flush timed out");
        }

        Assert.True(File.Exists(LogPath), "current file exists");
        Assert.True(File.Exists(LogPath + ".1"), "first rollover exists");
        Assert.True(File.Exists(LogPath + ".2"), "second rollover exists");
        Assert.False(File.Exists(LogPath + ".3"), "cap: nothing beyond the two expected rolls");

        // The current file must be the tail, not a stale head: its first line is newer than
        // the last line of .1.
        static int IndexOf(string line) => int.Parse(Regex.Match(line, @"line-(\d{4})-").Groups[1].Value);
        var currentIdx = IndexOf(File.ReadLines(LogPath).First());
        var rolledIdx = IndexOf(File.ReadLines(LogPath + ".1").Last());
        Assert.True(currentIdx > rolledIdx, $"current ({currentIdx}) must follow rolled ({rolledIdx})");
    }

    [Fact]
    public async Task ConcurrentProducers_NeverInterleaveALine()
    {
        using var sink = new RollingFileLogSink(LogPath, maxBytes: 128 * 1024, maxFiles: 2);
        var producers = Enumerable.Range(0, 6).Select(p => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                sink.Write(LogLevel.Debug, $"p{p}-{i:D4}-" + new string('y', 30));
            }
        }));
        await Task.WhenAll(producers);
        Assert.True(sink.Flush(TimeSpan.FromSeconds(10)), "flush timed out");

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(6 * 200, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3} \[DEBUG\] p\d-\d{4}-y{30}$", l));
    }

    [Fact]
    public void DisposeFlushes_QueuedEntries()
    {
        var sink = new RollingFileLogSink(LogPath);
        sink.Write(LogLevel.Error, "last words");
        sink.Dispose();

        Assert.Contains("last words", File.ReadAllText(LogPath));
    }

    [Fact]
    public void UnwritablePath_DisablesInsteadOfThrowingOrSpinning()
    {
        // A path inside a file (not a directory) can never be created: every write fails.
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "occupied");
        var sink = new RollingFileLogSink(Path.Combine(blocker, "sub", "test.log"));

        for (int i = 0; i < 50; i++)
        {
            sink.Write(LogLevel.Info, $"attempt {i}");
        }
        Assert.True(sink.Flush(TimeSpan.FromSeconds(5)), "drained despite failures");
        sink.Dispose();
    }
}
