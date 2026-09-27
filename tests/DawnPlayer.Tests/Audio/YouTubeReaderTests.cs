using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Network.YouTube;
using NAudio.Wave;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// The YouTube pipe reader's termination and position invariants, exercised through a fake
/// process runner (no binaries, no network):
/// - end-of-stream and mid-stream process death both END the track (never the radio's
///   forever-silence — a finite source must advance),
/// - underrun while the source is alive is silence that does not advance the position,
/// - seek reports the target immediately, restarts the chain, and stale bytes never play,
/// - dispose kills the spawned chain (no zombie processes).
/// Chunk sizes are ≥ the 1.5 s prebuffer so Connect never waits out its timeout.
/// </summary>
public sealed class YouTubeStreamReaderTests
{
    private const int BytesPerSecond = 48_000 * 2 * 2; // s16le 48 kHz stereo
    private const int ChunkFloats = 4800;              // 100 ms of stereo per provider read

    private static YouTubeTrackMeta Meta(double seconds) =>
        new("Title", "Uploader", (long)(seconds * 1000), null);

    private static byte[] Pcm(double seconds) => new byte[(long)(seconds * BytesPerSecond)];

    private sealed class FakeChain : IYouTubeStreamChain
    {
        private readonly Stream _stream;
        private readonly Action? _onDispose;

        public FakeChain(byte[] pcm, Action? onDispose = null, string stderr = "fake stderr")
            : this(new MemoryStream(pcm), onDispose, stderr) { }

        public FakeChain(Stream stream, Action? onDispose = null, string stderr = "fake stderr")
        {
            _stream = stream;
            _onDispose = onDispose;
        }

        public Stream PcmStream => _stream;

        public string StderrTail => "fake stderr";

        public bool ExitedCleanly() => true;

        public void Dispose()
        {
            _stream.Dispose();
            _onDispose?.Invoke();
        }
    }

    /// <summary>A stream whose first Read delivers a chunk and whose subsequent Reads block until
    /// released — models a live-but-stalled pipe with deterministic sequencing.</summary>
    private sealed class HoldStream(byte[] firstChunk) : Stream
    {
        private readonly SemaphoreSlim _release = new(0);

        public void Release() => _release.Release();

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Deliver the chunk progressively — the fill loop reads in 64 KB blocks, so a chunk
            // larger than that must survive across successive calls.
            if (_sent < firstChunk.Length)
            {
                int take = Math.Min(count, firstChunk.Length - _sent);
                Array.Copy(firstChunk, _sent, buffer, offset, take);
                _sent += take;
                return take;
            }
            _release.Wait();
            return 0;
        }

        private int _sent;

        private bool _holdDisposed;

