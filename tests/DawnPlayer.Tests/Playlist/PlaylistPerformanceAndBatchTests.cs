using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Xunit;

namespace DawnPlayer.Tests.Playlists;

public class PlaylistPerformanceAndBatchTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MusicLibrary _library;
    private readonly PlaylistManager _manager;

    public PlaylistPerformanceAndBatchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DawnTest_Pl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var dbPath = Path.Combine(_tempDir, "test_lib.db");
        _library = new MusicLibrary(dbPath);
        _manager = new PlaylistManager(_library);
    }

    public void Dispose()
    {
        _library.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private static void CreateMinimalWav(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + 100); // chunk size
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16); // subchunk1 size (16 for PCM)
        writer.Write((short)1); // audio format (1 = PCM)
        writer.Write((short)2); // num channels (2)
        writer.Write(44100); // sample rate
        writer.Write(44100 * 2 * 2); // byte rate
        writer.Write((short)4); // block align
        writer.Write((short)16); // bits per sample
        writer.Write("data"u8);
        writer.Write(100); // subchunk2 size
        writer.Write(new byte[100]); // audio data
    }

    private static Track CreateTrack(string title, long durationMs, string? path = null)
    {
        return new Track
        {
            Title = title,
            Artist = "Test Artist",
            Album = "Test Album",
            DurationMs = durationMs,
            Path = path ?? $@"C:\music\{title}.wav"
        };
    }

    [Fact]
    public void Playlist_TotalDuration_MaintainsExactDurationAcrossBatchOperations()
    {
        var pl = new DawnPlayer.Core.Playlists.Playlist("DurationTest");
        Assert.Equal(TimeSpan.Zero, pl.TotalDuration);

        // 1. AddRange
        var tracks = Enumerable.Range(1, 1000)
            .Select(i => new PlaylistItem(CreateTrack($"T{i}", 1000))) // each 1000ms
            .ToList();

        pl.Items.AddRange(tracks);
        Assert.Equal(TimeSpan.FromSeconds(1000), pl.TotalDuration);

        // 2. RemoveRange
        var toRemove = tracks.Take(300).ToList();
        pl.Items.RemoveRange(toRemove);
        Assert.Equal(TimeSpan.FromSeconds(700), pl.TotalDuration);

        // 3. Single Add
        pl.Items.Add(new PlaylistItem(CreateTrack("Bonus", 50000)));
        Assert.Equal(TimeSpan.FromSeconds(750), pl.TotalDuration);

        // 4. Single Remove
        var last = pl.Items[^1];
        pl.Items.Remove(last);
        Assert.Equal(TimeSpan.FromSeconds(700), pl.TotalDuration);

        // 5. ReplaceAll
        pl.Items.ReplaceAll(new[] { new PlaylistItem(CreateTrack("Solo", 12000)) });
        Assert.Equal(TimeSpan.FromSeconds(12), pl.TotalDuration);

        // 6. Clear
        pl.Items.Clear();
        Assert.Equal(TimeSpan.Zero, pl.TotalDuration);
    }

    [Fact]
    public void Playlist_StressBatch20000Tracks_CompletesSubMillisecondDurationCalculations()
    {
        var pl = new DawnPlayer.Core.Playlists.Playlist("StressPl");
        var largeBatch = Enumerable.Range(1, 20000)
            .Select(i => new PlaylistItem(CreateTrack($"Track_{i}", 180000))) // 3 minutes each
            .ToList();

        var sw = Stopwatch.StartNew();
        pl.Items.AddRange(largeBatch);
        sw.Stop();

        // 20,000 tracks added in batch
        Assert.Equal(20000, pl.Items.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(20000L * 180000L), pl.TotalDuration);
        Assert.True(sw.ElapsedMilliseconds < 500, $"Batch addition took too long: {sw.ElapsedMilliseconds}ms");

        // O(1) duration read
        sw.Restart();
        for (int i = 0; i < 10000; i++)
        {
            var d = pl.TotalDuration;
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100, $"O(1) duration access took too long: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void PlaylistManager_CreatePlaylistFromTracks_InitializesPlaylistWithTracks()
    {
        var tracks = new[]
        {
            CreateTrack("Track 1", 3000),
            CreateTrack("Track 2", 4000),
            CreateTrack("Track 3", 5000)
        };

        var pl = _manager.CreatePlaylistFromTracks("Custom Playlist", tracks);

        Assert.Equal("Custom Playlist", pl.Name);
        Assert.Equal(3, pl.Items.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(12000), pl.TotalDuration);
        Assert.Contains(pl, _manager.Playlists);
    }

    [Fact]
    public async Task PlaylistManager_AddPathsAsync_ConcurrentTagIngestion()
    {
        var pl = _manager.CreatePlaylist("AsyncPathsTest");

        // Create valid audio files
        var filePaths = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            var f = Path.Combine(_tempDir, $"dummy_{i}.wav");
            CreateMinimalWav(f);
            filePaths.Add(f);
        }

        var added = await _manager.AddPathsAsync(pl, filePaths);

        Assert.Equal(20, added.Count);
        Assert.Equal(20, pl.Items.Count);
    }

    [Fact]
    public async Task M3u_WriteAndRead_AtomicWritingPreservesIntegrity()
    {
        var m3uPath = Path.Combine(_tempDir, "export_test.m3u8");
        var items = new[]
        {
            new PlaylistItem(CreateTrack("Song A", 185200, @"C:\Music\SongA.flac")),
            new PlaylistItem(CreateTrack("Song B", 210000, @"C:\Music\SongB.flac"))
        };

        await Task.Run(() => M3u.Write(m3uPath, items, "Exported Test"));
        Assert.True(File.Exists(m3uPath));

        // Read
        var entries = M3u.Read(m3uPath);
        Assert.Equal(2, entries.Count);
        Assert.Equal(@"C:\Music\SongA.flac", entries[0].Path);
        Assert.Equal(@"C:\Music\SongB.flac", entries[1].Path);
        Assert.Equal("Test Artist - Song A", entries[0].Title);
        Assert.Equal("Test Artist - Song B", entries[1].Title);
    }

    [Fact]
    public async Task PlaylistManager_ImportPlaylistAsync_CreatesPlaylistFromM3u()
    {
        var m3uPath = Path.Combine(_tempDir, "import_test.m3u8");

        // Create valid audio files
        var target1 = Path.Combine(_tempDir, "audio1.wav");
        var target2 = Path.Combine(_tempDir, "audio2.wav");
        CreateMinimalWav(target1);
        CreateMinimalWav(target2);

        var items = new[]
        {
            new PlaylistItem(CreateTrack("Audio 1", 100000, target1)),
            new PlaylistItem(CreateTrack("Audio 2", 150000, target2))
        };

        M3u.Write(m3uPath, items, "Imported Collection");

        var importedPl = await _manager.ImportPlaylistAsync(m3uPath, "Imported Collection");

        Assert.NotNull(importedPl);
        Assert.Equal("Imported Collection", importedPl!.Name);
        Assert.Equal(2, importedPl.Items.Count);
    }

    [Fact]
    public void AlbumGroup_Duration_IsCachedAndCalculatesAccurately()
    {
        var group = new AlbumGroup
        {
            Album = "Greatest Hits",
            Artist = "Master Band",
            Year = 2024
        };

        Assert.Equal(TimeSpan.Zero, group.Duration);

        group.AddItem(new PlaylistItem(CreateTrack("Hit 1", 60000)));
        group.AddItem(new PlaylistItem(CreateTrack("Hit 2", 120000)));

        Assert.Equal(2, group.Count);
        Assert.Equal(TimeSpan.FromMinutes(3), group.Duration);
        Assert.Equal("3 min 0 s", group.DurationFormatted);
    }
}
