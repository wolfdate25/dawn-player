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
        new DsfTrackReaderProvider(),
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
    public static ITrackReader Open(string path)
    {
        // A cue-sheet virtual track addresses a range inside a physical file; open the parent and
        // wrap it in a range reader that the sequencer can chain gaplessly.
        if (AppPaths.TryDecodeCuePath(path, out var physical, out var startMs, out var endMs))
        {
            var inner = Open(physical);
            return new CueTrackReader(inner,
                TimeSpan.FromMilliseconds(startMs),
                TimeSpan.FromMilliseconds(endMs));
        }

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

        ITrackReaderProvider? chosen = null;
        lock (_gate)
        {
            foreach (var provider in _providers)
            {
                if (provider.CanOpen(path, ext))
                {
                    chosen = provider;
                    break;
                }
            }
        }

        if (chosen == null)
        {
            throw new AudioOpenException(
                $"지원하지 않는 형식입니다: {System.IO.Path.GetFileName(path)}",
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
            throw new AudioOpenException($"파일을 열 수 없습니다: {System.IO.Path.GetFileName(path)}", ex);
        }
    }
}

public sealed class AudioOpenException : Exception
{
    public AudioOpenException(string message, Exception inner) : base(message, inner) { }
}
