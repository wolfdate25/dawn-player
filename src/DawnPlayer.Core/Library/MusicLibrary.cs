using System.Collections.Concurrent;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;
using Microsoft.Data.Sqlite;

namespace DawnPlayer.Core.Library;

public sealed record ScanProgress(int Done, int Total, string CurrentFile, bool Finished);

/// <summary>SQLite-backed music library with an in-memory working set.</summary>
public sealed class MusicLibrary : IMusicLibrary
{
    /// <summary>Layout stamped into the file as <c>PRAGMA user_version</c>.</summary>
    private const int SchemaVersion = 1;

    /// <summary>
    /// The one column order shared by the SELECT in <see cref="LoadFromDb"/>, the INSERT in
    /// <see cref="CreateUpsertCommand"/>, and the ordinals in <see cref="ReadTrack"/>.
    /// </summary>
    private const string TrackColumns =
        "path,title,artist,album_artist,album,genre,year,track_no,disc_no,duration_ms," +
        "sample_rate,channels,bits,codec,bitrate,size,mtime,has_lrc,art_path," +
        "rg_track_gain,rg_track_peak,rg_album_gain,rg_album_peak";

    /// <summary>Shortest gap between two non-final scan reports, in milliseconds.</summary>
    private const int ProgressThrottleMs = 100;

    private readonly SqliteConnection _conn;
    private readonly object _ioLock = new();

    // Held in a field rather than a const so a test can force several batches without laying down
    // thousands of files.
    private int _upsertBatchSize = 500;

    private Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<Track> _tracksView = Array.Empty<Track>();

    public IReadOnlyList<Track> Tracks => _tracksView;
    public int Count => _tracks.Count;

    /// <summary>Schema version found in (or stamped onto) the database file when it was opened.</summary>
    public int DatabaseSchemaVersion { get; private set; }

    /// <summary>Raised on the UI thread via marshaling by the app layer after load/scan.</summary>
    public event Action? TracksChanged;
    public event Action<ScanProgress>? ScanProgress;

    public MusicLibrary(string? dbPath = null)
    {
        string path = !string.IsNullOrWhiteSpace(dbPath) ? dbPath : AppPaths.LibraryDbPath;
        string connectionString = path.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"Data Source={path}";
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        ConfigureConnection();
        EnsureSchema();
    }

