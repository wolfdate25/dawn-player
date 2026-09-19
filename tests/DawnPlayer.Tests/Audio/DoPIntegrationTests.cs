using System;
using System.Linq;
using System.IO;
using System.Text;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;
using NAudio.Wave;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// DoP packing integrity. DoP rides the float pipeline as opaque 24-bit words — a single
/// corrupted bit in the 0x05/0xFA markers makes a DAC resync mid-stream — so these tests pin
/// the whole chain bit-exactly: packing, the float encoding, and the PcmConvert hop the
/// sequencer performs on its way to the device.
/// </summary>
public sealed class DoPPackingTests
{
    [Fact]
    public void PackSample_ProducesSignedContainerBytes()
    {
        // Marker in the middle byte; a 0xFA marker turns the container negative.
        // The container is a SIGNED 24-bit word: a first DSD byte with the top bit set flips
        // the sign, which is exactly what the bytes the DAC reads must preserve.
        int positive = DopTrackReader.PackSample(0x12, DopTrackReader.MarkerEven, 0xCD);
        Assert.Equal(0x1205CD, positive);

        int negative = DopTrackReader.PackSample(0xAB, DopTrackReader.MarkerEven, 0xCD);
        Assert.Equal(0xAB05CD - 0x1000000, negative);

        int neg = DopTrackReader.PackSample(0xFA, DopTrackReader.MarkerOdd, 0x01);
        Assert.Equal(0xFAFA01 - 0x1000000, neg);
    }

    [Fact]
    public void FloatEncoding_RoundTripsEveryContainerByte()
    {
        // Sweep the full byte space for the first DSD byte (which drives the sign bit) with
        // both markers, and demand bit-exact survival through the float hop and PcmConvert.
        var floats = new float[256 * 2];
        var expected = new byte[256 * 2 * 3];
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            foreach (var marker in new byte[] { DopTrackReader.MarkerEven, DopTrackReader.MarkerOdd })
            {
                int packed = DopTrackReader.PackSample((byte)b, marker, 0x42);
                floats[n] = DopTrackReader.ToContainerFloat(packed);
                expected[n * 3] = (byte)packed;
                expected[n * 3 + 1] = (byte)(packed >> 8);
                expected[n * 3 + 2] = (byte)(packed >> 16);
                n++;
            }
        }

