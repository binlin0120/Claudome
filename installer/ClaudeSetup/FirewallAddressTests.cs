using System.Net;
using System.Numerics;

namespace ClaudeSetup;

public static class FirewallAddressTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        Task Sync(Action action) { action(); return Task.CompletedTask; }
        const string Normalized = "8000::/1,::/1,128.0.0.0/128.0.0.0,126.0.0.0/255.0.0.0,124.0.0.0/254.0.0.0,120.0.0.0/252.0.0.0,112.0.0.0/248.0.0.0,96.0.0.0/240.0.0.0,64.0.0.0/224.0.0.0,0.0.0.0/192.0.0.0";
        await check("firewall-address-output-avoids-zero-prefix-and-unspecified-literal", () => Sync(() => {
            foreach (var token in GatewayFirewall.RemoteRange.Split(',')) {
                var parts = token.Split('/');
                Assert(parts.Length == 2 && int.TryParse(parts[1], out var prefix) && prefix >= 1, "unsupported zero-length prefix or range emitted");
                var address = IPAddress.Parse(parts[0]);
                Assert(!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(address), "special literal emitted");
            }
        }));
        await check("firewall-address-coverage-retains-entire-nonloopback-v4-and-entire-v6", () => Sync(() => {
            var ranges = GatewayFirewall.ParseRemoteScope(GatewayFirewall.RemoteRange)!;
            Assert(ranges.SequenceEqual(new[] { new FirewallAddressRange(32, 0, 0x7effffff), new FirewallAddressRange(32, 0x80000000, uint.MaxValue), new FirewallAddressRange(128, 0, (BigInteger.One << 128) - 1) }), "protection coverage changed");
            foreach (var sample in new[] { "0.0.0.0", "10.0.0.1", "126.255.255.255", "127.0.0.0", "127.0.0.1", "127.255.255.255", "128.0.0.0", "224.0.0.1", "255.255.255.255", "::", "::1", "::ffff:127.0.0.1", "7fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "8000::", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff" }) {
                var address = IPAddress.Parse(sample); var bytes = address.GetAddressBytes(); var number = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
                var included = ranges.Any(x => x.Bits == bytes.Length * 8 && number >= x.First && number <= x.Last);
                Assert(included == !(bytes.Length == 4 && bytes[0] == 127), "coverage boundary changed: " + sample);
            }
        }));
        await check("firewall-address-readback-accepts-normalized-masks-order-and-ipv6", () => Sync(() => {
            Assert(GatewayFirewall.RemoteScopeMatches(Normalized), "Windows normalized readback rejected");
            Assert(GatewayFirewall.RemoteScopeMatches(GatewayFirewall.RemoteRange), "registration representation rejected");
            Assert(GatewayFirewall.RemoteScopeMatches(Normalized + ",8.8.8.8,2001:4860:4860::8888"), "redundant covered entries rejected");
        }));
        await check("firewall-address-readback-rejects-gaps-and-expanded-loopback-block", () => Sync(() => {
            foreach (var invalid in new[] { "*", "0.0.0.0/1,128.0.0.0/1,::/1,8000::/1", Normalized.Replace(",126.0.0.0/255.0.0.0", ""), Normalized.Replace("8000::/1,", ""), GatewayFirewall.RemoteRange + ",127.0.0.1", GatewayFirewall.RemoteRange.Replace("::2/1", "::2/2") })
                Assert(!GatewayFirewall.RemoteScopeMatches(invalid), "different firewall scope accepted");
        }));
        await check("firewall-address-parser-rejects-malformed-and-unbounded-input", () => Sync(() => {
            foreach (var invalid in new string?[] { null, "", " ", ",", "*,1.1.1.1", "LocalSubnet", "1.1.1.1-::2", "2.2.2.2-1.1.1.1", "1.1.1.1/255.0.255.0", "::/129", "::/-1", "fe80::1%2/64", "127.1/8", "1.1.1.1/", new string('x', 16385), string.Join(',', Enumerable.Repeat("1.1.1.1", 129)) })
                Assert(GatewayFirewall.ParseRemoteScope(invalid) is null, "malformed scope accepted");
        }));
        await check("firewall-registration-checks-actual-address-coverage-before-enable", () => Sync(() => {
            var spec = new GatewayFirewallRule("fixture-owned", @"C:\Fixture\VirtualBoxVM.exe", 50001);
            FirewallRegistrationTests.Rule? stored = null; var phases = new List<string>();
            try {
                GatewayFirewall.RegisterRule(new FirewallRegistrationTests.Rule(), spec, raw => { stored = ((FirewallRegistrationTests.Rule)raw).Clone(); stored.Readback = _ => "*"; return stored; }, phases.Add);
                throw new Exception("Expected mismatched scope rejection");
            } catch (SetupException) {
                Assert(stored is not null && !stored.Enabled && phases[^1] == "verify-remote", "mismatched registered rule became active");
            }
        }));
        await check("firewall-registration-can-enable-normalized-equivalent-rule", () => Sync(() => {
            var spec = new GatewayFirewallRule("fixture-owned", @"C:\Fixture\VirtualBoxVM.exe", 50001);
            FirewallRegistrationTests.Rule? stored = null;
            GatewayFirewall.RegisterRule(new FirewallRegistrationTests.Rule(), spec, raw => { stored = ((FirewallRegistrationTests.Rule)raw).Clone(); stored.Readback = _ => Normalized; return stored; }, _ => {});
            Assert(stored is not null && stored.Enabled, "native normalization prevented complete registration");
        }));
    }
}
