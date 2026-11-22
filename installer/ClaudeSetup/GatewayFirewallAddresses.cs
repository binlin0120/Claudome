using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace ClaudeSetup;

internal readonly record struct FirewallAddressRange(int Bits, BigInteger First, BigInteger Last);

public static partial class GatewayFirewall
{
    // Compare address coverage: Windows can reorder entries or return dotted IPv4 masks.
    // The expected intervals are independent of the presentation used when registering.
    internal static bool RemoteScopeMatches(string? value)
    {
        var actual = ParseRemoteScope(value);
        return actual is not null && actual.SequenceEqual(new[] {
            new FirewallAddressRange(32, 0, 0x7effffff),
            new FirewallAddressRange(32, 0x80000000, uint.MaxValue),
            new FirewallAddressRange(128, 0, (BigInteger.One << 128) - 1)
        });
    }

    private static bool ReadAddress(string value, out IPAddress address, out BigInteger number, out int bits)
    {
        address = IPAddress.None; number = 0; bits = 0;
        if (value.Contains('%') || !IPAddress.TryParse(value, out var parsed)) return false;
        if (parsed.AddressFamily == AddressFamily.InterNetwork && parsed.ToString() != value) return false;
        address = parsed; bits = parsed.GetAddressBytes().Length * 8;
        number = new BigInteger(parsed.GetAddressBytes(), isUnsigned: true, isBigEndian: true);
        return bits is 32 or 128;
    }

    internal static IReadOnlyList<FirewallAddressRange>? ParseRemoteScope(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16384) return null;
        var tokens = value.Split(',');
        if (tokens.Length > 128) return null;
        var ranges = new List<FirewallAddressRange>();
        foreach (var part in tokens) {
            var token = part.Trim();
            if (token.Length == 0) return null;
            if (token == "*") {
                if (tokens.Length != 1) return null;
                ranges.Add(new(32, 0, uint.MaxValue));
                ranges.Add(new(128, 0, (BigInteger.One << 128) - 1));
                continue;
            }
            if (token.Contains('-')) {
                var ends = token.Split('-');
                if (ends.Length != 2 || !ReadAddress(ends[0], out _, out var first, out var bits) ||
                    !ReadAddress(ends[1], out _, out var last, out var endBits) || bits != endBits || first > last) return null;
                ranges.Add(new(bits, first, last));
                continue;
            }
            var subnet = token.Split('/');
            if (subnet.Length > 2 || !ReadAddress(subnet[0], out _, out var address, out var width)) return null;
            var prefix = width;
            if (subnet.Length == 2) {
                if (subnet[1].Contains('.')) {
                    if (width != 32 || !ReadAddress(subnet[1], out _, out var mask, out var maskWidth) || maskWidth != 32) return null;
                    var inverse = uint.MaxValue ^ mask;
                    if ((inverse & (inverse + 1)) != 0) return null;
                    prefix = 32;
                    while (inverse > 0) { prefix--; inverse >>= 1; }
                } else if (!int.TryParse(subnet[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > width) return null;
            }
            var count = BigInteger.One << (width - prefix);
            var start = address / count * count;
            ranges.Add(new(width, start, start + count - 1));
        }
        var merged = new List<FirewallAddressRange>();
        foreach (var next in ranges.OrderBy(x => x.Bits).ThenBy(x => x.First).ThenBy(x => x.Last)) {
            if (merged.Count > 0 && merged[^1] is var previous && previous.Bits == next.Bits && next.First <= previous.Last + 1)
                merged[^1] = previous with { Last = BigInteger.Max(previous.Last, next.Last) };
            else merged.Add(next);
        }
        return merged;
    }
}
