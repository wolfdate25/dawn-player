using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

/// <summary>Trivial channel up/down-mix (1↔2) since WASAPI exclusive and shared
/// mix formats may not match the file's channel count.</summary>
public sealed class ChannelConverterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _inCh;
    private readonly int _outCh;
    private float[] _inBuf = new float[8192];

    public WaveFormat WaveFormat { get; }

    public ChannelConverterSampleProvider(ISampleProvider source, int outChannels)
    {
        _source = source;
        _inCh = source.WaveFormat.Channels;
        _outCh = outChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outChannels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int outFrames = count / _outCh;
        int needIn = outFrames * _inCh;
        if (_inBuf.Length < needIn) Array.Resize(ref _inBuf, needIn);

        int inRead = _source.Read(_inBuf, 0, needIn);
        if (inRead == 0) return 0;

        int frames = inRead / _inCh;
        int written = 0;
        for (int f = 0; f < frames; f++)
        {
            if (_inCh == 1 && _outCh == 2)
            {
                var s = _inBuf[f];
                buffer[offset + written++] = s;
                buffer[offset + written++] = s;
            }
            else if (_inCh == 2 && _outCh == 1)
            {
                buffer[offset + written++] = (_inBuf[f * 2] + _inBuf[f * 2 + 1]) * 0.5f;
            }
            else
            {
                for (int c = 0; c < _outCh; c++)
                    buffer[offset + written++] = _inBuf[f * _inCh + Math.Min(c, _inCh - 1)];
            }
        }
        return written;
    }
}

/// <summary>Float → PCM byte conversion for 16/24/32-bit integer and 32-bit float targets.
/// Handles plain formats and <see cref="WaveFormatExtensible"/> (the usual shape of a
/// WASAPI shared-mode mix format and of exclusive 24/32-bit formats).</summary>
public static class PcmConvert
{
    private static readonly Guid SubFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubFormatIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static void ToBytes(float[] src, int floatCount, byte[] dest, int destOffset, WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm;

        if (format is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == SubFormatIeeeFloat) isFloat = true;
            else if (ext.SubFormat == SubFormatPcm) isPcm = true;
        }

        if (isFloat && format.BitsPerSample == 32)
        {
            Buffer.BlockCopy(src, 0, dest, destOffset, floatCount * 4);
            return;
        }

        if (isPcm)
        {
            switch (format.BitsPerSample)
            {
                case 16:
                    {
                        int d = destOffset;
                        for (int i = 0; i < floatCount; i++)
                        {
                            var v = (int)Math.Clamp(src[i] * 32767f, -32768f, 32767f);
                            dest[d++] = (byte)v;
                            dest[d++] = (byte)(v >> 8);
                        }
                        return;
                    }
                case 24:
                    {
                        int d = destOffset;
                        for (int i = 0; i < floatCount; i++)
                        {
                            var v = (int)Math.Clamp(src[i] * 8388607f, -8388608f, 8388607f);
                            dest[d++] = (byte)v;
                            dest[d++] = (byte)(v >> 8);
                            dest[d++] = (byte)(v >> 16);
                        }
                        return;
                    }
                case 32:
                    {
                        // int.MaxValue has no exact float representation: 2147483647f rounds up to
                        // 2^31. Clamping against it therefore lets a full-scale sample reach 2^31,
                        // which is out of range for the int cast and flips the sample's polarity —
                        // an ear-splitting click on every sample that hits 0 dBFS. Clamp to the
                        // largest float that still fits in an int instead.
                        const float max32 = 2147483520f; // largest float below int.MaxValue
                        const float min32 = -2147483648f; // int.MinValue, exactly representable
                        int d = destOffset;
                        for (int i = 0; i < floatCount; i++)
                        {
                            var v = (int)Math.Clamp(src[i] * 2147483647f, min32, max32);
                            dest[d++] = (byte)v;
                            dest[d++] = (byte)((uint)v >> 8);
                            dest[d++] = (byte)((uint)v >> 16);
                            dest[d++] = (byte)((uint)v >> 24);
                        }
                        return;
                    }
            }
        }

        throw new NotSupportedException($"Unsupported output format: {format}");
    }
}
