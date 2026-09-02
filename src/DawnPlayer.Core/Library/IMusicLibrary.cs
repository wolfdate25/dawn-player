using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.Core.Library;

/// <summary>
/// Domain service contract for SQLite-backed audio track metadata catalog and folder scanning.
/// </summary>
public interface IMusicLibrary : IDisposable
{
    /// <summary>In-memory view of sorted tracks.</summary>
    IReadOnlyList<Track> Tracks { get; }

    /// <summary>Total track count in library.</summary>
    int Count { get; }

    /// <summary>Raised on UI thread after database load or scanning completion.</summary>
    event Action? TracksChanged;

    /// <summary>Raised during background directory scanning progress updates.</summary>
    event Action<ScanProgress>? ScanProgress;

    /// <summary>Retrieves a cached track by file path.</summary>
    Track? GetTrack(string path);

    /// <summary>Persists the listening statistics of a working-set track (play/skip counts,
    /// last played) without touching any other column.</summary>
    void UpdateStats(Track track);

    /// <summary>Loads track catalog into memory from SQLite database.</summary>
    void LoadFromDb();

    /// <summary>Scans configured library folders asynchronously using provided settings.</summary>
    Task ScanAsync(AppSettings settings, CancellationToken ct = default);
}
