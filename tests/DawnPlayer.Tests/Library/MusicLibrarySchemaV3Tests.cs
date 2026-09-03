using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DawnPlayer.Tests.Library;

/// <summary>
/// Library schema v3: the rating column (persistence, rescan carry-forward), the play_events
/// history table, and in-place upgrade of a v2 database file.
/// </summary>
public sealed class MusicLibrarySchemaV3Tests
{
    [Fact]
    public async Task UpdateRating_PersistsAcrossReopen()
    {
        var root = NewTempDir();
        var dbPath = Path.Combine(root, "rating.db");
        var file = Path.Combine(root, "rated.wav");
        File.WriteAllBytes(file, MinimalWav());
        try
        {
            var settings = AppSettings.CreateDefault();
            settings.Library.Folders = new List<string> { root };

            using (var library = new MusicLibrary(dbPath))
            {
                await library.ScanAsync(settings);
                var track = library.GetTrack(file);
                Assert.NotNull(track);
                Assert.Equal(0, track!.Rating);

                track.Rating = 4;
                library.UpdateRating(track);
            }

            using var reopened = new MusicLibrary(dbPath);
            reopened.LoadFromDb();
            Assert.Equal(4, reopened.GetTrack(file)!.Rating);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Rescan_OfChangedFile_PreservesRating()
    {
        var root = NewTempDir();
        var dbPath = Path.Combine(root, "keeprating.db");
        var file = Path.Combine(root, "keep.wav");
        File.WriteAllBytes(file, MinimalWav());
        try
        {
            var settings = AppSettings.CreateDefault();
            settings.Library.Folders = new List<string> { root };

            using (var library = new MusicLibrary(dbPath))
            {
                await library.ScanAsync(settings);
                var track = library.GetTrack(file)!;
                track.Rating = 5;
                library.UpdateRating(track);

                // Force the next scan down the re-read path (a tag rewrite would do this).
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow + TimeSpan.FromSeconds(5));

                await library.ScanAsync(settings);
                Assert.Equal(5, library.GetTrack(file)!.Rating);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Rating_IsClamped_WhenLoaded()
    {
        var root = NewTempDir();
        var dbPath = Path.Combine(root, "clamp.db");
        try
        {
            using var library = new MusicLibrary(dbPath);
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                // A hand-edited or foreign-tool-written row with an out-of-range value must not
                // leak 9 stars into the UI.
                cmd.CommandText = "INSERT INTO tracks(path, rating) VALUES ('x', 9)";
                cmd.ExecuteNonQuery();
            }

            library.LoadFromDb();
            Assert.Equal(5, library.GetTrack("x")!.Rating);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PlayEvents_RoundTrip_AndFilterBySince()
    {
        var root = NewTempDir();
        var dbPath = Path.Combine(root, "events.db");
        try
        {
            using var library = new MusicLibrary(dbPath);
            var old = DateTime.UtcNow - TimeSpan.FromDays(10);
            var recent = DateTime.UtcNow - TimeSpan.FromDays(1);

            InsertEventAt(dbPath, old, "old.wav");
            library.RecordPlayEvent(new Track { Path = "recent.wav" });
            InsertEventAt(dbPath, recent, "manual.wav");

            var all = library.ReadPlayEvents();
            Assert.Equal(3, all.Count);
            Assert.Equal(new[] { "old.wav", "manual.wav", "recent.wav" }, all.Select(e => e.Path).ToArray());

            var since = library.ReadPlayEvents((DateTime.UtcNow - TimeSpan.FromDays(2)).Ticks);
            Assert.Equal(new[] { "manual.wav", "recent.wav" }, since.Select(e => e.Path).ToArray());
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>Direct insert with an explicit timestamp, bypassing RecordPlayEvent's UtcNow.</summary>
    private static void InsertEventAt(string dbPath, DateTime playedUtc, string path)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO play_events(played_utc, path) VALUES (@ts, @p)";
        cmd.Parameters.AddWithValue("@ts", playedUtc.Ticks);
        cmd.Parameters.AddWithValue("@p", path);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void V2Database_UpgradesInPlace_WithRatingAndEvents()
    {
        var root = NewTempDir();
        var dbPath = Path.Combine(root, "v2.db");
        try
        {
            // Hand-build a v2-shaped file: tracks table without `rating`, no play_events table,
            // user_version stamped 2.
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE tracks(
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
                        rg_album_gain REAL, rg_album_peak REAL,
                        play_count INTEGER NOT NULL DEFAULT 0,
                        skip_count INTEGER NOT NULL DEFAULT 0,
                        last_played INTEGER NOT NULL DEFAULT 0,
                        first_seen INTEGER NOT NULL DEFAULT 0
                    );
                    INSERT INTO tracks(path, play_count) VALUES ('kept', 11);
                    PRAGMA user_version = 2;
                    """;
                cmd.ExecuteNonQuery();
            }

            using var library = new MusicLibrary(dbPath);
            Assert.Equal(3, library.DatabaseSchemaVersion);

            library.LoadFromDb();
            var track = library.GetTrack("kept");
            Assert.NotNull(track);
            Assert.Equal(11, track!.PlayCount); // v2 data survived
            Assert.Equal(0, track.Rating);       // new column defaulted

            track.Rating = 3;
            library.UpdateRating(track);
            library.RecordPlayEvent(track);
            Assert.Single(library.ReadPlayEvents());

            using var conn2 = new SqliteConnection($"Data Source={dbPath}");
            conn2.Open();
            using var verify = conn2.CreateCommand();
            verify.CommandText = "SELECT rating FROM tracks WHERE path='kept'";
            Assert.Equal(3L, verify.ExecuteScalar());
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DawnPlayer_SchemaV3_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static byte[] MinimalWav()
    {
        const int sampleRate = 44100;
        const int channels = 1;
        int frames = 100;
        const short bits = 16;
        int dataBytes = frames * channels * (bits / 8);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF".ToCharArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * (bits / 8));
        w.Write((short)(channels * (bits / 8)));
        w.Write(bits);
        w.Write("data".ToCharArray());
        w.Write(dataBytes);
        for (int i = 0; i < frames * channels; i++) w.Write((short)0);
        w.Flush();
        return ms.ToArray();
    }
}
