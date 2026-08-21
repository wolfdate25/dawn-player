using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Persistence;

public sealed record M3uEntry(string Path, string? Title, double? DurationSeconds);

/// <summary>High-performance streaming M3U / M3U8 reader &amp; atomic writer.</summary>
public static class M3u
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    static M3u()
    {
        // Needed for Encoding.GetEncoding(0) (the system ANSI code page) on .NET Core.
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
    }

    /// <summary>
    /// Picks the encoding to read a playlist with. A BOM always wins. `.m3u8` is UTF-8 by
    /// definition. A plain `.m3u` from foobar2000/Winamp is usually in the system code page, and
    /// decoding those bytes as UTF-8 replaced every non-ASCII path character with U+FFFD, so every
    /// File.Exists failed and the import produced a silently empty playlist.
    /// </summary>
    private static Encoding DetectEncoding(string file, FileStream stream)
    {
        try
        {
            Span<byte> bom = stackalloc byte[4];
            int read = stream.Read(bom);
            stream.Position = 0;

            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
            if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
            if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;

            if (string.Equals(Path.GetExtension(file), ".m3u8", StringComparison.OrdinalIgnoreCase))
                return Encoding.UTF8;

            // No BOM and not .m3u8: accept UTF-8 only if the bytes really are valid UTF-8.
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var buffer = new byte[Math.Min(stream.Length, 1 << 16)];
            int got = stream.Read(buffer, 0, buffer.Length);
            stream.Position = 0;
            try
            {
                strict.GetString(buffer, 0, got);
                return Encoding.UTF8;
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8. The system code page is the right guess for a legacy .m3u — except
                // when it *is* UTF-8 (Windows' "use Unicode UTF-8 worldwide" option), which would
                // just reproduce the replacement characters. Fall back to Latin-1 there: it maps
                // every byte to a character, so the path survives intact instead of being
                // destroyed, and can still be matched on disk.
                try
                {
                    var ansi = Encoding.GetEncoding(0);
                    if (ansi.CodePage != Encoding.UTF8.CodePage) return ansi;
                }
                catch { }

                return Encoding.Latin1;
            }
        }
        catch
        {
            return Encoding.UTF8;
        }
        finally
        {
            try { stream.Position = 0; } catch { }
        }
    }

    public static List<M3uEntry> Read(string file)
    {
        var entries = new List<M3uEntry>();
        string? pendingTitle = null;
        double? pendingDuration = null;

        var dir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? "";
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, DetectEncoding(file, stream), detectEncodingFromByteOrderMarks: true);

        string? raw;
        while ((raw = reader.ReadLine()) != null)
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var body = line[8..];
                // #EXTINF:<duration>,<title> — the title may itself contain commas, so the
                // FIRST comma is the separator. LastIndexOf truncated any such title.
                var comma = body.IndexOf(',');
                if (comma >= 0)
                {
                    pendingTitle = body[(comma + 1)..].Trim();
                    if (double.TryParse(body[..comma].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        pendingDuration = d < 0 ? null : d;
                }
                continue;
            }
            if (line.StartsWith('#')) continue; // #EXTM3U, #PLAYLIST:, comments

            var path = line;
            if (!Path.IsPathRooted(path))
                path = Path.GetFullPath(Path.Combine(dir, path));
            entries.Add(new M3uEntry(path, pendingTitle, pendingDuration));
            pendingTitle = null;
            pendingDuration = null;
        }
        return entries;
    }

    /// <summary>
    /// Writes the playlist. <paramref name="unresolvedPaths"/> are raw path lines that were in the
    /// file but could not be resolved to a track when it was loaded (offline volume, for example);
    /// they are re-emitted verbatim so a save never silently drops them.
    /// </summary>
    public static void Write(string file, IReadOnlyList<PlaylistItem> items, string? playlistName = null,
        IReadOnlyList<string>? unresolvedPaths = null)
    {
        var fullPath = Path.GetFullPath(file);
        var dir = Path.GetDirectoryName(fullPath) ?? "";

        AtomicFile.Write(fullPath, stream =>
        {
            using var writer = new StreamWriter(stream, Utf8NoBom, 65536, leaveOpen: true);

            writer.WriteLine("#EXTM3U");
            if (!string.IsNullOrEmpty(playlistName))
                writer.WriteLine($"#PLAYLIST:{playlistName}");

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item?.Track == null) continue;
                var t = item.Track;
                var title = string.IsNullOrEmpty(t.Artist) ? t.Title : $"{t.Artist} - {t.Title}";
                writer.Write("#EXTINF:");
                writer.Write((t.DurationMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
                writer.Write(",");
                writer.WriteLine(title);
                writer.WriteLine(MakeRelativeIfPossible(dir, t.Path));
            }

            if (unresolvedPaths != null)
            {
                for (int i = 0; i < unresolvedPaths.Count; i++)
                {
                    var line = unresolvedPaths[i];
                    if (!string.IsNullOrWhiteSpace(line)) writer.WriteLine(line);
                }
            }

            writer.Flush();
        }, keepBackup: true, flushToDisk: true);
    }

    private static string MakeRelativeIfPossible(string baseDir, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(baseDir, path);
            // A relative path is only worth writing when it stays inside the playlist's own
            // directory tree. One that climbs out ("..\..\elsewhere") breaks as soon as the
            // playlist moves, so those keep the absolute form.
            if (Path.IsPathRooted(rel) || rel.StartsWith("..", StringComparison.Ordinal)) return path;
            return rel;
        }
        catch
        {
            return path;
        }
    }
}