    /// <summary>
    /// Write-ahead logging plus a relaxed sync mode. With the defaults, a scan's bulk commit paid a
    /// full fsync per transaction and blocked readers for its whole duration; WAL lets the in-memory
    /// working set be read while the commit is in flight, and NORMAL is safe here because losing the
    /// tail of a library index after a power cut just means rescanning.
    /// </summary>
    private void ConfigureConnection()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // A database that cannot take these (e.g. read-only media) still works with the defaults.
        }
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tracks(
                path TEXT PRIMARY KEY,
                title TEXT NOT NULL DEFAULT '',
                artist TEXT NOT NULL DEFAULT '',
                album_artist TEXT NOT NULL DEFAULT '',
                album TEXT NOT NULL DEFAULT '',
                genre TEXT NOT NULL DEFAULT '',
                year INTEGER NOT NULL DEFAULT 0,
                track_no INTEGER NOT NULL DEFAULT 0,
                disc_no INTEGER NOT NULL DEFAULT 0,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                sample_rate INTEGER NOT NULL DEFAULT 0,
                channels INTEGER NOT NULL DEFAULT 0,
                bits INTEGER NOT NULL DEFAULT 0,
                codec TEXT NOT NULL DEFAULT '',
                bitrate INTEGER NOT NULL DEFAULT 0,
                size INTEGER NOT NULL DEFAULT 0,
                mtime INTEGER NOT NULL DEFAULT 0,
                has_lrc INTEGER NOT NULL DEFAULT 0,
                art_path TEXT,
                rg_track_gain REAL, rg_track_peak REAL,
                rg_album_gain REAL, rg_album_peak REAL
            );
            """;
        cmd.ExecuteNonQuery();

        // Deliberately no secondary indexes. Every read path pulls the whole table into the
        // in-memory working set and filters there, so an index would buy nothing at read time and
        // charge every scan for maintaining it.

        cmd.CommandText = "PRAGMA user_version;";
        var stored = cmd.ExecuteScalar();
        int version = stored is null or DBNull ? 0 : Convert.ToInt32(stored);

        if (version < SchemaVersion)
        {
            // A future migration branches here: step the file up one version at a time, then let
            // the stamp below record where it landed. Version 1 is the first stamped layout and is
            // identical to the unstamped one, so nothing has to be rewritten yet.
            try
            {
                cmd.CommandText = $"PRAGMA user_version = {SchemaVersion};";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Read-only media: the layout is version 1 regardless, it just cannot be stamped.
            }
            version = SchemaVersion;
        }

        DatabaseSchemaVersion = version;
    }

    public Track? GetTrack(string path) =>
        _tracks.TryGetValue(path, out var t) ? t : null;

    public void LoadFromDb()
    {
        var loaded = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        lock (_ioLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT " + TrackColumns + " FROM tracks";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var t = ReadTrack(r);
                loaded[t.Path] = t;
            }
        }
        SwapTracks(loaded);
    }

    /// <summary>Scans the configured folders (background). New/changed files are re-read,
    /// vanished files under scanned roots are pruned.</summary>
    public async Task ScanAsync(AppSettings settings, CancellationToken ct = default)
    {
        var folders = settings.Library.Folders.Where(Directory.Exists).ToList();
        if (folders.Count == 0) { ScanProgress?.Invoke(new ScanProgress(0, 0, "", Finished: true)); return; }

        var files = new List<string>();
        // Only roots we actually finished walking may have their tracks pruned. Treating an
        // offline root (unplugged drive, NAS not mounted yet at login) as "everything under it
        // was deleted" wiped the whole catalogue for that root on every launch.
        var scannedRoots = new List<string>(folders.Count);
        foreach (var folder in folders)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(AppPaths.IsSupportedAudioFile));
                scannedRoots.Add(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                       + Path.DirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* unreadable folder: leave its existing rows alone */ }
        }

        var existing = _tracks; // path → track
        var result = new ConcurrentDictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        var filesToRead = new List<string>();

        // One folder-art probe costs ~18 File.Exists calls plus two directory enumerations, and a
        // whole album shares one directory. This cache must not outlive the scan that created it:
        // cover files can be added, replaced or removed between scans, so a longer-lived cache
        // would keep handing out whatever it found first.
        var folderArt = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        int done = 0;
        int total = files.Count;
        long lastReportTicks = 0;

        // Workers raise ScanProgress concurrently and the app layer marshals every report to the UI
        // thread, so non-final reports are collapsed to one per ProgressThrottleMs. The finished
        // report at the end of the scan bypasses this and always fires.
        void Report(int currentDone, string currentFile)
        {
            if (currentDone % 10 != 0 && currentDone != total) return;

            var handler = ScanProgress;
            if (handler == null) return;

            long now = Environment.TickCount64;
            long previous = Interlocked.Read(ref lastReportTicks);
            if (now - previous < ProgressThrottleMs) return;
            // Losing the race means another worker just reported; that report stands in for this one.
            if (Interlocked.CompareExchange(ref lastReportTicks, now, previous) != previous) return;

            handler(new ScanProgress(currentDone, total, currentFile, Finished: false));
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            bool useCached = false;
            if (existing.TryGetValue(file, out var cached))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.LastWriteTimeUtc.Ticks == cached.FileModifiedUtcTicks && fi.Length == cached.FileSize)
                    {
                        result[file] = cached;
                        useCached = true;
                    }
                }
                catch { }
            }

            if (!useCached)
            {
                filesToRead.Add(file);
            }
            else
            {
                Report(Interlocked.Increment(ref done), file);
            }
        }

        if (filesToRead.Count > 0)
        {
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
            };

            // Committing per batch is what makes cancellation cheap: a scan that is cancelled, or
            // an app that exits mid-scan, keeps every batch that already landed and loses at most
            // the one in flight.
            var pending = filesToRead.ToArray();
            int batchSize = Math.Max(1, _upsertBatchSize);

            for (int start = 0; start < pending.Length; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();

                int length = Math.Min(batchSize, pending.Length - start);
                var upserts = new ConcurrentBag<Track>();

                await Parallel.ForEachAsync(new ArraySegment<string>(pending, start, length), parallelOptions, (file, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var t = TagReader.TryRead(file, out var pic);
                    if (t != null)
                    {
                        // Must be the same key the model uses for album grouping: a key that
                        // collided across untagged tracks resolved them all to one cover image.
                        var albumKey = AlbumArtService.ComputeAlbumKey(t);
                        var art = (pic != null ? TagReader.TryExtractArt(t, albumKey, pic) : null)
                                  ?? TagReader.FindFolderArt(file, folderArt);
                        var track = t with { ArtPath = art };
                        result[file] = track;
                        upserts.Add(track);
                    }

                    Report(Interlocked.Increment(ref done), file);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

                CommitBatch(upserts, null);
            }
        }

        // Prune only tracks that live under a root this pass actually walked and that really are
        // gone from disk. Everything else — including every track under a root that was offline —
        // is carried forward untouched.
        var toDelete = new List<string>();
        var carriedOver = new List<Track>();
        foreach (var kv in existing)
        {
            if (result.ContainsKey(kv.Key)) continue;

            bool underScannedRoot = false;
            for (int i = 0; i < scannedRoots.Count; i++)
            {
                if (kv.Key.StartsWith(scannedRoots[i], StringComparison.OrdinalIgnoreCase))
                {
                    underScannedRoot = true;
                    break;
                }
            }

            if (underScannedRoot && !File.Exists(kv.Key)) toDelete.Add(kv.Key);
            else carriedOver.Add(kv.Value);
        }

        // Prunes ride in the final batch, so a cancelled scan never drops rows it has not replaced.
        ct.ThrowIfCancellationRequested();
        CommitBatch(null, toDelete);

        var next = new Dictionary<string, Track>(result, StringComparer.OrdinalIgnoreCase);
        foreach (var t in carriedOver) next[t.Path] = t;
        SwapTracks(next);
        ScanProgress?.Invoke(new ScanProgress(total, total, "", Finished: true));
    }

    private void CommitBatch(IReadOnlyCollection<Track>? upserts, IReadOnlyCollection<string>? deletes)
    {
        bool hasUpserts = upserts is { Count: > 0 };
        bool hasDeletes = deletes is { Count: > 0 };
        if (!hasUpserts && !hasDeletes) return;

        lock (_ioLock)
        {
            using var tx = _conn.BeginTransaction();

            // One prepared command reused for every row. Building a fresh SqliteCommand and 23
            // AddWithValue parameters per track dominated the cost of indexing a large library.
            if (hasUpserts)
            {
                using var upsert = CreateUpsertCommand(tx);
                foreach (var t in upserts!) ExecuteUpsert(upsert, t);
            }

            if (hasDeletes)
            {
                using var delete = _conn.CreateCommand();
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM tracks WHERE path = @p";
                var pathParam = delete.Parameters.Add("@p", SqliteType.Text);
                foreach (var p in deletes!)
                {
                    pathParam.Value = p;
                    delete.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
    }

    private void SwapTracks(Dictionary<string, Track> next)
    {
        _tracks = next;

        // The sort keys are materialized once because the comparison is culture-sensitive and runs
        // O(n log n) times, on whatever thread loaded the library — the UI thread at startup.
        // Index is the last tiebreaker so the unstable Array.Sort reproduces the stable ordering
        // it replaces for tracks that compare equal on all three visible keys.
        var keys = new DisplaySortKey[next.Count];
        int i = 0;
        foreach (var t in next.Values)
        {
            keys[i] = new DisplaySortKey(t.SortArtist, t.Album, t.AlbumSortKey, i, t);
            i++;
        }

        // Snapshot the comparer once per swap: the property builds a fresh culture-bound comparer
        // on every access, and the display order has to stay culture-aware for the user.
        var byName = StringComparer.CurrentCultureIgnoreCase;
        Array.Sort(keys, (a, b) =>
        {
            int c = byName.Compare(a.Artist, b.Artist);
            if (c != 0) return c;
            c = byName.Compare(a.Album, b.Album);
            if (c != 0) return c;
            c = a.AlbumSortKey.CompareTo(b.AlbumSortKey);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });

        var view = new Track[keys.Length];
        for (int k = 0; k < keys.Length; k++) view[k] = keys[k].Item;

        _tracksView = view;
        TracksChanged?.Invoke();
    }

    private readonly record struct DisplaySortKey(string Artist, string Album, long AlbumSortKey, int Index, Track Item);

    /// <summary>Reads by ordinal, so the indexes below and the order of <c>TrackColumns</c> must
    /// stay in step; a column inserted on one side alone loads wrong values into every track.</summary>
    private static Track ReadTrack(SqliteDataReader r)
    {
        return new Track
        {
            Path = r.GetString(0),
            Title = r.GetString(1),
            Artist = r.GetString(2),
            AlbumArtist = r.GetString(3),
            Album = r.GetString(4),
            Genre = r.GetString(5),
            Year = r.GetInt32(6),
            TrackNo = r.GetInt32(7),
            DiscNo = r.GetInt32(8),
            DurationMs = r.GetInt64(9),
            SampleRate = r.GetInt32(10),
            Channels = r.GetInt32(11),
            BitsPerSample = r.GetInt32(12),
            Codec = r.GetString(13),
            BitrateKbps = r.GetInt32(14),
            FileSize = r.GetInt64(15),
            FileModifiedUtcTicks = r.GetInt64(16),
            HasLrc = r.GetInt32(17) != 0,
            ArtPath = r.IsDBNull(18) ? null : r.GetString(18),
            RgTrackGainDb = r.IsDBNull(19) ? null : r.GetDouble(19),
            RgTrackPeak = r.IsDBNull(20) ? null : r.GetDouble(20),
            RgAlbumGainDb = r.IsDBNull(21) ? null : r.GetDouble(21),
            RgAlbumPeak = r.IsDBNull(22) ? null : r.GetDouble(22),
        };
    }

    private SqliteCommand CreateUpsertCommand(SqliteTransaction tx)
    {
        var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT OR REPLACE INTO tracks (" + TrackColumns + ") VALUES " +
            "(@p,@t,@a,@aa,@al,@g,@y,@tn,@dn,@dur,@sr,@ch,@bits,@codec,@br,@size,@mtime,@lrc,@art," +
            " @rg1,@rg2,@rg3,@rg4)";

        cmd.Parameters.Add("@p", SqliteType.Text);
        cmd.Parameters.Add("@t", SqliteType.Text);
        cmd.Parameters.Add("@a", SqliteType.Text);
        cmd.Parameters.Add("@aa", SqliteType.Text);
        cmd.Parameters.Add("@al", SqliteType.Text);
        cmd.Parameters.Add("@g", SqliteType.Text);
        cmd.Parameters.Add("@y", SqliteType.Integer);
        cmd.Parameters.Add("@tn", SqliteType.Integer);
        cmd.Parameters.Add("@dn", SqliteType.Integer);
        cmd.Parameters.Add("@dur", SqliteType.Integer);
        cmd.Parameters.Add("@sr", SqliteType.Integer);
        cmd.Parameters.Add("@ch", SqliteType.Integer);
        cmd.Parameters.Add("@bits", SqliteType.Integer);
        cmd.Parameters.Add("@codec", SqliteType.Text);
        cmd.Parameters.Add("@br", SqliteType.Integer);
        cmd.Parameters.Add("@size", SqliteType.Integer);
        cmd.Parameters.Add("@mtime", SqliteType.Integer);
        cmd.Parameters.Add("@lrc", SqliteType.Integer);
        cmd.Parameters.Add("@art", SqliteType.Text);
        cmd.Parameters.Add("@rg1", SqliteType.Real);
        cmd.Parameters.Add("@rg2", SqliteType.Real);
        cmd.Parameters.Add("@rg3", SqliteType.Real);
        cmd.Parameters.Add("@rg4", SqliteType.Real);
        cmd.Prepare();
        return cmd;
    }

    private static void ExecuteUpsert(SqliteCommand cmd, Track t)
    {
        var p = cmd.Parameters;
        p["@p"].Value = t.Path;
        p["@t"].Value = t.Title;
        p["@a"].Value = t.Artist;
        p["@aa"].Value = t.AlbumArtist;
        p["@al"].Value = t.Album;
        p["@g"].Value = t.Genre;
        p["@y"].Value = t.Year;
        p["@tn"].Value = t.TrackNo;
        p["@dn"].Value = t.DiscNo;
        p["@dur"].Value = t.DurationMs;
        p["@sr"].Value = t.SampleRate;
        p["@ch"].Value = t.Channels;
        p["@bits"].Value = t.BitsPerSample;
        p["@codec"].Value = t.Codec;
        p["@br"].Value = t.BitrateKbps;
        p["@size"].Value = t.FileSize;
        p["@mtime"].Value = t.FileModifiedUtcTicks;
        p["@lrc"].Value = t.HasLrc ? 1 : 0;
        p["@art"].Value = (object?)t.ArtPath ?? DBNull.Value;
        p["@rg1"].Value = (object?)t.RgTrackGainDb ?? DBNull.Value;
        p["@rg2"].Value = (object?)t.RgTrackPeak ?? DBNull.Value;
        p["@rg3"].Value = (object?)t.RgAlbumGainDb ?? DBNull.Value;
        p["@rg4"].Value = (object?)t.RgAlbumPeak ?? DBNull.Value;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_ioLock)
        {
            _conn.Close();
            // ClearPool reads the connection's own connection string to find the pool, so it needs
            // an object that has not been disposed yet.
            SqliteConnection.ClearPool(_conn);
            _conn.Dispose();
        }
    }
}
