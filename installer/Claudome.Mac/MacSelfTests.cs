using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ClaudeSetup;

/// <summary>Offline self-tests for Claudome.Mac backends, security primitives, and provisioning generators.</summary>
public static class MacSelfTests
{
    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var rows = new List<object>();
        var failed = 0;

        void Assert(bool condition, string reason)
        {
            if (!condition) throw new Exception(reason);
        }

        void Reject(Action action, string reason)
        {
            var threw = false;
            try { action(); }
            catch (SetupException) { threw = true; }
            Assert(threw, reason);
        }

        void Check(string name, Action test)
        {
            try
            {
                test();
                rows.Add(new { name, passed = true });
            }
            catch (Exception error)
            {
                failed++;
                rows.Add(new { name, passed = false, error = error.Message });
            }
        }

        Check("mac-rosetta-still-identifies-apple-silicon-and-parses-vm-stat", () =>
        {
            var facts = MacHostEnvironment.ParseSysctlFacts(
                "x86_64", "1", "1", "Apple M3 Pro", "15.1",
                18L * 1024 * 1024 * 1024,
                "Mach Virtual Memory Statistics: (page size of 16384 bytes)\nPages free: 120000.\nPages inactive: 200000.\nPages speculative: 10000.\n",
                12, "/Applications/VMware Fusion.app/Contents/Library/vmrun", null, false);
            Assert(facts.IsAppleSilicon && facts.RunningUnderRosetta && facts.GuestArchitecture == "arm64", "Apple Silicon under Rosetta was misclassified");
            Assert(facts.FreeMemoryBytes == (120000L + 200000L + 10000L) * 16384L, "vm_stat free memory calculation mismatch");
            var choices = MacHostEnvironment.Recommend(facts);
            Assert(choices.Count == 2 && choices[0].Id == "fusion" && choices[1].Id == "utm" && choices.All(c => c.Compatible), "Mac recommendations mismatch");
        });

