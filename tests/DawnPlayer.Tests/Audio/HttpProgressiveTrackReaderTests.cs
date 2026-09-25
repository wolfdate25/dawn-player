using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Util;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// End-to-end spool-reader behavior against a real local HTTP server (TcpListener-based raw
/// responses — HttpListener needs URL ACL rights). These exercise the N1 failure scenarios:
/// successful finite download + local decode + seek, server errors, and dispose racing a download.
/// </summary>
public sealed class HttpProgressiveTrackReaderTests : IDisposable
{
    private readonly RawHttpServer _server = new();

    public void Dispose() => _server.Dispose();

    /// <summary>A hand-rolled 44.1k/16-bit/stereo WAV so the test owes nothing to an encoder.</summary>
    private static byte[] MakeWav(double seconds)
    {
        int frames = (int)(seconds * 44100);
        int dataLen = frames * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8); bw.Write(36 + dataLen); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)2);
        bw.Write(44100); bw.Write(44100 * 4); bw.Write((short)4); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(dataLen);
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(Math.Sin(i * 0.05) * 12000);
            bw.Write(v);
            bw.Write(v);
        }
        bw.Flush();
        return ms.ToArray();
    }

    [Fact]
    public async Task FiniteFile_DownloadsDecodesAndSeeksLikeALocalTrack()
    {
        var url = _server.Serve("song.wav", MakeWav(1.0));
        using var reader = new HttpProgressiveTrackReader(url);

        // Same contract as the controller: Connect (and the Media Foundation COM init it ends up
        // doing) runs on the thread pool, not the test thread.
        await Task.Run(reader.Connect);

        Assert.Equal(44100, reader.SourceFormat.SampleRate);
        Assert.Equal(2, reader.SourceFormat.Channels);
        Assert.True(Math.Abs(reader.TotalTime.TotalSeconds - 1.0) < 0.1, $"total={reader.TotalTime}");

        reader.CurrentTime = TimeSpan.FromSeconds(0.5);
        Assert.True(Math.Abs(reader.CurrentTime.TotalSeconds - 0.5) < 0.1, $"pos={reader.CurrentTime}");
    }

    [Fact]
    public void ServerError_SurfacesAsAudioOpenException()
    {
        var url = _server.Serve404("gone.flac");
        using var reader = new HttpProgressiveTrackReader(url);

        Assert.Throws<AudioOpenException>(reader.Connect);
    }

    [Fact]
    public async Task DisposeDuringSlowDownload_CancelsAndCleansUp()
    {
        var url = _server.ServeDripping("slow.wav", MakeWav(1.0));
        var reader = new HttpProgressiveTrackReader(url);

        var connect = Task.Run(() =>
        {
            try { reader.Connect(); return "opened"; }
            catch (AudioOpenException) { return "failed"; }
        });

        await _server.FirstRequestReachedAsync(TimeSpan.FromSeconds(5));
        reader.Dispose();

        var completed = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(connect, completed);
        Assert.Equal("failed", await connect);

        // The spool file must not linger once the reader is disposed.
        await Task.Delay(200);
        if (Directory.Exists(AppPaths.HttpSpoolDir))
            Assert.Empty(Directory.GetFiles(AppPaths.HttpSpoolDir));
    }

    /// <summary>Minimal raw-HTTP file server: no HttpListener ACL requirements, configurable
    /// failure modes. One client request per test; keep-alive is not implemented.</summary>
    internal sealed class RawHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<TcpClient> _clients = [];
        private volatile TaskCompletionSource _firstRequest = NewTcs();
        private TimeSpan _dripDelay;

        public RawHttpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        private static TaskCompletionSource NewTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Port => ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);

        public string Serve(string name, byte[] body)
        {
            _files[name] = body;
            return Url(name);
        }

        public string Serve404(string name) => Url(name);

        /// <summary>Serves the body one small chunk at a time so a download can be disposed mid-flight.</summary>
        public string ServeDripping(string name, byte[] body)
        {
            _dripDelay = TimeSpan.FromMilliseconds(30);
            return Serve(name, body);
        }

        public Task FirstRequestReachedAsync(TimeSpan timeout) => _firstRequest.Task.WaitAsync(timeout);

        private string Url(string name) => $"http://127.0.0.1:{Port}/{name}";

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }
                lock (_clients) _clients.Add(client);
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using var stream = client.GetStream();
                var header = new StringBuilder();
                var buf = new byte[4096];
                while (!header.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int n = await stream.ReadAsync(buf);
                    if (n == 0) return;
                    header.Append(Encoding.ASCII.GetString(buf, 0, n));
                }

                _firstRequest.TrySetResult();

                var requestLine = header.ToString().Split("\r\n")[0];
                var path = requestLine.Split(' ')[1].TrimStart('/');
                if (!_files.TryGetValue(path, out var body))
                {
                    var notFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
                    await stream.WriteAsync(notFound);
                    await DrainUntilClientClosesAsync(stream);
                    return;
                }

                // Keep-alive semantics with the whole response in ONE write. Closing our side
                // right after the body (Connection: close + immediate shutdown) makes the FIN
                // overtake the tail bytes on loopback, and HttpClient's body read then never
                // completes — a real DLNA server keeps the connection open past the response,
                // which is the behavior mirrored here.
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\n\r\n");
                var all = new byte[head.Length + body.Length];
                head.CopyTo(all, 0);
                body.CopyTo(all, head.Length);
                await stream.WriteAsync(all);

                if (_dripDelay == TimeSpan.Zero)
                {
                    await DrainUntilClientClosesAsync(stream);
                    return;
                }

                // Drip mode: tiny chunks with pauses; writing throws once the client is disposed.
                for (int offset = 0; offset < body.Length; offset += 512)
                {
                    await Task.Delay(_dripDelay);
                    await stream.WriteAsync(body.AsMemory(offset, Math.Min(512, body.Length - offset)));
                }
            }
            catch
            {
                // Client vanished mid-download — exactly what the dispose test wants.
            }
        }

        private static async Task DrainUntilClientClosesAsync(NetworkStream stream)
        {
            var drain = new byte[1024];
            try { while (await stream.ReadAsync(drain) > 0) { } }
            catch { /* reset when the client goes away */ }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            lock (_clients)
            {
                foreach (var client in _clients)
                {
                    try { client.Dispose(); } catch { }
                }
            }
        }
    }
}