        var dest = new byte[n * 3];
        PcmConvert.ToBytes(floats, n, dest, 0, new WaveFormat(176400, 24, 2));
        Assert.Equal(expected, dest);
    }

    [Fact]
    public void MarkerPhase_AlternatesPerAbsoluteSample_SoSeekLandsInPhase()
    {
        // Byte 0xAA (10101010) is the canary: the packed pattern is 0xAA marker AA / FA marker FA
        // alternating; after a seek the alternation must follow the absolute sample index.
        var floats = new float[4 * 2];
        for (int s = 0; s < 4; s++)
        {
            byte marker = (s & 1) == 0 ? DopTrackReader.MarkerEven : DopTrackReader.MarkerOdd;
            int packed = DopTrackReader.PackSample(0xAA, marker, 0xAA);
            floats[s * 2] = DopTrackReader.ToContainerFloat(packed);
            floats[s * 2 + 1] = DopTrackReader.ToContainerFloat(packed);
        }

        var dest = new byte[floats.Length * 3];
        PcmConvert.ToBytes(floats, floats.Length, dest, 0, new WaveFormat(176400, 24, 2));
        for (int s = 0; s < 4; s++)
        {
            byte marker = (s & 1) == 0 ? DopTrackReader.MarkerEven : DopTrackReader.MarkerOdd;
            Assert.Equal(marker, dest[s * 6 + 4]); // middle byte of the right-channel sample
        }
    }

    [Fact]
    public void SourceFormat_Is24BitPcmAtOneSixteenthOfDsdRate()
    {
        var raw = new SyntheticDsdRawReader(dsdRate: 2822400, channels: 2, totalFrames: 2822400);
        using var dop = new DopTrackReader(raw, "synthetic.dsf");
        Assert.Equal(176400, dop.SourceFormat.SampleRate);
        Assert.Equal(24, dop.SourceFormat.BitsPerSample);
        Assert.Equal(WaveFormatEncoding.Pcm, dop.SourceFormat.Encoding);
        Assert.Equal(2, dop.SourceFormat.Channels);
        Assert.Equal(TimeSpan.FromSeconds(1), dop.TotalTime);
    }

    [Fact]
    public void Read_PacksChannelBytesWithAlternatingMarkers()
    {
        // 32 DSD frames = 2 DoP samples per channel. Channel data crafted so each packed word
        // is verifiable: ch0 bytes 0x12,0x34 then 0x56,0x78; ch1 all 0xFF.
        var raw = new SyntheticDsdRawReader(dsdRate: 2822400, channels: 2, totalFrames: 32)
        {
            ChannelData = new[]
            {
                new byte[] { 0x12, 0x34, 0x56, 0x78 },
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
            },
        };

        using var dop = new DopTrackReader(raw, "synthetic.dsf");
        var buffer = new float[4 * 2];
        int read = dop.Samples.Read(buffer);
        // 32 DSD frames = 2 DoP samples per channel = 2 interleaved stereo frames.
        Assert.Equal(4, read);

        int p0 = DopTrackReader.PackSample(0x12, DopTrackReader.MarkerEven, 0x34);
        int p1 = DopTrackReader.PackSample(0x56, DopTrackReader.MarkerOdd, 0x78);
        int f0 = DopTrackReader.PackSample(0xFF, DopTrackReader.MarkerEven, 0xFF);
        int f1 = DopTrackReader.PackSample(0xFF, DopTrackReader.MarkerOdd, 0xFF);

        float ToF(int v) => DopTrackReader.ToContainerFloat(v);
        Assert.Equal(ToF(p0), buffer[0]);
        Assert.Equal(ToF(f0), buffer[1]);
        Assert.Equal(ToF(p1), buffer[2]);
        Assert.Equal(ToF(f1), buffer[3]);
    }

    [Fact]
    public void Seek_RepositionsTheRawReader()
    {
        var raw = new SyntheticDsdRawReader(dsdRate: 2822400, channels: 2, totalFrames: 2822400);
        using var dop = new DopTrackReader(raw, "synthetic.dsf");
        dop.CurrentTime = TimeSpan.FromSeconds(0.5);
        Assert.Equal(88200 * DopTrackReader.DsdFramesPerSample, raw.PositionFrames); // 0.5 s × 176400 samples × 16 DSD frames
        Assert.Equal(TimeSpan.FromSeconds(0.5), dop.CurrentTime);
    }

    /// <summary>In-memory raw DSD source standing in for a file container.</summary>
    private sealed class SyntheticDsdRawReader : IDsdRawReader
    {
        public byte[][]? ChannelData;
        private long _position;

        public SyntheticDsdRawReader(long dsdRate, int channels, long totalFrames)
        {
            DsdSampleRate = dsdRate;
            Channels = channels;
            TotalDsdFrames = totalFrames;
        }

        public int Channels { get; }
        public long DsdSampleRate { get; }
        public long TotalDsdFrames { get; }

        public long PositionFrames
        {
            get => _position;
            set => _position = value - value % 8;
        }

        public int ReadRawFrames(byte[][] channelBytes, int framesWanted)
        {
            long remaining = TotalDsdFrames - _position;
            int frames = (int)Math.Min(framesWanted - framesWanted % 8, Math.Max(remaining, 0));
            if (frames <= 0) return 0;
            int bytes = frames / 8;
            for (int c = 0; c < Channels; c++)
            {
                byte[] src = ChannelData is { } data && data.Length > c ? data[c] : new byte[bytes];
                for (int i = 0; i < bytes; i++)
                {
                    channelBytes[c][i] = src[i % src.Length];
                }
            }
            _position += frames;
            return frames;
        }

        public void Dispose() { }
    }
}

