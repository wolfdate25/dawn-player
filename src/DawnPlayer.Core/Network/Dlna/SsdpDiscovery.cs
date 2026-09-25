using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DawnPlayer.Core.Network.Dlna;

/// <summary>
/// Client-side SSDP discovery: one multicast M-SEARCH sweep for UPnP media servers. The socket
/// layer is deliberately thin — everything that touches bytes-on-the-wire formats lives in
/// <see cref="SsdpResponseParser"/> so it can be contract-tested without a network.
/// </summary>
public sealed class SsdpDiscovery
{
    public static readonly string[] MediaServerSearchTargets =
    {
        "urn:schemas-upnp-org:device:MediaServer:1",
        "urn:schemas-upnp-org:device:MediaServer:2",
        "urn:schemas-upnp-org:service:ContentDirectory:1",
        "urn:schemas-upnp-org:service:ContentDirectory:2",
    };

    private static readonly IPEndPoint MulticastEndPoint = new(IPAddress.Parse("239.255.255.250"), 1900);

    /// <summary>
    /// Sends M-SEARCH for every target and collects responses until <paramref name="timeout"/>,
    /// deduplicated by USN (LOCATION as a fallback key). Servers that answer both a device and a
    /// service target arrive once. Never throws for network conditions — an empty list means
    /// "nothing found" (or multicast blocked); the caller decides what to show.
    /// </summary>
    public static async Task<IReadOnlyList<SsdpDeviceHit>> SearchAsync(
        TimeSpan timeout,
        IReadOnlyList<string>? searchTargets = null,
        CancellationToken cancellationToken = default)
    {
        var targets = searchTargets as string[] ?? MediaServerSearchTargets;
        var byKey = new Dictionary<string, SsdpDeviceHit>(StringComparer.OrdinalIgnoreCase);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using var udp = new UdpClient();
        try
        {
            udp.Client.ReceiveBufferSize = 64 * 1024;
            foreach (var target in targets)
            {
                var request = Encoding.UTF8.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 3\r\n" +
                    $"ST: {target}\r\n\r\n");
                await udp.SendAsync(request, request.Length, MulticastEndPoint).ConfigureAwait(false);
            }

            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break; // e.g. ICMP port unreachable racing the timeout on some stacks
                }

                var hit = SsdpResponseParser.TryParse(Encoding.UTF8.GetString(received.Buffer));
                if (hit == null) continue;

                // Same device answers several STs with the same USN; devices without a usable USN
                // still exist, so the location is the fallback identity.
                var key = hit.Usn.Length > 0 ? hit.Usn : hit.Location.AbsoluteUri;
                byKey.TryAdd(key, hit);
            }
        }
        catch
        {
            // Discovery is best-effort; never take the UI down with it.
        }

        return byKey.Values.ToList();
    }
}
