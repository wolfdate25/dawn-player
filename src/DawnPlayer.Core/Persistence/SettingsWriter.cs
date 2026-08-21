using System;
using System.Threading;

namespace DawnPlayer.Core.Persistence;

/// <summary>
/// Coalesces settings writes so the UI thread never blocks on disk I/O.
/// <para>
/// UI code mutates settings on every volume/zoom slider tick, every splitter drag step and every
/// tab switch. Writing the whole JSON document synchronously on each of those turned a slider drag
/// into dozens of directory-enumerate + write + atomic-move sequences on the UI thread.
/// </para>
/// <para>
/// <see cref="Schedule"/> serializes immediately on the calling thread — cheap, and it captures a
/// consistent snapshot while the caller still owns the object — then defers the file write until
/// the caller stops changing things. Only the newest snapshot is ever written.
/// </para>
/// </summary>
public static class SettingsWriter
{
    private static readonly object Gate = new();
    private static readonly Timer FlushTimer = new(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    private static string? _pendingJson;
    private static string? _pendingPath;

    /// <summary>How long to wait for further changes before writing.</summary>
    public static TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Replaces the destination of scheduled writes. Tests set this so a debounce timer that
    /// fires after a test has finished cannot write over the settings file another test is
    /// asserting on, and so the coalescing behaviour itself can be observed without disk I/O.
    /// </summary>
    public static Action<string>? WriteSink { get; set; }

    /// <summary>True while a snapshot is waiting to be written.</summary>
    public static bool HasPendingWrite
    {
        get { lock (Gate) return _pendingJson != null; }
    }

    /// <summary>
    /// Takes a snapshot of <paramref name="settings"/> now and schedules it to be written shortly.
    /// Repeated calls collapse into a single write of the last snapshot.
    /// </summary>
    public static void Schedule(AppSettings? settings)
    {
        if (settings == null) return;

        string json;
        try
        {
            json = SettingsStore.Serialize(settings);
        }
        catch
        {
            // A concurrent mutation can break serialization; the next Schedule will pick it up.
            return;
        }

        lock (Gate)
        {
            _pendingJson = json;
            // The destination is captured with the snapshot: if the data directory changes
            // between Schedule and Flush (portable-mode switch, test sandbox redirect), the
            // snapshot must still land where it was taken from.
            _pendingPath = Util.AppPaths.SettingsFile;
            FlushTimer.Change(Delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Writes the pending snapshot immediately, if there is one. Call on shutdown so nothing is
    /// lost, and in tests to make scheduling deterministic.
    /// </summary>
    public static void Flush()
    {
        string? json;
        string? path;
        lock (Gate)
        {
            FlushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            json = _pendingJson;
            path = _pendingPath;
            _pendingJson = null;
            _pendingPath = null;
        }

        if (json == null) return;

        var sink = WriteSink;
        if (sink != null) sink(json);
        else SettingsStore.WriteJson(json, path ?? Util.AppPaths.SettingsFile);
    }

    /// <summary>
    /// Snapshots <paramref name="settings"/> and writes it synchronously, discarding anything
    /// already pending. Use for the final save on shutdown.
    /// </summary>
    public static void FlushNow(AppSettings? settings)
    {
        if (settings == null)
        {
            Flush();
            return;
        }

        Schedule(settings);
        Flush();
    }

    /// <summary>Drops the pending snapshot without writing it (tests).</summary>
    public static void DiscardPending()
    {
        lock (Gate)
        {
            FlushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _pendingJson = null;
            _pendingPath = null;
        }
    }
}
