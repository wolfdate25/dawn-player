using System.Diagnostics;
using DawnPlayer.Core.Network.YouTube;
using DawnPlayer.Core.Util;
using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// One-shot position re-anchor request from a reader whose clock is more truthful than the
/// sequencer's served-byte counter. The sequencer counts every block it is handed — including the
/// silence padding a streaming reader emits while its source restarts — so after a seek-restart
/// gap its position would permanently run ahead of what the listener actually heard. When the
/// reader's first real audio after a seek is served, it raises this once and the sequencer
/// re-anchors <c>_bytesServed</c> to the reader's position; "reported = heard" is restored.
/// Public for the contract tests.
/// </summary>
public interface IResyncRequestSource
{
    /// <summary>Consumes a pending re-anchor request. Returns false when none is pending.</summary>
    bool TryConsumeResync(out TimeSpan readerPosition);
}

/// <summary>
/// Streams a YouTube page through the yt-dlp → ffmpeg chain into the same buffered-PCM shape the
/// radio reader feeds the sequencer. Unlike radio this is a <em>finite</em> source: end-of-stream
/// (and a mid-stream process death) ends the track so the sequencer advances — a broken YouTube
/// stream must never degrade into the radio's forever-silence. Seek = synchronous position
/// bookkeeping plus an async chain restart from the new offset; the reported position is the seek
/// target plus actually-played PCM, so it can never drift from what the listener has heard.
/// </summary>
public sealed class YouTubeStreamReader : ITrackReader, IResyncRequestSource
{
    /// <summary>Decoded seconds to buffer before reporting the reader as open.</summary>
    private const double PrebufferSeconds = 1.5;

    /// <summary>Audio buffered below this after the connect timeout means the chain is stalled
    /// (hung resolve, signature loop) — open fails instead of starting an endless silent stall.</summary>
    private const double MinOpenSeconds = 0.25;

    /// <summary>Backpressure cap: yt-dlp downloads at network speed, not realtime, so without a
    /// ceiling a long video would pile up gigabytes in RAM (pauses included).</summary>
    private const double MaxBufferedSeconds = 30;

    private readonly string _pageUrl;
    private readonly YouTubeTrackMeta _meta;
    private readonly IYouTubeProcessRunner _runner;
    private readonly BufferedPcm _pcm = new();
    private readonly WaveFormat _format = new(48000, 16, 2);
    private long _maxBufferedBytes;
    private FinitePcmSampleProvider? _provider;
    private IYouTubeStreamChain? _chain;
    private Task? _fillTask;
    private double _seekTargetSeconds;
    private int _resyncPending;
    private Action? _onFirstRealAudio;

    // Generation of the active producer chain. Incremented atomically on every seek/dispose so
    // concurrent seekers can never lose an increment (which would let two fill loops write the
    // same buffer).
    private int _generation;
    private int _disposed;

    public YouTubeStreamReader(string pageUrl, YouTubeTrackMeta meta, IYouTubeProcessRunner runner)
    {
        _pageUrl = pageUrl;
        _meta = meta;
        _runner = runner;
        _maxBufferedBytes = (long)(MaxBufferedSeconds * _format.AverageBytesPerSecond);
    }

    /// <summary>Resolved metadata from the -J pass (title/uploader/duration/thumbnail). The
    /// controller copies it onto the playing track so playlists stop showing the bare URL.</summary>
    public YouTubeTrackMeta Meta => _meta;

    /// <summary>Raised on a background thread when the chain ended before the video's real end
    /// (process exited non-zero — throttled, signed-out error, decode failure). The controller
    /// surfaces it as a warning; the buffered tail still plays out and the track advances.</summary>
    public event Action<string>? PrematureEnd;

    /// <summary>Fixed by the ffmpeg arguments — the sequencer resamples/channels as needed.</summary>
    public WaveFormat SourceFormat => _format;

    public ISampleProvider Samples => _provider!;

