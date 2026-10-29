using DawnPlayer.Core.Models;
using DawnPlayer.Core.Util;
using NAudio.Wave;

namespace DawnPlayer.Core.Audio;

/// <summary>
/// Plays a finite remote audio file over HTTP by spooling the whole body to a temp file and
/// opening it through the ordinary local reader chain. Chosen over "MF over URL" by the
/// 2026-09-20 spike: MF's network byte stream failed for every container tested
/// (0xC00D0029/0xC00D426A), while the local chain is the player's daily-proven decode path — so
/// remote tracks get the same decoders, lengths and seeking as local ones. The tradeoff is a
/// first-play latency equal to the download time (sub-second on LAN); Range support is unnecessary
/// for a full spool, so servers without Range work as-is.
/// </summary>
public sealed class HttpProgressiveTrackReader : ITrackReader
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(100) };
    private readonly CancellationTokenSource _cancel = new();
    private readonly string _url;
    private readonly string _spoolPath;
    private ITrackReader? _inner;

    public HttpProgressiveTrackReader(string url)
    {
        _url = url;
        var ext = ExtensionFromUrl(url);
        _spoolPath = Path.Combine(SpoolDirectory(), Guid.NewGuid().ToString("N") + ext);
    }

    /// <summary>Downloads the whole body, then opens the spooled file locally. Throws
    /// <see cref="AudioOpenException"/> on any failure and cleans up after itself. Runs on the
    /// thread pool (same contract as <see cref="RadioStreamReader.Connect"/>).</summary>
    public void Connect()
    {
        try
        {
            SpoolAsync().GetAwaiter().GetResult();
            _inner = AudioFileReaderFactory.Open(_spoolPath, TrackSourceKind.File);
        }
        catch (AudioOpenException)
        {
            Cleanup();
            throw;
        }
        catch (Exception ex)
        {
            Cleanup();
            throw new AudioOpenException($"원격 트랙을 내려받지 못했습니다: {_url}", ex);
        }
    }

    private async Task SpoolAsync()
    {
        using var response = await _client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, _cancel.Token);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(_cancel.Token);
        await using var target = new FileStream(_spoolPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        // With ResponseHeadersRead the HttpClient.Timeout covers the HEADERS only — a body that
        // stopped mid-transfer (server hang) blocked the open forever, and nothing disposed the
        // reader until Connect returned, so the open was not even cancellable. Each read gets an
        // idle timeout, and the spool has a hard size cap.
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), _cancel.Token)
                    .AsTask().WaitAsync(ReadIdleTimeout);
            }
            catch (TimeoutException)
            {
                throw new IOException($"download stalled for {ReadIdleTimeout.TotalSeconds:0}s: {_url}");
            }
            if (read <= 0) break;
            total += read;
            if (total > MaxSpoolBytes)
            {
                throw new IOException($"download exceeds the {MaxSpoolBytes >> 20} MiB spool cap: {_url}");
            }
            await target.WriteAsync(buffer.AsMemory(0, read), _cancel.Token);
        }
    }

    /// <summary>Per-read idle timeout: a healthy LAN spool never sees a gap this long.</summary>
    private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Hard cap on the spooled body (1 GiB) — a runaway or bogus Content-Length must not
    /// be able to fill the system disk before anyone notices.</summary>
    public const long MaxSpoolBytes = 1L << 30;

    public WaveFormat SourceFormat => Inner.SourceFormat;
    public ISampleProvider Samples => Inner.Samples;
    public TimeSpan TotalTime => Inner.TotalTime;

    public TimeSpan CurrentTime
    {
        get => Inner.CurrentTime;
        set => Inner.CurrentTime = value;
    }

    string ITrackReader.Path => _url;

    private ITrackReader Inner => _inner ?? throw new InvalidOperationException("reader not connected");

    private void Cleanup()
    {
        try { File.Delete(_spoolPath); } catch { }
        _inner = null;
    }

    public void Dispose()
    {
        // Cancels an in-flight spool (superseded commands dispose the reader before Connect ends),
        // tears down the local reader, and removes the spool file — a crashed process leaves files
        // behind, which the stale sweep on provider construction eventually reclaims.
        _cancel.Cancel();
        _inner?.Dispose();
        _inner = null;
        try { _client.Dispose(); } catch { }
        Cleanup();
    }

    private static string ExtensionFromUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path);
            return AppPaths.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ? ext : "";
        }
        catch
        {
            return "";
        }
    }

    private static string SpoolDirectory()
    {
        var dir = AppPaths.HttpSpoolDir;
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }
}
