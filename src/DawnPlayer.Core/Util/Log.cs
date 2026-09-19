namespace DawnPlayer.Core.Util;

/// <summary>Severity of a log entry. Ordered: Trace &lt; Debug &lt; Info &lt; Warning &lt; Error.</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
}

/// <summary>
/// Receiver for log entries. Implementations must be thread-safe and must never throw from
/// <see cref="Write"/> — the facade swallows failures, so a throwing sink only loses that entry.
/// Sinks attached while audio plays get called from any thread except the render thread;
/// implementations that do I/O should queue and write on their own thread
/// (see <see cref="RollingFileLogSink"/>).
/// </summary>
public interface ILogSink : IDisposable
{
    /// <summary>Entries below this level are filtered out before reaching the sink.</summary>
    LogLevel MinimumLevel { get; }

    /// <summary>Record one entry. Called concurrently; must not block or throw.</summary>
    void Write(LogLevel level, string message);
}

/// <summary>
/// Process-wide logging facade. Core code logs through this static class; the host injects a
/// sink at startup (<see cref="SetSink"/>, tests use an in-memory sink). Without a sink every
/// call is a volatile read and a branch — safe to leave in hot paths defensively, though the
/// audio render thread should still log only from marshaled-off handoff points.
/// </summary>
public static class Log
{
    private static ILogSink? _sink;

    /// <summary>Current sink, or null when logging is disabled. Reads are lock-free.</summary>
    public static ILogSink? Sink => Volatile.Read(ref _sink);

    /// <summary>
    /// Installs a sink, disposing the previous one (which flushes buffered entries).
    /// Passing null disables logging.
    /// </summary>
    public static void SetSink(ILogSink? sink)
    {
        var old = Interlocked.Exchange(ref _sink, sink);
        if (old != null)
        {
            try { old.Dispose(); } catch { }
        }
    }

    /// <summary>Disposes the current sink, flushing any buffered entries. Call on shutdown
    /// before the process exits, or tail entries within the flush window are lost.</summary>
    public static void Shutdown()
    {
        SetSink(null);
    }

    public static bool IsEnabled(LogLevel level)
    {
        var sink = Sink;
        return sink != null && level >= sink.MinimumLevel;
    }

    public static void Trace(string message) => Write(LogLevel.Trace, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>Central write. Never throws — a broken sink must not take playback down.</summary>
    public static void Write(LogLevel level, string message)
    {
        var sink = Sink;
        if (sink == null || level < sink.MinimumLevel) return;
        try { sink.Write(level, message); } catch { }
    }
}
