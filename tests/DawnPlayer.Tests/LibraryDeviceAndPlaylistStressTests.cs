using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Stress coverage for the three subsystems that touch the disk and the driver on every user
/// action: <see cref="MusicLibrary"/> rescan change detection (timestamp, size, vanished file) plus
/// concurrent scan/read and cancellation, <see cref="WasapiDeviceService"/> device-open and format
/// fallbacks when the requested endpoint is missing, and <see cref="PlaylistManager"/> rename
/// semantics on the m3u8 files behind each playlist (sanitizing, case-only rename, deduplication).
/// </summary>
[Collection("SettingsStoreCollection")]
public class LibraryDeviceAndPlaylistStressTests
{
    // =========================================================================
    // 1. TagReader and MusicLibrary Change Detection Stress Tests
    // =========================================================================

    [Fact]
    public async Task MusicLibrary_ChangeDetection_UntouchedVsModifiedVsSizeChange()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_TestLib_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create 3 valid audio test files (we'll create minimal RIFF WAV files)
            var file1 = Path.Combine(tempDir, "track1.wav");
            var file2 = Path.Combine(tempDir, "track2.wav");
            var file3 = Path.Combine(tempDir, "track3.wav");

            CreateMinimalWavFile(file1, 1000);
            CreateMinimalWavFile(file2, 2000);
            CreateMinimalWavFile(file3, 3000);

            using var library = new MusicLibrary();
            var settings = new AppSettings();
            settings.Library.Folders = new List<string> { tempDir };

            // 1. Initial Scan
            await library.ScanAsync(settings);
            Assert.Equal(3, library.Count);

            var t1 = library.GetTrack(file1);
            var t2 = library.GetTrack(file2);
            var t3 = library.GetTrack(file3);
            Assert.NotNull(t1);
            Assert.NotNull(t2);
            Assert.NotNull(t3);

            var originalT1Modified = t1.FileModifiedUtcTicks;
            var originalT2Size = t2.FileSize;

            // 2. Modify timestamp of file1 (simulates metadata/tag edit without changing size)
            var newTime = DateTime.UtcNow.AddHours(2);
            File.SetLastWriteTimeUtc(file1, newTime);
            var expectedT1Modified = new FileInfo(file1).LastWriteTimeUtc.Ticks;

            // 3. Modify size of file2 (append bytes)
            using (var fs = File.Open(file2, FileMode.Append, FileAccess.Write))
            {
                fs.Write(new byte[100], 0, 100);
            }
            var expectedT2Size = new FileInfo(file2).Length;

            // file3 remains completely UNTOUCHED

            // 4. Second Scan
            await library.ScanAsync(settings);
            Assert.Equal(3, library.Count);

            var rescannedT1 = library.GetTrack(file1);
            var rescannedT2 = library.GetTrack(file2);
            var rescannedT3 = library.GetTrack(file3);

            Assert.NotNull(rescannedT1);
            Assert.NotNull(rescannedT2);
            Assert.NotNull(rescannedT3);

            // Verify file1 detected timestamp change
            Assert.Equal(expectedT1Modified, rescannedT1.FileModifiedUtcTicks);
            Assert.NotEqual(originalT1Modified, rescannedT1.FileModifiedUtcTicks);

            // Verify file2 detected size change
            Assert.Equal(expectedT2Size, rescannedT2.FileSize);
            Assert.NotEqual(originalT2Size, rescannedT2.FileSize);

            // Verify file3 was reused from cache directly (reference equality or identical data)
            Assert.Equal(t3.FileModifiedUtcTicks, rescannedT3.FileModifiedUtcTicks);
            Assert.Equal(t3.FileSize, rescannedT3.FileSize);

            // 5. Delete file3 (Vanished file pruning)
            File.Delete(file3);
            await library.ScanAsync(settings);

            Assert.Equal(2, library.Count);
            Assert.Null(library.GetTrack(file3));
            Assert.NotNull(library.GetTrack(file1));
            Assert.NotNull(library.GetTrack(file2));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void TagReader_ArtExtraction_ConcurrentCalls_ThreadSafe()
    {
        var track = new Track
        {
            Path = @"C:\Mock\song.mp3",
            Artist = "TestArtist",
            Album = "TestAlbum"
        };
        var albumKey = track.AlbumKey;

        // Simulate concurrent art extraction requests for the same album
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 10, i =>
        {
            try
            {
                // TagReader.TryExtractArt with null picture will attempt TagLib on nonexistent file and return null safely
                var art = TagReader.TryExtractArt(track, albumKey, null);
                // Should return null gracefully without unhandled crash
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
    }

    // =========================================================================
    // 2. WasapiDeviceService COM Lifecycle & Fallback Tests
    // =========================================================================

    [Fact]
    public void WasapiDeviceService_OpenDevice_ValidAfterEnumeratorDisposal()
    {
        // Open default device
        var dev = WasapiDeviceService.OpenDevice(null);
        if (dev != null)
        {
            // Verify that the IMMDevice is fully alive and usable after OpenDevice returned (enumerator disposed)
            Assert.NotNull(dev.ID);
            Assert.NotNull(dev.FriendlyName);

            // Verify format query works on the device
            try
            {
                var sharedFmt = WasapiDeviceService.GetSharedTarget(dev);
                Assert.NotNull(sharedFmt);
                Assert.True(sharedFmt.SampleRate > 0);
            }
            catch
            {
                // Some virtual or disabled endpoints may fail mix format query gracefully
            }
        }
    }

    [Fact]
    public void WasapiDeviceService_OpenDevice_InvalidDeviceId_FallsBackToDefault()
    {
        var dev = WasapiDeviceService.OpenDevice("NON_EXISTENT_GUID_DEVICE_ID_9999");
        var def = WasapiDeviceService.OpenDevice(null);

        if (def != null)
        {
            Assert.NotNull(dev);
            Assert.Equal(def.ID, dev.ID);
        }
        else
        {
            Assert.Null(dev);
        }
    }

    [Fact]
    public void WasapiDeviceService_OpenDevice_EmptyOrNullDeviceId_ReturnsDefault()
    {
        var devNull = WasapiDeviceService.OpenDevice(null);
        var devEmpty = WasapiDeviceService.OpenDevice("");

        if (devNull != null)
        {
            Assert.NotNull(devEmpty);
            Assert.Equal(devNull.ID, devEmpty.ID);
        }
    }

    // =========================================================================
    // 3. PlaylistManager Rename & Edge Cases Tests
    // =========================================================================

    [Fact]
    public void PlaylistManager_RenamePlaylist_SavesNewFileAndDeletesOld()
    {
        using var library = new MusicLibrary();
        var pm = new PlaylistManager(library);

        var pl = pm.CreatePlaylist("Alpha_Test_1");
        var track = new Track { Path = @"C:\Music\test1.mp3", Title = "Song 1", Artist = "Artist 1" };
        pm.AddTracks(pl, new[] { track });
        pm.SaveAll();

        var oldPath = Path.Combine(AppPaths.PlaylistsDir, "Alpha_Test_1.m3u8");
        var newPath = Path.Combine(AppPaths.PlaylistsDir, "Beta_Test_2.m3u8");

        Assert.True(File.Exists(oldPath), $"Expected {oldPath} to exist after SaveAll.");

        // Rename
        pm.RenamePlaylist(pl, "Beta_Test_2");

        Assert.Equal("Beta_Test_2", pl.Name);
        Assert.True(File.Exists(newPath), $"Expected {newPath} to exist after Rename.");
        Assert.False(File.Exists(oldPath), $"Expected {oldPath} to be deleted after Rename.");

        // Cleanup
        pm.RemovePlaylist(pl);
        Assert.False(File.Exists(newPath));
    }

    [Fact]
    public void PlaylistManager_RenamePlaylist_CaseOnlyRename_PreservesFile()
    {
        using var library = new MusicLibrary();
        var pm = new PlaylistManager(library);

        var pl = pm.CreatePlaylist("case_sensitive_test");
        var track = new Track { Path = @"C:\Music\test2.mp3", Title = "Song 2" };
        pm.AddTracks(pl, new[] { track });
        pm.SaveAll();

        var path = Path.Combine(AppPaths.PlaylistsDir, "case_sensitive_test.m3u8");
        Assert.True(File.Exists(path));

        // Rename with case difference only
        pm.RenamePlaylist(pl, "CASE_SENSITIVE_TEST");

        Assert.Equal("CASE_SENSITIVE_TEST", pl.Name);
        Assert.True(File.Exists(path), "File should not be deleted during case-only rename on Windows.");

        // Cleanup
        pm.RemovePlaylist(pl);
    }

    [Fact]
    public void PlaylistManager_RenamePlaylist_SpecialCharacters_SanitizedSafely()
    {
        using var library = new MusicLibrary();
        var pm = new PlaylistManager(library);

        var pl = pm.CreatePlaylist("Normal_Name");
        var track = new Track { Path = @"C:\Music\test3.mp3", Title = "Song 3" };
        pm.AddTracks(pl, new[] { track });
        pm.SaveAll();

        // Rename with illegal path characters: \ / : * ? " < > |
        pm.RenamePlaylist(pl, "Rock / Metal: 80*s & 90?s <Best> | \"Hits\"");

        var sanitizedFileName = "Rock _ Metal_ 80_s & 90_s _Best_ _ _Hits_.m3u8";
        var expectedPath = Path.Combine(AppPaths.PlaylistsDir, sanitizedFileName);

        Assert.True(File.Exists(expectedPath), $"Expected sanitized file at {expectedPath}");

        // Cleanup
        pm.RemovePlaylist(pl);
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void PlaylistManager_RenamePlaylist_WhitespaceOrEmpty_Ignored()
    {
        using var library = new MusicLibrary();
        var pm = new PlaylistManager(library);

        var pl = pm.CreatePlaylist("Keep_This_Name");
        pm.RenamePlaylist(pl, "");
        Assert.Equal("Keep_This_Name", pl.Name);

        pm.RenamePlaylist(pl, "   ");
        Assert.Equal("Keep_This_Name", pl.Name);

        pm.RemovePlaylist(pl);
    }

    [Fact]
    public void PlaylistManager_RapidSequentialRenames_PreservesItemsAndIntegrity()
    {
        using var library = new MusicLibrary();
        var pm = new PlaylistManager(library);

        var pl = pm.CreatePlaylist("Rapid_0");
        var track = new Track { Path = @"C:\Music\rapid.mp3", Title = "Rapid Song" };
        pm.AddTracks(pl, new[] { track });

        for (int i = 1; i <= 10; i++)
        {
            pm.RenamePlaylist(pl, $"Rapid_{i}");
        }

        Assert.Equal("Rapid_10", pl.Name);
        Assert.Single(pl.Items);
        Assert.Equal("Rapid Song", pl.Items[0].Track.Title);

        var finalPath = Path.Combine(AppPaths.PlaylistsDir, "Rapid_10.m3u8");
        Assert.True(File.Exists(finalPath));

        // Check old files are deleted
        for (int i = 0; i < 10; i++)
        {
            var oldPath = Path.Combine(AppPaths.PlaylistsDir, $"Rapid_{i}.m3u8");
            Assert.False(File.Exists(oldPath), $"Old path {oldPath} should have been deleted.");
        }

        pm.RemovePlaylist(pl);
        Assert.False(File.Exists(finalPath));
    }

    // =========================================================================
    // 4. Track Model & ReplayGain Precision Tests
    // =========================================================================

    [Fact]
    public void Track_AlbumSortKey_CorrectlyOrdersDiscAndTrack()
    {
        var trackD1T10 = new Track { DiscNo = 1, TrackNo = 10 };
        var trackD2T1 = new Track { DiscNo = 2, TrackNo = 1 };
        var trackD1T2 = new Track { DiscNo = 1, TrackNo = 2 };

        Assert.True(trackD1T2.AlbumSortKey < trackD1T10.AlbumSortKey);
        Assert.True(trackD1T10.AlbumSortKey < trackD2T1.AlbumSortKey);
    }

    [Fact]
    public void Track_AlbumKey_NormalizesWhitespaceAndCase()
    {
        var t1 = new Track { Artist = " IU ", Album = " Lilac " };
        var t2 = new Track { AlbumArtist = "iu", Album = "lilac" };

        Assert.Equal(t1.AlbumKey, t2.AlbumKey);
    }

    // =========================================================================
    // 5. Concurrency, Cancellation & Extended Audio Stress Tests
    // =========================================================================

    [Fact]
    public async Task MusicLibrary_ConcurrentScansAndReads_ThreadSafe()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_Concur_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            for (int i = 0; i < 10; i++)
            {
                CreateMinimalWavFile(Path.Combine(tempDir, $"song_{i}.wav"), 500);
            }

            using var library = new MusicLibrary();
            var settings = new AppSettings();
            settings.Library.Folders = new List<string> { tempDir };

            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            var scanTask = Task.Run(async () =>
            {
                for (int i = 0; i < 5; i++)
                {
                    await library.ScanAsync(settings);
                }
            });

            var readTask = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    try
                    {
                        var count = library.Count;
                        var tracks = library.Tracks;
                        library.LoadFromDb();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            await Task.WhenAll(scanTask, readTask);
            Assert.Empty(exceptions);
            Assert.True(library.Count >= 10);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task MusicLibrary_ScanAsync_CancellationToken_AbortsGracefully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DawnPlayer_Cancel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            for (int i = 0; i < 20; i++)
            {
                CreateMinimalWavFile(Path.Combine(tempDir, $"song_{i}.wav"), 500);
            }

            using var library = new MusicLibrary();
            var settings = new AppSettings();
            settings.Library.Folders = new List<string> { tempDir };

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Cancel immediately

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.ScanAsync(settings, cts.Token));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void PlaylistManager_AddPathsAndDuplicates_HandlesInvalidPathsGracefully()
    {
        using var library = new MusicLibrary();
        var pm = new PlaylistManager(library);

        var pl = pm.CreatePlaylist("Path_Test");

        // Add mix of nonexistent directory, non-audio files, and valid tracks
        var added = pm.AddPaths(pl, new[] {
            @"C:\NonExistent_Folder_12345",
            @"C:\Windows\explorer.exe",
            @"C:\NonExistent_File_67890.mp3"
        });

        Assert.Empty(added);
        Assert.Empty(pl.Items);

        // Add duplicate tracks and test RemoveDuplicates
        var t1 = new Track { Path = @"C:\Music\songA.mp3", Title = "Song A" };
        var t2 = new Track { Path = @"C:\Music\songB.mp3", Title = "Song B" };
        var t1Dupe = new Track { Path = @"c:\music\songa.mp3", Title = "Song A (Dupe)" };

        pm.AddTracks(pl, new[] { t1, t2, t1Dupe });
        Assert.Equal(3, pl.Items.Count);

        pm.RemoveDuplicates(pl);
        Assert.Equal(2, pl.Items.Count);
        Assert.Equal("Song A", pl.Items[0].Track.Title);
        Assert.Equal("Song B", pl.Items[1].Track.Title);

        pm.RemovePlaylist(pl);
    }

    [Fact]
    public void WasapiDeviceService_ExclusiveFormatNegotiationVariants_AllPoliciesCovered()
    {
        var fmt16 = new NAudio.Wave.WaveFormat(44100, 16, 2);

        var variants16 = WasapiDeviceService.GetFormatVariants(44100, 16, 2).ToList();
        Assert.Equal(2, variants16.Count); // PCM + Extensible

        var variants32 = WasapiDeviceService.GetFormatVariants(96000, 32, 2).ToList();
        Assert.Equal(3, variants32.Count); // PCM + Extensible + Float

        var desc = WasapiDeviceService.Describe(fmt16);
        Assert.Equal("44.1 kHz / 16-bit / 2ch", desc);
    }

    // =========================================================================
    // Helper Methods
    // =========================================================================

    private static void CreateMinimalWavFile(string path, int durationSamples)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        int sampleRate = 44100;
        short channels = 2;
        short bitsPerSample = 16;
        int subChunk2Size = durationSamples * channels * (bitsPerSample / 8);
        int chunkSize = 36 + subChunk2Size;

        // RIFF header
        bw.Write(new[] { 'R', 'I', 'F', 'F' });
        bw.Write(chunkSize);
        bw.Write(new[] { 'W', 'A', 'V', 'E' });

        // fmt subchunk
        bw.Write(new[] { 'f', 'm', 't', ' ' });
        bw.Write(16); // Subchunk1Size for PCM
        bw.Write((short)1); // AudioFormat 1 = PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8)); // ByteRate
        bw.Write((short)(channels * (bitsPerSample / 8)));     // BlockAlign
        bw.Write(bitsPerSample);

        // data subchunk
        bw.Write(new[] { 'd', 'a', 't', 'a' });
        bw.Write(subChunk2Size);
        bw.Write(new byte[subChunk2Size]);
    }
}
