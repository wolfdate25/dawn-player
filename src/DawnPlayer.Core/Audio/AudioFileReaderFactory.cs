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

public static class AudioFileReaderFactory
{
    /// <summary>Opens a supported audio file. Throws <see cref="AudioOpenException"/> on failure.</summary>
    public static ITrackReader Open(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext is ".ogg" or ".oga"
                ? new VorbisTrackReader(path)
                : new MfTrackReader(path);
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
