using DawnPlayer.Core.Network.YouTube;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// The one real-process integration gate: the production YouTubeProcessRunner spawns actual
/// child processes and wires yt-dlp stdout → ffmpeg stdin → PCM stdout. Real YouTube/ffmpeg
/// binaries are out of scope (they are the manual matrix), so command shims stand in for both
/// tools and the full spawn → pump → drain → dispose chain is exercised against genuine OS
/// processes — including the .cmd shim wrapping that pip-installed yt-dlp needs.
/// </summary>
public sealed class YouTubeProcessShimTests : IDisposable
{
    private readonly string _dir;

    public YouTubeProcessShimTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DawnPlayerYtShimTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteShim(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void Probe_RunsVersionThroughCmdShim()
    {
        var shim = WriteShim("ytdlp.cmd", "@echo 2026.08.19");
        var runner = new YouTubeProcessRunner(shim, shim);

        var ok = runner.TryProbe(shim, out var version);

        Assert.True(ok);
        Assert.Contains("2026.08.19", version, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_FallsBackToSingleDash_WhenDoubleDashIsRejected()
    {
        // ffmpeg dialect: "--version" is an unrecognized option (exit 8), "-version" works. The
        // probe must fall back instead of declaring a working binary "not executable".
        var shim = WriteShim("ffmpeg-dialect.cmd", "@echo off\r\nif \"%~1\"==\"--version\" exit /b 8\r\necho 2026-09-24-git");
        var runner = new YouTubeProcessRunner(shim, shim);

        var ok = runner.TryProbe(shim, out var version);

        Assert.True(ok);
        Assert.Contains("2026-09-24-git", version, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_BothFlagsRejected_ReturnsFalse()
    {
        var shim = WriteShim("broken.cmd", "@echo off\r\nif \"%~1\"==\"--version\" exit /b 8\r\nif \"%~1\"==\"-version\" exit /b 9\r\nexit /b 7");
        var runner = new YouTubeProcessRunner(shim, shim);

        Assert.False(runner.TryProbe(shim, out _));
    }

    [Fact]
    public void Probe_MissingBinary_ReturnsFalse()
    {
        var runner = new YouTubeProcessRunner();

        Assert.False(runner.TryProbe("definitely-not-installed-ytdlp", out var version));
        Assert.Equal("", version);
    }

    [Fact]
    public void ResolveJson_ReturnsStdout_AndRejectsNonZeroExit()
    {
        var okShim = WriteShim("resolve-ok.cmd", "@echo {\"title\": \"Shim\"}");
        var failShim = WriteShim("resolve-fail.cmd", "@exit 2");
        var runner = new YouTubeProcessRunner(okShim, okShim);

        var json = runner.ResolveJson("https://www.youtube.com/watch?v=abc", TimeSpan.FromSeconds(15), CancellationToken.None);
        Assert.Contains("\"title\"", json, StringComparison.Ordinal);

        var failing = new YouTubeProcessRunner(failShim, failShim);
        Assert.Throws<YouTubeProcessException>(
            () => failing.ResolveJson("https://www.youtube.com/watch?v=abc", TimeSpan.FromSeconds(15), CancellationToken.None));
    }

    [Fact]
    public void ResolveJson_NonZeroExit_CarriesTheToolsStderr()
    {
        // The reason a resolve failed (private/removed/region-locked) lives on stderr — it must
        // reach the user's warning, not vanish into a drained pipe.
        var shim = WriteShim("resolve-stderr.cmd", "@echo VIDEO-UNAVAILABLE-DETAIL 1>&2\r\n@exit 2");
        var runner = new YouTubeProcessRunner(shim, shim);

        var ex = Assert.Throws<YouTubeProcessException>(
            () => runner.ResolveJson("https://www.youtube.com/watch?v=abc", TimeSpan.FromSeconds(15), CancellationToken.None));

        Assert.Contains("VIDEO-UNAVAILABLE-DETAIL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamChain_PumpsYtDlpIntoFfmpeg_AndEndsAtEof()
    {
        // "yt-dlp" emits canned payload bytes; "ffmpeg" (findstr) echoes its stdin to stdout, so
        // whatever survives the pump IS the payload — proving the three-process wiring.
        var ytDlp = WriteShim("stream-ytdlp.cmd", "@echo PAYLOAD-PCM-123");
        var ffmpeg = WriteShim("stream-ffmpeg.cmd", "@findstr \"^\"");
        var runner = new YouTubeProcessRunner(ytDlp, ffmpeg);

        using var chain = runner.StartStream("https://www.youtube.com/watch?v=abc", 0);
        using var pcm = new MemoryStream();
        chain.PcmStream.CopyTo(pcm); // ends when the pump closes ffmpeg's stdin

        Assert.True(pcm.Length > 0, "the chain produced no bytes");
        var text = System.Text.Encoding.ASCII.GetString(pcm.ToArray());
        Assert.Contains("PAYLOAD-PCM-123", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamChain_Dispose_KillsProcesses_Promptly()
    {
        var ytDlp = WriteShim("hang-ytdlp.cmd", "@ping -n 30 127.0.0.1 > nul");
        var ffmpeg = WriteShim("copy-ffmpeg.cmd", "@findstr \"^\"");
        var runner = new YouTubeProcessRunner(ytDlp, ffmpeg);

        var chain = runner.StartStream("https://www.youtube.com/watch?v=abc", 0);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        chain.Dispose(); // must not wait out the 30-second ping
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"dispose took {watch.Elapsed.TotalMilliseconds:0} ms — the chain did not die promptly");
        await Task.CompletedTask;
    }
}
