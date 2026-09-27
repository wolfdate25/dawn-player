using System.Diagnostics;
using System.Globalization;

namespace DawnPlayer.Core.Network.YouTube;

/// <summary>One running yt-dlp → ffmpeg chain: the PCM stream to read, plus disposal that kills
/// both children — a dropped reader must never leave zombie processes behind.</summary>
public interface IYouTubeStreamChain : IDisposable
{
    /// <summary>ffmpeg's stdout: s16le 48 kHz stereo PCM until end-of-stream.</summary>
    Stream PcmStream { get; }

    /// <summary>Last line(s) the tools wrote to stderr — surfaced in open failures so a
    /// region/age/private rejection is visible instead of a bare "failed".</summary>
    string StderrTail { get; }

    /// <summary>Waits briefly for both processes and reports whether they exited cleanly (both
    /// exit code 0). A non-clean exit at end-of-stream means the stream was truncated mid-way.
    /// Safe to call from a background thread only — it blocks up to ~2 s.</summary>
    bool ExitedCleanly();
}

/// <summary>
/// Process seam for the YouTube reader: the production runner spawns the real tools resolved from
/// PATH, tests substitute canned JSON and synthetic PCM streams (including instant-death and
/// malformed-output modes) without any network or binary. All spawn/run failures surface as
/// <see cref="YouTubeProcessException"/> so the provider can translate them into
/// <c>AudioOpenException</c> with actionable guidance.
/// </summary>
public interface IYouTubeProcessRunner
{
    /// <summary>Runs <c>yt-dlp -J</c> and returns the stdout JSON. Throws on missing binary,
    /// non-zero exit, or timeout; the tools' stderr rides along in the message.</summary>
    string ResolveJson(string pageUrl, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Starts the streaming chain for the page, from <paramref name="startSeconds"/>
    /// onward (0 for a fresh start). Throws on spawn failure.</summary>
    IYouTubeStreamChain StartStream(string pageUrl, double startSeconds);

    /// <summary>Runs <c>&lt;binary&gt; --version</c> for dependency detection. Returns false when
    /// the binary is missing, times out, or exits non-zero.</summary>
    bool TryProbe(string binary, out string version);

    /// <summary>The configured yt-dlp binary — a bare name means PATH default, an absolute path
    /// is a user override. Default implementations keep simple fakes small.</summary>
    string YtDlpBinary => "yt-dlp";

    string FfmpegBinary => "ffmpeg";

    /// <summary>True when at least one binary path is a user override (not the PATH default).</summary>
    bool HasBinaryOverrides => false;
}

public sealed class YouTubeProcessException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>The process runner used by the YouTube components. Test seam: swap before use
/// (the test assembly runs serialized).</summary>
public static class YouTubeProcess
{
    public static IYouTubeProcessRunner Runner { get; set; } = new YouTubeProcessRunner();
}

/// <summary>Production runner: spawns yt-dlp and ffmpeg resolved from PATH (or the configured
/// absolute paths — pip installs expose .cmd shims, which are wrapped through cmd.exe). Empty
/// settings paths coalesce to the bare names (PATH resolution).</summary>
public sealed class YouTubeProcessRunner : IYouTubeProcessRunner
{
    private readonly string _ytDlpBinary;
    private readonly string _ffmpegBinary;

    public YouTubeProcessRunner(string? ytDlpBinary = "yt-dlp", string? ffmpegBinary = "ffmpeg")
    {
        _ytDlpBinary = Coalesce(ytDlpBinary, "yt-dlp");
        _ffmpegBinary = Coalesce(ffmpegBinary, "ffmpeg");
    }

    /// <summary>The configured yt-dlp binary — a bare name means PATH default, an absolute path
    /// is a user override.</summary>
    public string YtDlpBinary => _ytDlpBinary;

    public string FfmpegBinary => _ffmpegBinary;

