using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Playlists;

/// <summary>
/// Eole-style album group model used by the grouped playlist view and CollectionViewSource.
/// </summary>
public sealed class AlbumGroup : IReadOnlyList<PlaylistItem>
{
    public string Key { get; set; } = "";
    public string Album { get; set; } = "";
    public string Artist { get; set; } = "";
    public int Year { get; set; }
    public string? ArtPath { get; set; }
    public string? Art => ArtPath;
    public object SyncRoot { get; } = new();
    public List<PlaylistItem> Items { get; } = new();
    public int Count { get { lock (SyncRoot) return Items.Count; } }
    public PlaylistItem this[int index] { get { lock (SyncRoot) return Items[index]; } }

    public PlaylistItem[] GetSnapshot()
    {
        lock (SyncRoot)
        {
            return CollectionSnapshot.CaptureSafe(Items);
        }
    }

    public TimeSpan Duration
    {
        get
        {
            PlaylistItem[] snapshot;
            lock (SyncRoot)
            {
                snapshot = CollectionSnapshot.CaptureSafe(Items);
            }
            long sum = 0;
            for (int i = 0; i < snapshot.Length; i++)
            {
                var itm = snapshot[i];
                if (itm?.Track != null)
                {
                    sum += itm.Track.DurationMs;
                }
            }
            return TimeSpan.FromMilliseconds(sum);
        }
    }

    public string DurationFormatted => FormatEoleDuration(Duration);
    public string YearFormatted => Year > 0 ? Year.ToString(CultureInfo.InvariantCulture) : "";

    public string Info
    {
        get
        {
            var parts = new List<string>(4);
            if (!string.IsNullOrEmpty(Artist)) parts.Add(Artist);
            if (Year > 0) parts.Add(Year.ToString(CultureInfo.InvariantCulture));
            parts.Add($"{Count}곡");
            parts.Add(FormatEoleDuration(Duration));
            return string.Join("  •  ", parts);
        }
    }

    public void AddItem(PlaylistItem item)
    {
        if (item == null) return;
        lock (SyncRoot)
        {
            Items.Add(item);
        }
    }

    // Instance API kept deliberately: callers hold AlbumGroup references, and the test
    // suite exercises it through one.
#pragma warning disable CA1822
    public void InvalidateDuration()
#pragma warning restore CA1822
    {
        // Dynamic calculation requires no internal cache invalidation, maintained for API compatibility
    }

    public static string FormatEoleDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} h {ts.Minutes} min {ts.Seconds} s";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes} min {ts.Seconds} s";
        return $"{ts.Seconds} s";
    }

    public IEnumerator<PlaylistItem> GetEnumerator()
    {
        PlaylistItem[] snapshot;
        lock (SyncRoot) { snapshot = CollectionSnapshot.CaptureSafe(Items); }
        return ((IEnumerable<PlaylistItem>)snapshot).GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
