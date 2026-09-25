using System.Linq;
using DawnPlayer.Core.Util;
using NAudio.Vorbis;
using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

public sealed class MfTrackReader : ITrackReader
{
    private readonly MediaFoundationReader _reader;
    private ISampleProvider? _samples;

    public MfTrackReader(string path)
    {
        _reader = new MediaFoundationReader(path);
        Path = path;
    }

    public ISampleProvider Samples => _samples ??= _reader.ToSampleProvider();
    public WaveFormat SourceFormat => _reader.WaveFormat;
    public TimeSpan TotalTime => _reader.TotalTime;

    public TimeSpan CurrentTime
    {
        get => _reader.CurrentTime;
        set => _reader.Position = ClampToBlock(_reader.WaveFormat, TimeToBytes(_reader.WaveFormat, value), _reader.Length);
    }

    public string Path { get; }

    public void Dispose() => _reader.Dispose();

    internal static long TimeToBytes(WaveFormat fmt, TimeSpan t) =>
        (long)(t.TotalSeconds * fmt.AverageBytesPerSecond);

    internal static long ClampToBlock(WaveFormat fmt, long bytes, long length)
    {
        bytes -= bytes % fmt.BlockAlign;
        return Math.Max(0, Math.Min(bytes, length));
    }
}

public sealed class VorbisTrackReader : ITrackReader
{
    private readonly VorbisWaveReader _reader;

    public VorbisTrackReader(string path)
    {
        _reader = new VorbisWaveReader(path);
        Path = path;
    }

    public ISampleProvider Samples => _reader;
    public WaveFormat SourceFormat => _reader.WaveFormat;
    public TimeSpan TotalTime => _reader.TotalTime;

    public TimeSpan CurrentTime
    {
        get => _reader.CurrentTime;
        set => _reader.Position = MfTrackReader.ClampToBlock(
            _reader.WaveFormat, MfTrackReader.TimeToBytes(_reader.WaveFormat, value), _reader.Length);
    }

    public string Path { get; }

    public void Dispose() => _reader.Dispose();
}

/// <summary>
/// Opens a supported audio file through the registered <see cref="ITrackReaderProvider"/> chain
/// (highest order first; the Media Foundation provider is the unconditional catch-all). Throws
/// <see cref="AudioOpenException"/> on failure. Registration mutates a copy-on-write snapshot,
/// so hot opens never lock.
/// </summary>
public static class AudioFileReaderFactory
{
    private static readonly object _gate = new();
    private static ITrackReaderProvider[] _providers =
    {
        new RadioStreamTrackReaderProvider(),
        new HttpFileTrackReaderProvider(),
        new DsfTrackReaderProvider(),
        new DffTrackReaderProvider(),
        new VorbisTrackReaderProvider(),
        new MfTrackReaderProvider(),
    };

    /// <summary>Snapshot of the active provider chain, highest order first.</summary>
    public static IReadOnlyList<ITrackReaderProvider> Providers
    {
        get { lock (_gate) return _providers.ToArray(); }
    }

    /// <summary>Adds a provider to the chain, keeping descending-order arrangement.</summary>
    public static void Register(ITrackReaderProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
        {
            _providers = _providers.Append(provider).OrderByDescending(p => p.Order).ToArray();
        }
    }

    /// <summary>Removes a previously registered provider. Returns false when it was not registered.</summary>
    public static bool Unregister(ITrackReaderProvider provider)
    {
        lock (_gate)
        {
            int before = _providers.Length;
            _providers = _providers.Where(p => !ReferenceEquals(p, provider)).ToArray();
            return _providers.Length != before;
        }
    }

    /// <summary>Opens a supported audio file. Throws <see cref="AudioOpenException"/> on failure.</summary>
    public static ITrackReader Open(string path) => Open(path, Models.TrackSourceKind.File);

    /// <summary>
    /// Opens a track with its source kind. The kind only reroutes http(s) URLs: <see cref="Models.TrackSourceKind.Radio"/>
    /// and the legacy default (a bare stream URL, kept radio for compatibility) stay on the live-stream
    /// reader, <see cref="Models.TrackSourceKind.Dlna"/> goes to the spooling file reader, and local
    /// files ignore the kind entirely.
    /// </summary>
    public static ITrackReader Open(string path, Models.TrackSourceKind sourceKind)
    {
        // A cue-sheet virtual track addresses a range inside a physical file; open the parent and
        // wrap it in a range reader that the sequencer can chain gaplessly.
        if (AppPaths.TryDecodeCuePath(path, out var physical, out var startMs, out var endMs))
        {
            var inner = Open(physical, sourceKind);
            return new CueTrackReader(inner,
                TimeSpan.FromMilliseconds(startMs),
                TimeSpan.FromMilliseconds(endMs));
        }

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var chosen = SelectProvider(path, ext, sourceKind);

        if (chosen == null)
        {
            throw new AudioOpenException(
                CoreMessages.Encode(CoreMessageKey.UnsupportedFormat, System.IO.Path.GetFileName(path)),
                new InvalidOperationException($"no track reader provider accepts '{ext}'"));
        }

        try
        {
            return chosen.Open(path);
        }
        catch (AudioOpenException)
        {
            throw; // already user-facing (e.g. radio connect failure)
        }
        catch (Exception ex)
        {
            throw new AudioOpenException(
                CoreMessages.Encode(CoreMessageKey.FileOpenFailed, System.IO.Path.GetFileName(path)), ex);
        }
    }

    /// <summary>Picks the provider for a path under the source-kind routing rules. Public for the
    /// routing contract tests — selection must stay observable without opening anything.</summary>
    public static ITrackReaderProvider? SelectProvider(string path, string extension, Models.TrackSourceKind sourceKind)
    {
        // Remote file sources are pinned to the spooling reader; everything else keeps the
        // historical chain (radio first, which is exactly why the legacy bare-URL case still
        // behaves like a live stream).
        bool fileOverHttp = sourceKind == Models.TrackSourceKind.Dlna
            || sourceKind == Models.TrackSourceKind.YouTube;

        lock (_gate)
        {
            foreach (var provider in _providers)
            {
                if (!provider.CanOpen(path, extension)) continue;
                if (provider is HttpFileTrackReaderProvider != fileOverHttp) continue;
                return provider;
            }
        }
        return null;
    }
}

public sealed class AudioOpenException : Exception
{
    public AudioOpenException(string message, Exception inner) : base(message, inner) { }
}
