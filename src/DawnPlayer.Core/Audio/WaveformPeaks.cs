using System.Collections.Concurrent;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Peak envelope of a track: one max-abs float per bucket (0..1 nominal). Results are cached in
/// memory keyed by (path, range, file mtime) — a rescan of the same file replaces the entry, and
/// the cache is trimmed oldest-first when it overflows. Currently without a UI surface (the
/// bottom bar proved too small to show a whole-track envelope legibly); kept for a future
/// large-canvas view such as a full-screen Now Playing page.
/// </summary>
public static class WaveformPeaks
{
    private const int DefaultBuckets = 480;

    private sealed record CacheEntry(long MtimeTicks, float[] Peaks, long LastUsedTicks);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCacheEntries = 48;

    /// <summary>
    /// Returns the peak envelope for a track path (cue fragments decode only their range), or
    /// null when the file cannot be opened/decoded. Blocking — call off the UI thread.
    /// </summary>
    public static float[]? GetOrScan(string path, int buckets = DefaultBuckets)
    {
        if (string.IsNullOrEmpty(path) || buckets < 16) return null;

        var physical = Util.AppPaths.PhysicalPath(path);
        long mtime;
        try
        {
            mtime = File.GetLastWriteTimeUtc(physical).Ticks;
        }
        catch (Exception ex)
        {
            Log.Debug($"[waveform] mtime probe failed for '{physical}': {ex.Message}");
            return null;
        }

        var key = path;
        if (Cache.TryGetValue(key, out var cached) && cached.MtimeTicks == mtime)
        {
            Cache[key] = cached with { LastUsedTicks = Environment.TickCount64 };
            return cached.Peaks;
        }

        float[]? peaks = null;
        try
        {
            peaks = Scan(path, buckets);
        }
        catch (Exception ex)
        {
            // Undecodable file: no waveform, playback itself will surface the real error.
            Log.Debug($"[waveform] scan failed for '{path}': {ex.Message}");
        }

        if (peaks != null)
        {
            Cache[key] = new CacheEntry(mtime, peaks, Environment.TickCount64);
            Trim();
        }
        else
        {
            Cache.TryRemove(key, out _);
        }
        return peaks;
    }

    /// <summary>Drops the cached envelope for a path (tag edits etc. invalidate nothing here
    /// because the mtime key already re-scans, but callers may free memory explicitly).</summary>
    public static void Invalidate(string path) => Cache.TryRemove(path, out _);

    private static float[]? Scan(string path, int buckets)
    {
        // Open the PHYSICAL file: TotalTime here must be the parent's duration. Opening the
        // fragment path wraps it in a CueTrackReader whose TotalTime is the range length —
        // feeding that back as the file length degenerated every second cue track of an album
        // image into a zero-frame scan.
        var physical = Util.AppPaths.PhysicalPath(path);
        using var reader = AudioFileReaderFactory.Open(physical);

        long startMs = 0;
        long endMs = (long)reader.TotalTime.TotalMilliseconds;
        if (Util.AppPaths.TryDecodeCuePath(path, out _, out var fragStart, out var fragEnd))
        {
            startMs = fragStart;
            endMs = fragEnd > fragStart ? Math.Min(fragEnd, endMs) : endMs;
            if (endMs <= startMs) return null;
        }

        var fmt = reader.SourceFormat;
        double startSec = startMs / 1000.0;
        double endSec = endMs / 1000.0;
        long totalFrames = (long)((endSec - startSec) * fmt.SampleRate);
        if (totalFrames <= 0) return null;

        reader.CurrentTime = TimeSpan.FromMilliseconds(startMs);

        var peaks = new float[buckets];
        double framesPerBucket = totalFrames / (double)buckets;

        var buffer = new float[fmt.SampleRate * fmt.Channels]; // ~1 s slices
        long framesSeen = 0;
        int read;
        while (framesSeen < totalFrames && (read = reader.Samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            int frames = read / fmt.Channels;
            for (int f = 0; f < frames && framesSeen < totalFrames; f++, framesSeen++)
            {
                int frameBucket = (int)(framesSeen / framesPerBucket);
                if (frameBucket >= buckets) frameBucket = buckets - 1;
                int baseIdx = f * fmt.Channels;
                float max = 0;
                for (int c = 0; c < fmt.Channels; c++)
                {
                    float a = Math.Abs(buffer[baseIdx + c]);
                    if (a > max) max = a;
                }
                if (max > peaks[frameBucket]) peaks[frameBucket] = max;
            }
        }

        // A file shorter than its tags claim just leaves the tail buckets at 0.
        return peaks;
    }

    private static void Trim()
    {
        if (Cache.Count <= MaxCacheEntries) return;

        // Oldest-use eviction. Ordering the whole dictionary per trim is fine at this size.
        foreach (var key in Cache.OrderBy(kv => kv.Value.LastUsedTicks)
                     .Take(Cache.Count - MaxCacheEntries)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            Cache.TryRemove(key, out _);
        }
    }
}
