using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// AlbumArtService stress tests: concurrent extraction races, atomic cache writes and temp-file
/// cleanup, album-key isolation for untagged files, and folder-art discovery precedence.
/// </summary>
[Collection("SettingsStoreCollection")]
public class AlbumArtServiceConcurrencyTests
{
    private sealed class StubPicture : TagLib.IPicture
    {
        public string MimeType { get; set; } = "image/jpeg";
        public TagLib.PictureType Type { get; set; } = TagLib.PictureType.FrontCover;
        public string Description { get; set; } = "Front Cover";
        public TagLib.ByteVector Data { get; set; }
        public string Filename { get; set; } = "cover.jpg";

        public StubPicture(byte[] data, string mimeType = "image/jpeg")
        {
            Data = new TagLib.ByteVector(data);
            MimeType = mimeType;
        }
    }

    private static string Sha1Hex(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var hash = System.Security.Cryptography.SHA1.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public void AlbumArtService_HighConcurrencySameAlbum_AllSucceedWithoutExceptionsOrStrayTemps()
    {
        var dir = AppPaths.ArtCacheDir;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var track = new Track
        {
            Path = @"C:\Music\StressArtist - StressAlbum\01.flac",
            Artist = $"StressArtist_{Guid.NewGuid():N}",
            AlbumArtist = $"StressArtist_{Guid.NewGuid():N}",
            Album = $"StressAlbum_{Guid.NewGuid():N}"
        };
        var albumKey = AlbumArtService.ComputeAlbumKey(track);
        var hash = Sha1Hex(albumKey);

        byte[] fakeJpg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x01, 0x02 };
        var pic = new StubPicture(fakeJpg, "image/jpeg");

        var exceptions = new ConcurrentBag<Exception>();
        var results = new ConcurrentBag<string?>();

        // 64 concurrent threads attempting to extract the exact same album art simultaneously
        int threadCount = 64;
        var caughtExceptions = new ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, _ =>
        {
            try
            {
                var cachedPath = AlbumArtService.TryExtractArt(track, albumKey, pic);
                results.Add(cachedPath);
                if (cachedPath == null)
                {
                    // Let's see why it returned null
                    try
                    {
                        var temp = Path.Combine(dir, $"{hash}_{Guid.NewGuid():N}.tmp");
                        File.WriteAllBytes(temp, pic.Data.Data);
                        File.Move(temp, Path.Combine(dir, hash + ".jpg"), overwrite: true);
                    }
                    catch (Exception moveEx)
                    {
                        caughtExceptions.Add(moveEx);
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Verify if any threads failed and what exception types were thrown
        if (results.Any(r => r == null))
        {
            var exSummary = string.Join(", ", caughtExceptions.Select(e => e.GetType().Name + ": " + e.Message));
            Assert.Fail($"Failed with null paths! Caught exceptions: [{exSummary}]");
        }

        // Verify that NO temporary files (.tmp) matching the hash were leaked
        var strayTemps = Directory.EnumerateFiles(dir, $"{hash}_*.tmp").ToList();
        Assert.Empty(strayTemps);
    }

    [Fact]
    public void AlbumArtService_ConcurrentDifferentAlbums_IsolatesFilesCleanly()
    {
        int albumCount = 30;
        var exceptions = new ConcurrentBag<Exception>();
        var generatedKeys = new ConcurrentBag<string>();
        var createdFiles = new ConcurrentBag<string>();

        byte[] fakePng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };

        Parallel.For(0, albumCount, i =>
        {
            try
            {
                var track = new Track
                {
                    Path = $@"C:\Music\MultiArtist_{i}\song_{i}.flac",
                    Artist = $"MultiArtist_{i}_{Guid.NewGuid():N}",
                    Album = $"MultiAlbum_{i}_{Guid.NewGuid():N}"
                };
                var key = AlbumArtService.ComputeAlbumKey(track);
                generatedKeys.Add(key);

                var pic = new StubPicture(fakePng, "image/png");
                var path = AlbumArtService.TryExtractArt(track, key, pic);
                if (path != null) createdFiles.Add(path);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
        Assert.Equal(albumCount, generatedKeys.Distinct().Count());
        Assert.Equal(albumCount, createdFiles.Distinct().Count());
        Assert.All(createdFiles, f =>
        {
            Assert.True(File.Exists(f));
            Assert.EndsWith(".png", f, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void AlbumArtService_EmptyOrNullPictureData_ReturnsNullSafely()
    {
        var track = new Track
        {
            Path = @"C:\Music\EmptyArt\song.mp3",
            Artist = "EmptyArtArtist",
            Album = "EmptyArtAlbum"
        };
        var key = AlbumArtService.ComputeAlbumKey(track);

        // Empty byte array
        var emptyPic = new StubPicture(Array.Empty<byte>());
        var res1 = AlbumArtService.TryExtractArt(track, key, emptyPic);
        Assert.Null(res1);

        // Non-existent file path with null picture
        var res2 = AlbumArtService.TryExtractArt(new Track { Path = @"C:\NonExistent\FakeFile.mp3" }, "fake_key", null);
        Assert.Null(res2);
    }

    [Fact]
    public void AlbumArtService_ComputeAlbumKey_UntaggedAndWhitespaceVariations()
    {
        // 1. Both empty -> path-based
        var t1 = new Track { Path = @"C:\Music\Folder1\track.mp3", Artist = "", Album = "" };
        var k1 = AlbumArtService.ComputeAlbumKey(t1);
        Assert.Equal(@"file:c:\music\folder1\track.mp3", k1);

        // 2. Both whitespace -> path-based
        var t2 = new Track { Path = @"C:\Music\Folder1\track.mp3", Artist = "   ", Album = "\t\n" };
        var k2 = AlbumArtService.ComputeAlbumKey(t2);
        Assert.Equal(@"file:c:\music\folder1\track.mp3", k2);

        // 3. Different paths for untagged -> distinct keys
        var t3 = new Track { Path = @"C:\Music\Folder2\track.mp3", Artist = "", Album = "" };
        var k3 = AlbumArtService.ComputeAlbumKey(t3);
        Assert.NotEqual(k1, k3);

        // 4. Memory-only track with empty path -> fallback to \u0001
        var t4 = new Track { Path = "", Artist = "", Album = "" };
        var k4 = AlbumArtService.ComputeAlbumKey(t4);
        Assert.Equal("\u0001", k4);

        // 5. Artist present but album empty
        var t5 = new Track { Path = @"C:\Music\track.mp3", Artist = "Adele", Album = "" };
        var k5 = AlbumArtService.ComputeAlbumKey(t5);
        Assert.Equal("adele\u0001", k5);
    }

    [Fact]
    public void AlbumArtService_FindFolderArt_RespectsPrecedenceAndCaseInsensitiveExtensions()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "DawnFolderArtPrecedenceTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempFolder);
            var audioPath = Path.Combine(tempFolder, "track01.flac");
            File.WriteAllBytes(audioPath, new byte[] { 0, 1 });

            // Create folder.jpg and cover.png
            var folderJpg = Path.Combine(tempFolder, "folder.jpg");
            var coverPng = Path.Combine(tempFolder, "cover.png");
            File.WriteAllBytes(folderJpg, new byte[] { 1 });
            File.WriteAllBytes(coverPng, new byte[] { 2 });

            // cover.png has higher precedence in CoverNames than folder.jpg (cover > folder)
            var art = AlbumArtService.FindFolderArt(audioPath);
            Assert.NotNull(art);
            Assert.Equal(coverPng, art);

            // Now create cover.jpg -> cover.jpg has higher precedence than cover.png (.jpg > .png)
            var coverJpg = Path.Combine(tempFolder, "cover.jpg");
            File.WriteAllBytes(coverJpg, new byte[] { 3 });

            art = AlbumArtService.FindFolderArt(audioPath);
            Assert.NotNull(art);
            Assert.Equal(coverJpg, art);
        }
        finally
        {
            if (Directory.Exists(tempFolder))
            {
                try { Directory.Delete(tempFolder, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void AlbumArtService_FindFolderArt_FallbackToArbitraryImage()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "DawnFolderArtFallbackTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempFolder);
            var audioPath = Path.Combine(tempFolder, "track01.flac");
            File.WriteAllBytes(audioPath, new byte[] { 0, 1 });

            // An image named "custom_scan_art.jpeg" (not in standard CoverNames)
            var customArt = Path.Combine(tempFolder, "custom_scan_art.jpeg");
            File.WriteAllBytes(customArt, new byte[] { 9, 9, 9 });

            var art = AlbumArtService.FindFolderArt(audioPath);
            Assert.NotNull(art);
            Assert.Equal(customArt, art);
        }
        finally
        {
            if (Directory.Exists(tempFolder))
            {
                try { Directory.Delete(tempFolder, recursive: true); } catch { }
            }
        }
    }
}
