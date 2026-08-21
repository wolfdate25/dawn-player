using System;
using System.IO;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Audio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Comprehensive unit tests for audio PCM byte conversion and channel sample provider up/down-mixing:
/// 1. PcmConvert.ToBytes: 16-bit, 24-bit, 32-bit integer PCM, 32-bit IEEE Float fast path, WaveFormatExtensible.
/// 2. ChannelConverterSampleProvider: Mono to Stereo up-mixing, Stereo to Mono down-mixing (* 0.5f), buffer resizing.
/// </summary>
public class PcmConvertAndSampleProviderTests
{
    #region 1. PcmConvert.ToBytes Tests

    [Fact]
    public void PcmConvert_16Bit_ConvertsCorrectly_AndClamps()
    {
        var format = new WaveFormat(44100, 16, 2);
        float[] src = { 0.0f, 1.0f, -1.0f, 0.5f, -0.5f, 2.5f, -3.0f };
        byte[] dest = new byte[src.Length * 2];

        PcmConvert.ToBytes(src, src.Length, dest, 0, format);

        // 0.0f -> 0 (0x0000)
        Assert.Equal(0, BitConverter.ToInt16(dest, 0));

        // 1.0f -> 32767 (0x7FFF)
        Assert.Equal(32767, BitConverter.ToInt16(dest, 2));

        // -1.0f -> (int)(-1.0f * 32767f) = -32767
        Assert.Equal(-32767, BitConverter.ToInt16(dest, 4));

        // 0.5f -> (int)(0.5f * 32767f) = 16383 (0x3FFF)
        Assert.Equal((short)(0.5f * 32767f), BitConverter.ToInt16(dest, 6));

        // -0.5f -> (int)(-0.5f * 32767f) = -16383
        Assert.Equal((short)(-0.5f * 32767f), BitConverter.ToInt16(dest, 8));

        // 2.5f -> clamped to 32767
        Assert.Equal(32767, BitConverter.ToInt16(dest, 10));

        // -3.0f -> clamped to -32768
        Assert.Equal(-32768, BitConverter.ToInt16(dest, 12));
    }

    [Fact]
    public void PcmConvert_16Bit_RespectsDestOffset()
    {
        var format = new WaveFormat(44100, 16, 1);
        float[] src = { 1.0f };
        byte[] dest = new byte[10];

        PcmConvert.ToBytes(src, 1, dest, 4, format);

        // Bytes 0-3 should remain 0
        Assert.Equal(0, dest[0]);
        Assert.Equal(0, dest[1]);
        Assert.Equal(0, dest[2]);
        Assert.Equal(0, dest[3]);

        // Bytes 4-5 should contain 32767 (0xFF, 0x7F)
        Assert.Equal(0xFF, dest[4]);
        Assert.Equal(0x7F, dest[5]);

        // Bytes 6-9 should remain 0
        Assert.Equal(0, dest[6]);
        Assert.Equal(0, dest[7]);
    }

    [Fact]
    public void PcmConvert_16Bit_ExtremeBoundaryClamping()
    {
        var format = new WaveFormat(44100, 16, 1);
        float[] src = {
            1.0f, -1.0f,
            1.0001f, -1.0001f,
            100.0f, -100.0f,
            float.MaxValue, float.MinValue,
            float.PositiveInfinity, float.NegativeInfinity,
            0.00001f, -0.00001f
        };
        byte[] dest = new byte[src.Length * 2];

        PcmConvert.ToBytes(src, src.Length, dest, 0, format);

        // 1.0f -> 32767
        Assert.Equal(32767, BitConverter.ToInt16(dest, 0));
        // -1.0f -> -32767
        Assert.Equal(-32767, BitConverter.ToInt16(dest, 2));
        // > 1.0f clamped to 32767
        Assert.Equal(32767, BitConverter.ToInt16(dest, 4));
        // < -1.0f clamped to -32768
        Assert.Equal(-32768, BitConverter.ToInt16(dest, 6));
        // Extreme positive clamped to 32767
        Assert.Equal(32767, BitConverter.ToInt16(dest, 8));
        // Extreme negative clamped to -32768
        Assert.Equal(-32768, BitConverter.ToInt16(dest, 10));
        // float.MaxValue clamped to 32767
        Assert.Equal(32767, BitConverter.ToInt16(dest, 12));
        // float.MinValue clamped to -32768
        Assert.Equal(-32768, BitConverter.ToInt16(dest, 14));
        // PositiveInfinity clamped to 32767
        Assert.Equal(32767, BitConverter.ToInt16(dest, 16));
        // NegativeInfinity clamped to -32768
        Assert.Equal(-32768, BitConverter.ToInt16(dest, 18));
    }

