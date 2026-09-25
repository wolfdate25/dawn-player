using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Decoder seam: one candidate for opening a track path. <see cref="AudioFileReaderFactory"/>
/// walks providers by descending <see cref="Order"/> and opens the first whose
/// <see cref="CanOpen"/> accepts the path, so a new format is a registration, not a factory edit.
/// </summary>
/// <remarks>
/// Providers must be stateless and thread-safe: <see cref="ITrackReader.Open"/> is called from
/// the thread pool (advance, prefetch) and from UI-triggered commands. The factory translates
/// every open failure into <see cref="AudioOpenException"/> so callers can skip unreadable
/// files uniformly.
/// </remarks>
public interface ITrackReaderProvider
{
    /// <summary>Higher wins when several providers claim the same path.</summary>
    int Order { get; }

    /// <summary>Whether this provider can open the path (extension lowercased, with dot).</summary>
    bool CanOpen(string path, string extension);

    /// <summary>Opens the path. May throw; the factory wraps failures.</summary>
    ITrackReader Open(string path);
}

/// <summary>Internet radio streams (Icecast/Shoutcast over HTTP). Claimed by URL scheme, not extension.</summary>
public sealed class RadioStreamTrackReaderProvider : ITrackReaderProvider
{
    public int Order => 300;

    public bool CanOpen(string path, string extension) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public ITrackReader Open(string path)
    {
        var radio = new RadioStreamReader(path);
        radio.Connect();
        return radio;
    }
}

/// <summary>
/// Finite remote audio files over HTTP (DLNA today). Selected by the factory only for
/// file-over-http source kinds — the radio provider outbids it for every other http URL, which is
/// what keeps bare stream URLs behaving like live radio.
/// </summary>
public sealed class HttpFileTrackReaderProvider : ITrackReaderProvider
{
    private static bool _staleSweepDone;

    public int Order => 280;

    public bool CanOpen(string path, string extension) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public ITrackReader Open(string path)
    {
        SweepStaleSpoolFiles();
        var reader = new HttpProgressiveTrackReader(path);
        reader.Connect();
        return reader;
    }

    /// <summary>Removes spool files left by a crashed process (older than a day). Best effort.</summary>
    private static void SweepStaleSpoolFiles()
    {
        if (_staleSweepDone) return;
        _staleSweepDone = true;
        try
        {
            var dir = new DirectoryInfo(Util.AppPaths.HttpSpoolDir);
            if (!dir.Exists) return;
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var file in dir.EnumerateFiles())
            {
                try { if (file.LastWriteTimeUtc < cutoff) file.Delete(); } catch { }
            }
        }
        catch { }
    }
}

/// <summary>DSD Stream File: boxcar-decimated PCM by default, DoP packing when the DSD
/// playback setting prefers it and no device has rejected DoP this session.</summary>
public sealed class DsfTrackReaderProvider : ITrackReaderProvider
{
    public int Order => 200;

    public bool CanOpen(string path, string extension) => extension == ".dsf";

    public ITrackReader Open(string path) =>
        DsdSupport.PlaybackMode == DsdPlaybackMode.DoPPriority && !DsdSupport.IsDoPBlocked
            ? DopTrackReader.Open(path)
            : new DsfTrackReader(path);
}

/// <summary>DSDIFF (DFF): the interleaved-container sibling of DSF, same playback modes.</summary>
public sealed class DffTrackReaderProvider : ITrackReaderProvider
{
    public int Order => 200;

    public bool CanOpen(string path, string extension) => extension == ".dff";

    public ITrackReader Open(string path) =>
        DsdSupport.PlaybackMode == DsdPlaybackMode.DoPPriority && !DsdSupport.IsDoPBlocked
            ? DopTrackReader.Open(path)
            : new DsdPcmTrackReader(new DffRawReader(path));
}

/// <summary>Ogg Vorbis via NVorbis; the Media Foundation path cannot read Vorbis comments.</summary>
public sealed class VorbisTrackReaderProvider : ITrackReaderProvider
{
    public int Order => 100;

    public bool CanOpen(string path, string extension) => extension is ".ogg" or ".oga";

    public ITrackReader Open(string path) => new VorbisTrackReader(path);
}

/// <summary>Everything Media Foundation decodes (MP3, AAC/ALAC, FLAC, Opus, WAV…). The
/// catch-all of last resort: its <see cref="CanOpen"/> is unconditional, so it must keep
/// <see cref="Order"/> 0.</summary>
public sealed class MfTrackReaderProvider : ITrackReaderProvider
{
    public int Order => 0;

    public bool CanOpen(string path, string extension) => true;

    public ITrackReader Open(string path) => new MfTrackReader(path);
}
