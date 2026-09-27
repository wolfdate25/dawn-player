using DawnPlayer.Core.Network.YouTube;
using DawnPlayer.Core.Util;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// YouTube source: resolves the page with yt-dlp (-J), then streams bestaudio through an ffmpeg
/// s16le pipe. SelectProvider pins SourceKind=YouTube here — this provider claims http(s) URLs
/// and nothing else, and only ever sees YouTube-kind paths. Dependency problems and resolve
/// failures surface as <c>AudioOpenException</c> with actionable, localized guidance (never a
/// silent failure), and the reader is returned only after real audio is flowing.
/// </summary>
public sealed class YouTubeTrackReaderProvider : ITrackReaderProvider
{
    public int Order => 350;

    public bool CanOpen(string path, string extension) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public ITrackReader Open(string path)
    {
        var runner = YouTubeProcess.Runner;

        // Re-validate the URL at the gate: UI input is normalized upstream, but a restored M3U8
        // reaches this point raw, and the resolve/spawn chain must never see anything that is
        // not a canonical video page (defense in depth for the process arguments).
        var canonical = YouTubePageUrl.TryNormalize(path)
            ?? throw new AudioOpenException(
                CoreMessages.Encode(CoreMessageKey.YouTubeResolveFailed, path),
                new FormatException("not a YouTube video page URL"));
        path = canonical;

        // Probe once per open attempt when unusable, so a freshly installed toolset is picked up
        // without an app restart. The probe runs on the (thread-pool) open path, not the UI.
        var status = YouTubeDependency.GetStatus();
        if (!status.IsUsable) status = YouTubeDependency.Probe(runner);
        if (!status.IsUsable)
        {
            // User-configured paths get their own message — "install the tools" is wrong advice
            // when a picked path went missing or never worked.
            if (status.HasOverrides)
            {
                var detail = $"yt-dlp='{runner.YtDlpBinary}' ffmpeg='{runner.FfmpegBinary}'";
                throw new AudioOpenException(
                    CoreMessages.Encode(CoreMessageKey.YouTubeOverrideInvalid, detail),
                    new InvalidOperationException($"override paths unusable: {detail}"));
            }
            throw new AudioOpenException(
                CoreMessages.Encode(CoreMessageKey.YouTubeDependencyMissing),
                new InvalidOperationException($"yt-dlp={status.YtDlpAvailable} ffmpeg={status.FfmpegAvailable}"));
        }

        YouTubeTrackMeta meta;
        try
        {
            // Cache-aware: a section-initiated resolve seconds ago is reused verbatim, so the
            // common "pick from the recent grid / paste and play" path spawns yt-dlp once.
            meta = YouTubeResolve.ResolveMeta(runner, path);
        }
        catch (Exception ex)
        {
            throw new AudioOpenException(
                CoreMessages.Encode(CoreMessageKey.YouTubeResolveFailed, path), ex);
        }

        var reader = new YouTubeStreamReader(path, meta, runner);
        try
        {
            reader.Connect();
            return reader;
        }
        catch
        {
            reader.Dispose(); // never leak a half-open process chain
            throw;
        }
    }
}