/// <summary>
/// The M3 rate-mismatch policy now has a DSD dimension: packed DoP cannot survive a resampler
/// and PCM cannot ride a DoP session, so a DoP/PCM boundary always restarts regardless of the
/// user's seamlessness choice. These cells pin that override alongside the original matrix.
/// </summary>
public sealed class ExclusiveDopPolicyTests
{
    private static WaveFormat Fmt(int rate, int bits = 24, int channels = 2) => new WaveFormat(rate, bits, channels);

    [Fact]
    public void DopBoundary_AlwaysRestarts_EvenUnderResamplePolicy()
    {
        var session = Fmt(176400);
        var pcmNext = Fmt(44100);
        Assert.True(PlaybackController.ExclusiveSessionRestartNeeded(
            ExclusiveRateMismatchPolicy.ResampleToCurrent, pcmNext, session, sessionIsDop: true, nextIsDop: false));
        Assert.True(PlaybackController.ExclusiveSessionRestartNeeded(
            ExclusiveRateMismatchPolicy.ResampleToCurrent, Fmt(176400), session, sessionIsDop: false, nextIsDop: true));
    }

    [Fact]
    public void DopSession_NeverHotSwaps_ToAnything()
    {
        Assert.False(PlaybackController.ExclusiveSessionAcceptsTrack(
            ExclusiveRateMismatchPolicy.ResampleToCurrent, null, Fmt(176400), sessionIsDop: true, nextIsDop: false));
        Assert.False(PlaybackController.ExclusiveSessionAcceptsTrack(
            ExclusiveRateMismatchPolicy.RestartSession, Fmt(176400), Fmt(176400), sessionIsDop: true, nextIsDop: true));
        Assert.False(PlaybackController.ExclusiveSessionAcceptsTrack(
            ExclusiveRateMismatchPolicy.ResampleToCurrent, Fmt(44100), Fmt(44100), sessionIsDop: false, nextIsDop: true));
    }

    [Fact]
    public void PcmCells_Unchanged_WhenNeitherSideIsDop()
    {
        var session = Fmt(44100);
        Assert.True(PlaybackController.ExclusiveSessionRestartNeeded(
            ExclusiveRateMismatchPolicy.RestartSession, Fmt(96000), session, sessionIsDop: false, nextIsDop: false));
        Assert.False(PlaybackController.ExclusiveSessionRestartNeeded(
            ExclusiveRateMismatchPolicy.ResampleToCurrent, Fmt(96000), session, sessionIsDop: false, nextIsDop: false));
        Assert.True(PlaybackController.ExclusiveSessionAcceptsTrack(
            ExclusiveRateMismatchPolicy.RestartSession, Fmt(44100), session, sessionIsDop: false, nextIsDop: false));
    }
}

/// <summary>
/// DFF (DSDIFF) container parsing: the FRM8 chunk walk, the 80-bit extended sample rate, and
/// channel de-interleaving of the DSD chunk. DST-compressed files must be rejected with a
/// clear error, never mis-decoded as raw DSD.
/// </summary>
public sealed class DffRawReaderTests : IDisposable
{
    private readonly string _path;