    [Fact]
    public void PcmConvert_24Bit_ConvertsCorrectly_AndClamps()
    {
        var format = new WaveFormat(48000, 24, 2);
        float[] src = { 0.0f, 1.0f, -1.0f, 0.5f, 5.0f, -10.0f };
        byte[] dest = new byte[src.Length * 3];

        PcmConvert.ToBytes(src, src.Length, dest, 0, format);

        // Helper to read 24-bit signed integer little-endian
        static int ReadInt24(byte[] b, int offset)
        {
            int val = b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16);
            if ((val & 0x800000) != 0) val |= unchecked((int)0xFF000000);
            return val;
        }

        // 0.0f -> 0
        Assert.Equal(0, ReadInt24(dest, 0));

        // 1.0f -> 8388607 (0x7FFFFF: 0xFF, 0xFF, 0x7F)
        Assert.Equal(8388607, ReadInt24(dest, 3));
        Assert.Equal(0xFF, dest[3]);
        Assert.Equal(0xFF, dest[4]);
        Assert.Equal(0x7F, dest[5]);

        // -1.0f -> -8388607 (0x800001: 0x01, 0x00, 0x80)
        Assert.Equal(-8388607, ReadInt24(dest, 6));
        Assert.Equal(0x01, dest[6]);
        Assert.Equal(0x00, dest[7]);
        Assert.Equal(0x80, dest[8]);

        // 0.5f -> (int)(0.5f * 8388607f) = 4194303
        Assert.Equal((int)(0.5f * 8388607f), ReadInt24(dest, 9));

        // 5.0f -> clamped to 8388607
        Assert.Equal(8388607, ReadInt24(dest, 12));

