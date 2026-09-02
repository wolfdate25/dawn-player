using System.Text;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DawnPlayer.Tests.Library;

/// <summary>
/// Play-statistics persistence (library schema v2): recording counts, preserving them across a
/// rescan of a changed file, and upgrading a v1 database in place.
/// </summary>
public sealed class PlayStatisticsTests
{
    [Fact]
    public async Task UpdateStats_PersistsAcrossReopen()
    {
        var root = NewTempDir();
        var dbPath = Path.Combine(root, "stats.db");
        var file = Path.Combine(root, "stat.wav");
        File.WriteAllBytes(file, MinimalWav());
        try
        {
            var settings = AppSettings.CreateDefault();
            settings.Library.Folders = new List<string> { root };

            Track? scanned;
            using (var library = new MusicLibrary(dbPath))
            {
                await library.ScanAsync(settings);
                scanned = library.GetTrack(file);
                Assert.NotNull(scanned);

                scanned!.PlayCount = 7;
                scanned.SkipCount = 2;
                scanned.LastPlayedUtcTicks = 638500000000000000L;
                library.UpdateStats(scanned);
            }

            using var reopened = new MusicLibrary(dbPath);
            reopened.LoadFromDb();
            var loaded = reopened.GetTrack(file);

            Assert.NotNull(loaded);
            Assert.Equal(7, loaded!.PlayCount);
            Assert.Equal(2, loaded.SkipCount);
            Assert.Equal(638500000000000000L, loaded.LastPlayedUtcTicks);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Rescan_OfChangedFile_PreservesListeningHistory()
    {
        var root = NewTempDir();
        var dbPath = Path.Combine(root, "keepstats.db");
        var file = Path.Combine(root, "keep.wav");
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

                track!.PlayCount = 12;
                track.SkipCount = 3;
                track.LastPlayedUtcTicks = 638500000000000001L;
                library.UpdateStats(track);

                // Simulate the tag rewrite that makes the next scan re-read the file: the cached
                // mtime no longer matches, so the scan takes the re-read path where INSERT OR
                // REPLACE would otherwise zero the row.
                track.FileModifiedUtcTicks = 0;

                await library.ScanAsync(settings);
                var rescanned = library.GetTrack(file);
                Assert.NotNull(rescanned);
                Assert.Equal(12, rescanned!.PlayCount);
                Assert.Equal(3, rescanned.SkipCount);
                Assert.Equal(638500000000000001L, rescanned.LastPlayedUtcTicks);
                Assert.True(rescanned.FirstSeenUtcTicks > 0, "first-seen must be stamped for the row");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task V1Database_MigratesToV2_AndBackfillsFirstSeen()
    {
        var root = NewTempDir();
        var dbPath = Path.Combine(root, "v1.db");
        try
        {
            long mtime = 638400000000000000L;
            using (var raw = new SqliteConnection($"Data Source={dbPath}"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                // The original v1 layout, without any of the statistics columns.
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
                        rg_album_gain REAL, rg_album_peak REAL
                    );
                    INSERT INTO tracks(path, title, mtime) VALUES ('C:/m/old.flac', 'Old', 638400000000000000);
                    PRAGMA user_version = 1;
                    """;
                cmd.ExecuteNonQuery();
            }

            using var migrated = new MusicLibrary(dbPath);
            Assert.Equal(2, migrated.DatabaseSchemaVersion);
            migrated.LoadFromDb();

            var track = migrated.GetTrack("C:/m/old.flac");
            Assert.NotNull(track);
            Assert.Equal(0, track!.PlayCount);
            Assert.Equal(0, track.SkipCount);
            Assert.Equal(0, track.LastPlayedUtcTicks);
            Assert.Equal(mtime, track.FirstSeenUtcTicks);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DawnPlayer_PlayStats_{Guid.NewGuid():N}");
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
        const short channels = 2;
        const short bits = 16;
        const int frames = 256;
        int dataBytes = frames * channels * (bits / 8);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF".ToCharArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray());
        w.Write(16);
        w.Write((short)1);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * (bits / 8));
        w.Write((short)(channels * (bits / 8)));
        w.Write(bits);
        w.Write("data".ToCharArray());
        w.Write(dataBytes);
        w.Write(new byte[dataBytes]);
        w.Flush();
        return ms.ToArray();
    }
}
