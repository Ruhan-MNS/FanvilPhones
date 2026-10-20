using System.Net;
using System.Net.Sockets;

namespace FancilPhones.Application;

public static class PhoneNetworkRange
{
    /// <summary>
    /// Enumerate IPs from a CIDR ("192.168.55.0/24") or dotted range
    /// ("192.168.55.1-254" or "192.168.55.10-192.168.55.40").
    /// Network and broadcast addresses are excluded for /24 and smaller.
    /// </summary>
    public static IEnumerable<string> EnumerateIps(string cidrOrRange)
    {
        cidrOrRange = (cidrOrRange ?? "").Trim();
        if (cidrOrRange.Length == 0) yield break;

        if (cidrOrRange.Contains('/'))
        {
            var parts = cidrOrRange.Split('/', 2);
            if (!IPAddress.TryParse(parts[0], out var baseIp) ||
                !int.TryParse(parts[1], out var prefix) ||
                baseIp.AddressFamily != AddressFamily.InterNetwork ||
                prefix is < 0 or > 32)
                yield break;

            var baseBytes = baseIp.GetAddressBytes();
            var baseInt = (uint)((baseBytes[0] << 24) | (baseBytes[1] << 16) | (baseBytes[2] << 8) | baseBytes[3]);
            var hostBits = 32 - prefix;
            var mask = hostBits == 32 ? 0u : 0xFFFFFFFFu << hostBits;
            var network = baseInt & mask;
            var broadcast = hostBits == 0 ? network : network | ~mask;

            var first = prefix >= 31 ? network : network + 1;
            var last = prefix >= 31 ? broadcast : broadcast - 1;
            for (var x = first; x <= last && x >= first; x++)
            {
                yield return $"{(x >> 24) & 0xFF}.{(x >> 16) & 0xFF}.{(x >> 8) & 0xFF}.{x & 0xFF}";
                if (x == 0xFFFFFFFFu) break;
            }
            yield break;
        }

        if (cidrOrRange.Contains('-'))
        {
            var dash = cidrOrRange.IndexOf('-');
            var left = cidrOrRange[..dash].Trim();
            var right = cidrOrRange[(dash + 1)..].Trim();

            if (!IPAddress.TryParse(left, out var startIp) ||
                startIp.AddressFamily != AddressFamily.InterNetwork)
                yield break;

            IPAddress endIp;
            if (right.Contains('.'))
            {
                if (!IPAddress.TryParse(right, out var parsed)) yield break;
                endIp = parsed;
            }
            else
            {
                if (!byte.TryParse(right, out var lastOctet)) yield break;
                var sb = startIp.GetAddressBytes();
                endIp = new IPAddress(new[] { sb[0], sb[1], sb[2], lastOctet });
            }

            uint ToUint(IPAddress ip)
            {
                var b = ip.GetAddressBytes();
                return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
            }

            var s = ToUint(startIp);
            var e = ToUint(endIp);
            if (e < s) (s, e) = (e, s);
            for (var x = s; x <= e && x >= s; x++)
            {
                yield return $"{(x >> 24) & 0xFF}.{(x >> 16) & 0xFF}.{(x >> 8) & 0xFF}.{x & 0xFF}";
                if (x == 0xFFFFFFFFu) break;
            }
            yield break;
        }

        if (IPAddress.TryParse(cidrOrRange, out _))
            yield return cidrOrRange;
    }

}
