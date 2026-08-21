using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

/// <summary>Uniform decoder abstraction over Media Foundation (wav/mp3/aac/flac/alac)
/// and NVorbis (ogg).</summary>
public interface ITrackReader : IDisposable
{
    /// <summary>Float samples at the file's native sample rate / channel count.</summary>
    ISampleProvider Samples { get; }

    /// <summary>Native source format (sample rate, channels, bits).</summary>
    WaveFormat SourceFormat { get; }

    TimeSpan TotalTime { get; }

    /// <summary>Current playback position; settable for seeking.</summary>
    TimeSpan CurrentTime { get; set; }

    string Path { get; }
}