    public DffRawReaderTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "DawnDffTest_" + Guid.NewGuid().ToString("N") + ".dff");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private static void WriteChunk(Stream s, string id, byte[] payload)
    {
        // Chunk IDs are always 4 bytes in the file; short ids get space padding.
        var idBytes = Encoding.ASCII.GetBytes(id.PadRight(4));
        s.Write(idBytes, 0, 4);
        long size = payload.Length;
        for (int i = 7; i >= 0; i--) s.WriteByte((byte)(size >> (i * 8)));
        s.Write(payload, 0, payload.Length);
        if (payload.Length % 2 == 1) s.WriteByte(0); // even alignment
    }

    private static byte[] Extended80(double value)
    {
        var b = new byte[10];
        int exponent = 16383 + 63;
        // value must be an integer representable as a 64-bit mantissa × 2^k
        ulong mantissa = (ulong)value;
        while ((mantissa >> 63) == 0 && mantissa != 0)
        {
            mantissa <<= 1;
            exponent--;
        }
        b[0] = (byte)((exponent >> 8) & 0x7F);
        b[1] = (byte)exponent;
        for (int i = 0; i < 8; i++)
        {
            b[9 - i] = (byte)(mantissa >> (i * 8));
        }
        return b;
    }

    [Fact]
    public void ParsesHeader_AndDeinterleavesChannels()
    {
        {
            var body = new MemoryStream();
            WriteChunk(body, "FVER", new byte[] { 1, 5, 0, 0 });
            using (var prop = new MemoryStream())
            {
                WriteChunk(prop, "SND", Array.Empty<byte>());
                WriteChunk(prop, "FS", Extended80(2822400));
                WriteChunk(prop, "CHAN", new byte[] { 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0 });
                // PROP payload = the "SND " form type followed by property chunks.
                WriteChunk(body, "PROP", Encoding.ASCII.GetBytes("SND ").Concat(prop.ToArray()).ToArray());
            }
            // 3 byte-frames of stereo: L=AA 55 81, R=01 FF 7F → interleaved
            WriteChunk(body, "DSD ", new byte[] { 0xAA, 0x01, 0x55, 0xFF, 0x81, 0x7F });
            var bodyBytes = body.ToArray();

            var header = new MemoryStream();
            header.Write(Encoding.ASCII.GetBytes("FRM8"), 0, 4);
            long formSize = 4 + bodyBytes.Length;
            for (int i = 7; i >= 0; i--) header.WriteByte((byte)(formSize >> (i * 8)));
            header.Write(Encoding.ASCII.GetBytes("DSD "), 0, 4);
            header.Write(bodyBytes, 0, bodyBytes.Length);
            File.WriteAllBytes(_path, header.ToArray());
        }

        using var raw = new DffRawReader(_path);
        Assert.Equal(2822400, raw.DsdSampleRate);
        Assert.Equal(2, raw.Channels);
        Assert.Equal(24, raw.TotalDsdFrames);

        var ch0 = new byte[4];
        var ch1 = new byte[4];
        int frames = raw.ReadRawFrames(new[] { ch0, ch1 }, 24);
        Assert.Equal(24, frames);
        Assert.Equal(new byte[] { 0xAA, 0x55, 0x81, 0 }, ch0);
        Assert.Equal(new byte[] { 0x01, 0xFF, 0x7F, 0 }, ch1);
    }

    [Fact]
    public void DstCompression_IsRejectedWithClearError()
    {
        {
            var body = new MemoryStream();
            WriteChunk(body, "FVER", new byte[] { 1, 5, 0, 0 });
            using (var prop = new MemoryStream())
            {
                WriteChunk(prop, "SND", Array.Empty<byte>());
                WriteChunk(prop, "FS", Extended80(2822400));
                WriteChunk(prop, "CHAN", new byte[] { 0, 0, 0, 2 });
                // PROP payload = the "SND " form type followed by property chunks.
                WriteChunk(body, "PROP", Encoding.ASCII.GetBytes("SND ").Concat(prop.ToArray()).ToArray());
            }
            WriteChunk(body, "DST ", new byte[] { 1, 2, 3, 4 });
            WriteChunk(body, "DSTI", new byte[] { 5, 6, 7, 8 });

            var header = new MemoryStream();
            header.Write(Encoding.ASCII.GetBytes("FRM8"), 0, 4);
            long formSize = 4 + body.Length;
            for (int i = 7; i >= 0; i--) header.WriteByte((byte)(formSize >> (i * 8)));
            header.Write(Encoding.ASCII.GetBytes("DSD "), 0, 4);
            body.Position = 0;
            body.CopyTo(header);
            File.WriteAllBytes(_path, header.ToArray());
        }

        var ex = Assert.Throws<AudioOpenException>(() => new DffRawReader(_path));
        Assert.Contains("DST", ex.Message);
    }

    [Fact]
    public void DefaultDsdMode_IsPcmConversion()
    {
        // DoP must be opt-in: existing installs keep the boxcar path.
        Assert.Equal(DsdPlaybackMode.PcmAlways, new AppSettings().Output.DsdPlaybackMode);
    }
}
