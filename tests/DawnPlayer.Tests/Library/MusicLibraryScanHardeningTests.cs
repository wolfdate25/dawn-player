using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.Library;

/// <summary>
/// Covers the parts of the SQLite library layer that are observable without real audio files:
/// batched commits, cancellation, progress throttling, per-scan folder-art memoization, the
/// stamped schema version, and the SELECT/ordinal pairing in ReadTrack.
/// </summary>
public sealed class MusicLibraryScanHardeningTests
{
    [Fact]
    public async Task ScanAsync_CancelledAfterFirstBatches_KeepsCommittedRowsInDatabase()
    {
        var root = NewTempDir("BatchCancel");
        var dbPath = Path.Combine(root, "batch.db");
        try
        {
            for (int i = 0; i < 12; i++)
            {
                File.WriteAllBytes(Path.Combine(root, $"t{i:D2}.wav"), MinimalWav());
            }

            var settings = AppSettings.CreateDefault();
            settings.Library.Folders = new List<string> { root };

            using var cts = new CancellationTokenSource();
            using (var library = new MusicLibrary(dbPath))
            {
                SetUpsertBatchSize(library, 4);

                // The first report a scan can raise lands on the tenth file, which is inside the
                // third batch of four — so the first two batches have provably been committed by
                // the time the token is cancelled.
                library.ScanProgress += _ => cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => library.ScanAsync(settings, cts.Token));
            }

            using var reopened = new MusicLibrary(dbPath);
            reopened.LoadFromDb();

            Assert.True(reopened.Count > 0, "A cancelled scan must not discard the batches it already committed.");
            Assert.InRange(reopened.Count, 8, 12);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ScanAsync_AllFilesCached_CollapsesProgressBurstAndStillReportsFinal()
    {
        const int fileCount = 120;
        var root = NewTempDir("Throttle");
        var dbPath = Path.Combine(root, "throttle.db");
        try
        {
            for (int i = 0; i < fileCount; i++)
            {
                File.WriteAllBytes(Path.Combine(root, $"t{i:D3}.wav"), MinimalWav());
            }

            var settings = AppSettings.CreateDefault();
            settings.Library.Folders = new List<string> { root };

            using var library = new MusicLibrary(dbPath);
            await library.ScanAsync(settings);
            Assert.Equal(fileCount, library.Count);

            // Every file is unchanged now, so this second pass only stats them and finishes well
            // inside one throttle window. Unthrottled it would raise one report per ten files.
            var reports = new List<ScanProgress>();
            library.ScanProgress += p => reports.Add(p);
            await library.ScanAsync(settings);

            int nonFinal = reports.Count(r => !r.Finished);
            Assert.InRange(nonFinal, 1, 4);
            Assert.Equal(1, reports.Count(r => r.Finished));

            var last = reports[^1];
            Assert.True(last.Finished, "The final report must arrive even when it lands inside the throttle window.");
            Assert.Equal(fileCount, last.Done);
            Assert.Equal(fileCount, last.Total);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FindFolderArt_SeveralTracksInOneDirectory_ProbesTheDirectoryOnce()
    {
        var root = NewTempDir("FolderArtMemo");
        try
        {
            var cover = Path.Combine(root, "cover.jpg");
            File.WriteAllBytes(cover, new byte[] { 1, 2, 3 });

            var memo = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var first = AlbumArtService.FindFolderArt(Path.Combine(root, "01.flac"), memo);

            Assert.Equal(cover, first);
            Assert.Equal(1, memo.Count);

            // Removing the cover makes a fresh probe observable: the memoized calls must keep
            // returning the first answer, and no second directory entry may appear.
            File.Delete(cover);

            for (int i = 2; i <= 5; i++)
            {
                Assert.Equal(cover, AlbumArtService.FindFolderArt(Path.Combine(root, $"{i:D2}.flac"), memo));
            }
            Assert.Equal(cover, TagReader.FindFolderArt(Path.Combine(root, "06.flac"), memo));
            Assert.Equal(1, memo.Count);

            Assert.Null(AlbumArtService.FindFolderArt(Path.Combine(root, "01.flac"), null));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ScanAsync_AlbumFolderWithCover_GivesEveryTrackTheSameFolderArt()
    {
        var root = NewTempDir("FolderArtScan");
        var dbPath = Path.Combine(root, "art.db");
        try
        {
            var cover = Path.Combine(root, "cover.jpg");
            File.WriteAllBytes(cover, new byte[] { 9, 9, 9 });
            for (int i = 0; i < 4; i++)
            {
                File.WriteAllBytes(Path.Combine(root, $"t{i}.wav"), MinimalWav());
            }

            var settings = AppSettings.CreateDefault();
            settings.Library.Folders = new List<string> { root };

            using var library = new MusicLibrary(dbPath);
            await library.ScanAsync(settings);

            Assert.Equal(4, library.Count);
            Assert.All(library.Tracks, t => Assert.Equal(cover, t.ArtPath));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void MusicLibrary_NewDatabase_StampsSchemaVersionOne()
    {
        var root = NewTempDir("SchemaVersion");
        var dbPath = Path.Combine(root, "version.db");
        try
        {
            using (var created = new MusicLibrary(dbPath))
            {
                Assert.Equal(1, created.DatabaseSchemaVersion);
            }

            using var reopened = new MusicLibrary(dbPath);
            Assert.Equal(1, reopened.DatabaseSchemaVersion);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task LoadFromDb_AfterScan_RoundTripsEveryColumn()
    {
        var root = NewTempDir("ColumnRoundTrip");
        var dbPath = Path.Combine(root, "columns.db");
        try
        {
            var file = Path.Combine(root, "roundtrip.wav");
            File.WriteAllBytes(file, MinimalWav());

            var settings = AppSettings.CreateDefault();
            settings.Library.Folders = new List<string> { root };

            Track? scanned;
            using (var library = new MusicLibrary(dbPath))
            {
                await library.ScanAsync(settings);
                scanned = library.GetTrack(file);
            }

            Assert.NotNull(scanned);

            using var reopened = new MusicLibrary(dbPath);
            reopened.LoadFromDb();
            var loaded = reopened.GetTrack(file);

            Assert.NotNull(loaded);
            // Record equality compares every column, so a SELECT list that drifts out of step with
            // ReadTrack's ordinals fails here instead of silently shifting values.
            Assert.Equal(scanned, loaded);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // The batch size is private; forcing it small keeps the fixture at a dozen files instead of the
    // thousands a 500-row batch would need.
    private static void SetUpsertBatchSize(MusicLibrary library, int size) =>
        typeof(MusicLibrary)
            .GetField("_upsertBatchSize", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(library, size);

    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DawnPlayer_ScanHarden_{tag}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