    public bool HasBinaryOverrides =>
        !string.Equals(_ytDlpBinary, "yt-dlp", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(_ffmpegBinary, "ffmpeg", StringComparison.OrdinalIgnoreCase);

    private static string Coalesce(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured;

    public string ResolveJson(string pageUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = BuildPsi(ResolveBinary(_ytDlpBinary));
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add(pageUrl);

        using var process = Start(psi);
        var stderr = new StderrTail();
        process.ErrorDataReceived += (_, e) => stderr.Append(e.Data);
        process.BeginErrorReadLine();

        string stdout;
        try
        {
            // The token flows into both the read and the timeout race.
            stdout = process.StandardOutput.ReadToEndAsync(cancellationToken)
                .WaitAsync(timeout, cancellationToken).GetAwaiter().GetResult();
        }
        catch (TimeoutException ex)
        {
            KillTree(process);
            throw new YouTubeProcessException("yt-dlp -J timed out", ex);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        // stdout EOF can race the process actually exiting — wait it out so ExitCode is real.
        if (!process.WaitForExit(5000)) KillTree(process);
        // The parameterless overload also flushes the async stderr handlers, so the tail is
        // complete before we compose the failure message.
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var detail = stderr.Snapshot();
            throw new YouTubeProcessException(string.IsNullOrEmpty(detail)
                ? $"yt-dlp exited with {process.ExitCode}"
                : $"yt-dlp exited with {process.ExitCode}: {detail}");
        }

        return stdout;
    }

    public IYouTubeStreamChain StartStream(string pageUrl, double startSeconds)
    {
        var ytDlpPsi = BuildPsi(ResolveBinary(_ytDlpBinary));
        ytDlpPsi.ArgumentList.Add("-f");
        ytDlpPsi.ArgumentList.Add("bestaudio");
        ytDlpPsi.ArgumentList.Add("--no-warnings");
        ytDlpPsi.ArgumentList.Add("-o");
        ytDlpPsi.ArgumentList.Add("-");
        ytDlpPsi.ArgumentList.Add(pageUrl);

        var ffmpegPsi = BuildPsi(ResolveBinary(_ffmpegBinary));
        ffmpegPsi.ArgumentList.Add("-hide_banner");
        ffmpegPsi.ArgumentList.Add("-loglevel");
        ffmpegPsi.ArgumentList.Add("error");
        if (startSeconds > 0.5)
        {
            // Input seeking on a pipe decodes-and-discards; simple and correct, with latency
            // that grows with the distance (documented v1 trade-off).
            ffmpegPsi.ArgumentList.Add("-ss");
            ffmpegPsi.ArgumentList.Add(startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        ffmpegPsi.ArgumentList.Add("-i");
        ffmpegPsi.ArgumentList.Add("pipe:0");
        ffmpegPsi.ArgumentList.Add("-f");
        ffmpegPsi.ArgumentList.Add("s16le");
        ffmpegPsi.ArgumentList.Add("-acodec");
        ffmpegPsi.ArgumentList.Add("pcm_s16le");
        ffmpegPsi.ArgumentList.Add("-ac");
        ffmpegPsi.ArgumentList.Add("2");
        ffmpegPsi.ArgumentList.Add("-ar");
        ffmpegPsi.ArgumentList.Add("48000");
        ffmpegPsi.ArgumentList.Add("pipe:1");

        Process? ytDlp = null;
        Process? ffmpeg = null;
        try
        {
            ytDlp = Start(ytDlpPsi);
            ffmpeg = Start(ffmpegPsi);
        }
        catch (Exception ex)
        {
            KillTree(ytDlp);
            KillTree(ffmpeg);
            try { ytDlp?.Dispose(); } catch { }
            throw new YouTubeProcessException("failed to start the yt-dlp/ffmpeg chain", ex);
        }

        var stderrTail = new StderrTail();
        ytDlp.ErrorDataReceived += (_, e) => stderrTail.Append(e.Data);
        ffmpeg.ErrorDataReceived += (_, e) => stderrTail.Append(e.Data);
        ytDlp.BeginErrorReadLine();
        ffmpeg.BeginErrorReadLine();

        // Pump: yt-dlp stdout → ffmpeg stdin. When yt-dlp finishes, closing ffmpeg's stdin lets
        // it drain and exit; when either side breaks, the pump dies and Dispose cleans up.
        var pump = Task.Run(() =>
        {
            try
            {
                ytDlp.StandardOutput.BaseStream.CopyTo(ffmpeg.StandardInput.BaseStream);
                ffmpeg.StandardInput.BaseStream.Flush();
            }
            catch
            {
                // Broken pipe on kill — the fill loop notices via EOF or exception.
            }
            finally
            {
                try { ffmpeg.StandardInput.BaseStream.Close(); } catch { }
            }
        });

        return new StreamChain(ytDlp, ffmpeg, pump, stderrTail);
    }

    public bool TryProbe(string binary, out string version)
    {
        version = "";
        // yt-dlp speaks "--version"; ffmpeg only knows "-version" (measured: "--version" exits 8
        // with "Unrecognized option"). Try the common flag first, fall back once on an explicit
        // non-zero exit — a hung binary times out and gives up instead of retrying.
        foreach (var flag in new[] { "--version", "-version" })
        {
            try
            {
                var psi = BuildPsi(ResolveBinary(binary));
                psi.ArgumentList.Add(flag);
                using var process = Start(psi);
                process.BeginErrorReadLine();
                if (!process.WaitForExit(5000)) { KillTree(process); return false; }
                // The parameterless overload also flushes the async stderr handlers.
                process.WaitForExit();
                if (process.ExitCode != 0) continue; // wrong flag dialect → try the other one
                version = process.StandardOutput.ReadToEnd().Trim();
                return version.Length > 0;
            }
            catch
            {
                return false; // spawn failure (missing binary) — retrying cannot help
            }
        }
        return false;
    }

    /// <summary>
    /// Turns a bare tool name into an absolute path using PATH + PATHEXT — CreateProcess alone
    /// only finds .exe files, so a pip-style <c>yt-dlp.cmd</c> install would read as "not
    /// installed". Absolute/relative paths with a separator (tests) pass through untouched.
    /// </summary>
    internal static string ResolveBinary(string name)
    {
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return name;

        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (dir.Length == 0) continue;
            foreach (var ext in pathExt)
            {
                var candidate = Path.Combine(dir, name + ext.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }
        // Bare name: CreateProcess still resolves .exe images on its own.
        return name;
    }

    private static ProcessStartInfo BuildPsi(string binary)
    {
        // pip installs expose .cmd shims, which CreateProcess cannot exec directly under
        // UseShellExecute=false — route them through cmd.exe.
        if (binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            binary.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(binary);
            return psi;
        }

        return new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    private static Process Start(ProcessStartInfo psi)
    {
        var process = Process.Start(psi)
            ?? throw new YouTubeProcessException($"could not start '{psi.FileName}'");
        return process;
    }

    internal static void KillTree(Process? process)
    {
        if (process == null) return;
        try { process.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>Keeps the last few stderr lines (bounded) for failure messages.</summary>
    internal sealed class StderrTail
    {
        private readonly object _lock = new();
        private string _tail = "";

        public void Append(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_lock)
            {
                _tail = (_tail.Length == 0 ? line : _tail + " | " + line);
                if (_tail.Length > 400) _tail = _tail[^400..];
            }
        }

        public string Snapshot()
        {
            lock (_lock) return _tail;
        }
    }

    private sealed class StreamChain(Process ytDlp, Process ffmpeg, Task pump, StderrTail stderrTail)
        : IYouTubeStreamChain
    {
        public Stream PcmStream => ffmpeg.StandardOutput.BaseStream;

        public string StderrTail => stderrTail.Snapshot();

        public bool ExitedCleanly()
        {
            if (!ytDlp.WaitForExit(2000)) return false;
            if (!ffmpeg.WaitForExit(2000)) return false;
            try { return ytDlp.ExitCode == 0 && ffmpeg.ExitCode == 0; }
            catch { return false; }
        }

        public void Dispose()
        {
            // Kill immediately — cheap, and breaking the pipes releases every reader (safe to
            // call from the render thread). The slow parts (joining the pump, disposing the
            // Process handles) run off-thread so no caller ever stalls on them.
            KillTree(ytDlp);
            KillTree(ffmpeg);
            _ = Task.Run(() =>
            {
                try { pump.Wait(TimeSpan.FromSeconds(2)); } catch { }
                try { ytDlp.Dispose(); } catch { }
                try { ffmpeg.Dispose(); } catch { }
            });
        }
    }
}
