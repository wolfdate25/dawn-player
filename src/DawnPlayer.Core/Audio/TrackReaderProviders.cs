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

/// <summary>DSD Stream File: managed boxcar-decimated playback, no Media Foundation support.</summary>
public sealed class DsfTrackReaderProvider : ITrackReaderProvider
{
    public int Order => 200;

    public bool CanOpen(string path, string extension) => extension == ".dsf";

    public ITrackReader Open(string path) => new DsfTrackReader(path);
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
