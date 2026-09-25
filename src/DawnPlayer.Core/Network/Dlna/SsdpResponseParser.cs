namespace DawnPlayer.Core.Network.Dlna;

/// <summary>One SSDP M-SEARCH response worth following up: where the device publishes its
/// description document (<see cref="Location"/>) and who it claims to be.</summary>
public sealed record SsdpDeviceHit(Uri Location, string Usn, string St, string ServerHeader);

/// <summary>
/// Parses one SSDP (unicast M-SEARCH response) datagram. Pure, so the wire-format contract —
/// case-insensitive headers, LF tolerance, mandatory LOCATION/USN — is testable without sockets.
/// </summary>
public static class SsdpResponseParser
{
    public static SsdpDeviceHit? TryParse(string datagram)
    {
        if (string.IsNullOrWhiteSpace(datagram)) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool firstLine = true;
        foreach (var rawLine in datagram.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ');
            if (line.Length == 0) continue;

            if (firstLine)
            {
                firstLine = false;
                // Only search responses, never stray NOTIFY advertisements.
                if (!line.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase)) return null;
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0 || colon == line.Length - 1) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (!headers.TryGetValue("LOCATION", out var location) || !Uri.TryCreate(location, UriKind.Absolute, out var locationUri))
            return null;
        if (!headers.TryGetValue("USN", out var usn) || usn.Length == 0)
            return null;

        headers.TryGetValue("ST", out var st);
        headers.TryGetValue("SERVER", out var server);
        return new SsdpDeviceHit(locationUri, usn, st ?? "", server ?? "");
    }
}
