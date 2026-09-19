using System.Linq;
using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// A raw DSD bit-stream source: DSF (channel-major blocks) and DFF (channel-interleaved) files
/// expose their un-decoded DSD bytes through this contract so the DoP packer and the boxcar PCM
/// converter can sit on top of either container. Frame positions are in DSD frames — one 1-bit
/// sample per channel — and must land on byte boundaries (multiples of 8) when reading.
/// </summary>
public interface IDsdRawReader : IDisposable
{
    int Channels { get; }
    long DsdSampleRate { get; }
    long TotalDsdFrames { get; }
    long PositionFrames { get; set; }

    /// <summary>Reads up to <paramref name="framesWanted"/> DSD frames (bits) per channel into
    /// the per-channel byte buffers (8 frames per byte, MSB first). Returns frames actually
    /// read, a multiple of 8; 0 at end of stream.</summary>
    int ReadRawFrames(byte[][] channelBytes, int framesWanted);
}

/// <summary>
/// Raw access to a DSF file's channel-major DSD blocks. Parsing mirrors
/// <see cref="DsfTrackReader"/> (kept untouched — it is the shipped PCM path) but hands out
/// undecoded bytes instead of decimated floats.
/// </summary>
internal sealed class DsfRawReader : IDsdRawReader
{
    private readonly FileStream _stream;
    private readonly long _dataOffset;
    private readonly long _dataBytes;
    private readonly int _channelCount;
    private readonly int _blockSizePerChannel;
    private long _positionFrames;

    public int Channels => _channelCount;
    public long DsdSampleRate { get; }
    public long TotalDsdFrames { get; }

    public long PositionFrames
    {
        get => _positionFrames;
        set => _positionFrames = Math.Clamp(value - value % 8, 0, TotalDsdFrames);
    }

    public DsfRawReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(_stream, System.Text.Encoding.UTF8, leaveOpen: true);
        try
        {
            if (new string(reader.ReadChars(4)) != "DSD ")
                throw new AudioOpenException($"DSF 파일이 아닙니다: {path}", new InvalidDataException("bad magic"));
            reader.ReadInt64(); // chunk size
            reader.ReadInt64(); // total file size
            reader.ReadInt64(); // metadata pointer

            if (new string(reader.ReadChars(4)) != "fmt ")
                throw new AudioOpenException($"DSF fmt 청크가 없습니다: {path}", new InvalidDataException("no fmt"));
            reader.ReadInt64(); // fmt size
            reader.ReadInt32(); // version
            reader.ReadInt32(); // format id
            reader.ReadInt32(); // channel type
            _channelCount = reader.ReadInt32();
            DsdSampleRate = reader.ReadInt32();
            int bitsPerSample = reader.ReadInt32();
            reader.ReadInt64(); // sample count
            _blockSizePerChannel = reader.ReadInt32();
            reader.ReadInt32(); // reserved

            if (_channelCount is < 1 or > 8 || DsdSampleRate <= 0 || _blockSizePerChannel <= 0)
                throw new AudioOpenException($"DSF 헤더가 비정상입니다: {path}", new InvalidDataException("bad header"));

            if (new string(reader.ReadChars(4)) != "data")
                throw new AudioOpenException($"DSF data 청크가 없습니다: {path}", new InvalidDataException("no data"));
            _dataBytes = reader.ReadInt64() - 8;
            _dataOffset = _stream.Position;
            TotalDsdFrames = _dataBytes / _channelCount * 8;
        }
        catch (AudioOpenException)
        {
            _stream.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            _stream.Dispose();
            throw new AudioOpenException($"DSD 파일을 열 수 없습니다: {path}", ex);
        }
    }

    public int ReadRawFrames(byte[][] channelBytes, int framesWanted)
    {
        long remaining = TotalDsdFrames - _positionFrames;
        int frames = (int)Math.Min(framesWanted - framesWanted % 8, Math.Max(remaining, 0));
        if (frames <= 0) return 0;

        int blockFrames = _blockSizePerChannel * 8;
        int done = 0;
        while (done < frames)
        {
            long frameInBlock = _positionFrames % blockFrames;
            int blockIndex = (int)(_positionFrames / blockFrames);
            long blockFileOffset = _dataOffset + (long)blockIndex * _blockSizePerChannel * _channelCount;
            if (blockFileOffset - _dataOffset >= _dataBytes) break;

            int take = (int)Math.Min((long)(frames - done), blockFrames - frameInBlock);
            int bytes = take / 8;
            for (int c = 0; c < _channelCount; c++)
            {
                _stream.Seek(blockFileOffset + (long)c * _blockSizePerChannel + frameInBlock / 8, SeekOrigin.Begin);
                int got = ReadExactly(_stream, channelBytes[c], done / 8, bytes);
                if (got < bytes)
                {
                    // Partial tail read: shrink uniformly across channels.
                    int gotFrames = got * 8;
                    if (gotFrames <= done) return done;
                    _positionFrames += gotFrames;
                    return done + gotFrames;
                }
            }
            done += bytes * 8;
            _positionFrames += bytes * 8;
        }
        return done;
    }

    internal static int ReadExactly(FileStream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int got = stream.Read(buffer, offset + total, count - total);
            if (got <= 0) break;
            total += got;
        }
        return total;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// Raw access to a DSDIFF (DFF) file's interleaved DSD chunk. Parses the FRM8 container
