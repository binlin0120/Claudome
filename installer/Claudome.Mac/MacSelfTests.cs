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
        outputDirectory = Path.GetFullPath(outputDirectory);
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

        Check("mac-guest-assets-have-verified-sha256-for-arm64-and-x64", () =>
        {
            foreach (var isArm in new[] { true, false })
            {
                var resources = MacProvisioningMedia.GuestResources(isArm);
                Assert(resources.Count == 4, $"expected 4 guest resources for arch arm={isArm}");
                Assert(resources.Select(r => r.Id).SequenceEqual(["pwsh", "clash", "vcredist", "adspower"]), "unexpected Mac guest resource IDs");
                Assert(resources.All(r => r.Url.StartsWith("https://", StringComparison.Ordinal) && r.Sha256.Length == 64 && r.Size > 0),
                    "Mac guest asset missing verified SHA-256 or HTTPS URL");
            }
            Assert(Catalog.EmbeddedText("Configure.ps1").Contains("'fusion'", StringComparison.Ordinal) &&
                   Catalog.EmbeddedText("Configure.ps1").Contains("'utm'", StringComparison.Ordinal) &&
                   Catalog.EmbeddedText("NetworkAudit.ps1").Contains("'Claude DNS bootstrap'", StringComparison.Ordinal) &&
                   Catalog.EmbeddedBytes("YamlDotNet.dll").Length > 10000,
                "Embedded guest scripts or YamlDotNet.dll missing fusion/utm NAT support");
        });

        Check("mac-seed-bootstrap-writes-claudebootstrap-identity-for-configure-ps1", () =>
        {
            var root = Path.Combine(outputDirectory, "mac-seed-fixture");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var seedState = new SessionState
            {
                VmId = Guid.NewGuid().ToString(),
                Options = new() { Root = root, PlatformId = "fusion" }
            };
            var seedPath = MacProvisioningMedia.EnsureSeedIsoAsync(
                seedState,
                new Secrets { VmPassword = "FictionalMacPass!1" },
                isArm64: true,
                new NoOpRunner(),
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(seedPath.EndsWith("mac-seed.iso", StringComparison.OrdinalIgnoreCase), "unexpected seed ISO path");
            var initCmd = File.ReadAllText(Path.Combine(seedState.Options.Private, "mac-seed", "ClaudeBootstrap", "Initialize.cmd"));
            Assert(initCmd.Contains(@"C:\ProgramData\ClaudeBootstrap\identity.json", StringComparison.Ordinal),
                "Initialize.cmd did not match Configure.ps1 identity path");
        });

        Check("mac-deployment-orchestrator-runs-end-to-end-with-mock-fusion-and-pfctl", () =>
        {
            var root = Path.Combine(outputDirectory, "mac-deploy-fixture");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Paths.EnsureWorkspace(root);
            var fakeIso = Path.Combine(outputDirectory, "mac-deploy-win11-arm64.iso");
            using (var fs = File.Create(fakeIso)) fs.SetLength(1024L * 1024 * 1024 + 4096);

            var deployState = new SessionState
            {
                Options = new()
                {
                    Root = root,
                    PlatformId = "fusion",
                    VmName = "MacDeployTest",
                    CpuCount = Math.Clamp(Environment.ProcessorCount, 2, 4),
                    MemoryMb = 8192,
                    DiskGb = 80,
                    IsoPath = fakeIso,
                    NetworkSource = "节点链接 / Clash 配置文本",
                    Country = "US",
                    TimeZoneId = "Pacific Standard Time",
                    AcceptChanges = true,
                    AcceptWindowsLicense = true
                }
            };
            Directory.CreateDirectory(deployState.Options.Cache);
            var fakeBackend = new MockMacBackend();
            var fakeFirewall = new MockMacFirewall();
            var fakeNodeJson = """{"name":"TestNode","type":"socks5","server":"1.1.1.1","port":443}""";
            var resources = MacProvisioningMedia.GuestResources(true);
            foreach (var r in resources) File.WriteAllText(Paths.Under(deployState.Options.Cache, r.FileName), "fixture");

            var facts = MacHostEnvironment.ParseSysctlFacts(
                "arm64", "1", "0", "Apple M3", "15.1", 16L * 1024 * 1024 * 1024,
                "Pages free: 300000.\n", 8, "/Applications/VMware Fusion.app/Contents/Library/vmrun", null, false);
            var logs = new List<string>();
            var result = MacDeployment.DeployAsync(
                deployState,
                new Secrets { VmPassword = "FictionalMacPass!1", SelectedNodeJson = fakeNodeJson },
                facts,
                new Progress<string>(logs.Add),
                CancellationToken.None,
                fakeBackend,
                fakeFirewall,
                resources,
                skipEndpointDnsResolveForTest: true).GetAwaiter().GetResult();

            Assert(fakeFirewall.ApplyCalls == 1 && fakeBackend.ConfigureCalls == 1 && fakeBackend.InstallCalls == 1 && fakeBackend.TarExtractCalls == 1 && fakeBackend.ConfigureRunCalls == 1,
                "Mac deployment orchestrator skipped required lifecycle stages");
            Assert(fakeBackend.CopiedFiles.Contains("YamlDotNet.dll") && fakeBackend.CopiedFiles.Contains("Configure.ps1") && fakeBackend.CopiedFiles.Contains("NetworkAudit.ps1"),
                "Mac deployment missing embedded guest dependency upload");
            Assert(fakeBackend.LastEndpointPort == 443 && !fakeBackend.LastPayloadJson.Contains("198.19.250.1", StringComparison.Ordinal),
                "Mac NAT guest payload endpointPort or DNS bootstrap mismatch");
            Assert(result.NetworkPassed && result.SetupFinished && result.Completed.Contains("vm-created") && result.Completed.Contains("guest-ready"),
                "Mac deployment did not persist completed state");
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

    private sealed class NoOpRunner : ICommandRunner
    {
        public Task<string> Run(string fileName, IEnumerable<string> arguments, CancellationToken token, int seconds = 60) =>
            Task.FromResult("");
    }

    private sealed class MockMacFirewall : IHostFirewall
    {
        public int ApplyCalls { get; private set; }
        public Task ApplyAsync(string workspaceId, string guestInterface, string[] endpointIps, int endpointPort, int gatewayPort, CancellationToken token)
        {
            ApplyCalls++;
            return Task.CompletedTask;
        }
        public Task VerifyAsync(string workspaceId, string[] endpointIps, int endpointPort, int gatewayPort, CancellationToken token) => Task.CompletedTask;
        public Task ClearAsync(string workspaceId, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class MockMacBackend : IVmBackend
    {
        private string? lastPayloadRunId;
        private bool running;
        private bool pwshReady;
        public int ConfigureCalls { get; private set; }
        public int InstallCalls { get; private set; }
        public int TarExtractCalls { get; private set; }
        public int ConfigureRunCalls { get; private set; }
        public int GuestRunCalls { get; private set; }
        public int LastEndpointPort { get; private set; }
        public string LastPayloadJson { get; private set; } = "";
        public HashSet<string> CopiedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task Prepare(SetupOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<string> EnsureMachine(SessionState state, CancellationToken token)
        {
            state.VmId = string.IsNullOrEmpty(state.VmId) ? Guid.NewGuid().ToString() : state.VmId;
            return Task.FromResult(state.VmId);
        }
        public Task Configure(SessionState state, CancellationToken token) { ConfigureCalls++; return Task.CompletedTask; }
        public Task InstallWindows(SessionState state, Secrets secrets, CancellationToken token) { InstallCalls++; running = true; return Task.CompletedTask; }
        public Task<bool> IsRunning(SessionState state, CancellationToken token) => Task.FromResult(running);
        public Task<bool> GuestReady(SessionState state, Secrets secrets, CancellationToken token) => Task.FromResult(running);
        public Task CleanupWindowsMedia(SessionState state, CancellationToken token) => Task.CompletedTask;
        public Task<string> GuestRun(SessionState state, Secrets secrets, string executable, IEnumerable<string> args, CancellationToken token, int seconds = 300)
        {
            GuestRunCalls++;
            if (executable.EndsWith("tar.exe", StringComparison.OrdinalIgnoreCase))
            {
                TarExtractCalls++;
                pwshReady = true;
            }
            else if (executable.EndsWith("pwsh.exe", StringComparison.OrdinalIgnoreCase))
            {
                ConfigureRunCalls++;
            }
            return Task.FromResult("completed");
        }
        public Task<bool> GuestFileExists(SessionState state, Secrets secrets, string file, CancellationToken token) =>
            Task.FromResult(file.EndsWith("pwsh.exe", StringComparison.OrdinalIgnoreCase) && pwshReady);
        public Task CopyTo(SessionState state, Secrets secrets, string local, string destination, CancellationToken token)
        {
            var fileName = Path.GetFileName(destination);
            CopiedFiles.Add(fileName);
            if (fileName.StartsWith("payload-", StringComparison.Ordinal) && fileName.EndsWith(".json", StringComparison.Ordinal))
            {
                lastPayloadRunId = fileName["payload-".Length..^".json".Length];
                LastPayloadJson = File.ReadAllText(local);
                using var doc = JsonDocument.Parse(LastPayloadJson);
                if (doc.RootElement.TryGetProperty("endpointPort", out var portProp))
                    LastEndpointPort = portProp.GetInt32();
            }
            return Task.CompletedTask;
        }
        public Task CopyFrom(SessionState state, Secrets secrets, string guest, string destination, CancellationToken token)
        {
            JsonFile.Write(destination, new
            {
                runId = lastPayloadRunId ?? "",
                networkPassed = true,
                outagePassed = true,
                clashConfigured = true,
                browserProfileConfigured = true,
                code = "NEEDS_BROWSER_ACCEPTANCE"
            });
            return Task.CompletedTask;
        }
        public Task Open(SessionState state, CancellationToken token) => Task.CompletedTask;
    }
}