        Check("keychain-aes-gcm-roundtrip-and-tamper-rejection", () =>
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var payload = Encoding.UTF8.GetBytes("{\"VmPassword\":\"FictionalMacPass!1\"}");
            var sealedBytes = KeychainSecretStore.ProtectWithKey(payload, key);
            var restored = KeychainSecretStore.UnprotectWithKey(sealedBytes, key);
            Assert(payload.AsSpan().SequenceEqual(restored), "AES-GCM roundtrip mismatch");

            var tampered = (byte[])sealedBytes.Clone();
            tampered[^1] ^= 0xFF;
            Reject(() => KeychainSecretStore.UnprotectWithKey(tampered, key), "tampered ciphertext accepted");

            var wrongKey = RandomNumberGenerator.GetBytes(32);
            Reject(() => KeychainSecretStore.UnprotectWithKey(sealedBytes, wrongKey), "wrong key accepted");
        });

        Check("mac-pfctl-anchor-rules-fail-closed-and-verify-readback", () =>
        {
            var anchor = MacGatewayFirewall.AnchorName("11111111-2222-3333-4444-555555555555");
            Assert(anchor == "claudome_1111111122223333", "unexpected anchor name");
            var rules = MacGatewayFirewall.BuildAnchorRules("bridge100", "127.0.0.1", 60181, ["1.1.1.1"], 443);
            Assert(rules.Contains("block drop quick on bridge100 all", StringComparison.Ordinal) &&
                   rules.Contains("to 127.0.0.1 port 60181", StringComparison.Ordinal) &&
                   rules.Contains("to 1.1.1.1 port 443", StringComparison.Ordinal), "pf anchor rules missing required lines");
            Reject(() => MacGatewayFirewall.BuildAnchorRules("bridge100", "127.0.0.1", 60181, ["192.168.1.1"], 443), "private endpoint accepted by pf rule builder");

            const string readback = """
                pass in quick on bridge100 inet proto udp from any to 127.0.0.1 port = 60181
                pass in quick on bridge100 inet proto tcp from any to 127.0.0.1 port = 60181
                block drop quick on bridge100 all
                pass out quick inet proto tcp from any to 1.1.1.1 port = 443 user = 0
                """;
            MacGatewayFirewall.VerifyAnchorReadback(readback, "bridge100", "127.0.0.1", 60181, ["1.1.1.1"], 443);
            Reject(() => MacGatewayFirewall.VerifyAnchorReadback("pass out quick on bridge100 all", "bridge100", "127.0.0.1", 60181, ["1.1.1.1"], 443), "permissive pf readback accepted");
        });

        Check("fusion-vmx-and-utm-plist-hardening-and-arm64-unattend", () =>
        {
            var root = Path.Combine(outputDirectory, "mac-fixture");
            var state = new SessionState
            {
                VmId = Guid.NewGuid().ToString(),
                Options = new() { Root = root, PlatformId = "fusion", CpuCount = 4, MemoryMb = 8192, Country = "US", TimeZoneId = "Pacific Standard Time" }
            };
            var vmx = FusionBackend.BuildVmx(state, appleSilicon: true, "/tmp/disk.vmdk", "/tmp/win11-arm64.iso", "/tmp/mac-seed.iso");
            Assert(vmx.Contains("guestOS = \"arm-windows11-64\"", StringComparison.Ordinal) &&
                   vmx.Contains("isolation.tools.hgfs.disable = \"TRUE\"", StringComparison.Ordinal) &&
                   vmx.Contains("isolation.tools.copy.disable = \"FALSE\"", StringComparison.Ordinal) &&
                   vmx.Contains("isolation.tools.paste.disable = \"FALSE\"", StringComparison.Ordinal) &&
                   vmx.Contains("isolation.tools.dnd.disable = \"TRUE\"", StringComparison.Ordinal) &&
                   vmx.Contains("uefi.secureBoot.enabled = \"TRUE\"", StringComparison.Ordinal), "Fusion VMX missing hardening, default clipboard, or ARM64 guestOS");

            var plist = UtmBackend.BuildUtmConfigPlist(state, appleSilicon: true, "02:aa:bb:cc:dd:ee", "disk0.qcow2", "/tmp/win11-arm64.iso", "/tmp/mac-seed.iso");
            var plistDoc = XDocument.Parse(plist);
            Assert(plistDoc.Root?.Name == "plist" && plist.Contains("<string>aarch64</string>") && plist.Contains("<key>TPMDevice</key><true/>") && plist.Contains("<key>ClipboardSharing</key><true/>") && plist.Contains("<key>DirectoryShareMode</key><string>None</string>"),
                "UTM config.plist XML invalid or missing TPM/default clipboard/folder isolation");

            state.Options.SharedClipboardEnabled = false;
            var disabledVmx = FusionBackend.BuildVmx(state, appleSilicon: true, "/tmp/disk.vmdk", "/tmp/win11-arm64.iso", null);
            var disabledPlist = UtmBackend.BuildUtmConfigPlist(state, appleSilicon: true, "02:aa:bb:cc:dd:ee", "disk0.qcow2", "/tmp/win11-arm64.iso", null);
            Assert(disabledVmx.Contains("isolation.tools.copy.disable = \"TRUE\"", StringComparison.Ordinal) &&
                   disabledVmx.Contains("isolation.tools.paste.disable = \"TRUE\"", StringComparison.Ordinal) &&
                   disabledPlist.Contains("<key>ClipboardSharing</key><false/>", StringComparison.Ordinal),
                "Shared clipboard disable toggle did not apply to Fusion VMX or UTM config.plist");
            state.Options.SharedClipboardEnabled = true;

            var xml = MacProvisioningMedia.BuildAutounattendXml(state.Options, "FictionalMacPass!1", "arm64");
            var unattendDoc = XDocument.Parse(xml);
            Assert(unattendDoc.Root is not null && xml.Contains("processorArchitecture=\"arm64\"") && xml.Contains("Pacific Standard Time"),
                "ARM64 Autounattend XML invalid");

            IsolationPolicy.RequireSupportedPlatform(state, isMacOverride: true);
            IsolationPolicy.RequireSupportedPlatform(new SessionState { Options = new() { PlatformId = "utm" } }, isMacOverride: true);
            Reject(() => IsolationPolicy.RequireSupportedPlatform(new SessionState { Options = new() { PlatformId = "unknown" } }, isMacOverride: true),
                "unsupported Mac platform accepted");
        });

        JsonFile.Write(Path.Combine(outputDirectory, "mac-tests.json"), new
        {
            dateUtc = DateTime.UtcNow,
            type = "isolated macOS backend contract tests; no Mac VM created",
            passed = rows.Count - failed,
            failed,
            tests = rows
        });
        Console.WriteLine(JsonSerializer.Serialize(new { passed = rows.Count - failed, failed }));
        return failed == 0 ? 0 : 1;
    }
}
