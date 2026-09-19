using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// DSD Stream File (DSF) reader: parses the DSF header and streams the channel-major DSD blocks
/// through a boxcar decimation to float PCM (output rate = DSD rate / decimation factor, landing
/// on a standard 44.1k/48k family rate). Seeking maps a PCM frame index back to the DSD block and
/// byte offsets. DFF (DSDIFF) is not supported. The decimation is a plain bit average — playable
/// and honest rather than a delta-sigma-fancy reconstruction; the chain's soft limiter catches
/// intersample peaks.
/// </summary>
public sealed class DsfTrackReader : ITrackReader
{
    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private readonly long _dataOffset;
    private readonly long _dataBytes;
    private readonly int _channelCount;
    private readonly int _blockSizePerChannel; // DSF blocks are channel-major: ch0 block, ch1 block, ...
    private readonly int _decimation;          // DSD bits per output PCM sample
    private readonly byte[] _byteBuf = new byte[8192];
    private readonly double[] _sum = new double[8];
    private long _dsdFrameOffset; // next DSD frame (one 1-bit sample per channel) to consume

    public WaveFormat SourceFormat { get; }
    public string Path { get; }

    /// <summary>Total 1-bit frames across all channels (channel blocks each hold blockSize*8).</summary>
    private long TotalDsdFrames => _dataBytes / _channelCount * 8;

    public TimeSpan TotalTime { get; }

    public TimeSpan CurrentTime
    {
        get
        {
            long pcmFrames = _dsdFrameOffset / _decimation;
            return TimeSpan.FromSeconds(pcmFrames / (double)SourceFormat.SampleRate);
        }
        set
        {
            var clamped = value;
            if (clamped < TimeSpan.Zero) clamped = TimeSpan.Zero;
            if (clamped > TotalTime) clamped = TotalTime;
            _dsdFrameOffset = (long)(clamped.TotalSeconds * SourceFormat.SampleRate) * _decimation;
            Array.Clear(_sum);
        }
    }

    public ISampleProvider Samples { get; }

    public DsfTrackReader(string path)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(_stream);

        try
        {
            if (new string(_reader.ReadChars(4)) != "DSD ")
            {
                throw new AudioOpenException($"DSF 파일이 아닙니다: {path}", new InvalidDataException("bad magic"));
            }
            _reader.ReadInt64(); // chunk size (28)
            _reader.ReadInt64(); // total file size
            _reader.ReadInt64(); // metadata chunk pointer (0 = none)

            if (new string(_reader.ReadChars(4)) != "fmt ")
            {
                throw new AudioOpenException($"DSF fmt 청크가 없습니다: {path}", new InvalidDataException("no fmt"));
            }
            _reader.ReadInt64(); // fmt size (52)
            _reader.ReadInt32(); // format version
            _reader.ReadInt32(); // format id (0 = DSD raw)
            _reader.ReadInt32(); // channel type (mono/stereo/... layout id)
            _channelCount = _reader.ReadInt32();
            int dsdSampleRate = _reader.ReadInt32();
            int bitsPerSample = _reader.ReadInt32(); // 1 = bit-packed, 8 = byte per (1-bit) sample
            _reader.ReadInt64(); // sample count (per channel, DSD samples)
            _blockSizePerChannel = _reader.ReadInt32();
            _reader.ReadInt32(); // reserved

            if (_channelCount is < 1 or > 8 || dsdSampleRate <= 0 || _blockSizePerChannel <= 0)
            {
                throw new AudioOpenException($"DSF 헤더가 비정상입니다: {path}", new InvalidDataException("bad header"));
            }

            // Decimation must be an integer so the output lands on a standard rate: 64fs→44.1k,
            // 128fs→88.2k, 48k-family 64fs (3072000)→48k, ...
            _decimation = dsdSampleRate switch
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
                     : throw new AudioOpenException($"지원하지 않는 DSD 샘플레이트({dsdSampleRate}Hz): {path}",
                         new NotSupportedException("unsupported dsd rate")),
            };

            if (new string(_reader.ReadChars(4)) != "data")
            {
                throw new AudioOpenException($"DSF data 청크가 없습니다: {path}", new InvalidDataException("no data"));
            }
            _dataBytes = _reader.ReadInt64() - 8; // the size field includes itself
            _dataOffset = _stream.Position;

            SourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(dsdSampleRate / _decimation, _channelCount);
            TotalTime = TimeSpan.FromSeconds(TotalDsdFrames / _decimation / (double)SourceFormat.SampleRate);
            Samples = new DsdSampleProvider(this);
        }
        catch (AudioOpenException)
        {
            _reader.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            _reader.Dispose();
            throw new AudioOpenException($"DSD 파일을 열 수 없습니다: {path}", ex);
        }
    }

    /// <summary>
    /// Decodes the next <paramref name="framesWanted"/> output frames into the per-channel
    /// buffers. Whole output frames only: a block boundary may leave a few bits unconsumed; the
    /// read position advances by decoded frames only, so they are picked up on the next call.
    /// All counts here are DSD frames (1 bit per channel each) unless noted.
    /// </summary>
    internal int DecodeFrames(float[][] channelBuffers, int framesWanted)
    {
        long remainingDsdFrames = TotalDsdFrames - _dsdFrameOffset;
        if (remainingDsdFrames <= 0) return 0;

        int channels = _channelCount;
        int frames = (int)Math.Min(framesWanted, remainingDsdFrames / _decimation);
        if (frames <= 0) return 0;

        int blockSamples = _blockSizePerChannel * 8; // DSD frames per channel block
        int decoded = 0;
        while (decoded < frames)
        {
            long frameInBlock = _dsdFrameOffset % blockSamples;
            int blockIndex = (int)(_dsdFrameOffset / blockSamples);
            long blockFileOffset = _dataOffset + (long)blockIndex * _blockSizePerChannel * channels;
            if (blockFileOffset - _dataOffset >= _dataBytes) break;

            int take = (int)Math.Min((long)(frames - decoded) * _decimation, blockSamples - frameInBlock);

            for (int c = 0; c < channels; c++)
            {
                long pos = blockFileOffset + (long)c * _blockSizePerChannel + frameInBlock / 8;
                _stream.Seek(pos, SeekOrigin.Begin);

                var acc = channelBuffers[c];
                int bitOffset = (int)(frameInBlock % 8);
                int bitsLeft = take;
                int written = decoded;
                double sum = 0;
                int count = 0;
                int bytesToConsume = (bitOffset + bitsLeft + 7) / 8;

                while (bitsLeft > 0 && bytesToConsume > 0)
                {
                    int chunk = Math.Min(_byteBuf.Length, bytesToConsume);
                    int got = _reader.Read(_byteBuf, 0, chunk);
                    if (got <= 0) break;
                    bytesToConsume -= got;

                    for (int b = 0; b < got && bitsLeft > 0; b++)
                    {
                        int start = b == 0 ? bitOffset : 0;
                        for (int bit = start; bit < 8 && bitsLeft > 0; bit++)
                        {
                            sum += (_byteBuf[b] >> (7 - bit)) & 1;
                            if (++count == _decimation)
                            {
                                acc[written++] = (float)((sum / _decimation) * 2.0 - 1.0);
                                count = 0;
                                sum = 0;
                                bitsLeft--;
                            }
                        }
                    }
                }
                // Every channel sees the same bit count, so `written` matches across channels.
            }

            int producedFrames = take / _decimation;
            decoded += producedFrames;
            _dsdFrameOffset += producedFrames * _decimation;
        }

        return decoded;
    }

    public void Dispose() => _reader.Dispose();

    /// <summary>Serves the decoded per-channel buffers as interleaved floats.</summary>
    private sealed class DsdSampleProvider : ISampleProvider
    {
        private readonly DsfTrackReader _owner;
        private float[][] _buffers = Array.Empty<float[]>();

        public DsdSampleProvider(DsfTrackReader owner) => _owner = owner;

        public WaveFormat WaveFormat => _owner.SourceFormat;

        public int Read(Span<float> buffer)
        {
            int channels = _owner.SourceFormat.Channels;
            int framesWanted = buffer.Length / channels;
            if (_buffers.Length != channels || _buffers[0].Length < framesWanted)
            {
                int allocFrames = Math.Max(framesWanted, 4096);
                _buffers = new float[channels][];
                for (int c = 0; c < channels; c++) _buffers[c] = new float[allocFrames];
            }

            int frames = _owner.DecodeFrames(_buffers, framesWanted);

            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    buffer[f * channels + c] = _buffers[c][f];
                }
            }
            return frames * channels;
        }
    }
}
