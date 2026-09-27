using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Persistence;

/// <summary>One recently opened YouTube video: the canonical page URL (playlists persist this,
/// never the expiring media URL) plus the display metadata captured from the -J resolve.</summary>
public sealed record YouTubeRecentEntry(
    string PageUrl,
    string Title,
    string Uploader,
    long DurationMs,
    string? ThumbnailUrl,
    long LastPlayedUtcTicks);

/// <summary>
/// Loads and saves the YouTube section's recent-items list, following the radio-station store's
/// resilience recipe (atomic write with backup, .bak fallback on load, normalize hardening).
/// <see cref="Add"/> deduplicates by page URL and moves the entry to the front, so the grid reads
/// newest-first; the list is capped at <see cref="MaxRecent"/> entries.
/// </summary>
public sealed class YouTubeRecentStore
{
    public const int MaxRecent = 20;

    private readonly object _gate = new();
    private readonly string _filePath;
    private List<YouTubeRecentEntry> _entries;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public YouTubeRecentStore() : this(Util.AppPaths.YouTubeRecentFile)
    {
    }

    /// <summary>The path is a parameter so tests never touch the process-wide base directory.</summary>
    public YouTubeRecentStore(string filePath)
    {
        _filePath = filePath;
        _entries = Load(filePath);
    }

    /// <summary>Snapshot of the current entries, newest first. Never null.</summary>
    public IReadOnlyList<YouTubeRecentEntry> Entries
    {
        get { lock (_gate) { return _entries.ToArray(); } }
    }

    /// <summary>
    /// Records an entry: a URL already in the list is refreshed (new metadata, moved to front),
    /// otherwise it is inserted at the front and the tail is trimmed to the cap. The caller saves.
    /// </summary>
    public void Add(YouTubeRecentEntry entry)
    {
        if (entry is null || !IsValidUrl(entry.PageUrl)) return;

        lock (_gate)
        {
            _entries.RemoveAll(e => string.Equals(e.PageUrl, entry.PageUrl, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, entry);
            if (_entries.Count > MaxRecent)
                _entries.RemoveRange(MaxRecent, _entries.Count - MaxRecent);
        }
    }

    /// <summary>Removes the entry with this exact page URL. Returns true when one was removed.</summary>
    public bool Remove(string pageUrl)
    {
        lock (_gate)
        {
            int index = _entries.FindIndex(e => string.Equals(e.PageUrl, pageUrl, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            _entries.RemoveAt(index);
        }
        return true;
    }

    /// <summary>Persists the current list atomically. Best-effort, like every other store here.</summary>
    public void Save()
    {
        string json;
        lock (_gate) json = JsonSerializer.Serialize(_entries, Options);
        SaveJson(json, _filePath);
    }

    public static bool IsValidUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static List<YouTubeRecentEntry> Load(string filePath)
    {
        var entries = TryRead(filePath) ?? TryRead(filePath + ".bak");
        return Normalize(entries);
    }

    private static List<YouTubeRecentEntry>? TryRead(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<List<YouTubeRecentEntry>>(json, Options);
        }
        catch (Exception)
        {
            // Corrupted or unreadable → caller falls back to .bak, then to an empty list.
            return null;
        }
    }

    /// <summary>Drops unusable rows, deduplicates by URL (later entry wins), clamps to the cap.</summary>
    public static List<YouTubeRecentEntry> Normalize(List<YouTubeRecentEntry>? entries)
    {
        if (entries is null || entries.Count == 0) return [];

        var byUrl = new Dictionary<string, YouTubeRecentEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in entries)
        {
            if (raw is null || !IsValidUrl(raw.PageUrl)) continue;
            var url = raw.PageUrl.Trim();
            byUrl[url] = raw with { PageUrl = url };
        }

        var ordered = byUrl.Values.OrderByDescending(e => e.LastPlayedUtcTicks).ToList();
        if (ordered.Count > MaxRecent)
            ordered = ordered[..MaxRecent];
        return ordered;
    }

    private static void SaveJson(string json, string filePath)
    {
        try
        {
            AtomicFile.WriteAllText(filePath, json, keepBackup: true, flushToDisk: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"[youtube-recent] save failed: {ex.Message}");
        }
    }
}
