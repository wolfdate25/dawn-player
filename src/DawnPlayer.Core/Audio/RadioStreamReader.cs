using System.Diagnostics;
using System.Net.Http;
using System.Text;
using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Icecast/Shoutcast MP3 radio stream: HTTP GET with ICY metadata, MP3 frame demux + ACM decode
/// into a growing PCM buffer that the sequencer pulls from. The stream never ends, so
/// <see cref="TotalTime"/> is zero and seeking is a no-op; buffer underruns emit silence instead
/// of end-of-stream (the sequencer must not advance away from a live radio on a network hiccup).
/// </summary>
public sealed class RadioStreamReader : ITrackReader, ILiveMetadataSource
{
    /// <summary>Decoded seconds to buffer before reporting the reader as open.</summary>
    private const double PrebufferSeconds = 1.5;

    private readonly HttpClient _client;
    private readonly BufferedPcm _pcm = new();
    private readonly string _url;
    private readonly Stopwatch _playedClock = Stopwatch.StartNew();
    private Task? _fillTask;
    private volatile string _streamTitle = "";

    public event Action<string>? StreamTitleChanged;

    /// <summary>Station name announced via the ICY <c>icy-name</c> header; empty when absent.</summary>
    public string StationName { get; private set; } = "";

    public RadioStreamReader(string url)
    {
        _url = url;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _client.DefaultRequestHeaders.Add("Icy-MetaData", "1");
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("DawnPlayer/1.0");
    }

    /// <summary>Most recent ICY StreamTitle (the station's now-playing string), empty until seen.</summary>
    public string StreamTitle => _streamTitle;

    public WaveFormat SourceFormat { get; private set; } = null!;

    public ISampleProvider Samples { get; private set; } = null!;

    public TimeSpan TotalTime => TimeSpan.Zero;

    /// <summary>Time since open — the only meaningful "position" for a live stream.</summary>
    public TimeSpan CurrentTime => _playedClock.Elapsed;

    /// <summary>Opening the URL, negotiating the format and prebuffering. Throws AudioOpenException.</summary>
    public void Connect()
    {
        try
        {
            ConnectCore();
        }
        catch (AudioOpenException) { throw; }
        catch (Exception ex)
        {
            throw new AudioOpenException($"라디오 스트림을 열 수 없습니다: {_url}", ex);
        }
    }