    /// <summary>Video duration from the -J resolve; zero means live/unknown, which behaves like
    /// radio (no seek, no natural end).</summary>
    public TimeSpan TotalTime => TimeSpan.FromMilliseconds(_meta.DurationMs);

    string ITrackReader.Path => _pageUrl;

    TimeSpan ITrackReader.CurrentTime
    {
        // Real PCM played since the last seek, on top of the seek target. Silence padding during
        // a restart does not advance it — the "reported position = heard position" invariant.
        get => TimeSpan.FromSeconds(_seekTargetSeconds + (_provider?.PlayedSeconds ?? 0));
        set => SeekTo(value);
    }

    /// <summary>Starts the streaming chain and waits until real audio is flowing. A chain that
    /// died instantly (private/removed video, region lock, client rejection) or stays alive
    /// without producing audio surfaces here as an <see cref="AudioOpenException"/> with the
    /// tools' own stderr — never as a silent zero-length track.</summary>
    public void Connect()
    {
        _onFirstRealAudio = () => Volatile.Write(ref _resyncPending, 1);
        _provider = new FinitePcmSampleProvider(_pcm, _format, firstRealAudio: _onFirstRealAudio);
        StartChain(0);
        var prebufferBytes = (long)(PrebufferSeconds * _format.AverageBytesPerSecond);
        _pcm.WaitUntilBufferedBytes(prebufferBytes, TimeSpan.FromSeconds(10));
        if (_pcm.BufferedBytes < (long)(MinOpenSeconds * _format.AverageBytesPerSecond))
        {
            // Covers both flavors of a dead open: instant death (buffered 0, ended) and an alive
            // but hung chain (buffered 0, not ended — would otherwise open into eternal silence).
            var detail = _chain?.StderrTail;
            throw new AudioOpenException(
                CoreMessages.Encode(CoreMessageKey.YouTubeStreamFailed,
                    string.IsNullOrEmpty(detail) ? _pageUrl : detail),
                new InvalidOperationException("the yt-dlp/ffmpeg chain produced no usable audio"));
        }
    }