        // -10.0f -> clamped to -8388608 (0x800000: 0x00, 0x00, 0x80)
        Assert.Equal(-8388608, ReadInt24(dest, 15));
        Assert.Equal(0x00, dest[15]);
        Assert.Equal(0x00, dest[16]);
        Assert.Equal(0x80, dest[17]);
    }

    [Fact]
    public void PcmConvert_24Bit_ExactBitwiseEncodingAndSignExtension()
    {
        var format = new WaveFormat(48000, 24, 1);
        // Test values: 0, 1/8388607 (gives 1), -1/8388607 (gives -1), max (8388607), min (-8388608)
        float[] src = {
            0.0f,
            1.0f / 8388607f,
            -1.0f / 8388607f,
            1.0f,
            -1.0000002f, // Clamps to -8388608
            100.0f,
            -100.0f
        };
        byte[] dest = new byte[src.Length * 3];

        PcmConvert.ToBytes(src, src.Length, dest, 0, format);

        static int ReadInt24(byte[] b, int offset)
        {
            int val = b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16);
            if ((val & 0x800000) != 0) val |= unchecked((int)0xFF000000);
            return val;
        }

        // 0.0f -> 0 (0x000000)
        Assert.Equal(0, ReadInt24(dest, 0));
        Assert.Equal(0, dest[0]);
        Assert.Equal(0, dest[1]);
        Assert.Equal(0, dest[2]);

        // +1 sample -> 1 (0x000001)
        Assert.Equal(1, ReadInt24(dest, 3));
        Assert.Equal(1, dest[3]);
        Assert.Equal(0, dest[4]);
        Assert.Equal(0, dest[5]);

        // -1 sample -> -1 (0xFFFFFF: 0xFF, 0xFF, 0xFF)
        Assert.Equal(-1, ReadInt24(dest, 6));
        Assert.Equal(0xFF, dest[6]);
        Assert.Equal(0xFF, dest[7]);
        Assert.Equal(0xFF, dest[8]);

        // 1.0f -> 8388607 (0x7FFFFF: 0xFF, 0xFF, 0x7F)
        Assert.Equal(8388607, ReadInt24(dest, 9));
        Assert.Equal(0xFF, dest[9]);
        Assert.Equal(0xFF, dest[10]);
        Assert.Equal(0x7F, dest[11]);

        // -1.0000002f -> -8388608 (0x800000: 0x00, 0x00, 0x80)
        Assert.Equal(-8388608, ReadInt24(dest, 12));
        Assert.Equal(0x00, dest[12]);
        Assert.Equal(0x00, dest[13]);
        Assert.Equal(0x80, dest[14]);

        // Clamped +100.0f -> 8388607
        Assert.Equal(8388607, ReadInt24(dest, 15));
        // Clamped -100.0f -> -8388608
        Assert.Equal(-8388608, ReadInt24(dest, 18));
    }

    [Fact]
    public void PcmConvert_32Bit_Integer_NormalRangeAndNegativeClamping()
    {
        var format = new WaveFormat(96000, 32, 1);
        float[] src = { 0.5f, -0.5f, 0.99999f, -1.0f, -10.0f };
        byte[] dest = new byte[src.Length * 4];

        PcmConvert.ToBytes(src, src.Length, dest, 0, format);

        Assert.Equal((int)(0.5f * 2147483647f), BitConverter.ToInt32(dest, 0));
        Assert.Equal((int)(-0.5f * 2147483647f), BitConverter.ToInt32(dest, 4));
        Assert.Equal((int)(0.99999f * 2147483647f), BitConverter.ToInt32(dest, 8));
        Assert.Equal(int.MinValue, BitConverter.ToInt32(dest, 12));
        Assert.Equal(int.MinValue, BitConverter.ToInt32(dest, 16));
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(1.0000001f)]
    [InlineData(2.0f)]
    [InlineData(1000.0f)]
    [InlineData(float.PositiveInfinity)]
    public void PcmConvert_32Bit_Integer_AtOrAboveFullScale_SaturatesPositive(float sample)
    {
        // 2147483647f is not representable in single precision — it rounds up to 2^31 — so a
        // naive clamp against int.MaxValue lets a full-scale sample reach 2^31 and wrap to
        // int.MinValue on the cast, inverting the sample's polarity. That produces a full-scale
        // click on every sample that touches 0 dBFS, so the conversion must saturate positive.
        var format = new WaveFormat(96000, 32, 1);
        byte[] dest = new byte[4];

        PcmConvert.ToBytes(new[] { sample }, 1, dest, 0, format);

        int result = BitConverter.ToInt32(dest, 0);
        Assert.True(result > 0, $"positive full-scale input {sample} produced {result}");
        Assert.True(result >= 2147483520, $"expected saturation near int.MaxValue, got {result}");
    }

    [Theory]
    [InlineData(-1.0f)]
    [InlineData(-2.0f)]
    [InlineData(-1000.0f)]
    [InlineData(float.NegativeInfinity)]
    public void PcmConvert_32Bit_Integer_AtOrBelowNegativeFullScale_SaturatesNegative(float sample)
    {
        var format = new WaveFormat(96000, 32, 1);
        byte[] dest = new byte[4];

        PcmConvert.ToBytes(new[] { sample }, 1, dest, 0, format);

        int result = BitConverter.ToInt32(dest, 0);
        Assert.True(result < 0, $"negative full-scale input {sample} produced {result}");
        Assert.True(result <= -2147483520, $"expected saturation near int.MinValue, got {result}");
    }

    [Fact]
    public void PcmConvert_32Bit_Integer_FullScaleRamp_NeverChangesSign()
    {
        // Sweeping up to and past full scale must stay monotonically non-negative: any sign
        // flip here is the polarity-inversion defect.
        var format = new WaveFormat(96000, 32, 1);
        var src = new float[256];
        for (int i = 0; i < src.Length; i++) src[i] = 0.90f + (i * 0.001f); // 0.90 .. 1.155
        var dest = new byte[src.Length * 4];

        PcmConvert.ToBytes(src, src.Length, dest, 0, format);

        for (int i = 0; i < src.Length; i++)
        {
            int v = BitConverter.ToInt32(dest, i * 4);
            Assert.True(v > 0, $"sample {i} (input {src[i]}) converted to {v}");
        }
    }

    [Fact]
    public void PcmConvert_32Bit_IeeeFloat_FastPath_CopiesDirectly()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        float[] src = { 0.0f, 1.0f, -1.0f, 0.123456f, 999.5f, -888.25f, float.NaN, float.PositiveInfinity };
        byte[] dest = new byte[src.Length * 4];

        PcmConvert.ToBytes(src, src.Length, dest, 0, format);

        for (int i = 0; i < src.Length; i++)
        {
            float actual = BitConverter.ToSingle(dest, i * 4);
            if (float.IsNaN(src[i]))
            {
                Assert.True(float.IsNaN(actual));
            }
            else
            {
                Assert.Equal(src[i], actual);
            }
        }
    }

    [Fact]
    public void PcmConvert_WaveFormatExtensible_PcmAndFloatSupport()
    {
        // 1. WaveFormatExtensible with SubFormat PCM 24-bit
        var extPcm24 = new WaveFormatExtensible(48000, 24, 2);
        float[] srcPcm = { 1.0f, -5.0f };
        byte[] destPcm = new byte[srcPcm.Length * 3];

        PcmConvert.ToBytes(srcPcm, srcPcm.Length, destPcm, 0, extPcm24);

        Assert.Equal(0xFF, destPcm[0]);
        Assert.Equal(0xFF, destPcm[1]);
        Assert.Equal(0x7F, destPcm[2]); // 8388607
        Assert.Equal(0x00, destPcm[3]);
        Assert.Equal(0x00, destPcm[4]);
        Assert.Equal(0x80, destPcm[5]); // -8388608 (clamped)

        // 2. WaveFormatExtensible with SubFormat IEEE Float 32-bit
        var extFloat32 = WaveFormatExtensible.CreateIeeeFloatWaveFormat(44100, 2);
        float[] srcFloat = { 0.75f, -0.25f };
        byte[] destFloat = new byte[srcFloat.Length * 4];

        PcmConvert.ToBytes(srcFloat, srcFloat.Length, destFloat, 0, extFloat32);

        Assert.Equal(0.75f, BitConverter.ToSingle(destFloat, 0));
        Assert.Equal(-0.25f, BitConverter.ToSingle(destFloat, 4));
    }

    [Fact]
    public void PcmConvert_UnsupportedFormat_ThrowsNotSupportedException()
    {
        // 8-bit PCM is not supported
        var format8Bit = new WaveFormat(44100, 8, 1);
        float[] src = { 0.5f };
        byte[] dest = new byte[4];

        Assert.Throws<NotSupportedException>(() => PcmConvert.ToBytes(src, 1, dest, 0, format8Bit));
    }

    #endregion

    #region 2. ChannelConverterSampleProvider Tests

    private sealed class MockSampleSource : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public WaveFormat WaveFormat { get; }

        public MockSampleSource(int sampleRate, int channels, float[] samples)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _samples = samples;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, _samples.Length - _position);
            if (available <= 0) return 0;
            Array.Copy(_samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }

    [Fact]
    public void ChannelConverter_MonoToStereo_DuplicatesChannels()
    {
        float[] monoSamples = { 0.1f, 0.2f, -0.5f, 0.9f };
        var source = new MockSampleSource(44100, 1, monoSamples);
        var converter = new ChannelConverterSampleProvider(source, 2);

        Assert.Equal(2, converter.WaveFormat.Channels);
        Assert.Equal(44100, converter.WaveFormat.SampleRate);

        float[] buffer = new float[8];
        int read = converter.Read(buffer, 0, buffer.Length);

        Assert.Equal(8, read);
        // Each mono sample is duplicated to (L, R)
        Assert.Equal(0.1f, buffer[0]);
        Assert.Equal(0.1f, buffer[1]);
        Assert.Equal(0.2f, buffer[2]);
        Assert.Equal(0.2f, buffer[3]);
        Assert.Equal(-0.5f, buffer[4]);
        Assert.Equal(-0.5f, buffer[5]);
        Assert.Equal(0.9f, buffer[6]);
        Assert.Equal(0.9f, buffer[7]);
    }

    [Fact]
    public void ChannelConverter_StereoToMono_AveragingPreservesEnergy()
    {
        float[] stereo = { 1.0f, 1.0f, -1.0f, 1.0f, 0.5f, -0.5f, 0.3f, 0.7f };
        var src = new MockSampleSource(48000, 2, stereo);
        var conv = new ChannelConverterSampleProvider(src, 1);

        Assert.Equal(1, conv.WaveFormat.Channels);
        Assert.Equal(48000, conv.WaveFormat.SampleRate);

        float[] output = new float[4];
        int read = conv.Read(output, 0, output.Length);

        Assert.Equal(4, read);
        Assert.Equal(1.0f, output[0], precision: 5);
        Assert.Equal(0.0f, output[1], precision: 5);
        Assert.Equal(0.0f, output[2], precision: 5);
        Assert.Equal(0.5f, output[3], precision: 5);
    }

    [Fact]
    public void ChannelConverter_PassThrough_PreservesSamples()
    {
        float[] stereoSamples = { 0.1f, 0.2f, 0.3f, 0.4f };
        var source = new MockSampleSource(44100, 2, stereoSamples);
        var converter = new ChannelConverterSampleProvider(source, 2);

        float[] buffer = new float[4];
        int read = converter.Read(buffer, 0, buffer.Length);

        Assert.Equal(4, read);
        Assert.Equal(stereoSamples, buffer);
    }

    [Fact]
    public void ChannelConverter_MassiveBufferResize_Stress()
    {
        // Initial buffer in ChannelConverterSampleProvider is 8192 floats;
        // 65536 mono samples -> 131072 stereo samples forces repeated regrowth.
        int frameCount = 65536;
        float[] mono = new float[frameCount];
        for (int i = 0; i < frameCount; i++) mono[i] = (float)Math.Sin(i * 0.01);

        var src = new MockSampleSource(44100, 1, mono);
        var conv = new ChannelConverterSampleProvider(src, 2);

        float[] output = new float[frameCount * 2];
        int read = conv.Read(output, 0, output.Length);

        Assert.Equal(frameCount * 2, read);
        for (int i = 0; i < frameCount; i++)
        {
            Assert.Equal(mono[i], output[i * 2]);
            Assert.Equal(mono[i], output[i * 2 + 1]);
        }
    }

    [Fact]
    public void ChannelConverter_EndOfStream_ReturnsZero()
    {
        float[] empty = Array.Empty<float>();
        var source = new MockSampleSource(44100, 1, empty);
        var converter = new ChannelConverterSampleProvider(source, 2);

        float[] buffer = new float[16];
        int read = converter.Read(buffer, 0, buffer.Length);

        Assert.Equal(0, read);
    }

    #endregion

    #region 3. SoftLimiterDspEffect Tests

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.1f)]
    [InlineData(-0.1f)]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    [InlineData(0.85f)]
    [InlineData(-0.85f)]
    [InlineData(0.90f)]
    [InlineData(-0.90f)]
    public void SoftLimiter_SamplesBelowOrAtThreshold_PassThroughUnchanged(float sample)
    {
        float limited = SoftLimiterDspEffect.Limit(sample, threshold: 0.90f);
        Assert.Equal(sample, limited);
    }

    [Theory]
    [InlineData(0.95f)]
    [InlineData(1.0f)]
    [InlineData(1.5f)]
    [InlineData(2.0f)]
    [InlineData(5.0f)]
    [InlineData(50.0f)]
    [InlineData(1000.0f)]
    public void SoftLimiter_PositiveExceedingThreshold_CompressesBelowOne(float sample)
    {
        float limited = SoftLimiterDspEffect.Limit(sample, threshold: 0.90f);
        Assert.True(limited >= 0.90f, "Limited sample should be at or above threshold");
        Assert.True(limited < 1.0f, "Limited sample should strictly stay below 1.0");
        Assert.True(limited < sample, "Limited sample should compress amplitude");
    }

    [Theory]
    [InlineData(-0.95f)]
    [InlineData(-1.0f)]
    [InlineData(-1.5f)]
    [InlineData(-2.0f)]
    [InlineData(-5.0f)]
    [InlineData(-50.0f)]
    [InlineData(-1000.0f)]
    public void SoftLimiter_NegativeExceedingThreshold_CompressesAboveMinusOne(float sample)
    {
        float limited = SoftLimiterDspEffect.Limit(sample, threshold: 0.90f);
        Assert.True(limited <= -0.90f, "Limited sample should be at or below -threshold");
        Assert.True(limited > -1.0f, "Limited sample should strictly stay above -1.0");
        Assert.True(limited > sample, "Limited sample should compress amplitude towards 0");
    }

    [Fact]
    public void SoftLimiter_IsStrictlyMonotonic()
    {
        float prev = float.NegativeInfinity;
        for (float x = -5.0f; x <= 5.0f; x += 0.01f)
        {
            float y = SoftLimiterDspEffect.Limit(x, threshold: 0.85f);
            Assert.True(y > prev, $"Monotonicity failed at x={x}: y={y} <= prev={prev}");
            prev = y;
        }
    }

    [Fact]
    public void SoftLimiter_ProcessBuffer_CompressesOnlySamplesAboveThreshold()
    {
        float[] buffer = { 0.0f, 0.5f, 0.9f, 1.2f, 2.0f, -0.5f, -0.9f, -1.5f };
        var limiter = new SoftLimiterDspEffect(threshold: 0.80f);

        Assert.Equal(0.80f, limiter.Threshold);

        limiter.Process(buffer, 0, buffer.Length);

        // Linear part
        Assert.Equal(0.0f, buffer[0]);
        Assert.Equal(0.5f, buffer[1]);
        // Compressed part
        Assert.True(buffer[3] > 0.80f && buffer[3] < 1.0f);
        Assert.True(buffer[4] > buffer[3] && buffer[4] < 1.0f);
        Assert.True(buffer[7] < -0.80f && buffer[7] > -1.0f);
        // Stateless processor: the configured threshold survives processing
        Assert.Equal(0.80f, limiter.Threshold);
    }

    #endregion
}