    private void ConnectCore()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _url);
        var response = _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();

        int metaInterval = 0;
        if (response.Headers.TryGetValues("icy-metaint", out var metaValues) &&
            int.TryParse(metaValues.FirstOrDefault(), out var parsed))
        {
            metaInterval = parsed;
        }

        string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var icyName = response.Headers.TryGetValues("icy-name", out var names) ? names.FirstOrDefault() : null;
        if (!string.IsNullOrWhiteSpace(icyName))
        {
            // Station identity lives in its own slot; StreamTitle stays empty until the stream
            // actually sends now-playing metadata.
            StationName = icyName.Trim();
        }

        Stream audio = stream;
        if (metaInterval > 0)
        {
            audio = new IcyMetadataFilter(stream, metaInterval,
                title => { _streamTitle = title; StreamTitleChanged?.Invoke(title); });
        }

        // First MP3 frame fixes the output format; the ACM decoder then runs on the fill task.
        var firstFrame = Mp3Frame.LoadFromStream(audio)
            ?? throw new AudioOpenException($"MP3 스트림이 아닙니다 ({contentType}): {_url}",
                new NotSupportedException(contentType));
        var decompressor = new AcmMp3FrameDecompressor(new Mp3WaveFormat(firstFrame.SampleRate, firstFrame.ChannelMode == ChannelMode.Mono ? 1 : 2, firstFrame.FrameLength, firstFrame.BitRate));

        SourceFormat = decompressor.OutputFormat;
        Samples = new PcmSampleProvider(_pcm, SourceFormat);

        _fillTask = Task.Run(() => FillLoop(audio, firstFrame, decompressor));
        _pcm.WaitUntilBuffered(PrebufferSeconds, TimeSpan.FromSeconds(8));
    }

    private void FillLoop(Stream audio, Mp3Frame first, AcmMp3FrameDecompressor decompressor)
    {
        try
        {
            var frame = first;
            while (frame != null)
            {
                var decoded = new byte[decompressor.OutputFormat.AverageBytesPerSecond];
                int decodedBytes = decompressor.DecompressFrame(frame, decoded, 0);
                if (decodedBytes > 0)
                {
                    _pcm.AddBytes(decoded, decodedBytes);
                }
                frame = Mp3Frame.LoadFromStream(audio);
            }
        }
        catch
        {
            // Network died: mark the stream dead; the sample provider keeps emitting silence so
            // playback halts quietly instead of tearing down the session.
            _pcm.MarkDead();
        }
    }

    TimeSpan ITrackReader.CurrentTime
    {
        get => CurrentTime;
        set { /* no-op */ }
    }

    string ITrackReader.Path => _url;

    public void Dispose()
    {
        _pcm.MarkDead();
        try { _client.Dispose(); } catch { }
        try { _fillTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    // ---------------- ICY metadata stripping ----------------

    /// <summary>Wraps the HTTP body and removes ICY metadata blocks, surfacing StreamTitle.</summary>
    private sealed class IcyMetadataFilter : Stream
    {
        private readonly Stream _inner;
        private readonly int _metaInterval;
        private readonly Action<string> _onTitle;
        private int _untilMeta;

        public IcyMetadataFilter(Stream inner, int metaInterval, Action<string> onTitle)
        {
            _inner = inner;
            _metaInterval = metaInterval;
            _untilMeta = metaInterval;
            _onTitle = onTitle;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                if (_untilMeta == 0)
                {
                    var lengthByte = _inner.ReadByte();
                    if (lengthByte < 0) break;
                    int metaLen = lengthByte * 16;
                    if (metaLen > 0)
                    {
                        var meta = new byte[metaLen];
                        int got = 0;
                        while (got < metaLen)
                        {
                            int r = _inner.Read(meta, got, metaLen - got);
                            if (r <= 0) return total > 0 ? total : (got > 0 ? total : 0);
                            got += r;
                        }
                        _onTitle(RadioTrack.ParseStreamTitle(meta));
                    }
                    _untilMeta = _metaInterval;
                }

                int want = Math.Min(count - total, _untilMeta);
                int read = _inner.Read(buffer, offset + total, want);
                if (read <= 0) break;
                _untilMeta -= read;
                total += read;
            }
            return total;
        }


        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---------------- PCM buffering ----------------

    // The thread-safe byte queue lives in BufferedPcm.cs, shared with the YouTube pipe reader.
    // Radio keeps its own end-of-stream policy here: silence on underrun, never an end.

    /// <summary>Serves the buffered 16-bit PCM as floats; silence on underrun (never end-of-stream).</summary>
    private sealed class PcmSampleProvider : ISampleProvider
    {
        private readonly BufferedPcm _pcm;
        private readonly WaveFormat _format;
        private byte[] _byteScratch = new byte[8192];

        public PcmSampleProvider(BufferedPcm pcm, WaveFormat format)
        {
            _pcm = pcm;
            _format = format;
        }

        public WaveFormat WaveFormat => _format;

        public int Read(Span<float> buffer)
        {
            int count = buffer.Length;
            int bytesNeeded = count * 2; // 16-bit mono-equivalent
            if (_byteScratch.Length < bytesNeeded) _byteScratch = new byte[bytesNeeded];

            int got = _pcm.Read(_byteScratch, 0, bytesNeeded);
            int frames = got / 2 / _format.Channels;

            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < _format.Channels; c++)
                {
                    int byteIdx = (f * _format.Channels + c) * 2;
                    short sample = (short)(_byteScratch[byteIdx] | (_byteScratch[byteIdx + 1] << 8));
                    buffer[f * _format.Channels + c] = sample / 32768f;
                }
            }

            // Underrun: emit silence. Returning 0 would tell the sequencer the track ended and
            // advance the playlist away from a live station on a momentary network stall.
            for (int i = frames * _format.Channels; i < count; i++)
            {
                buffer[i] = 0f;
            }
            return count;
        }
    }
}
