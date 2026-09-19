using DawnPlayer.Core.Persistence;
using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Process-wide DSD playback state. The App layer sets <see cref="PlaybackMode"/> from settings
/// (Core cannot reach the settings service); the DoP availability memo makes a device that
/// rejected DoP once fall back to PCM for the rest of the session instead of failing — and
/// warning — on every track.
/// </summary>
public static class DsdSupport
{
    /// <summary>True when the reader under <paramref name="reader"/> feeds packed DoP frames
    /// (directly or inside a cue range wrapper). Such readers must never be volume/DSP-processed
    /// or resampled: any arithmetic corrupts the DoP marker bytes the DAC resyncs on.</summary>
    public static bool IsRawDsdReader(ITrackReader reader) => reader switch
    {
        DopTrackReader => true,
        CueTrackReader cue => IsRawDsdReader(cue.Inner),
        _ => false,
    };

    public static DsdPlaybackMode PlaybackMode { get; set; } = DsdPlaybackMode.PcmAlways;

    private static int _doPBlocked;

    /// <summary>A device rejected DoP: keep opening DSD tracks as PCM until the setting changes.</summary>
    public static bool IsDoPBlocked => Volatile.Read(ref _doPBlocked) == 1;

    public static void BlockDoP() => Volatile.Write(ref _doPBlocked, 1);

    /// <summary>Called when the DSD playback setting changes, so a newly chosen mode gets a
    /// fresh chance even if a device rejected DoP earlier in the session.</summary>
    public static void ResetDoPBlock() => Volatile.Write(ref _doPBlocked, 0);

    /// <summary>Re-opens a DSD track through its plain boxcar-PCM reader. Used when a session
    /// cannot carry DoP (shared mode, or the exclusive probe rejected the DoP rate) — the
    /// pending track's DoP reader must be swapped before the session is built.</summary>
    public static ITrackReader OpenPcmReader(string path) =>
        System.IO.Path.GetExtension(path).Equals(".dff", StringComparison.OrdinalIgnoreCase)
            ? new DsdPcmTrackReader(new DffRawReader(path))
            : new DsfTrackReader(path);

    /// <summary>Re-opens a pending track with its reader replaced (the failed sequencer owned
    /// and disposed the old one). DoP state on the pending track resets with the reader.</summary>
    public static PendingTrack ReopenAsPcm(PendingTrack pending) => new()
    {
        Playlist = pending.Playlist,
        Item = pending.Item,
        Reader = OpenPcmReader(pending.Item.Track.Path),
        StartPosition = pending.StartPosition,
        RequiresRestart = pending.RequiresRestart,
    };
}

/// <summary>
/// Packs a raw DSD stream into DoP (DSD over PCM) frames: each 24-bit sample carries
/// [DSD byte 1][marker 0x05/0xFA alternating][DSD byte 2], so DSD64 becomes 176.4 kHz / 24-bit
/// PCM — a format a DSD-capable DAC accepts over an ordinary WASAPI exclusive session. The
/// samples leave this reader as floats that round-trip the container bytes exactly; the
/// sequencer must run in raw-passthrough mode because any volume/EQ arithmetic destroys the
/// markers.
/// </summary>
public sealed class DopTrackReader : ITrackReader
{
    /// <summary>DoP markers, alternating per sample per channel so the DAC can resync and tell
    /// DoP apart from plain PCM.</summary>
    public const byte MarkerEven = 0x05;
    public const byte MarkerOdd = 0xFA;

    public const int DsdFramesPerSample = 16; // two DSD bytes per 24-bit sample

    private readonly IDsdRawReader _raw;
    private readonly byte[][] _channelBytes = new byte[8][];
    private readonly ISampleProvider _samples;
    private long _sampleOffset; // PCM samples (per channel) consumed

    public WaveFormat SourceFormat { get; }
    public string Path { get; }
    public TimeSpan TotalTime { get; }
    public ISampleProvider Samples => _samples;

