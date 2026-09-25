using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Persistence;

/// <summary>
/// Loads and saves the user's internet-radio stations as JSON, following the settings store's
/// resilience recipe: atomic replacement with a kept backup, a <c>.bak</c> fallback on load, and
/// <see cref="Normalize"/> hardening so a hand-edited file never reaches the UI with bad rows.
/// </summary>
public sealed class RadioStationStore
{
    /// <summary>Upper bound enforced by <see cref="Normalize"/>. Stations are cheap, but a runaway
    /// import loop must not grow the file without limit.</summary>
    public const int MaxStations = 500;

    private readonly object _gate = new();
    private readonly string _filePath;
    private List<RadioStation> _stations;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RadioStationStore() : this(Util.AppPaths.NetworkStationsFile)
    {
    }

    /// <summary>The path is a parameter so tests never touch the process-wide base directory.</summary>
    public RadioStationStore(string filePath)
    {
        _filePath = filePath;
        _stations = Load(filePath);
    }

    /// <summary>Snapshot of the current stations, in added order. Never null.</summary>
    public IReadOnlyList<RadioStation> Stations
    {
        get { lock (_gate) { return _stations.ToArray(); } }
    }

    /// <summary>
    /// Adds a station, or replaces the existing one with the same URL (renames keep their place in
    /// the added order via the original ticks). Returns true when the list changed.
    /// </summary>
    public bool AddOrReplace(RadioStation station)
    {
        if (!IsValidUrl(station?.Url)) return false;

        lock (_gate)
        {
            int existing = FindIndexByUrl(station!.Url);
            if (existing >= 0)
            {
                var kept = station with { AddedUtcTicks = _stations[existing].AddedUtcTicks };
                if (kept == _stations[existing]) return false;
                _stations[existing] = kept;
            }
            else
            {
                if (_stations.Count >= MaxStations) return false;
                _stations.Add(station);
            }
        }
        return true;
    }

    /// <summary>Removes the station with this exact URL. Returns true when one was removed.</summary>
    public bool Remove(string url)
    {
        lock (_gate)
        {
            int index = FindIndexByUrl(url);
            if (index < 0) return false;
            _stations.RemoveAt(index);
        }
        return true;
    }

    /// <summary>Records a play timestamp for the station with this URL, if present.</summary>
    public void TouchLastPlayed(string url, long utcTicks)
    {
        lock (_gate)
        {
            int index = FindIndexByUrl(url);
            if (index < 0) return;
            _stations[index] = _stations[index] with { LastPlayedUtcTicks = utcTicks };
        }
    }

    /// <summary>Persists the current list atomically. Best-effort, like every other store here.</summary>
    public void Save()
    {
        string json;
        lock (_gate) json = JsonSerializer.Serialize(_stations, Options);
        SaveJson(json, _filePath);
    }

    private int FindIndexByUrl(string url) =>
        _stations.FindIndex(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));

    public static bool IsValidUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static List<RadioStation> Load(string filePath)
    {
        var stations = TryRead(filePath) ?? TryRead(filePath + ".bak");
        return Normalize(stations);
    }

    private static List<RadioStation>? TryRead(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<List<RadioStation>>(json, Options);
        }
        catch (Exception)
        {
            // Corrupted or unreadable → caller falls back to .bak, then to an empty list.
            return null;
        }
    }

    /// <summary>
    /// Hardens a deserialized (or just mutated) list: drops rows without a usable URL, fills blank
    /// names from the URL, deduplicates by URL (the later entry wins, as in a hand-merged file),
    /// keeps added order, and clamps to <see cref="MaxStations"/> newest.
    /// </summary>
    public static List<RadioStation> Normalize(List<RadioStation>? stations)
    {
        if (stations is null || stations.Count == 0) return [];

        var byUrl = new Dictionary<string, RadioStation>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in stations)
        {
            if (raw is null || !IsValidUrl(raw.Url)) continue;
            var url = raw.Url.Trim();
            var name = string.IsNullOrWhiteSpace(raw.Name) ? url : raw.Name.Trim();
            byUrl[url] = raw with { Name = name, Url = url };
        }

        var ordered = byUrl.Values.OrderBy(s => s.AddedUtcTicks).ToList();
        if (ordered.Count > MaxStations)
            ordered = ordered[^MaxStations..];
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
            Log.Warn($"[stations] save failed: {ex.Message}");
        }
    }
}
