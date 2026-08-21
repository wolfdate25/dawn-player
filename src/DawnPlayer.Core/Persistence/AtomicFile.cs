using System;
using System.IO;
using System.Threading;

namespace DawnPlayer.Core.Persistence;

/// <summary>
/// Durable, atomic single-file writes: content goes to a temp file in the same directory, is
/// optionally flushed to the physical disk, then replaces the target in one rename.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="flushToDisk"/> matters for anything the user would notice losing. A rename is
/// atomic with respect to other readers, but without the flush the bytes may still be sitting in
/// the OS cache: a power cut right after the rename can leave a zero-length or half-written file
/// where valid content used to be.
/// </para>
/// <para>
/// <c>keepBackup</c> retains exactly one previous generation next to the target (<c>.bak</c>) so a
/// reader that finds unparseable content has something to fall back to.
/// </para>
/// <para>
/// There is deliberately no copy-based fallback when the rename cannot be completed: a partially
/// written target is worse than a stale one, so the write is reported as failed instead.
/// </para>
/// </remarks>
public static class AtomicFile
{
    /// <summary>Atomically writes UTF-8 text (no BOM).</summary>
    public static void WriteAllText(string path, string contents, bool keepBackup = true, bool flushToDisk = true) =>
        Write(path, stream =>
        {
            var bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
            stream.Write(bytes, 0, bytes.Length);
        }, keepBackup, flushToDisk);

    /// <summary>Atomically writes raw bytes.</summary>
    public static void WriteAllBytes(string path, byte[] bytes, bool keepBackup = false, bool flushToDisk = false) =>
        Write(path, stream => stream.Write(bytes, 0, bytes.Length), keepBackup, flushToDisk);

    /// <summary>
    /// Atomically writes whatever <paramref name="writer"/> emits into the temp stream. The stream
    /// is flushed and closed before the rename, so the callback must not retain it.
    /// </summary>
    public static void Write(string path, Action<Stream> writer, bool keepBackup = true, bool flushToDisk = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(writer);

        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath) ?? "";
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writer(stream);
                stream.Flush(flushToDisk);
            }

            ReplaceWithRetry(tempPath, fullPath, keepBackup);
            tempPath = null!;
        }
        finally
        {
            if (tempPath != null) DeleteBestEffort(tempPath);
        }
    }

    /// <summary>Deletes stale <c>*.tmp.*</c> leftovers for one target file (crash cleanup).</summary>
    public static void CleanupStaleTempFiles(string targetPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(targetPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            foreach (var tmp in Directory.EnumerateFiles(dir, Path.GetFileName(fullPath) + ".tmp.*"))
            {
                DeleteBestEffort(tmp);
            }
        }
        catch
        {
            // Directory access error — nothing to clean up then.
        }
    }

    private static void ReplaceWithRetry(string source, string destination, bool keepBackup, int maxRetries = 15)
    {
        var backupPath = keepBackup ? destination + ".bak" : null;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (File.Exists(destination))
                {
                    // File.Replace keeps the previous generation and is atomic on NTFS.
                    File.Replace(source, destination, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(source, destination);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxRetries - 1) throw;
                Thread.Sleep(5 + (attempt * 5));
            }
        }
    }

    private static void DeleteBestEffort(string path, int maxRetries = 10)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxRetries - 1) return;
                Thread.Sleep(5 + (attempt * 5));
            }
        }
    }
}