    public DopTrackReader(IDsdRawReader raw, string path)
    {
        _raw = raw;
        Path = path;
        int pcmRate = (int)(raw.DsdSampleRate / DsdFramesPerSample);
        if (pcmRate <= 0)
            throw new AudioOpenException("DSD 샘플레이트가 DoP 레이트로 변환될 수 없습니다.",
                new NotSupportedException("unsupported dsd rate"));
        SourceFormat = new WaveFormat(pcmRate, 24, raw.Channels);
        TotalTime = TimeSpan.FromSeconds(raw.TotalDsdFrames / (double)raw.DsdSampleRate);
        _samples = new Provider(this);
    }

    /// <summary>Builds the DoP reader for a file path, choosing the container by extension.</summary>
    public static DopTrackReader Open(string path) => new DopTrackReader(
        System.IO.Path.GetExtension(path).Equals(".dff", StringComparison.OrdinalIgnoreCase)
            ? new DffRawReader(path)
            : new DsfRawReader(path),
        path);

    public TimeSpan CurrentTime
    {
        get => TimeSpan.FromSeconds(_sampleOffset / (double)SourceFormat.SampleRate);
        set
        {
            var clamped = value;
            if (clamped < TimeSpan.Zero) clamped = TimeSpan.Zero;
            if (clamped > TotalTime) clamped = TotalTime;
            _sampleOffset = (long)(clamped.TotalSeconds * SourceFormat.SampleRate);
            _raw.PositionFrames = _sampleOffset * DsdFramesPerSample;
        }
    }

    public void Dispose() => _raw.Dispose();

    /// <summary>Combines two DSD bytes and a marker into the signed 24-bit container value.</summary>
    public static int PackSample(byte dsd1, byte marker, byte dsd2)
    {
        int v = (dsd1 << 16) | (marker << 8) | dsd2;
        if ((v & 0x800000) != 0) v -= 0x1000000; // signed 24-bit container
        return v;
    }

    /// <summary>The float encoding that survives <see cref="PcmConvert.ToBytes"/> (which scales
    /// by 8388607 and, since the DoP fix, rounds to nearest) bit-exactly.</summary>
    public static float ToContainerFloat(int signed24) => signed24 / 8388607f;

    /// <summary>Decodes raw DSD into packed DoP floats. Interleaved per channel; the marker
    /// alternates per sample within each channel.</summary>
    private int Read(Span<float> buffer)
    {
        int channels = SourceFormat.Channels;
        int framesWanted = buffer.Length / channels;
        if (framesWanted <= 0) return 0;

        int bytesWanted = framesWanted * DsdFramesPerSample / 8;
        for (int c = 0; c < channels; c++)
        {
            if (_channelBytes[c] == null || _channelBytes[c].Length < bytesWanted)
            {
                _channelBytes[c] = new byte[Math.Max(bytesWanted, 8192)];
            }
        }

        int dsdFrames = _raw.ReadRawFrames(_channelBytes, framesWanted * DsdFramesPerSample);
        int samples = Math.Min(framesWanted, dsdFrames / DsdFramesPerSample);
        if (samples <= 0) return 0;

        long baseSample = _sampleOffset;
        for (int c = 0; c < channels; c++)
        {
            var bytes = _channelBytes[c];
            for (int s = 0; s < samples; s++)
            {
                byte b1 = bytes[s * 2];
                byte b2 = bytes[s * 2 + 1];
                // The marker phase depends on the absolute sample index, so a seek lands back
                // in phase instead of flipping the alternation for the rest of the track.
                byte marker = ((baseSample + s) & 1) == 0 ? MarkerEven : MarkerOdd;
                buffer[s * channels + c] = ToContainerFloat(PackSample(b1, marker, b2));
            }
        }

        _sampleOffset += samples;
        return samples * channels;
    }

    /// <summary>Serves the packed floats.</summary>
    private sealed class Provider : ISampleProvider
    {
        private readonly DopTrackReader _owner;
        public Provider(DopTrackReader owner) => _owner = owner;
        public WaveFormat WaveFormat => _owner.SourceFormat;
        public int Read(Span<float> buffer) => _owner.Read(buffer);
    }
}

/// <summary>
/// Boxcar-decimated PCM playback over any raw DSD container — the DFF counterpart of
/// <see cref="DsfTrackReader"/>. The decimation is a plain bit average landing on the standard
/// 44.1k/48k family rate; the chain's soft limiter catches intersample peaks.
/// </summary>
public sealed class DsdPcmTrackReader : ITrackReader
{
    private readonly IDsdRawReader _raw;
    private readonly int _decimation;
    private readonly byte[][] _channelBytes = new byte[8][];
    private long _sampleOffset;

