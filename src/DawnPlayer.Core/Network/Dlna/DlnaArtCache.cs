using System.Net.Http;
using System.Security.Cryptography;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Network.Dlna;

/// <summary>
/// Downloads album art (upnp:albumArtURI) into the shared art cache so the existing local-file
/// art pipeline can use it. Content-addressed by URL hash — the same cover from a hundred tracks
/// costs one file — with a total-size cap enforced by deleting the least recently written files.
/// </summary>
public sealed class DlnaArtCache
{
    /// <summary>Cache budget. Album covers are ~50–500 KB, so this is hundreds of albums.</summary>
    public const long MaxTotalBytes = 64 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly object _gate = new();

    public DlnaArtCache(HttpClient? http = null, string? cacheDir = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _cacheDir = cacheDir ?? AppPaths.ArtCacheDir;
    }

    /// <summary>Local path for this URL's art, downloading on first use. Null on any failure —
    /// art is always optional. Thread-safe: concurrent callers for one URL share the result.</summary>
    public async Task<string?> GetOrDownloadAsync(Uri url, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = PathFor(url);
            lock (_gate)
            {
                if (File.Exists(path)) return path;
            }

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || !LooksLikeImage(bytes)) return null;

            lock (_gate)
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(_cacheDir);
                    AtomicFile.WriteAllBytes(path, bytes);
                }
            }
            EnforceBudget();
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"[dlna-art] download failed for '{url}': {ex.Message}");
            return null;
        }
    }

    internal string PathFor(Uri url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url.AbsoluteUri)));
        var ext = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png")) ext = ".jpg";
        return Path.Combine(_cacheDir, "dlna-" + hash + ext);
    }

    private static bool LooksLikeImage(byte[] bytes) => bytes.Length >= 4
        && (bytes[0] == 0xFF && bytes[1] == 0xD8               // JPEG
            || bytes[0] == 0x89 && bytes[1] == 0x50            // PNG
            || bytes[0] == 0x42 && bytes[1] == 0x4D);          // BMP

    private void EnforceBudget()
    {
        try
        {
            var dir = new DirectoryInfo(_cacheDir);
            if (!dir.Exists) return;

            long total = 0;
            var files = new List<(FileInfo File, DateTime Written)>();
            foreach (var file in dir.EnumerateFiles())
            {
                if (!file.Name.StartsWith("dlna-", StringComparison.Ordinal)) continue;
                total += file.Length;
                files.Add((file, file.LastWriteTimeUtc));
            }
            if (total <= MaxTotalBytes) return;

            foreach (var (file, _) in files.OrderBy(f => f.Written))
            {
                if (total <= MaxTotalBytes) break;
                try
                {
                    total -= file.Length;
                    file.Delete();
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[dlna-art] budget sweep failed: {ex.Message}");
        }
    }
}