    private void StartChain(double startSeconds)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var chain = _runner.StartStream(_pageUrl, startSeconds);
        var generation = Volatile.Read(ref _generation);
        // CAS guards against a concurrent seek/dispose: the loser of the slot (or a reader that
        // was disposed while the processes were spawning) kills its own chain — no leaks.
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _chain, chain, null) != null)
        {
            chain.Dispose();
            return;
        }
        _fillTask = Task.Run(() => FillLoop(chain, generation));
    }

    private void FillLoop(IYouTubeStreamChain chain, int generation)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            var stream = chain.PcmStream;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                _pcm.AddBytes(buffer, read, generation); // dropped silently once superseded
                // Backpressure: pace the download when the buffer ceiling is reached — the pipe
                // propagates it upstream to yt-dlp (bounded RAM even mid-pause).
                while (_pcm.BufferedBytes >= _maxBufferedBytes &&
                       generation == Volatile.Read(ref _generation) &&
                       Volatile.Read(ref _disposed) == 0)
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch
        {
            // Broken pipe: a dispose or a seek restart killed the chain. The generation gate
            // decides below whether this loop may still end the track.
        }
        finally
        {
            // Only the current generation may end the track — checked and applied atomically
            // inside the buffer's lock, so a seek's Reset can never slip between check and set.
            _pcm.MarkEnded(generation);
            // A chain that exited non-zero before its audio ended is a mid-stream failure, not a
            // natural end — surface the reason instead of silently skipping to the next track.
            if (generation == Volatile.Read(ref _generation) && !chain.ExitedCleanly())
            {
                PrematureEnd?.Invoke(chain.StderrTail);
            }
        }
    }

    private void SeekTo(TimeSpan target)
    {
        // A live/unknown-length stream has no timeline to seek within — radio semantics.
        if (TotalTime <= TimeSpan.Zero) return;
        if (Volatile.Read(ref _disposed) != 0) return;

        var clampedMs = Math.Clamp(target.TotalMilliseconds, 0, TotalTime.TotalMilliseconds);
        var startSeconds = clampedMs / 1000.0;

        int generation = Interlocked.Increment(ref _generation); // atomic under concurrent seeks
        var oldChain = Interlocked.Exchange(ref _chain, null);
        oldChain?.Dispose(); // kills both processes now; the slow joins run off-thread
        _pcm.Reset(generation); // drops stale bytes AND stale end-of-stream latches atomically
        _seekTargetSeconds = startSeconds;
        _provider?.ResetPosition();
        StartChain(startSeconds);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _generation, Interlocked.Increment(ref _generation));
        var chain = Interlocked.Exchange(ref _chain, null);
        chain?.Dispose(); // kills the processes; the slow joins run off-thread
        _pcm.MarkDead();
        // No join here: Dispose runs on the render thread at track switches. The fill loop exits
        // on its own once the killed pipe breaks (it can no longer touch the buffer — its
        // generation is stale), and the chain's background cleanup reaps the processes.
    }

    bool IResyncRequestSource.TryConsumeResync(out TimeSpan readerPosition)
    {
        if (Volatile.Read(ref _resyncPending) == 0)
        {
            readerPosition = default;
            return false;
        }
        Volatile.Write(ref _resyncPending, 0);
        readerPosition = ((ITrackReader)this).CurrentTime;
        return true;
    }

    /// <summary>
    /// Serves the buffered s16le PCM as floats with YouTube's termination policy: silence on
    /// underrun while the source is alive (a momentary stall must not end the track), end-of-stream
    /// once the source has ended and the buffer drained. Only real PCM advances the position —
    /// silence padding is excluded so the reader's clock never runs ahead of the audio heard — and
    /// the first real audio after a seek raises <paramref name="firstRealAudio"/> so the sequencer
    /// can re-anchor its clock to the reader's.
    /// </summary>
    private sealed class FinitePcmSampleProvider(BufferedPcm pcm, WaveFormat format, Action? firstRealAudio) : ISampleProvider
    {
        private byte[] _scratch = new byte[8192];
        private long _realBytes;
        private int _firstRealAudioSeen;

        public WaveFormat WaveFormat => format;

        public double PlayedSeconds =>
            Interlocked.Read(ref _realBytes) / (double)format.AverageBytesPerSecond;

        public void ResetPosition()
        {
            Interlocked.Exchange(ref _realBytes, 0);
            // Re-arm: the first real audio after THIS seek is what the sequencer re-anchors to.
            Volatile.Write(ref _firstRealAudioSeen, 0);
        }

        public int Read(Span<float> buffer)
        {
            int count = buffer.Length;
            int bytesNeeded = count * 2; // 16-bit mono-equivalent
            if (_scratch.Length < bytesNeeded) _scratch = new byte[bytesNeeded];

            int got = pcm.Read(_scratch, 0, bytesNeeded);
            if (got == 0)
            {
                if (pcm.HasEnded) return 0; // drained + source gone → the track ends
                buffer.Clear();             // alive underrun → silence
                return count;
            }

            int frames = got / 2 / format.Channels;
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < format.Channels; c++)
                {
                    int byteIdx = (f * format.Channels + c) * 2;
                    short sample = (short)(_scratch[byteIdx] | (_scratch[byteIdx + 1] << 8));
                    buffer[f * format.Channels + c] = sample / 32768f;
                }
            }

            Interlocked.Add(ref _realBytes, frames * format.Channels * 2);
            if (Volatile.Read(ref _firstRealAudioSeen) == 0)
            {
                Volatile.Write(ref _firstRealAudioSeen, 1);
                firstRealAudio?.Invoke(); // sequencer re-anchors to the reader's clock here
            }

            int produced = frames * format.Channels;
            if (produced < count)
            {
                if (pcm.HasEnded)
                {
                    // Final partial block at end-of-stream: return the real tail unpadded; the
                    // next read reports end-of-stream.
                    return produced;
                }
                for (int i = produced; i < count; i++) buffer[i] = 0f;
            }
            return count;
        }
    }
}
