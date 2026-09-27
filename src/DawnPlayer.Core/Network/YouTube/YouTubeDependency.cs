namespace DawnPlayer.Core.Network.YouTube;

/// <summary>What the YouTube source needs from the host machine. <see cref="IsUsable"/> gates
/// playback; the JS runtime (Deno) is informational only — recent yt-dlp falls back to
/// non-web clients (visionos was measured working) at the cost of some availability.
/// <see cref="HasOverrides"/> distinguishes "install the tools" guidance from "your configured
/// path is broken" guidance.</summary>
public sealed record YouTubeDependencyStatus(
    bool YtDlpAvailable,
    string YtDlpVersion,
    bool FfmpegAvailable,
    string FfmpegVersion,
    bool JsRuntimeAvailable,
    bool HasOverrides = false)
{
    public bool IsUsable => YtDlpAvailable && FfmpegAvailable;

    public static YouTubeDependencyStatus Unknown { get; } = new(false, "", false, "", false);
}

/// <summary>
/// Detects the external tools the YouTube source needs: PATH-resolved <c>yt-dlp</c> and
/// <c>ffmpeg</c> via a short <c>--version</c> run (missing, timeout and non-zero exit all count
/// as "not installed"), plus a Deno probe for the status display. Results are cached until
/// <see cref="Probe"/> runs again — the settings/section UI re-probes explicitly, and the
/// provider re-probes once on an open attempt so a fresh install is picked up without an app
/// restart. Never throws.
/// </summary>
public static class YouTubeDependency
{
    private static YouTubeDependencyStatus _cached = YouTubeDependencyStatus.Unknown;
    private static int _probing;

    /// <summary>Last probed status. <see cref="YouTubeDependencyStatus.Unknown"/> until the first
    /// explicit probe — probing spawns processes and must never run incidentally.</summary>
    public static YouTubeDependencyStatus GetStatus() => _cached;

    /// <summary>Re-probes all tools. Re-entrant calls (a double-clicked "re-check") coalesce
    /// onto the in-flight probe instead of spawning parallel process triplets.</summary>
    public static YouTubeDependencyStatus Probe()
    {
        if (Interlocked.Exchange(ref _probing, 1) == 1) return _cached;
        try
        {
            return _cached = Probe(YouTubeProcess.Runner);
        }
        finally
        {
            Volatile.Write(ref _probing, 0);
        }
    }

    public static YouTubeDependencyStatus Probe(IYouTubeProcessRunner runner)
    {
        // Probe the CONFIGURED binaries (a user override is an absolute path), not the bare
        // names — otherwise an override that works would still be reported as "not installed"
        // whenever the tool is missing from PATH.
        var ytDlpOk = runner.TryProbe(runner.YtDlpBinary, out var ytDlpVersion);
        var ffmpegOk = runner.TryProbe(runner.FfmpegBinary, out var ffmpegVersion);
        var denoOk = runner.TryProbe("deno", out _);
        _cached = new YouTubeDependencyStatus(
            ytDlpOk, ytDlpVersion, ffmpegOk, ffmpegVersion, denoOk, runner.HasBinaryOverrides);
        return _cached;
    }
}