        protected override void Dispose(bool disposing)
        {
            if (_holdDisposed) return;
            _holdDisposed = true;
            if (disposing) _release.Release();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeRunner : IYouTubeProcessRunner
    {
        public Func<double, IYouTubeStreamChain> ChainFactory { get; set; } =
            _ => new FakeChain([]);

        public List<IYouTubeStreamChain> Chains { get; } = [];

        public string ResolveJson(string pageUrl, TimeSpan timeout, CancellationToken cancellationToken) => "{}";

        public IYouTubeStreamChain StartStream(string pageUrl, double startSeconds)
        {
            var chain = ChainFactory(startSeconds);
            Chains.Add(chain);
            return chain;
        }

        public bool TryProbe(string binary, out string version)
        {
            version = "1.0";
            return true;
        }
    }

    private static (YouTubeStreamReader Reader, FakeRunner Runner) OpenReader(
        Func<double, IYouTubeStreamChain> chainFactory, double durationSeconds = 300)
    {
        var runner = new FakeRunner { ChainFactory = chainFactory };
        var reader = new YouTubeStreamReader("https://www.youtube.com/watch?v=abc", Meta(durationSeconds), runner);
        reader.Connect();
        return (reader, runner);
    }

    /// <summary>Drains the provider until end-of-stream. Bounded by wall time, not frame count:
    /// under a saturated thread pool the chain's fill task can start late, and the reader answers
    /// that window with silence — a frame cap would burn through it before any real audio lands.</summary>
    private static int ReadAll(ISampleProvider provider, List<float> into)
    {
        var scratch = new float[ChunkFloats];
        int total = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        int read;
        while ((read = provider.Read(scratch)) > 0)
        {
            into.AddRange(scratch[..read]);
            total += read;
            if (DateTime.UtcNow > deadline) break; // runaway guard — the asserts below still fail
        }
        return total;
    }

    [Fact]
    public void EndOfStream_EndsTheTrack_AndPositionMatchesPlayedPcm()
    {
        var (reader, _) = OpenReader(_ => new FakeChain(Pcm(2.0)), durationSeconds: 10);

        var frames = ReadAll(reader.Samples, []);

        // Exactly 2 seconds of stereo played, then a clean end (no infinite silence — I1).
        Assert.Equal(2.0 * 48_000 * 2, frames);
        Assert.Equal(2.0, ((ITrackReader)reader).CurrentTime.TotalSeconds, precision: 2);
        reader.Dispose();
    }

    [Fact]
    public void ChainThatProducesNothing_ConnectFails_WithGuidanceMessage()
    {
        var reader = new YouTubeStreamReader(
            "https://www.youtube.com/watch?v=abc", Meta(10),
            new FakeRunner { ChainFactory = _ => new FakeChain([]) });

        // A dead chain surfaces at open (I2) instead of a silent zero-length track.
        var ex = Assert.Throws<AudioOpenException>(reader.Connect);
        Assert.Contains("coremsg:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("YouTubeStreamFailed", ex.Message, StringComparison.Ordinal);
        reader.Dispose();
    }

    [Fact]
    public void Dispose_KillsTheChain_SoNoProcessSurvives()
    {
        int killed = 0;
        var (reader, runner) = OpenReader(_ => new FakeChain(Pcm(2.0), onDispose: () => killed++));

        reader.Dispose();

        Assert.Equal(1, killed);
        Assert.Single(runner.Chains);
    }

    [Fact]
    public void Seek_ReportsTargetImmediately_RestartsChain_AndStaleBytesNeverPlay()
    {
        var (reader, runner) = OpenReader(
            seconds => seconds > 0
                ? new FakeChain(Pcm(1.0))
                : new FakeChain(Pcm(2.0)),
            durationSeconds: 300);

        // Consume a slice of the first chain, then seek far past it.
        var scratch = new float[ChunkFloats];
        reader.Samples.Read(scratch);

        ((ITrackReader)reader).CurrentTime = TimeSpan.FromSeconds(90);

        Assert.Equal(2, runner.Chains.Count);            // the chain restarted
        Assert.Equal(90.0, ((ITrackReader)reader).CurrentTime.TotalSeconds, precision: 2);

        // The restarted chain delivers fresh audio from the target onward — the old chain's tail
        // is neither played nor able to end the new track. Silence padding between the seek and
        // the first data may inflate the frame count, so the lower bound plus the exact position
        // below carry the invariant (position counts real PCM only).
        var frames = ReadAll(reader.Samples, []);
        Assert.True(frames >= 1.0 * 48_000 * 2, $"expected at least 1 s of audio, got {frames} frames");
        Assert.Equal(91.0, ((ITrackReader)reader).CurrentTime.TotalSeconds, precision: 2);
        reader.Dispose();
    }

    [Fact]
    public void UnderrunWhileAlive_EmitsSilence_WithoutEndingOrAdvancingPosition()
    {
        var hold = new HoldStream(Pcm(2.0));
        var (reader, _) = OpenReader(_ => new FakeChain(hold), durationSeconds: 300);

        // Drain all the real PCM the chain delivers (the fill loop adds it in 64 KB blocks).
        var scratch = new float[ChunkFloats];
        for (int i = 0; i < 200; i++)
        {
            reader.Samples.Read(scratch);
            if (((ITrackReader)reader).CurrentTime.TotalSeconds >= 2.0) break;
        }
        Assert.Equal(2.0, ((ITrackReader)reader).CurrentTime.TotalSeconds, precision: 2);

        // Source alive but empty → silence, full count, never end-of-stream.
        Assert.Equal(ChunkFloats, reader.Samples.Read(scratch));

        // Silence does not advance the reported position (only real PCM does — I3).
        Assert.Equal(2.0, ((ITrackReader)reader).CurrentTime.TotalSeconds, precision: 2);

        // Release → the stream EOFs → the buffer drains → the track ends.
        hold.Release();
        var ended = false;
        for (int i = 0; i < 200 && !ended; i++)
        {
            ended = reader.Samples.Read(scratch) == 0;
            if (!ended) Thread.Sleep(20);
        }
        Assert.True(ended, "the track did not end after the source was released");
        reader.Dispose();
    }

    [Fact]
    public void LiveStream_SeekIsNoOp()
    {
        var (reader, runner) = OpenReader(_ => new FakeChain(Pcm(2.0)), durationSeconds: 0);

        ((ITrackReader)reader).CurrentTime = TimeSpan.FromSeconds(30);

        Assert.Single(runner.Chains); // no restart — a live stream has no timeline
        reader.Dispose();
    }

    [Fact]
    public void FirstRealAudioAfterSeek_RaisesOneResyncRequest_AtTheHeardPosition()
    {
        var (reader, _) = OpenReader(
            seconds => seconds > 0
                ? new FakeChain(Pcm(1.0))
                : new FakeChain(Pcm(2.0)),
            durationSeconds: 300);

        // Consume the initial chain's real audio; the connect-time resync (anchor ≈ 0) may or may
        // not still be pending — consume whatever is there so only the seek's request remains.
        var scratch = new float[ChunkFloats];
        reader.Samples.Read(scratch);
        var resync = (IResyncRequestSource)reader;
        while (resync.TryConsumeResync(out _)) { }

        ((ITrackReader)reader).CurrentTime = TimeSpan.FromSeconds(90);
        Assert.False(resync.TryConsumeResync(out _)); // nothing real played since the seek yet

        // The restarted chain fills on a background task — read until its first real audio lands
        // (bounded), which must raise exactly one resync at target + actually-heard PCM.
        var position = TimeSpan.Zero;
        var got = false;
        for (int i = 0; i < 200 && !got; i++)
        {
            reader.Samples.Read(scratch);
            got = resync.TryConsumeResync(out position);
            if (!got) Thread.Sleep(10);
        }
        Assert.True(got);
        Assert.InRange(position.TotalSeconds, 90.0, 90.5);
        Assert.False(resync.TryConsumeResync(out _)); // one-shot
        reader.Dispose();
    }
}

/// <summary>
/// Provider gates: missing dependencies and failed resolves surface as AudioOpenException with
/// the keyed guidance sentences (never silent), and the gate fires before any yt-dlp work. The
/// process runner is swapped through the YouTubeProcess seam; the test assembly runs serialized,
/// so the swap is safe.
/// </summary>
public sealed class YouTubeTrackReaderProviderTests
{
    private sealed class StubRunner : IYouTubeProcessRunner
    {
        public bool YtDlp = true;
        public bool Ffmpeg = true;
        public bool HasOverrides;
        public Func<string, string>? OnResolve;

        public bool HasBinaryOverrides => HasOverrides;

        string IYouTubeProcessRunner.ResolveJson(string pageUrl, TimeSpan timeout, CancellationToken cancellationToken)
            => OnResolve?.Invoke(pageUrl) ?? throw new YouTubeProcessException("resolve blew up");

        public IYouTubeStreamChain StartStream(string pageUrl, double startSeconds)
            => throw new InvalidOperationException("not expected in provider gate tests");

        public bool TryProbe(string binary, out string version)
        {
            var ok = binary switch
            {
                "yt-dlp" => YtDlp,
                "ffmpeg" => Ffmpeg,
                _ => false,
            };
            version = ok ? "stub-1.0" : "";
            return ok;
        }
    }

    private static StubRunner Swap(StubRunner runner)
    {
        YouTubeProcess.Runner = runner;
        return runner;
    }

    private static void Restore()
    {
        // No re-probe here: probing spawns real processes; the tests that need a specific cached
        // status probe their own stub explicitly.
        YouTubeProcess.Runner = new YouTubeProcessRunner();
    }

    [Fact]
    public void MissingDependency_ThrowsKeyedGuidance_AndDoesNotResolve()
    {
        var runner = Swap(new StubRunner { Ffmpeg = false });
        YouTubeDependency.Probe(runner); // the cache may hold a usable status from another test
        var resolved = false;
        runner.OnResolve = _ => { resolved = true; return "{}"; };

        try
        {
            var provider = new YouTubeTrackReaderProvider();
            var ex = Assert.Throws<AudioOpenException>(() => provider.Open("https://www.youtube.com/watch?v=abc"));
            Assert.Contains("YouTubeDependencyMissing", ex.Message, StringComparison.Ordinal);
            Assert.False(resolved); // the gate fires before any yt-dlp work
        }
        finally
        {
            Restore();
        }
    }

    [Fact]
    public void FailedResolve_ThrowsKeyedGuidance()
    {
        var runner = Swap(new StubRunner { OnResolve = _ => throw new YouTubeProcessException("video unavailable") });
        YouTubeDependency.Probe(runner); // hermetic: usable cache, so the gate passes

        try
        {
            var provider = new YouTubeTrackReaderProvider();
            var ex = Assert.Throws<AudioOpenException>(() => provider.Open("https://www.youtube.com/watch?v=abc"));
            Assert.Contains("YouTubeResolveFailed", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Restore();
        }
    }

    [Fact]
    public void UnparsableJson_ThrowsKeyedGuidance()
    {
        var runner = Swap(new StubRunner { OnResolve = _ => "not json" });
        YouTubeDependency.Probe(runner); // hermetic: usable cache

        try
        {
            var provider = new YouTubeTrackReaderProvider();
            var ex = Assert.Throws<AudioOpenException>(() => provider.Open("https://www.youtube.com/watch?v=abc"));
            Assert.Contains("YouTubeResolveFailed", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Restore();
        }
    }

    [Fact]
    public void NonVideoUrl_IsRejectedBeforeAnyProcessWork()
    {
        // A restored M3U8 reaches Open raw; nothing that is not a canonical video page may reach
        // the process arguments (defense for the cmd.exe wrapping of pip-style .cmd shims).
        var runner = Swap(new StubRunner());
        YouTubeDependency.Probe(runner); // hermetic: usable cache, so the URL gate is the only tripwire
        var resolved = false;
        runner.OnResolve = _ => { resolved = true; return "{}"; };

        try
        {
            var provider = new YouTubeTrackReaderProvider();
            var ex = Assert.Throws<AudioOpenException>(
                () => provider.Open("https://evil.example/not-a-video&format_cmd_break"));
            Assert.Contains("YouTubeResolveFailed", ex.Message, StringComparison.Ordinal);
            Assert.False(resolved); // rejected at the URL gate, no processes touched
        }
        finally
        {
            Restore();
        }
    }

    [Fact]
    public void Open_WithAFreshCachedResolve_DoesNotSpawnASecondResolve()
    {
        // The section resolves -J up front to fill the recent grid; the open that immediately
        // follows must reuse that result — a second yt-dlp spawn would double the open latency.
        var runner = Swap(new StubRunner());
        var resolveCount = 0;
        runner.OnResolve = _ => { resolveCount++; return "{\"title\":\"Cached\",\"duration\":19}"; };

        try
        {
            YouTubeResolveCache.Put("https://www.youtube.com/watch?v=abc",
                new YouTubeTrackMeta("Cached", "Uploader", 19_000, null));

            var provider = new YouTubeTrackReaderProvider();
            Assert.Throws<InvalidOperationException>(
                () => provider.Open("https://www.youtube.com/watch?v=abc")); // StartStream is not expected on the stub

            Assert.Equal(0, resolveCount); // the cached metadata satisfied the resolve step
        }
        finally
        {
            YouTubeResolveCache.Clear();
            Restore();
        }
    }

    [Fact]
    public void InvalidUserPath_ThrowsTheOverrideSpecificGuidance()
    {
        // "Install the tools" is wrong advice when the user's own configured path is broken —
        // the override gate must say so instead.
        var runner = Swap(new StubRunner { YtDlp = false, HasOverrides = true });
        YouTubeDependency.Probe(runner); // hermetic: unusable cache WITH overrides

        try
        {
            var provider = new YouTubeTrackReaderProvider();
            var ex = Assert.Throws<AudioOpenException>(() => provider.Open("https://www.youtube.com/watch?v=abc"));
            Assert.Contains("YouTubeOverrideInvalid", ex.Message, StringComparison.Ordinal);
            Assert.Contains("yt-dlp='", ex.Message, StringComparison.Ordinal); // the offending paths ride along
        }
        finally
        {
            Restore();
        }
    }
}

/// <summary>Dependency detection: both tools present → usable (with the runtime flag), any
/// missing → not usable, and the cached status reflects the latest probe.</summary>
public sealed class YouTubeDependencyTests
{
    private sealed class ProbeStub(bool ytDlp, bool ffmpeg, bool deno) : IYouTubeProcessRunner
    {
        public string ResolveJson(string pageUrl, TimeSpan timeout, CancellationToken cancellationToken)
            => throw new InvalidOperationException("not expected");

        public IYouTubeStreamChain StartStream(string pageUrl, double startSeconds)
            => throw new InvalidOperationException("not expected");

        public bool TryProbe(string binary, out string version)
        {
            var ok = binary switch
            {
                "yt-dlp" => ytDlp,
                "ffmpeg" => ffmpeg,
                "deno" => deno,
                _ => false,
            };
            version = ok ? "stub-1.0" : "";
            return ok;
        }
    }

    [Fact]
    public void BothToolsPresent_IsUsable_WithRuntimeFlag()
    {
        var status = YouTubeDependency.Probe(new ProbeStub(ytDlp: true, ffmpeg: true, deno: true));

        Assert.True(status.IsUsable);
        Assert.True(status.JsRuntimeAvailable);
        Assert.Equal("stub-1.0", status.YtDlpVersion);
        Assert.Equal("stub-1.0", YouTubeDependency.GetStatus().FfmpegVersion); // cached
    }

    [Fact]
    public void MissingFfmpeg_IsNotUsable()
    {
        var status = YouTubeDependency.Probe(new ProbeStub(ytDlp: true, ffmpeg: false, deno: false));

        Assert.False(status.IsUsable);
        Assert.False(YouTubeDependency.GetStatus().IsUsable);
    }

    /// <summary>The user's exact scenario: the tools are NOT on PATH, but configured absolute
    /// paths work. The probe must test the configured binaries — probing the bare names would
    /// report "not installed" for a working setup.</summary>
    private sealed class OverridePathRunner : IYouTubeProcessRunner
    {
        public List<string> Probed { get; } = [];

        public string ResolveJson(string pageUrl, TimeSpan timeout, CancellationToken cancellationToken)
            => throw new InvalidOperationException("not expected");

        public IYouTubeStreamChain StartStream(string pageUrl, double startSeconds)
            => throw new InvalidOperationException("not expected");

        string IYouTubeProcessRunner.YtDlpBinary => @"H:\음악\tools\yt-dlp\yt-dlp.exe";
        string IYouTubeProcessRunner.FfmpegBinary => @"H:\음악\tools\ffmpeg\bin\ffmpeg.exe";
        bool IYouTubeProcessRunner.HasBinaryOverrides => true;

        public bool TryProbe(string binary, out string version)
        {
            Probed.Add(binary);
            // Bare names (PATH lookup) fail; absolute configured paths succeed.
            var ok = binary.Contains(Path.DirectorySeparatorChar);
            version = ok ? "2026.08.19" : "";
            return ok;
        }
    }

    [Fact]
    public void Probe_TestsTheConfiguredOverridePaths_NotTheBareNames()
    {
        var runner = new OverridePathRunner();

        var status = YouTubeDependency.Probe(runner);

        Assert.True(status.IsUsable); // the bug: this was false when the tools were off PATH
        Assert.True(status.HasOverrides);
        Assert.Contains(@"H:\음악\tools\yt-dlp\yt-dlp.exe", runner.Probed);
        Assert.Contains(@"H:\음악\tools\ffmpeg\bin\ffmpeg.exe", runner.Probed);
        Assert.DoesNotContain("yt-dlp", runner.Probed); // bare-name PATH lookup must not run
    }

    [Fact]
    public void RunnerCoalescesEmptyPaths_ToPathDefaults_AndFlagsOverrides()
    {
        var defaults = (IYouTubeProcessRunner)new YouTubeProcessRunner("", "");
        Assert.Equal("yt-dlp", defaults.YtDlpBinary);
        Assert.Equal("ffmpeg", defaults.FfmpegBinary);
        Assert.False(defaults.HasBinaryOverrides);

        var overridden = (IYouTubeProcessRunner)new YouTubeProcessRunner(@"C:\tools\yt-dlp.cmd", "");
        Assert.Equal(@"C:\tools\yt-dlp.cmd", overridden.YtDlpBinary);
        Assert.Equal("ffmpeg", overridden.FfmpegBinary);
        Assert.True(overridden.HasBinaryOverrides);

        // The probe stamps the override flag onto the status so UI and provider can tell
        // "install the tools" apart from "your configured path is broken".
        var status = YouTubeDependency.Probe(new ProbeStub(ytDlp: false, ffmpeg: false, deno: false));
        Assert.False(status.HasOverrides);
    }
}
