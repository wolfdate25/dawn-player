using System.Collections.Concurrent;
using System.Text;

namespace DawnPlayer.Core.Util;

/// <summary>
/// File log sink with size-based rolling (dawnplayer.log → dawnplayer.1.log → …). Entries are
/// enqueued lock-free and written by a single background thread, so <see cref="Write"/> is safe
/// to call from any non-render thread and never blocks the caller on disk I/O.
///
/// Invariants:
/// - Only the writer thread touches the file, so rolling (delete oldest, shift, rename) needs
///   no additional locking.
/// - The sink never throws: after repeated write failures it disables itself silently (there
///   is nowhere left to report a broken log to).
/// - <see cref="Flush"/> returns only after every enqueued entry is on disk (or timed out), so
///   shutdown loses nothing and tests can synchronize deterministically.
/// </summary>
public sealed class RollingFileLogSink : ILogSink
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly Thread _writer;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly object _flushGate = new();

    private long _enqueued;
    private long _written;
    private long _currentSize;
    private int _consecutiveFailures;
    private bool _disabled;

    public LogLevel MinimumLevel { get; }

    public RollingFileLogSink(string path, LogLevel minimumLevel = LogLevel.Debug,
        long maxBytes = 5 * 1024 * 1024, int maxFiles = 3)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Log path is empty.", nameof(path));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);

        _path = path;
        MinimumLevel = minimumLevel;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;

        try { _currentSize = File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { _disabled = true; }

        _writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "DawnPlayer.LogWriter",
        };
        _writer.Start();
    }

    public void Write(LogLevel level, string message)
    {
        if (_disabled || _queue.IsAddingCompleted) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{LevelTag(level)}] {message}";
        try
        {
            _queue.Add(line);
            Interlocked.Increment(ref _enqueued);
        }
        catch (InvalidOperationException) { /* completed between the check and the add */ }
    }

    /// <summary>Blocks until all entries enqueued so far are on disk. Returns false on timeout.</summary>
    public bool Flush(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        var target = Volatile.Read(ref _enqueued);
        while (Volatile.Read(ref _written) < target)
        {
            if (Environment.TickCount64 >= deadline) return false;
            lock (_flushGate) { Monitor.Wait(_flushGate, 10); }
        }
        return true;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(3));
        _queue.Dispose();
    }

    private void WriterLoop()
    {
        var batch = new List<string>(64);
        while (!_queue.IsCompleted)
        {
            batch.Clear();
            try
            {
                batch.Add(_queue.Take());
            }
            catch (InvalidOperationException)
            {
                break; // completed and drained
            }
            while (batch.Count < 256 && _queue.TryTake(out var more)) batch.Add(more);

            AppendBatch(batch);
            Interlocked.Add(ref _written, batch.Count);
            lock (_flushGate) { Monitor.PulseAll(_flushGate); }
        }
    }

    private void AppendBatch(List<string> batch)
    {
        if (_disabled) return;
        try
        {
            // The batch is flushed in cap-respecting segments: a burst (startup, a scan) must
            // still roll exactly like a slow trickle would, not land as one oversized file.
            var segment = new StringBuilder();
            foreach (var line in batch)
            {
                // Count the pending segment against the cap too, or a whole batch that has not
                // been flushed yet would be treated as size zero and never trigger a roll.
                long effective = _currentSize + segment.Length;
                if (effective > 0 && effective + line.Length + 1 > _maxBytes)
                {
                    FlushSegment(segment);
                    Roll();
                }
                segment.Append(line).Append('\n');
            }
            FlushSegment(segment);
            _consecutiveFailures = 0;
        }
        catch
        {
            // Full disk, deleted directory, locked file: retry a few times in case it is
            // transient, then stop touching the disk for the rest of the session.
            if (Interlocked.Increment(ref _consecutiveFailures) >= 10) _disabled = true;
        }
    }

    private void FlushSegment(StringBuilder segment)
    {
        if (segment.Length == 0) return;
        File.AppendAllText(_path, segment.ToString());
        _currentSize += segment.Length;
        segment.Clear();
    }

    private void Roll()
    {
        try
        {
            for (var i = _maxFiles - 1; i >= 1; i--)
            {
                var from = $"{_path}.{i}";
                var to = $"{_path}.{i + 1}";
                if (File.Exists(to)) File.Delete(to);
                if (File.Exists(from)) File.Move(from, to);
            }
            File.Move(_path, $"{_path}.1");
            _currentSize = 0;
        }
        catch
        {
            // A stuck rolled file (open elsewhere) must not lose the live log: keep appending
            // to the current file even though it now exceeds the cap.
            _currentSize = 0;
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => level.ToString(),
    };
}
