using System.Collections.Concurrent;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.Core.Library;

/// <summary>
/// Service responsible for extracting, caching, and locating album artwork.
/// Provides thread-safe disk caching to prevent concurrent write collisions during parallel library scans.
/// </summary>
public static class AlbumArtService
{
    private static readonly string[] CoverNames = { "cover", "folder", "front", "albumart", "album", "artwork" };
    private static readonly string[] CoverExts = { ".jpg", ".jpeg", ".png" };

    // U+0001 is the artist/album separator inside every album key, so a missing track degenerates
    // to the "no artist, no album" key instead of throwing on a caller's stale reference.
    private static readonly string EmptyAlbumKey = new((char)1, 1);

    /// <summary>
    /// Returns the track's own album cache key, path-based fallback for untagged files included.
    /// The model owns the key so that art caching and album shuffle cannot disagree about which
    /// tracks belong to the same album.
    /// </summary>
    public static string ComputeAlbumKey(Track track) => track?.AlbumKey ?? EmptyAlbumKey;

    /// <summary>
    /// Extracts embedded album art and caches it under %AppData%/DawnPlayer/artcache.
    /// Uses atomic file writes to ensure thread safety during concurrent multi-track scans.
    /// </summary>
    public static string? TryExtractArt(Track track, string albumKey, TagLib.IPicture? picture = null)
    {
        try
        {
            var pic = picture;
            if (pic == null)
            {
                using var tf = TagLib.File.Create(track.Path);
                pic = tf.Tag.Pictures?
                    .Where(p => p.Data.Count > 0)
                    .OrderBy(p => p.Type == TagLib.PictureType.FrontCover ? 0 : 1)
                    .FirstOrDefault();
            }
            if (pic == null || pic.Data.Count == 0) return null;

            var ext = (pic.MimeType ?? "").Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            var dir = Util.AppPaths.ArtCacheDir;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var hash = TagReader.Sha1Hex(albumKey);
            var targetFile = Path.Combine(dir, hash + ext);
            try
            {
                if (File.Exists(targetFile) && new FileInfo(targetFile).Length > 0)
                {
                    return targetFile;
                }
            }
            catch { }

            try
            {
                // No fsync: this is a rebuildable art cache, so atomicity is worth the cost but
                // durability is not. A concurrent writer that already produced the file wins.
                AtomicFile.WriteAllBytes(targetFile, pic.Data.Data, keepBackup: false, flushToDisk: false);
                return targetFile;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        if (File.Exists(targetFile) && new FileInfo(targetFile).Length > 0)
                        {
                            return targetFile;
                        }
                    }
                    catch { }
                    Thread.Sleep(5);
                }
                return File.Exists(targetFile) ? targetFile : null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Looks for cover art files sitting next to the audio track.</summary>
    public static string? FindFolderArt(string trackPath) => FindFolderArt(trackPath, null);

    /// <summary>
    /// Looks for cover art files sitting next to the audio track, answering from
    /// <paramref name="folderArtCache"/> when the track's directory has already been probed.
    /// One probe is up to 18 <c>File.Exists</c> calls plus two directory enumerations, which every
    /// track of an album would otherwise repeat.
    /// </summary>
    /// <param name="folderArtCache">
    /// Directory → art path memo, or null to always probe. A cache must never outlive the single
    /// scan that created it: cover files can be added, replaced or removed between scans, and a
    /// longer-lived cache would keep serving whichever file it happened to find first.
    /// </param>
    public static string? FindFolderArt(string trackPath, ConcurrentDictionary<string, string?>? folderArtCache)
    {
        try
        {
            var dir = Path.GetDirectoryName(trackPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

            return folderArtCache is null
                ? ProbeFolderArt(dir)
                : folderArtCache.GetOrAdd(dir, static d => ProbeFolderArt(d));
        }
        catch
        {
            return null;
        }
    }

    private static string? ProbeFolderArt(string dir)
    {
        foreach (var n in CoverNames)
        {
            foreach (var e in CoverExts)
            {
                var p = Path.Combine(dir, n + e);
                if (File.Exists(p)) return p;
            }
        }

        return Directory.EnumerateFiles(dir, "*.jp*g")
            .Concat(Directory.EnumerateFiles(dir, "*.png"))
            .FirstOrDefault();
    }
}
