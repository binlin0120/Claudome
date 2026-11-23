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

            const string hostMain = "scrub-anchor \"com.apple/*\" all fragment reassemble\nanchor \"com.apple/*\" all\n";
            var withAnchor = MacGatewayFirewall.BuildMainRulesetWithAnchor(hostMain, anchor);
            var scrubIdx = withAnchor.IndexOf("scrub-anchor", StringComparison.Ordinal);
            var anchorIdx = withAnchor.IndexOf($"anchor \"{anchor}\"", StringComparison.Ordinal);
            Assert(scrubIdx >= 0 && anchorIdx > scrubIdx, "pf anchor inserted before scrub-anchor preamble");
            const string enabledInfo = "Status: Enabled for 0 days 00:00:05           Debug: Urgent\n";
            const string disabledInfo = "Status: Disabled\n";
            var combinedLf = $"===PF_INFO===\n{enabledInfo}\n===PF_MAIN===\n{withAnchor}\n===PF_ANCHOR===\n{readback}";
            foreach (var sep in new[] { "\n", "\r\n", "\r" })
            {
                var hostFirstVariant = sep == "\r"
                    ? $"===PF_INFO===\rStatus: Enabled for 0 days\r===PF_MAIN===\rscrub-anchor \"com.apple/*\" all\ranchor \"com.apple/*\" all\ranchor \"{anchor}\" all\r===PF_ANCHOR===\r{readback.Replace("\n", "\r")}"
                    : combinedLf.Replace("\n", sep);
                MacGatewayFirewall.VerifyCombinedReadback(hostFirstVariant, anchor, "bridge100", "127.0.0.1", 60181, ["1.1.1.1"], 443);
            }
            Reject(() => MacGatewayFirewall.VerifyCombinedReadback($"===PF_INFO===\n{disabledInfo}\n===PF_MAIN===\n{withAnchor}\n===PF_ANCHOR===\n{readback}", anchor, "bridge100", "127.0.0.1", 60181, ["1.1.1.1"], 443),
                "disabled PF accepted even though rules were readable");
            Reject(() => MacGatewayFirewall.VerifyCombinedReadback($"===PF_INFO===\n{enabledInfo}\n===PF_MAIN===\n{hostMain}\n===PF_ANCHOR===\n{readback}", anchor, "bridge100", "127.0.0.1", 60181, ["1.1.1.1"], 443),
                "unhooked pf anchor accepted without main ruleset reference");
            var cleared = MacGatewayFirewall.RemoveAnchorFromMainRuleset(withAnchor, anchor);
            Assert(cleared.Contains("anchor \"com.apple/*\"", StringComparison.Ordinal) && !cleared.Contains(anchor, StringComparison.Ordinal),
                "pf clear did not preserve host rules or remove task anchor");

            // Stateful PF script simulations executing actual generated Apply/Clear shell scripts:
            // 1. Valid Apply -> CR readback succeeds -> Clear preserves host rules cleanly.
            var pfSim = new StatefulPfStubRunner(hostMain, pfEnabled: false);
            var fwSim = new MacGatewayFirewall(pfSim);
            fwSim.ApplyCoreAsync("11111111-2222-3333-4444-555555555555", "bridge100", ["1.1.1.1"], 443, 60181, CancellationToken.None, isMacOverride: true).GetAwaiter().GetResult();
            Assert(pfSim.PfEnabled && pfSim.MainRules.Contains($"anchor \"{anchor}\"", StringComparison.Ordinal) &&
                   pfSim.MainRules.Contains("anchor \"com.apple/*\"", StringComparison.Ordinal) &&
                   pfSim.Anchors.TryGetValue(anchor, out var activeChild) && activeChild.Contains("block drop quick on bridge100 all", StringComparison.Ordinal),
                "Stateful PF apply did not enable PF and attach rules preserving host main rules");

            // 2. Partial failure during Apply: pfctl -e fails after pfctl -Rf & pfctl -a -> must roll back BOTH main rules and child anchor!
            var pfEnableFail = new StatefulPfStubRunner(hostMain, pfEnabled: false, failStep: "enable");
            Reject(() => new MacGatewayFirewall(pfEnableFail).ApplyCoreAsync("11111111-2222-3333-4444-555555555555", "bridge100", ["1.1.1.1"], 443, 60181, CancellationToken.None, isMacOverride: true).GetAwaiter().GetResult(),
                "ApplyCoreAsync accepted pfctl -e failure");
            Assert(!pfEnableFail.MainRules.Contains(anchor, StringComparison.Ordinal) &&
                   pfEnableFail.MainRules.Contains("anchor \"com.apple/*\"", StringComparison.Ordinal) &&
                   !pfEnableFail.Anchors.ContainsKey(anchor),
                "ApplyCoreAsync left partial anchor rules or mutated main ruleset after pfctl -e failure");

            // 3. Partial failure during Apply: pfctl -Rf fails after pfctl -a -> must roll back child anchor!
            var pfMainFail = new StatefulPfStubRunner(hostMain, pfEnabled: true, failStep: "apply-main");
            Reject(() => new MacGatewayFirewall(pfMainFail).ApplyCoreAsync("11111111-2222-3333-4444-555555555555", "bridge100", ["1.1.1.1"], 443, 60181, CancellationToken.None, isMacOverride: true).GetAwaiter().GetResult(),
                "ApplyCoreAsync accepted main reload failure");
            Assert(!pfMainFail.MainRules.Contains(anchor, StringComparison.Ordinal) && !pfMainFail.Anchors.ContainsKey(anchor),
                "ApplyCoreAsync left child anchor rules behind after pfctl -Rf failure");

            // 4. Partial failure during Clear: pfctl -Rf fails -> protection must remain completely intact (anchor in main + child rules untouched)!
            var pfClearMainFail = new StatefulPfStubRunner(withAnchor, pfEnabled: true, initialAnchorRules: rules, initialAnchorName: anchor, failStep: "clear-main");
            Reject(() => new MacGatewayFirewall(pfClearMainFail).ClearCoreAsync("11111111-2222-3333-4444-555555555555", CancellationToken.None, isMacOverride: true).GetAwaiter().GetResult(),
                "ClearCoreAsync ignored main ruleset reload failure");
            Assert(pfClearMainFail.MainRules.Contains(anchor, StringComparison.Ordinal) &&
                   pfClearMainFail.Anchors.TryGetValue(anchor, out var keptChild) && keptChild.Contains("block drop quick", StringComparison.Ordinal),
                "ClearCoreAsync left orphaned/emptied anchor after pfctl -Rf failure");

            // 5. Partial failure during Clear: pfctl -a -F all fails after pfctl -Rf -> must restore anchor in main ruleset so anchor is never orphaned!
            var pfClearFlushFail = new StatefulPfStubRunner(withAnchor, pfEnabled: true, initialAnchorRules: rules, initialAnchorName: anchor, failStep: "clear-flush");
            Reject(() => new MacGatewayFirewall(pfClearFlushFail).ClearCoreAsync("11111111-2222-3333-4444-555555555555", CancellationToken.None, isMacOverride: true).GetAwaiter().GetResult(),
                "ClearCoreAsync ignored child anchor flush failure");
            Assert(pfClearFlushFail.MainRules.Contains(anchor, StringComparison.Ordinal) && pfClearFlushFail.Anchors.ContainsKey(anchor),
                "ClearCoreAsync left anchor unlinked from main ruleset after flush failure");

            // 6. Read failure stops BEFORE pfctl -Rf in both Apply & Clear, and valid Clear removes anchor cleanly leaving host rules intact.
            var readFailRunner = new StatefulPfStubRunner(hostMain, pfEnabled: true, failStep: "read-main");
            Reject(() => new MacGatewayFirewall(readFailRunner).ApplyCoreAsync("11111111-2222-3333-4444-555555555555", "bridge100", ["1.1.1.1"], 443, 60181, CancellationToken.None, isMacOverride: true).GetAwaiter().GetResult(),
                "ApplyCoreAsync proceeded past host-rule read failure");
            Reject(() => new MacGatewayFirewall(readFailRunner).ClearCoreAsync("11111111-2222-3333-4444-555555555555", CancellationToken.None, isMacOverride: true).GetAwaiter().GetResult(),
                "ClearCoreAsync proceeded past host-rule read failure");
            Assert(!readFailRunner.MainReloadAttempted, "pfctl -Rf was attempted despite host rule read failure");

            // 7. M01-a: Existing anchor in main ruleset whose child rules cannot be read MUST abort before mutations and NOT emit PF_APPLY_ROLLED_BACK.
            var readAnchorFail = new StatefulPfStubRunner(withAnchor, pfEnabled: true, initialAnchorRules: rules, initialAnchorName: anchor, failStep: "read-anchor");
            try
            {
                new MacGatewayFirewall(readAnchorFail).ApplyCoreAsync("11111111-2222-3333-4444-555555555555", "bridge100", ["1.1.1.1"], 443, 60181, CancellationToken.None, isMacOverride: true).GetAwaiter().GetResult();
                throw new Exception("ApplyCoreAsync silently wiped unreadable existing anchor");
            }
            catch (SetupException ex)
            {
                Assert(!ex.Message.Contains("PF_APPLY_ROLLED_BACK", StringComparison.Ordinal) &&
                       !readAnchorFail.MainReloadAttempted &&
                       readAnchorFail.Anchors.TryGetValue(anchor, out var stillKept) && stillKept.Contains("block drop quick", StringComparison.Ordinal),
                    $"Existing unreadable anchor triggered wrong rollback/wipe: {ex.Message}");
            }

            // 8. M01-b: Final readback failure AFTER pfctl -e enabled PF MUST roll back child rules, main ruleset, AND disable PF back to original state!
            var readbackFail = new StatefulPfStubRunner(hostMain, pfEnabled: false, failStep: "readback");
            Reject(() => new MacGatewayFirewall(readbackFail).ApplyCoreAsync("11111111-2222-3333-4444-555555555555", "bridge100", ["1.1.1.1"], 443, 60181, CancellationToken.None, isMacOverride: true).GetAwaiter().GetResult(),
                "ApplyCoreAsync succeeded despite final PF readback failure");
            Assert(!readbackFail.PfEnabled &&
                   !readbackFail.MainRules.Contains(anchor, StringComparison.Ordinal) &&
                   readbackFail.MainRules.Contains("anchor \"com.apple/*\"", StringComparison.Ordinal) &&
                   !readbackFail.Anchors.ContainsKey(anchor),
                "Final readback failure did not disable newly enabled PF and restore prior rules");

            // 9. M01-c (Initial PF = Disabled, no existing anchor): C# VerifyCombinedReadback failure MUST invoke RestorePreApplySnapshotAsync -> disable newly enabled PF AND clear anchor!
            var verifyFailFromOff = new StatefulPfStubRunner(hostMain, pfEnabled: false, failStep: "verify-bad-anchor");
            Reject(() => new MacGatewayFirewall(verifyFailFromOff).ApplyCoreAsync("11111111-2222-3333-4444-555555555555", "bridge100", ["1.1.1.1"], 443, 60181, CancellationToken.None, isMacOverride: true).GetAwaiter().GetResult(),
                "ApplyCoreAsync accepted bad readback when PF started disabled");
            Assert(!verifyFailFromOff.PfEnabled &&
                   !verifyFailFromOff.MainRules.Contains(anchor, StringComparison.Ordinal) &&
                   verifyFailFromOff.MainRules.Contains("anchor \"com.apple/*\"", StringComparison.Ordinal) &&
                   !verifyFailFromOff.Anchors.ContainsKey(anchor),
                "C# VerifyCombinedReadback failure from PF-disabled state did not disable PF and restore host main ruleset");

            // 10. M01-d (Initial PF = Enabled + pre-existing task anchor): C# VerifyCombinedReadback failure MUST restore pre-existing anchor rules and keep PF enabled!
            var priorTaskRules = "# prior valid rules\nblock drop quick on bridge99 all\n";
            var verifyFailFromOn = new StatefulPfStubRunner(withAnchor, pfEnabled: true, initialAnchorRules: priorTaskRules, initialAnchorName: anchor, failStep: "verify-bad-anchor");
            Reject(() => new MacGatewayFirewall(verifyFailFromOn).ApplyCoreAsync("11111111-2222-3333-4444-555555555555", "bridge100", ["1.1.1.1"], 443, 60181, CancellationToken.None, isMacOverride: true).GetAwaiter().GetResult(),
                "ApplyCoreAsync accepted bad readback when pre-existing anchor existed");
            Assert(verifyFailFromOn.PfEnabled &&
                   verifyFailFromOn.MainRules.Contains(anchor, StringComparison.Ordinal) &&
                   verifyFailFromOn.Anchors.TryGetValue(anchor, out var restoredAnchor) &&
                   restoredAnchor.Contains("bridge99", StringComparison.Ordinal),
                "C# VerifyCombinedReadback failure wiped pre-existing task anchor instead of restoring snapshot");

            fwSim.ClearCoreAsync("11111111-2222-3333-4444-555555555555", CancellationToken.None, isMacOverride: true).GetAwaiter().GetResult();
            Assert(!pfSim.MainRules.Contains(anchor, StringComparison.Ordinal) && pfSim.MainRules.Contains("anchor \"com.apple/*\"", StringComparison.Ordinal) &&
                   !pfSim.Anchors.ContainsKey(anchor),
                "Valid ClearCoreAsync did not clean task anchor while preserving host rules");
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
            Assert(result.NetworkPassed && !result.SetupFinished && !result.ApplicationAccepted &&
                   result.Completed.Contains("vm-created") && result.Completed.Contains("guest-ready") &&
                   result.PendingActions.Any(a => a.Id == "acceptance"),
                "NEEDS_BROWSER_ACCEPTANCE must keep SetupFinished/ApplicationAccepted false with pending acceptance");

            var reloaded = JsonFile.Read<SessionState>(result.Options.StatePath);
            Assert(reloaded.NetworkPassed && !reloaded.SetupFinished && !reloaded.ApplicationAccepted &&
                   reloaded.PendingActions.Any(a => a.Id == "acceptance"),
                "reloaded Mac session state did not preserve pending browser acceptance");
            var acceptanceDoc = JsonDocument.Parse(File.ReadAllText(Paths.Under(root, "acceptance.json")));
            Assert(acceptanceDoc.RootElement.GetProperty("browserAcceptance").GetString() == "not-tested",
                "acceptance.json premature confirmation before user check");

            Reject(() => MacDeployment.ConfirmBrowserAcceptance(reloaded, browserConfirmed: false),
                "unconfirmed browser acceptance accepted");
            MacDeployment.ConfirmBrowserAcceptance(reloaded, browserConfirmed: true);
            var confirmedReloaded = JsonFile.Read<SessionState>(result.Options.StatePath);
            Assert(confirmedReloaded.SetupFinished && confirmedReloaded.ApplicationAccepted &&
                   confirmedReloaded.PendingActions.Count == 0 &&
                   confirmedReloaded.Protection.Browser.Status == CheckStatus.Passed,
                "explicit browser acceptance confirmation did not persist completion");

            var afterBrowserDoc = JsonDocument.Parse(File.ReadAllText(Paths.Under(root, "acceptance.json")));
            Assert(afterBrowserDoc.RootElement.GetProperty("browserAcceptance").GetString() == "user-confirmed" &&
                   afterBrowserDoc.RootElement.GetProperty("claudeLoginAndConversation").GetString() == "not-tested",
                "ConfirmBrowserAcceptance prematurely marked claudeLoginAndConversation as confirmed");

            Reject(() => MacDeployment.ConfirmClaudeLoginAndConversation(confirmedReloaded, claudeConfirmed: false),
                "unconfirmed Claude conversation accepted");
            MacDeployment.ConfirmClaudeLoginAndConversation(confirmedReloaded, claudeConfirmed: true);
            var afterClaudeDoc = JsonDocument.Parse(File.ReadAllText(Paths.Under(root, "acceptance.json")));
            Assert(afterClaudeDoc.RootElement.GetProperty("browserAcceptance").GetString() == "user-confirmed" &&
                   afterClaudeDoc.RootElement.GetProperty("claudeLoginAndConversation").GetString() == "user-confirmed",
                "independent Claude login/conversation confirmation did not persist");

            // Manual AdsPower mode must also stay pending until user confirms.
            reloaded.Options.ManualAdsPower = true;
            var manualState = MacDeployment.DeployAsync(
                new SessionState
                {
                    VmId = reloaded.VmId,
                    Completed = [.. reloaded.Completed],
                    Options = reloaded.Options
                },
                new Secrets { VmPassword = "FictionalMacPass!1", SelectedNodeJson = fakeNodeJson },
                facts,
                new Progress<string>(_ => { }),
                CancellationToken.None,
                fakeBackend,
                fakeFirewall,
                resources,
                skipEndpointDnsResolveForTest: true).GetAwaiter().GetResult();
            Assert(manualState.NetworkPassed && !manualState.SetupFinished && !manualState.ApplicationAccepted &&
                   manualState.PendingActions.Any(a => a.Id == CustomerGuide.ManualAdsPowerId),
                "manual AdsPower mode must remain pending browser confirmation");
        });

        Check("mac-consent-and-iso-architecture-mismatch-guards", () =>
        {
            var root = Path.Combine(outputDirectory, "mac-guard-fixture");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Paths.EnsureWorkspace(root);
            var fakeIso = Path.Combine(outputDirectory, "mac-guard-win11.iso");
            using (var fs = File.Create(fakeIso)) fs.SetLength(1024L * 1024 * 1024 + 4096);
            var facts = MacHostEnvironment.ParseSysctlFacts(
                "arm64", "1", "0", "Apple M3", "15.1", 16L * 1024 * 1024 * 1024,
                "Pages free: 300000.\n", 8, "/Applications/VMware Fusion.app/Contents/Library/vmrun", null, false);

            var noLicense = new SessionState
            {
                Options = new()
                {
                    Root = root,
                    PlatformId = "fusion",
                    CpuCount = Math.Clamp(Environment.ProcessorCount, 2, 4),
                    MemoryMb = 8192,
                    DiskGb = 80,
                    IsoPath = fakeIso,
                    AcceptChanges = true,
                    AcceptWindowsLicense = false
                }
            };
            Reject(() => MacDeployment.DeployAsync(
                noLicense,
                new Secrets { VmPassword = "FictionalMacPass!1" },
                facts,
                new Progress<string>(_ => { }),
                CancellationToken.None,
                new MockMacBackend(),
                new MockMacFirewall()).GetAwaiter().GetResult(),
                "deployment allowed without Windows license acceptance");

            noLicense.Options.AcceptWindowsLicense = true;
            noLicense.Options.AcceptChanges = false;
            Reject(() => MacDeployment.DeployAsync(
                noLicense,
                new Secrets { VmPassword = "FictionalMacPass!1" },
                facts,
                new Progress<string>(_ => { }),
                CancellationToken.None,
                new MockMacBackend(),
                new MockMacFirewall()).GetAwaiter().GetResult(),
                "deployment allowed without directory/isolation consent");

            Assert(MacHostEnvironment.RequireMatchingIsoBootFiles(expectArm64: true, hasArm64: true, hasX64: false) == "arm64",
                "ARM64 ISO failed match");
            Assert(MacHostEnvironment.RequireMatchingIsoBootFiles(expectArm64: false, hasArm64: false, hasX64: true) == "x64",
                "x64 ISO failed match");
            Reject(() => MacHostEnvironment.RequireMatchingIsoBootFiles(expectArm64: true, hasArm64: false, hasX64: true),
                "x64 ISO wrongly accepted for Apple Silicon");
            Reject(() => MacHostEnvironment.RequireMatchingIsoBootFiles(expectArm64: false, hasArm64: true, hasX64: false),
                "ARM64 ISO wrongly accepted for Intel Mac");

            // Two same-arch ISOs (A -> B) AND in-place byte replacement at SAME path:
            // switching path OR replacing file content (SHA-256 change) must reset BOTH arch verification AND license consent.
            var isoA = Path.Combine(outputDirectory, "win11-arm64-a.iso");
            var isoB = Path.Combine(outputDirectory, "win11-arm64-b.iso");
            File.WriteAllText(isoA, "iso-image-content-A");
            File.WriteAllText(isoB, "iso-image-content-B");
            var shaA = MacDeployment.ComputeFileSha256Async(isoA, CancellationToken.None).GetAwaiter().GetResult();
            var shaB = MacDeployment.ComputeFileSha256Async(isoB, CancellationToken.None).GetAwaiter().GetResult();

            var guard = new MacDeployment.MacIsoLicenseGuard();
            guard.SelectIso(isoA);
            guard.SetChangesAccepted(true);
            guard.MarkIsoVerified(isoA, "arm64", shaA);
            guard.SetLicenseAccepted(true);
            guard.ValidateForDeploy(installWindows: true, shaA);

            // Scenario 1: Same path isoA overwritten with different bytes -> even without path change, deploy must reject and clear license/verification!
            File.WriteAllText(isoA, "iso-image-content-A-replaced-bytes");
            var shaAReplaced = MacDeployment.ComputeFileSha256Async(isoA, CancellationToken.None).GetAwaiter().GetResult();
            Assert(shaAReplaced != shaA, "in-place replacement did not change SHA-256");
            Reject(() => guard.ValidateForDeploy(installWindows: true, shaAReplaced),
                "deployment allowed after same-path ISO content changed");
            Assert(!guard.IsIsoVerifiedForCurrent && !guard.IsLicenseConfirmedForCurrent,
                "same-path ISO replacement did not clear verification and license consent");
            // Even after re-verifying architecture of replaced isoA, deploy stays blocked until license is re-confirmed!
            guard.MarkIsoVerified(isoA, "arm64", shaAReplaced);
            Assert(guard.IsIsoVerifiedForCurrent && !guard.IsLicenseConfirmedForCurrent,
                "re-verifying replaced ISO restored old license consent");
            Reject(() => guard.ValidateForDeploy(installWindows: true, shaAReplaced),
                "deployment allowed on replaced same-path ISO without re-confirming license");

            // Also verify DeployAsync rejects TOCTOU ISO byte swap when IsoSha256 is recorded.
            var toctouState = new SessionState
            {
                Options = new()
                {
                    Root = root,
                    PlatformId = "fusion",
                    CpuCount = Math.Clamp(Environment.ProcessorCount, 2, 4),
                    MemoryMb = 8192,
                    DiskGb = 80,
                    IsoPath = fakeIso,
                    IsoSha256 = shaA, // does not match fakeIso's actual bytes
                    AcceptChanges = true,
                    AcceptWindowsLicense = true
                }
            };
            Reject(() => MacDeployment.DeployAsync(
                toctouState,
                new Secrets { VmPassword = "FictionalMacPass!1" },
                facts,
                new Progress<string>(_ => { }),
                CancellationToken.None,
                new MockMacBackend(),
                new MockMacFirewall()).GetAwaiter().GetResult(),
                "DeployAsync allowed ISO whose on-disk SHA-256 diverged from IsoSha256");

            // Scenario 2: Switch to different path isoB and verify isoB arch -> must still block until license is re-confirmed for isoB.
            guard.SetLicenseAccepted(true);
            guard.ValidateForDeploy(installWindows: true, shaAReplaced);
            guard.SelectIso(isoB);
            Assert(!guard.IsIsoVerifiedForCurrent && !guard.IsLicenseConfirmedForCurrent,
                "switching ISO path did not reset prior verification or license consent");
            guard.MarkIsoVerified(isoB, "arm64", shaB);
            Assert(guard.IsIsoVerifiedForCurrent && !guard.IsLicenseConfirmedForCurrent,
                "verifying isoB wrongly restored license consent from isoA");
            Reject(() => guard.ValidateForDeploy(installWindows: true, shaB),
                "deployment allowed after switching to isoB without re-confirming license for isoB");

            guard.SetLicenseAccepted(true);
            guard.ValidateForDeploy(installWindows: true, shaB);

            // Scenario 3: Loading a saved task and switching ISO must also reset license confirmation.
            guard.LoadTask(new SetupOptions { IsoPath = isoA, AcceptChanges = true, AcceptWindowsLicense = true });
            Assert(!guard.IsLicenseConfirmedForCurrent && !guard.IsIsoVerifiedForCurrent,
                "loading existing task retained unverified ISO license state");
            guard.SelectIso(isoB);
            guard.MarkIsoVerified(isoB, "arm64", shaB);
            Reject(() => guard.ValidateForDeploy(installWindows: true, shaB),
                "loaded task allowed deploy after ISO switch without re-confirming license");

            // M03: Mid-deployment TOCTOU swap after entry validation must still abort before InstallWindows, AND backend InstallWindows rejects stale IsoSha256 directly.
            var isoMid = Path.Combine(outputDirectory, "win11-arm64-mid.iso");
            using (var fs = File.Create(isoMid)) fs.SetLength(1024L * 1024 * 1024 + 4096);
            var midRoot = Path.Combine(outputDirectory, "mac-mid-toctou");
            if (Directory.Exists(midRoot)) Directory.Delete(midRoot, recursive: true);
            Paths.EnsureWorkspace(midRoot);
            var midState = new SessionState
            {
                Options = new()
                {
                    Root = midRoot,
                    PlatformId = "fusion",
                    VmName = "MacMidToctouTest",
                    CpuCount = Math.Clamp(Environment.ProcessorCount, 2, 4),
                    MemoryMb = 8192,
                    DiskGb = 80,
                    IsoPath = isoMid,
                    NetworkSource = "节点链接 / Clash 配置文本",
                    Country = "US",
                    TimeZoneId = "Pacific Standard Time",
                    AcceptChanges = true,
                    AcceptWindowsLicense = true
                }
            };
            var midResources = MacProvisioningMedia.GuestResources(true);
            Directory.CreateDirectory(midState.Options.Cache);
            foreach (var r in midResources) File.WriteAllText(Paths.Under(midState.Options.Cache, r.FileName), "fixture");
            var tamperBackend = new MockMacBackend
            {
                OnConfigure = () =>
                {
                    using var f = new FileStream(isoMid, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    f.WriteByte(0xAB);
                }
            };
            try
            {
                MacDeployment.DeployAsync(
                    midState,
                    new Secrets { VmPassword = "FictionalMacPass!1", SelectedNodeJson = """{"name":"TestNode","type":"socks5","server":"1.1.1.1","port":443}""" },
                    facts,
                    new Progress<string>(_ => { }),
                    CancellationToken.None,
                    tamperBackend,
                    new MockMacFirewall(),
                    midResources,
                    skipEndpointDnsResolveForTest: true).GetAwaiter().GetResult();
                throw new Exception("DeployAsync proceeded to InstallWindows despite mid-deployment ISO tamper");
            }
            catch (SetupException ex) when (ex.Message.Contains("SHA-256", StringComparison.Ordinal)) { }
            Assert(tamperBackend.ConfigureCalls == 1 && tamperBackend.InstallCalls == 0,
                $"DeployAsync state mismatch after Configure tamper (configure={tamperBackend.ConfigureCalls}, install={tamperBackend.InstallCalls})");

            // Backend InstallWindows defense-in-depth rejects tampered ISO before mounting media.
            Reject(() => new FusionBackend(new NoOpRunner()).InstallWindows(midState, new Secrets { VmPassword = "FictionalMacPass!1" }, CancellationToken.None).GetAwaiter().GetResult(),
                "FusionBackend.InstallWindows mounted tampered ISO");
            Reject(() => new UtmBackend(new NoOpRunner()).InstallWindows(midState, new Secrets { VmPassword = "FictionalMacPass!1" }, CancellationToken.None).GetAwaiter().GetResult(),
                "UtmBackend.InstallWindows mounted tampered ISO");

            // M03 mid-seeding TOCTOU: ISO modified AFTER entry hash check DURING EnsureSeedIsoAsync MUST abort BEFORE .vmx OR UTM config.plist is created!
            foreach (var platformId in new[] { "fusion", "utm" })
            {
                var isoSeed = Path.Combine(outputDirectory, $"win11-arm64-seed-{platformId}.iso");
                using (var fs = File.Create(isoSeed)) fs.SetLength(1024L * 1024 * 1024 + 4096);
                var expectedSeedSha = MacDeployment.ComputeFileSha256Async(isoSeed, CancellationToken.None).GetAwaiter().GetResult();
                var seedRoot = Path.Combine(outputDirectory, $"mac-seed-tamper-{platformId}");
                if (Directory.Exists(seedRoot)) Directory.Delete(seedRoot, true);
                Paths.EnsureWorkspace(seedRoot);
                var seedState = new SessionState
                {
                    VmId = Guid.NewGuid().ToString(),
                    Options = new()
                    {
                        Root = seedRoot,
                        PlatformId = platformId,
                        VmName = $"MacSeedTamper_{platformId}",
                        CpuCount = Math.Clamp(Environment.ProcessorCount, 2, 4),
                        MemoryMb = 8192,
                        DiskGb = 80,
                        IsoPath = isoSeed,
                        IsoSha256 = expectedSeedSha,
                        Country = "US",
                        TimeZoneId = "Pacific Standard Time",
                        AcceptChanges = true,
                        AcceptWindowsLicense = true
                    }
                };
                var prevHostEnv = HostPlatform.Environment;
                try
                {
                    HostPlatform.Environment = new SeedingTamperHostEnv(prevHostEnv, isoSeed);
                    IVmBackend backend = platformId == "fusion" ? new FusionBackend(new NoOpRunner()) : new UtmBackend(new NoOpRunner());
                    try
                    {
                        backend.InstallWindows(seedState, new Secrets { VmPassword = "FictionalMacPass!1" }, CancellationToken.None).GetAwaiter().GetResult();
                        throw new Exception($"{platformId} InstallWindows emitted VM config despite mid-seeding ISO tamper");
                    }
                    catch (SetupException ex) when (ex.Message.Contains("SHA-256", StringComparison.Ordinal)) { }

                    var vmxPath = Path.Combine(seedState.Options.VmDirectory, $"{seedState.Options.VmName}.vmx");
                    var utmPlistPath = Path.Combine(seedState.Options.VmDirectory, $"{seedState.Options.VmName}.utm", "config.plist");
                    Assert(!File.Exists(vmxPath) && !File.Exists(utmPlistPath),
                        $"{platformId} emitted unverified config file referencing tampered ISO");
                }
                finally
                {
                    HostPlatform.Environment = prevHostEnv;
                }
            }
        });

        Check("mac-storage-cleanup-unified-rules-and-platform-honesty", () =>
        {
            var caseDir = Path.Combine(outputDirectory, "mac-storage-cleanup-contract");
            if (Directory.Exists(caseDir)) Directory.Delete(caseDir, true);
            var settingsPath = Path.Combine(caseDir, "settings.json");
            var regPath = Path.Combine(caseDir, "registry.json");
            var wsDir = Path.Combine(caseDir, "ws");
            Directory.CreateDirectory(wsDir);
            new CleanupSettings { AutoCleanRouteConfig = true }.Save(settingsPath);

            var rev1 = Guid.NewGuid().ToString("N");
            var rev2 = Guid.NewGuid().ToString("N");
            var rev3 = Guid.NewGuid().ToString("N");
            var state = new SessionState
            {
                VmId = Guid.NewGuid().ToString(),
                Options = new() { Root = wsDir, PlatformId = "fusion", VmName = "MacCleanBox", Remember = true }
            };
            Directory.CreateDirectory(state.Options.Private);
            File.WriteAllText(Path.Combine(state.Options.Private, $"credentials-{rev1}.mac"), "r1");
            File.WriteAllText(Path.Combine(state.Options.Private, $"credentials-{rev2}.mac"), "r2");
            File.WriteAllText(Path.Combine(state.Options.Private, $"credentials-{rev3}.mac"), "r3");

            var prev = new SessionState
            {
                VmId = state.VmId,
                CredentialsRevision = rev2,
                Options = new() { Root = wsDir, PlatformId = "fusion", VmName = "MacCleanBox", WorkspaceId = state.Options.WorkspaceId, Remember = true }
            };
            state.CredentialsRevision = rev3;
            TaskCheckpointStore.Write(prev, settingsPath, regPath);
            TaskCheckpointStore.Write(state, settingsPath, regPath);
            new KeychainSecretStore(new NoOpRunner()).PruneRevisions(state, prev, settingsPath, regPath);

            Assert(!File.Exists(Path.Combine(state.Options.Private, $"credentials-{rev1}.mac")) &&
                   File.Exists(Path.Combine(state.Options.Private, $"credentials-{rev2}.mac")) &&
                   File.Exists(Path.Combine(state.Options.Private, $"credentials-{rev3}.mac")),
                "Mac KeychainSecretStore rotation failed to clean rev1 while keeping rev2 and rev3");

            var report = StorageCleanupService.Inspect(new StorageCleanupContext("mac", caseDir, caseDir, [wsDir], settingsPath, regPath));
            var macUpdateCat = report.Categories.Single(c => c.Category == BackupCategory.ProgramUpdate);
            Assert(macUpdateCat.PlatformNote.Contains("Mac", StringComparison.Ordinal),
                "Mac storage overview did not report honest ProgramUpdate platform scope");

            // B04 on Mac: corrupt BOTH setup-state.json and .previous.json -> ALL remaining .mac credentials MUST be Pending & unselectable!
            File.WriteAllText(state.Options.StatePath, "{corrupt");
            File.WriteAllText(TaskCheckpointStore.BackupPath(state.Options.StatePath), "{\"schema\":1,\"options\":null}");
            var corruptRep = StorageCleanupService.Inspect(new StorageCleanupContext("mac", caseDir, caseDir, [wsDir], settingsPath, regPath));
            var macCreds = corruptRep.Items.Where(i => i.Path.EndsWith(".mac", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert(macCreds.Count == 2 && macCreds.All(i => i.Status == CleanupItemStatus.Pending && !i.CanSelect),
                "Mac B04 dual-corrupt state left .mac credential selectable");
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

    private sealed class SeedingTamperHostEnv(IHostEnvironment inner, string isoPathToTamper) : IHostEnvironment
    {
        private bool mutated;
        public bool IsHostHypervisorActive => inner.IsHostHypervisorActive;
        public string HostEdition => inner.HostEdition;
        public bool IsHomeEdition(string? edition) => inner.IsHomeEdition(edition);
        public void RequireWindowsIsoFile(string isoPath) => inner.RequireWindowsIsoFile(isoPath);
        public void RestrictPrivateDirectory(string path)
        {
            inner.RestrictPrivateDirectory(path);
            if (!mutated && Path.GetFileName(path).Equals("mac-seed", StringComparison.OrdinalIgnoreCase))
            {
                mutated = true;
                using var f = new FileStream(isoPathToTamper, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                f.WriteByte(0xCD);
            }
        }
    }

    private sealed class NoOpRunner : ICommandRunner
    {
        public Task<string> Run(string fileName, IEnumerable<string> arguments, CancellationToken token, int seconds = 60) =>
            Task.FromResult("");
    }

    private sealed class StatefulPfStubRunner(
        string initialMainRules,
        bool pfEnabled,
        string? initialAnchorRules = null,
        string? initialAnchorName = null,
        string failStep = "") : ICommandRunner
    {
        public string MainRules { get; private set; } = initialMainRules;
        public bool PfEnabled { get; private set; } = pfEnabled;
        public bool MainReloadAttempted { get; private set; }
        public Dictionary<string, string> Anchors { get; } = initialAnchorName is not null && initialAnchorRules is not null
            ? new Dictionary<string, string> { [initialAnchorName] = initialAnchorRules }
            : [];

        public Task<string> Run(string fileName, IEnumerable<string> arguments, CancellationToken token, int seconds = 60)
        {
            var argsList = arguments.ToArray();
            var match = System.Text.RegularExpressions.Regex.Match(argsList.ElementAtOrDefault(1) ?? "", @"/bin/sh '([^']+)'");
            if (!match.Success) throw new SetupException("Missing shell script path");
            var script = File.ReadAllText(match.Groups[1].Value);

            var origMainPath = ExtractQuotedPath(script, @"if ! pfctl -sr > '([^']+)'");
            if (failStep == "read-main" && !script.Contains("===PF_RESTORE_INFO===", StringComparison.Ordinal))
                throw new SetupException("PF_READ_MAIN_FAILED");

            if (script.Contains("===PF_RESTORE_INFO===", StringComparison.Ordinal))
            {
                var restoreAnchorMatch = System.Text.RegularExpressions.Regex.Match(script, @"pfctl -a '([^']+)' -f '([^']+)'");
                var restoreAnchor = restoreAnchorMatch.Groups[1].Value;
                var restoreAnchorFile = restoreAnchorMatch.Groups[2].Value;
                var restoreMainFile = ExtractQuotedPath(script, @"pfctl -Rf '([^']+)'");
                var restoredChild = File.Exists(restoreAnchorFile) ? File.ReadAllText(restoreAnchorFile) : "";
                var hadOrig = script.Contains("if [ \"1\" -eq 1 ] || [ -s '", StringComparison.Ordinal);
                if (hadOrig || restoredChild.Length > 0)
                    Anchors[restoreAnchor] = restoredChild;
                else
                    Anchors.Remove(restoreAnchor);
                MainRules = File.Exists(restoreMainFile) ? File.ReadAllText(restoreMainFile) : MainRules;
                if (script.Contains("if [ \"1\" -eq 1 ]; then", StringComparison.Ordinal) &&
                    !System.Text.RegularExpressions.Regex.IsMatch(MainRules, @"^anchor\s+""?claudome-", System.Text.RegularExpressions.RegexOptions.Multiline))
                    PfEnabled = false;
                var restoreInfo = PfEnabled ? "Status: Enabled for 0 days\r" : "Status: Disabled\r";
                return Task.FromResult($"===PF_RESTORE_INFO===\r{restoreInfo}===PF_RESTORE_MAIN===\r{MainRules.Replace("\n", "\r")}");
            }

            var isApply = script.Contains("===PF_INFO===", StringComparison.Ordinal);
            if (isApply)
            {
                var anchorMatch = System.Text.RegularExpressions.Regex.Match(script, @"if ! pfctl -a '([^']+)' -f '([^']+)'");
                var anchor = anchorMatch.Groups[1].Value;
                var tempRulesPath = anchorMatch.Groups[2].Value;
                var origAnchorPath = ExtractQuotedPath(script, @"if ! pfctl -a '[^']+' -sr > '([^']+)'");
                if (string.IsNullOrEmpty(origAnchorPath))
                    origAnchorPath = ExtractQuotedPath(script, @"pfctl -a '[^']+' -sr > '([^']+)'");
                var mainTempPath = ExtractQuotedPath(script, @"if ! pfctl -Rf '([^']+)'");
                var origAnchorSnapshot = Anchors.TryGetValue(anchor, out var priorChild) ? priorChild : null;
                var origMainSnapshot = MainRules;

                // Mirror shell script's pre-mutation check when anchor already exists in main ruleset.
                if (failStep == "read-anchor" && origMainSnapshot.Contains(anchor, StringComparison.Ordinal))
                {
                    if (!script.Contains("PF_READ_ANCHOR_FAILED", StringComparison.Ordinal) ||
                        !script.Contains("had_orig_anchor=1", StringComparison.Ordinal))
                        throw new SetupException("Apply script missing PF_READ_ANCHOR_FAILED guard");
                    throw new SetupException("PF_READ_ANCHOR_FAILED");
                }

                var newChildRules = File.ReadAllText(tempRulesPath);
                var newMainRules = MacGatewayFirewall.BuildMainRulesetWithAnchor(origMainSnapshot, anchor);
                File.WriteAllText(origMainPath, origMainSnapshot);
                if (!string.IsNullOrEmpty(origAnchorPath)) File.WriteAllText(origAnchorPath, origAnchorSnapshot ?? "");
                File.WriteAllText(mainTempPath, newMainRules);

                // Step 1: load child rules
                Anchors[anchor] = newChildRules;
                // Step 2: reload main filter ruleset
                MainReloadAttempted = true;
                if (failStep == "apply-main")
                {
                    RestoreApply(anchor, origAnchorSnapshot, origMainSnapshot);
                    throw new SetupException("PF_APPLY_MAIN_FAILED (rolled back)");
                }
                MainRules = newMainRules;
                // Step 3: enable PF if needed
                var pfEnabledBefore = PfEnabled;
                if (!PfEnabled)
                {
                    if (failStep == "enable")
                    {
                        RestoreApply(anchor, origAnchorSnapshot, origMainSnapshot);
                        throw new SetupException("PF_ENABLE_FAILED (rolled back)");
                    }
                    PfEnabled = true;
                }
                // Step 4: final readback failure after pfctl -e must roll back AND disable PF back to pfEnabledBefore.
                if (failStep == "readback")
                {
                    if (!script.Contains("pf_enabled_by_us=1", StringComparison.Ordinal) ||
                        !script.Contains("pfctl -d", StringComparison.Ordinal) ||
                        !script.Contains("exit 27", StringComparison.Ordinal))
                        throw new SetupException("Apply script missing pf_enabled_by_us / pfctl -d readback rollback");
                    RestoreApply(anchor, origAnchorSnapshot, origMainSnapshot);
                    PfEnabled = pfEnabledBefore;
                    throw new SetupException("PF_READBACK_FAILED (PF_APPLY_ROLLED_BACK)");
                }
                var pfEnabledByUsFlag = (!pfEnabledBefore && PfEnabled) ? "1" : "0";
                var hadOrigFlag = origMainSnapshot.Contains(anchor, StringComparison.Ordinal) ? "1" : "0";
                var infoOut = (failStep == "verify-mismatch" || !PfEnabled) ? "Status: Disabled\r" : "Status: Enabled for 0 days\r";
                var anchorOut = failStep == "verify-bad-anchor" ? "# invalid readback missing block drop\r" : Anchors[anchor].Replace("\n", "\r");
                return Task.FromResult(
                    $"===PF_PRE_STATE===\rpf_enabled_by_us={pfEnabledByUsFlag}\rhad_orig_anchor={hadOrigFlag}\r" +
                    $"===PF_INFO===\r{infoOut}===PF_MAIN===\r{MainRules.Replace("\n", "\r")}===PF_ANCHOR===\r{anchorOut}");
            }
            else
            {
                var anchorMatch = System.Text.RegularExpressions.Regex.Match(script, @"if ! pfctl -a '([^']+)' -F all");
                var anchor = anchorMatch.Groups[1].Value;
                var origMainSnapshot = MainRules;
                var clearedMainRules = MacGatewayFirewall.RemoveAnchorFromMainRuleset(origMainSnapshot, anchor);

                // Step 1: unlink anchor from main ruleset first
                MainReloadAttempted = true;
                if (failStep == "clear-main")
                    throw new SetupException("PF_CLEAR_MAIN_FAILED_KEPT_ANCHOR");
                MainRules = clearedMainRules;

                // Step 2: flush child anchor rules; on failure restore orig main ruleset
                if (failStep == "clear-flush")
                {
                    MainRules = origMainSnapshot;
                    throw new SetupException("PF_CLEAR_ANCHOR_FAILED_RESTORED_MAIN");
                }
                Anchors.Remove(anchor);
                return Task.FromResult("");
            }
        }

        private void RestoreApply(string anchor, string? origChild, string origMain)
        {
            if (origChild is null) Anchors.Remove(anchor);
            else Anchors[anchor] = origChild;
            MainRules = origMain;
        }

        private static string ExtractQuotedPath(string script, string pattern)
        {
            var m = System.Text.RegularExpressions.Regex.Match(script, pattern);
            return m.Success ? m.Groups[1].Value : "";
        }
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
        public Action? OnConfigure { get; init; }
        public string LastPayloadJson { get; private set; } = "";
        public HashSet<string> CopiedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task Prepare(SetupOptions options, CancellationToken token) => Task.CompletedTask;
        public Task<string> EnsureMachine(SessionState state, CancellationToken token)
        {
            state.VmId = string.IsNullOrEmpty(state.VmId) ? Guid.NewGuid().ToString() : state.VmId;
            return Task.FromResult(state.VmId);
        }
        public Task Configure(SessionState state, CancellationToken token) { ConfigureCalls++; OnConfigure?.Invoke(); return Task.CompletedTask; }
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
            var fileName = destination.Split('\\', '/').Last();
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