    public WaveFormat SourceFormat { get; }
    public string Path { get; } = string.Empty;
    public TimeSpan TotalTime { get; }

    internal DsdPcmTrackReader(IDsdRawReader raw)
    {
        _raw = raw;
        _decimation = DsfTrackReaderRate.DecimationFor((int)raw.DsdSampleRate)
            ?? throw new AudioOpenException(
                $"지원하지 않는 DSD 샘플레이트({raw.DsdSampleRate}Hz)",
                new NotSupportedException("unsupported dsd rate"));
        SourceFormat = WaveFormat.CreateIeeeFloatWaveFormat((int)(raw.DsdSampleRate / _decimation), raw.Channels);
        TotalTime = TimeSpan.FromSeconds(raw.TotalDsdFrames / (double)raw.DsdSampleRate);
    }

    public TimeSpan CurrentTime
    {
        get => TimeSpan.FromSeconds(_sampleOffset / (double)SourceFormat.SampleRate);
        set
        {
            var clamped = value;
            if (clamped < TimeSpan.Zero) clamped = TimeSpan.Zero;
            if (clamped > TotalTime) clamped = TotalTime;
            _sampleOffset = (long)(clamped.TotalSeconds * SourceFormat.SampleRate);
            _raw.PositionFrames = _sampleOffset * _decimation;
        }
    }

    public void Dispose() => _raw.Dispose();

    public ISampleProvider Samples => new Provider(this);

    private int Read(Span<float> buffer)
    {
        int channels = SourceFormat.Channels;
        int framesWanted = buffer.Length / channels;
        if (framesWanted <= 0) return 0;

        int bytesPerChannel = (framesWanted * _decimation + 7) / 8;
        for (int c = 0; c < channels; c++)
        {
            if (_channelBytes[c] == null || _channelBytes[c].Length < bytesPerChannel)
            {
                _channelBytes[c] = new byte[Math.Max(bytesPerChannel, 8192)];
            }
        }

        long rawPos = _sampleOffset * _decimation;
        int wantedFrames = (int)Math.Min((long)framesWanted, (_raw.TotalDsdFrames - rawPos) / _decimation);
        if (wantedFrames <= 0) return 0;

        _raw.PositionFrames = rawPos;
        int dsdFrames = _raw.ReadRawFrames(_channelBytes, wantedFrames * _decimation);
        int samples = dsdFrames / _decimation;
        if (samples <= 0) return 0;

        int bytesPerSample = _decimation / 8;
        for (int c = 0; c < channels; c++)
        {
            var bytes = _channelBytes[c];
            for (int s = 0; s < samples; s++)
            {
                int ones = 0;
                for (int b = 0; b < bytesPerSample; b++)
                {
                    ones += System.Numerics.BitOperations.PopCount(bytes[s * bytesPerSample + b]);
                }
                buffer[s * channels + c] = (float)((ones / (double)_decimation) * 2.0 - 1.0);
            }
        }

        _sampleOffset += samples;
        return samples * channels;
    }

    private sealed class Provider : ISampleProvider
    {
        private readonly DsdPcmTrackReader _owner;
        public Provider(DsdPcmTrackReader owner) => _owner = owner;
        public WaveFormat WaveFormat => _owner.SourceFormat;
        public int Read(Span<float> buffer) => _owner.Read(buffer);
    }
}

/// <summary>DSD rate → boxcar decimation table, shared with DsfTrackReader's choices.</summary>
internal static class DsfTrackReaderRate
{
    public static int? DecimationFor(int dsdSampleRate) => dsdSampleRate switch
    {
        2822400 => 64,
        5644800 => 128,
        11289600 => 256,
        22579200 => 512,
        3072000 => 64,
        6144000 => 128,
        12288000 => 256,
        24576000 => 512,
        _ => dsdSampleRate % 44100 == 0 && dsdSampleRate / 44100 >= 8 ? dsdSampleRate / 44100
           : dsdSampleRate % 48000 == 0 && dsdSampleRate / 48000 >= 8 ? dsdSampleRate / 48000
           : null,
    };
}