/// (big-endian 64-bit chunk sizes, AIFF-style 80-bit extended sample rate). DST-compressed
/// sound data is rejected with a clear error rather than mis-decoded.
/// </summary>
public sealed class DffRawReader : IDsdRawReader
{
    private readonly FileStream _stream;
    private readonly long _dataOffset;
    private readonly long _dataBytes;
    private readonly int _channelCount;
    private readonly byte[] _interleaved = new byte[8192];
    private long _positionFrames;

    public int Channels => _channelCount;
    public long DsdSampleRate { get; }
    public long TotalDsdFrames { get; }

    public long PositionFrames
    {
        get => _positionFrames;
        set => _positionFrames = Math.Clamp(value - value % 8, 0, TotalDsdFrames);
    }

    public DffRawReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(_stream, System.Text.Encoding.UTF8, leaveOpen: true);
        try
        {
            if (new string(reader.ReadChars(4)) != "FRM8")
                throw new AudioOpenException($"DFF 파일이 아닙니다: {path}", new InvalidDataException("bad magic"));
            reader.ReadInt64(); // FRM8 payload size
            if (new string(reader.ReadChars(4)).TrimEnd() != "DSD")
                throw new AudioOpenException($"DFF 컨테이너 형식이 아닙니다: {path}", new InvalidDataException("bad form type"));

            bool haveRate = false;
            bool haveChannels = false;
            bool compressed = false;
            _dataOffset = -1;
            _dataBytes = 0;

            while (reader.BaseStream.Position + 12 <= reader.BaseStream.Length)
            {
                // Chunk IDs are 4 raw bytes, conventionally padded with spaces; normalize so
                // "FS  " and "FS" both match.
                string id = new string(reader.ReadChars(4)).TrimEnd();
                long size = ReadInt64BigEndian(reader);

                if (id == "FVER" || id == "COMT" || id == "DIIN" || id == "ABSS" || id == "LSCO")
                {
                    reader.BaseStream.Seek(size + size % 2, SeekOrigin.Current);
                }
                else if (id == "PROP")
                {
                    string propType = new string(reader.ReadChars(4));
                    long innerLeft = size - 4;
                    while (innerLeft >= 12)
                    {
                        string innerId = new string(reader.ReadChars(4)).TrimEnd();
                        long innerSize = ReadInt64BigEndian(reader);
                        long body = innerSize + innerSize % 2;
                        if (innerId == "FS" && innerSize >= 10)
                        {
                            DsdSampleRate = ReadExtended80(reader);
                            reader.BaseStream.Seek(innerSize - 10 + innerSize % 2, SeekOrigin.Current);
                            haveRate = true;
                        }
                        else if (innerId == "CHAN" && innerSize >= 4)
                        {
                            _channelCount = ReadInt32BigEndian(reader);
                            reader.BaseStream.Seek(innerSize - 4 + innerSize % 2, SeekOrigin.Current);
                            haveChannels = true;
                        }
                        else if (innerId == "CMPD" && innerSize >= 4)
                        {
                            string compression = new string(reader.ReadChars(4)).TrimEnd();
                            reader.BaseStream.Seek(innerSize - 4 + innerSize % 2, SeekOrigin.Current);
                            compressed = compression != "DSD";
                        }
                        else
                        {
                            reader.BaseStream.Seek(body, SeekOrigin.Current);
                        }
                        innerLeft -= 12 + body;
                    }
                }
                else if (id == "DST")
                {
                    compressed = true;
                    reader.BaseStream.Seek(size + size % 2, SeekOrigin.Current);
                }
                else if (id == "DSD")
                {
                    _dataOffset = _stream.Position;
                    _dataBytes = size;
                    reader.BaseStream.Seek(size + size % 2, SeekOrigin.Current);
                }
                else
                {
                    reader.BaseStream.Seek(size + size % 2, SeekOrigin.Current);
                }
            }

            if (compressed)
                throw new AudioOpenException(
                    $"DST 압축 DFF는 지원하지 않습니다 (무압축 DSD만): {path}",
                    new NotSupportedException("dst compression"));
            if (!haveRate || !haveChannels || _dataOffset < 0)
            {
                var missing = new List<string>();
                if (!haveRate) missing.Add("FS");
                if (!haveChannels) missing.Add("CHAN");
                if (_dataOffset < 0) missing.Add("DSD");
                throw new AudioOpenException(
                    $"DFF 헤더가 비정상입니다 ({string.Join(",", missing)} 누락): {path}",
                    new InvalidDataException("missing chunks: " + string.Join(",", missing)));
            }
            if (_channelCount is < 1 or > 8 || DsdSampleRate <= 0)
                throw new AudioOpenException($"DFF 헤더가 비정상입니다: {path}", new InvalidDataException("bad header"));

            TotalDsdFrames = _dataBytes / _channelCount * 8;
        }
        catch (AudioOpenException)
        {
            _stream.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            _stream.Dispose();
            throw new AudioOpenException($"DFF 파일을 열 수 없습니다: {path}", ex);
        }
    }

    public int ReadRawFrames(byte[][] channelBytes, int framesWanted)
    {
        long remaining = TotalDsdFrames - _positionFrames;
        int frames = (int)Math.Min(framesWanted - framesWanted % 8, Math.Max(remaining, 0));
        if (frames <= 0) return 0;

        int bytes = frames / 8;
        int channels = _channelCount;
        long bytePos = _positionFrames / 8;
        _stream.Seek(_dataOffset + bytePos * channels, SeekOrigin.Begin);

        int done = 0;
        while (done < bytes)
        {
            int chunk = Math.Min(_interleaved.Length / channels, bytes - done) * channels;
            int got = DsfRawReader.ReadExactly(_stream, _interleaved, 0, chunk);
            int gotFrames = got / channels;
            if (gotFrames == 0) break;
            for (int c = 0; c < channels; c++)
            {
                for (int i = 0; i < gotFrames; i++)
                {
                    channelBytes[c][done / 8 + i] = _interleaved[i * channels + c];
                }
            }
            done += gotFrames * 8;
        }

        _positionFrames += done;
        return done;
    }

    internal static long ReadInt64BigEndian(BinaryReader reader)
    {
        Span<byte> b = stackalloc byte[8];
        _ = reader.Read(b);
        if (BitConverter.IsLittleEndian) b.Reverse();
        return BitConverter.ToInt64(b);
    }

    internal static int ReadInt32BigEndian(BinaryReader reader)
    {
        Span<byte> b = stackalloc byte[4];
        _ = reader.Read(b);
        if (BitConverter.IsLittleEndian) b.Reverse();
        return BitConverter.ToInt32(b);
    }

    /// <summary>AIFF-style 80-bit IEEE 754 extended float (DSDIFF 'FS' chunk format).</summary>
    internal static long ReadExtended80(BinaryReader reader)
    {
        Span<byte> b = stackalloc byte[10];
        _ = reader.Read(b);
        int sign = (b[0] & 0x80) != 0 ? -1 : 1;
        int exponent = ((b[0] & 0x7F) << 8) | b[1];
        ulong mantissa = 0;
        for (int i = 2; i < 10; i++)
        {
            mantissa = (mantissa << 8) | b[i];
        }

        // The mantissa is a 64-bit fraction behind an implicit point: value = mantissa·2^(e−16383−63).
        double value = mantissa * Math.Pow(2, exponent - 16383 - 63);
        return (long)(sign * value);
    }

    public void Dispose() => _stream.Dispose();
}
