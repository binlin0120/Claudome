using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeSetup;

public static class SelfTests
{
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message) { var rejected = false; try { action(); } catch (SetupException) { rejected = true; } Assert(rejected, message); }
    private static async Task RejectAsync(Func<Task> action, string message) { var rejected = false; try { await action(); } catch (SetupException) { rejected = true; } Assert(rejected, message); }
    public static async Task<int> Run(string output)
    {
        Directory.CreateDirectory(output);
        var root = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var rows = new List<object>(); var failed = 0;
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); rows.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, passed = false, errorType = ex.GetType().Name, detail = ex.Message }); }
        }
        Task Sync(Action test) { test(); return Task.CompletedTask; }
        await TransportTests.Run(Check, root);
        await MediaTests.Run(Check,root);
        await ReleaseReviewTests.Run(Check, root);
        await RecoveryTests.Run(Check, root);
        await CleanupProgressTests.Run(Check, root);
        await AlignmentTests.Run(Check, root);
        await ExperienceFixTests.Run(Check,root);
        await CommandFailureTests.Run(Check,root);
        await FirewallFailureTests.Run(Check);
        await ResolverTests.Run(Check);
        await FirewallRegistrationTests.Run(Check);
        await FirewallAddressTests.Run(Check);
        await DesktopRecoveryTests.Run(Check,root);
        await BootstrapTests.Run(Check,root);
        await NetworkSniTests.Run(Check,root);
        await Check("clipboard-transfers-unicode-multiline-and-checks-operation-identity", () => Sync(() => {
            const string text = "汉字 / English / 😀\r\n第二行\t!@#\n";
            const string runId = "c10b2def939a4a3480262dff0e95cb4c";
            var report = Path.Combine(root, "fictional-clipboard.json");
            JsonFile.Write(report, new { runId, status = "ok", text });
            Assert(ClipboardTransfer.ReadResult(report, runId, false) == text, "Unicode or original line endings changed");
            Assert(ClipboardTransfer.ReadResult(report, runId, true).Length == 0, "Send echoed clipboard text");
            Reject(() => ClipboardTransfer.ReadResult(report, Guid.NewGuid().ToString("N"), false), "Clipboard report from another operation was accepted");
            JsonFile.Write(report, new { runId, status = "no-desktop", text });
            Reject(() => ClipboardTransfer.ReadResult(report, runId, false), "A guest without an interactive session was accepted");
            Reject(() => ClipboardTransfer.RequireText("x\0y"), "A NUL-containing string was accepted");
            Reject(() => ClipboardTransfer.RequireText(new string('x', ClipboardTransfer.TextLimit + 1)), "Oversize text was accepted");
            File.WriteAllBytes(report, new byte[ClipboardTransfer.ReportLimit + 1]);
            Reject(() => ClipboardTransfer.ReadResult(report, runId, false), "Oversize clipboard result was accepted");
        }));
        await Check("vbox-live-video-mode-fits-without-stretching-or-overflow", () => Sync(() => {
            var guest = VirtualBoxDisplayFit.ReadGuestSize("VMState=\"running\"\r\nVideoMode=\"1024,768,32\"@0,0 1\r\n");
            Assert(guest == new ConsolePixelSize(1024, 768), "real VirtualBox machine-readable mode shape was not recognized");
            Assert(VirtualBoxDisplayFit.ReadGuestSize("VideoMode=\"0,0,0\"@0,0 0") is null, "boot transition fabricated a desktop size");
            foreach (var (viewport, size) in new[] {
                (new ConsolePixelSize(812, 607), guest!.Value),
                (new ConsolePixelSize(1015, 759), guest.Value),
                (new ConsolePixelSize(600, 800), new ConsolePixelSize(1920, 1080)),
                (new ConsolePixelSize(1800, 1012), new ConsolePixelSize(3840, 2160))
            }) {
                var fit = VirtualBoxDisplayFit.Calculate(viewport, size)!.Value;
                var scale = double.Parse(fit.ScaleFactor, System.Globalization.CultureInfo.InvariantCulture);
                Assert(size.Width * scale <= viewport.Width - 60 && size.Height * scale <= viewport.Height - 60,
                    "guest edges would crop inside the Qt frame");
                Assert(Math.Min(viewport.Width - 60 - size.Width * scale, viewport.Height - 60 - size.Height * scale) < 6,
                    "guest did not use the limiting viewport dimension");
            }
        }));
        await Check("vbox-fit-corrects-the-observed-native-scroll-frame-without-oscillating", () => Sync(() => {
            var observed = NativeConsoleHost.FindScrollingContent(
                [new System.Windows.Rect(16, 39, 642, 455), new System.Windows.Rect(658, 39, 12, 455),
                 new System.Windows.Rect(16, 494, 642, 12)], 430, 329);
            Assert(observed == new ConsolePixelSize(642, 455), "native scrollbar layout not recognized");
            var viewport = new ConsolePixelSize(670, 506);
            var guest = new ConsolePixelSize(1024, 768);
            var fit = VirtualBoxDisplayFit.Calculate(viewport, guest, observed)!.Value;
            var scale = double.Parse(fit.ScaleFactor, System.Globalization.CultureInfo.InvariantCulture);
            Assert(768 * scale <= 447, "scaled display still extends below the real inner viewport");
            Assert(VirtualBoxDisplayFit.Calculate(viewport, guest, previous: fit) == fit, "scale expanded when rails disappeared");
            Assert(VirtualBoxDisplayFit.Calculate(new(1024, 768), guest, previous: fit)!.Value.ScaleFactor != fit.ScaleFactor,
                "resize did not reconsider the earlier limit");
        }));
        await Check("vbox-only-wakes-the-running-guest-when-its-monitor-is-blank", async () => {
            const string id = "11111111-1111-1111-1111-111111111111";
            const string blank = "VMState=\"running\"\r\nVideoMode=\"1600,900,0\"@0,0 2\r\n";
            var runner = new FakeRunner(_ => "");
            await using var box = new VirtualBox(runner, "fixture-vbox");
            foreach (var other in new[] {
                blank.Replace("running", "paused"),
                blank.Replace("0\"@0,0 2", "32\"@0,0 1"),
                blank.Replace(" 2\r\n", " 0\r\n"),
                "VMState=\"running\"\r\n"
            })
                await VirtualBoxDisplayFit.WakeBlankGuest(box, id, other, CancellationToken.None);
            await VirtualBoxDisplayFit.WakeBlankGuest(box, "invalid", blank, CancellationToken.None);
            Assert(runner.Commands.Count == 0, "A paused, disabled, active or unknown display received input.");
            await VirtualBoxDisplayFit.WakeBlankGuest(box, id, blank, CancellationToken.None);
            Assert(runner.Commands.Single().SequenceEqual(["controlvm", id, "keyboardputscancode", "2a", "aa"]),
                "The blank guest did not receive one scoped Shift press and release.");
            Assert(VirtualBoxDisplayFit.ReadGuestSize(blank) is null, "Blank monitor was treated as a valid framebuffer.");
        });
        await Check("bootstrap-transport-retries-are-bounded-and-only-for-transient-failures", async () => {
            var attempts = 0;
            var retries = new List<int>();
            await ExternalGateway.RetryBootstrapTransport(_ =>
            {
                if (++attempts < 3) throw new BootstrapProbeException("BOOTSTRAP_DNS_TRANSPORT_FAILED");
                return Task.CompletedTask;
            }, retries.Add, CancellationToken.None);
            Assert(attempts == 3 && retries.SequenceEqual([2, 3]), "the same-node reconnect did not recover after transient failures");
            attempts = 0;
            await RejectAsync(() => ExternalGateway.RetryBootstrapTransport(_ =>
            {
                attempts++;
                throw new BootstrapProbeException("BOOTSTRAP_DNS_TLS_FAILED");
            }, retries.Add, CancellationToken.None), "invalid TLS passed");
            Assert(attempts == 1 && retries.Count == 2, "certificate error wrongly retried");
            using var cancelled = new CancellationTokenSource();
            try
            {
                await ExternalGateway.RetryBootstrapTransport(_ => throw new BootstrapProbeException("BOOTSTRAP_TIMEOUT"),
                    _ => cancelled.Cancel(), cancelled.Token);
                throw new Exception("retry ignored pause");
            }
            catch (OperationCanceledException) { }
        });
        await Check("vbox-display-settings-are-task-scoped-idempotent-and-read-back", async () => {
            const string id = "11111111-1111-1111-1111-111111111111";
            var values = new Dictionary<string, string>();
            var runner = new FakeRunner(args => {
                Assert(args[1] == id && args[0] is "getextradata" or "setextradata", "display changes escaped the owned VM");
                if (args[0] == "setextradata") { values[args[2]] = args[3]; return ""; }
                return values.TryGetValue(args[2], out var value) ? "Value: " + value + "\r\n" : "No value set!";
            });
            var vbox = new VirtualBox(runner, "fixture-vbox");
            var layout = VirtualBoxDisplayFit.Calculate(new(812, 607), new(1024, 768))!.Value;
            await VirtualBoxDisplayFit.Prepare(vbox, id, CancellationToken.None);
            await VirtualBoxDisplayFit.Apply(vbox, id, layout, CancellationToken.None);
            await VirtualBoxDisplayFit.Prepare(vbox, id, CancellationToken.None);
            Assert(values[VirtualBoxDisplayFit.MenuBar] == "false" && values[VirtualBoxDisplayFit.StatusBar] == "false"
                && values[VirtualBoxDisplayFit.Scale] == layout.ScaleFactor
                && values[VirtualBoxDisplayFit.AutoResize] == "false"
                && runner.Commands.Count(a => a[0] == "setextradata") == 4, "verified settings were not applied once");
            var ignoring = new VirtualBox(new FakeRunner(_ => "No value set!"), "fixture-vbox");
            await RejectAsync(() => VirtualBoxDisplayFit.Prepare(ignoring, id, CancellationToken.None), "missing display readback was accepted");
        });
        await Check("vbox-fit-scales-the-confirmed-frame-without-feeding-guest-resizes-back", async () => {
            const string id = "11111111-1111-1111-1111-111111111111";
            var viewport = new ConsolePixelSize(1180, 740);
            var mode = new ConsolePixelSize(1649, 1200);
            var settings = new Dictionary<string, string>();
            var fake = new FakeRunner(args => {
                Assert(args[1] == id, "Display adjustment targeted a different VM");
                if (args[0] == "showvminfo") return $"GuestAdditionsRunLevel=3\nVideoMode=\"{mode.Width},{mode.Height},32\"@0,0 1\n";
                if (args[0] == "getextradata") return settings.TryGetValue(args[2], out var value) ? "Value: " + value : "No value set!";
                if (args[0] == "setextradata") { settings[args[2]] = args[3]; return ""; }
                throw new Exception("Display fit tried to change the guest resolution.");
            });
            await using var vbox = new VirtualBox(fake, "fixture-vbox");
            var fit = await VirtualBoxDisplayFit.Fit(vbox, id, viewport, null, null, CancellationToken.None);
            Assert(fit?.Guest == mode && fit?.ScaleFactor == settings[VirtualBoxDisplayFit.Scale], "Fit did not use the reported frame.");
            var commandsBefore = fake.Commands.Count;
            mode = new ConsolePixelSize(1651, 1200);
            var settled = await VirtualBoxDisplayFit.Fit(vbox, id, viewport, new(1122, 683), fit, CancellationToken.None);
            Assert(settled == fit && fake.Commands.Count == commandsBefore, "Transient guest resize fed back into scaling.");
            var smallerViewport = new ConsolePixelSize(920, 650);
            var fallback = await VirtualBoxDisplayFit.Fit(vbox, id, smallerViewport, null, fit, CancellationToken.None);
            Assert(fallback?.Guest == mode && fallback?.ScaleFactor != fit?.ScaleFactor && fallback?.ScaleFactor == settings[VirtualBoxDisplayFit.Scale],
                "Changing the host viewport did not scale the guest frame.");
            Assert(VirtualBoxDisplayFit.ReadGuestSize("VideoMode=\"1649,1200,0\"@0,0 0") is null, "Mode transition frame was accepted.");
        });
        await Check("vbox-fit-adapts-both-window-shapes-and-uses-the-mode-readback", async () => {
            const string id = "11111111-1111-1111-1111-111111111111";
            var wide = new ConsolePixelSize(1570, 809);
            var restored = new ConsolePixelSize(950, 630);
            var reserve = new ConsolePixelSize(24, 47);
            var initial = new ConsolePixelSize(1649, 1200);
            foreach (var acceptsMode in new[] { true, false })
            {
                var mode = initial;
                var scaleSetting = "";
                var hints = new List<ConsolePixelSize>();
                var fake = new FakeRunner(args =>
                {
                    Assert(args[1] == id, "Display hint targeted another VM");
                    if (args[0] == "showvminfo") return $"GuestAdditionsRunLevel=3\nVideoMode=\"{mode.Width},{mode.Height},32\"@0,0 1\n";
                    if (args[0] == "getextradata") return "Value: " + scaleSetting;
                    if (args[0] == "setextradata") { scaleSetting = args[3]; return ""; }
                    Assert(args is ["controlvm", _, "setvideomodehint", _, _, "32"], "Unexpected display command");
                    var hint = new ConsolePixelSize(int.Parse(args[3]), int.Parse(args[4]));
                    hints.Add(hint);
                    if (acceptsMode) mode = hint;
                    return "";
                });
                await using var box = new VirtualBox(fake, "fixture-vbox");
                var target = VirtualBoxDisplayFit.ViewportModeTarget(wide, initial, reserve);
                Assert(target.HasValue, "Wide panel did not suggest a compatible mode");
                var fit = await VirtualBoxDisplayFit.Fit(box, id, wide, null, null, CancellationToken.None,
                    adaptRatio: true, frameAllowance: reserve);
                Assert(fit?.Guest == (acceptsMode ? target : initial) && fit?.LastCheckedAspect is not null &&
                    hints.SequenceEqual([target!.Value]), "Fit did not use the confirmed mode after its first hint");
                var checkedAspect = fit!.Value.LastCheckedAspect;
                foreach (var width in new[] { 1548, 1526, 1504 })
                    fit = await VirtualBoxDisplayFit.Fit(box, id, new(width, 809), null, fit, CancellationToken.None,
                        adaptRatio: true, frameAllowance: reserve);
                Assert(fit?.LastCheckedAspect != checkedAspect, "Gradual changes never accumulated into a new aspect check");

                target = VirtualBoxDisplayFit.ViewportModeTarget(restored, mode, reserve);
                Assert(target.HasValue, "Restored window did not suggest a compatible mode");
                var previousCount = hints.Count;
                fit = await VirtualBoxDisplayFit.Fit(box, id, restored, null, fit, CancellationToken.None,
                    adaptRatio: true, frameAllowance: reserve);
                Assert(hints.Count == previousCount + 1 && hints[^1] == target &&
                    fit?.Guest == (acceptsMode ? target : initial), "Restored window did not use the observed display mode");
                if (acceptsMode)
                {
                    var frame = VirtualBoxDisplayFit.ScaledGuestSize(fit!.Value);
                    Assert(Math.Abs(restored.Width - reserve.Width - frame.Width) < 8 &&
                        Math.Abs(restored.Height - reserve.Height - frame.Height) < 8,
                        "The restored window left large bars around the guest frame.");
                }
                previousCount = hints.Count;
                await VirtualBoxDisplayFit.Fit(box, id, new(952, 632), null, fit, CancellationToken.None,
                    adaptRatio: true, frameAllowance: reserve);
                Assert(hints.Count == previousCount, "Minor layout jitter issued another resolution hint");
            }
        });
        await Check("vbox-selects-the-rounded-render-surface-instead-of-the-white-qt-panel", () => Sync(() => {
            var render = new System.Windows.Rect(21, 9, 889, 500);
            var outer = new System.Windows.Rect(0, 0, 946, 557);
            var panel = new System.Windows.Rect(8, 0, 930, 518);
            var expected = VirtualBoxDisplayFit.ScaledGuestSize(new("0.556", new(950, 630), new(1600, 900)));
            Assert(expected.Width == 890 && NativeConsoleHost.FindGuestFrameViewport([panel, render], outer, expected) == render,
                "The rounded-down framebuffer was rejected in favor of its wider panel.");
            Assert(NativeConsoleHost.FindGuestFrameViewport([panel, new(21, 9, 889, 478)], outer, expected) is null,
                "A partial guest surface or outer panel was mistaken for the whole desktop.");
        }));
        await Check("bundled-guest-bridge-scripts-and-third-party-notices-present",()=>Sync(()=>{
            var bridge=Catalog.EmbeddedBytes("ClaudomeGuestNetwork.exe");Assert(bridge.Length>1000000&&bridge[0]==0x4d&&bridge[1]==0x5a,"guest helper was omitted or invalid");
            foreach(var name in new[]{"InteractiveRun.ps1","BridgeInstall.ps1","HyperVWorker.ps1","Configure.ps1","Rollback.ps1"}) Assert(Catalog.EmbeddedText(name).StartsWith("#requires -Version 7.0"),"required guest script missing: "+name);
            var notices=Catalog.EmbeddedText("Gateway-THIRD-PARTY-NOTICES.txt");foreach(var name in new[]{"Wintun","YamlDotNet","Microsoft.WindowsDesktop.App.Runtime.win-x64","Microsoft.NETCore.App.Runtime.win-x64"})Assert(notices.Contains(name),"bundled dependency license missing: "+name);
        }));
        const string SharingInfo = "clipboard=\"disabled\"\ndraganddrop=\"disabled\"\nclipboard_file_transfers=\"off\"\nusb=\"off\"\nehci=\"off\"\nxhci=\"off\"\nvrde=\"off\"\n";
        SessionState OwnedVm(string folder) => new() { VmId = "11111111-1111-1111-1111-111111111111", Options = new() { Root = Path.Combine(root, folder) } };
        string VmInfo(SessionState state, string mode, bool offline) =>
            SharingInfo + $"UUID=\"{state.VmId}\"\nVMState=\"{mode}\"\nCfgFile=\"{Path.Combine(state.Options.VmDirectory, state.Options.VmName + ".vbox").Replace("\\", "\\\\")}\"\n" +
            (offline ? "nic1=\"none\"\n" : "nic1=\"generic\"\ngeneric1=\"UDPTunnel\"\nmacaddress1=\"021122334455\"\ncableconnected1=\"on\"\n") + string.Concat(Enumerable.Range(2, 7).Select(i => $"nic{i}=\"none\"\n"));
        await Check("vbox-shutdown-requests-one-acpi-event-and-waits-for-poweroff", async () => {
            foreach (var (initial, acknowledges) in new[] { ("running", true), ("running", false), ("stopping", true), ("poweroff", true), ("saved", false) })
            {
                var state = OwnedVm("shutdown-" + initial);
                var signaled = 0;
                var reads = 0;
                var fake = new FakeRunner(args =>
                {
                    if (args[0] == "list") return $"\"{state.Options.VmName}\" {{{state.VmId}}}";
                    Assert(args[1] == state.VmId, "Shutdown command targeted a different VM");
                    if (args[0] == "showvminfo")
                    {
                        var transitions = acknowledges && (signaled > 0 || initial == "stopping");
                        return VmInfo(state, transitions && ++reads >= 3 ? "poweroff" : initial, true);
                    }
                    Assert(args.SequenceEqual(["controlvm", state.VmId, "acpipowerbutton"]), "A forced or unrelated command was used");
                    signaled++;
                    return "";
                });
                await using var backend = new VirtualBox(fake, "fixture-vbox");
                if (acknowledges)
                    await backend.ShutdownOriginal(state, CancellationToken.None, attempts: 5, intervalMilliseconds: 1);
                else await RejectAsync(() => backend.ShutdownOriginal(state, CancellationToken.None, attempts: 3,
                    intervalMilliseconds: 1), "An unconfirmed shutdown was reported successful");
                Assert(signaled == (initial == "running" ? 1 : 0), "An additional ACPI event or a fixed-state mutation was issued");
            }
            var invalid = OwnedVm("shutdown-invalid");
            invalid.VmId = "";
            var noCommands = new FakeRunner(_ => throw new Exception("Empty VM identity ran a command"));
            await using var rejected = new VirtualBox(noCommands, "fixture-vbox");
            await RejectAsync(() => Backends.ShutdownOriginal(rejected, invalid, CancellationToken.None),
                "The shutdown dispatcher accepted a missing task ID");
        });

        await Check("workstation-shutdown-targets-only-the-registered-task-vmx-in-soft-mode", async () => {
            var state = OwnedVm("vmware-shutdown");
            state.Options.PlatformId = "workstation";
            Directory.CreateDirectory(state.Options.VmDirectory);
            JsonFile.Write(Paths.Under(state.Options.VmDirectory, ".claude-workspace.json"),
                new { vmId = state.VmId, workspaceId = state.Options.WorkspaceId });
            var config = Paths.Under(state.Options.VmDirectory, state.Options.VmName + ".vmx");
            var stops = 0;
            var fake = new FakeRunner(args =>
            {
                if (args.SequenceEqual(["list"])) return stops == 0 ? $"Total running VMs: 1\n{config}\n" : "Total running VMs: 0";
                Assert(args.SequenceEqual(["stop", config, "soft"]), "Wrong VM or hard stop was requested");
                stops++;
                return "";
            });
            await using var backend = new Workstation(fake, "fixture-vmware");
            await Backends.ShutdownOriginal(backend, state, CancellationToken.None);
            await Backends.ShutdownOriginal(backend, state, CancellationToken.None);
            Assert(stops == 1 && fake.Commands[^1][0] == "list", "An already stopped guest received another request");
        });

        await Check("saved-network-binding-is-read-without-modifying-or-discarding-state", async () => {
            var state = OwnedVm("saved-network"); var binding = new GatewayBinding(state.VmId, 49152, 49153, "02:11:22:33:44:55");
            var fake = new FakeRunner(args => args[0] == "list" ? $"\"Claude-Workspace\" {{{state.VmId}}}\n" : VmInfo(state, "saved", false));
            await VirtualBoxBoundary.Prepare(new VirtualBox(fake, "fixture-vbox"), state, binding, CancellationToken.None);
            Assert(fake.Commands.All(x => x[0] is "list" or "showvminfo"), "saved-state adapter was modified or discarded");
        });
        await Check("offline-saved-vm-does-not-require-modifiable-settings", async () => {
            var state = OwnedVm("saved-offline"); var fake = new FakeRunner(args => args[0] == "list" ? $"\"Claude-Workspace\" {{{state.VmId}}}\n" : VmInfo(state, "saved", true));
            await VirtualBoxBoundary.Disconnect(new VirtualBox(fake, "fixture-vbox"), state, CancellationToken.None);
            Assert(fake.Commands.All(x => x[0] is "list" or "showvminfo"), "offline saved VM settings changed");
        });
        await Check("offline-running-vm-with-network-is-rejected-without-modification", async () => {
            var state = OwnedVm("running-online"); var fake = new FakeRunner(args => args[0] == "list" ? $"\"Claude-Workspace\" {{{state.VmId}}}\n" : VmInfo(state, "running", false));
            await RejectAsync(() => VirtualBoxBoundary.Disconnect(new VirtualBox(fake, "fixture-vbox"), state, CancellationToken.None), "active network passed offline start");
            Assert(fake.Commands.All(x => x[0] is "list" or "showvminfo"), "active VM was modified without shutdown");
        });
        foreach (var initial in new[] { "poweroff", "saved", "aborted", "paused", "running" }) await Check("vm-console-open-" + initial, async () => {
            var state = OwnedVm("console-" + initial); var mode = initial; var starts = 0; var presenter = new FakeConsole();
            var fake = new FakeRunner(args => {
                if (args[0] == "list") return $"\"Claude-Workspace\" {{{state.VmId}}}\n";
                if (args[0] is "startvm" or "controlvm") { Assert(args[1] == state.VmId, "wrong VM launched"); mode = "running"; return ""; }
                return VmInfo(state, mode, true);
            });
            await new VirtualBox(fake, "fixture-vbox", presenter, _ => starts++).Open(state, CancellationToken.None);
            var cold = initial is "poweroff" or "saved" or "aborted";
            Assert(starts == (cold ? 1 : 0) && fake.Commands.Count(x => x[0] == "startvm") == (cold ? 1 : 0), "wrong start/memory-check behavior");
            Assert(fake.Commands.Count(x => x[0] == "controlvm") == (initial == "paused" ? 1 : 0), "wrong resume behavior");
            Assert(presenter.Calls == 1 && presenter.Attaches == 0 && mode == "running", "console activation was not verified");
        });
        await Check("hyperv-errors-distinguish-permission-components-and-timeout", () => Sync(() => {
            var denied = VmInventory.HyperVFailure(new System.Runtime.InteropServices.COMException("fixture", unchecked((int)0x80041003)));
            var missing = VmInventory.HyperVFailure(new System.Runtime.InteropServices.COMException("fixture", unchecked((int)0x8004100E)));
            Assert(denied.Contains("权限") && !denied.Contains("未启用"), "access denial misdiagnosed as missing feature");
            Assert(missing.Contains("管理接口不可用") && !missing.Contains("没有读取"), "missing namespace misdiagnosed as permission");
            Assert(VmInventory.HyperVFailure(new TimeoutException()).Contains("超时"), "timeout not distinguished");
        }));
        await Check("diagnostic-export-excludes-vm-names-and-private-paths", () => Sync(() => {
            var host = new HostInfo("Professional", 16L << 30, 9L << 30, 16, true, 26100, @"C:\PRIVATE_TOKEN\VBoxManage.exe");
            var facts = new PlatformFacts("Windows", "x64", "Professional", true, true, 16, true, false);
            var inventory = new VmInventoryResult([new("VirtualBox", "PRIVATE_TOKEN", "PRIVATE_TOKEN", "运行中", @"C:\PRIVATE_TOKEN\Windows.vbox")], ["尚未安装 VMware Workstation。"]);
            var report = Diagnostics.Create(host, facts, inventory); var text = JsonSerializer.Serialize(report);
            Assert(!text.Contains("PRIVATE_TOKEN") && report.RegisteredVmCounts["VirtualBox"] == 1, "diagnostic output exposed registration data");
        }));
        await Check("inventory-vbox-escaped-path-and-status", () => Sync(() => {
            var row = VmInventory.ParseVBoxInfo("11111111-1111-1111-1111-111111111111", "已有环境", "VMState=\"saved\"\nCfgFile=\"D:\\\\虚拟机\\\\My \\\"VM\\\"\\\\Windows.vbox\"\n");
            Assert(row.State == "已保存" && row.Location == "D:\\虚拟机\\My \"VM\"\\Windows.vbox", "VBox inventory parse corrupted path");
        }));
        await Check("inventory-vmware-registered-only", () => Sync(() => {
            var rows = VmInventory.ParseWorkstationInventory("vmlist0.config = \"D:\\已有环境\\Windows.vmx\"\nvmlist0.DisplayName = \"工作环境\"\nvmlist9.DisplayName = \"no config\"\nvmlist1.config = \"D:|5CVMs|5COther.vmx\"\n");
            Assert(rows.Count == 2 && rows[0].Name == "工作环境" && rows[1].Location == "D:\\VMs\\Other.vmx", "VMware inventory parse failed");
        }));
        await Check("gateway-adapter-no-fallback-and-exact-binding", () => Sync(() => {
            const string id = "11111111-1111-1111-1111-111111111111";
            var binding = new GatewayBinding(id, 49152, 49153, "02:11:22:33:44:55");
            var args = VirtualBoxBoundary.Arguments(binding);
            Assert(args.Contains("UDPTunnel") && args.Contains("dest=127.0.0.1") && !args.Contains("nat") && args.Contains("--nic8"), "adapter has fallback path");
            var state = new SessionState { VmId = id, Options = new() { Root = root } };
            string info = SharingInfo + $"UUID=\"{id}\"\nnic1=\"generic\"\ngeneric1=\"UDPTunnel\"\nmacaddress1=\"021122334455\"\ncableconnected1=\"on\"\nCfgFile=\"{Path.Combine(state.Options.VmDirectory, state.Options.VmName + ".vbox").Replace("\\", "\\\\")}\"\n" + string.Concat(Enumerable.Range(2, 7).Select(i => $"nic{i}=\"none\"\n"));
            VirtualBoxBoundary.CheckInfo(state, binding, info);
            Reject(() => VirtualBoxBoundary.CheckInfo(state, binding, info.Replace("nic8=\"none\"", "nic8=\"nat\"")), "secondary NIC bypass accepted");
            Reject(() => VirtualBoxBoundary.CheckInfo(state, binding, info.Replace("generic1=\"UDPTunnel\"", "generic1=\"other\"")), "unverified driver accepted");
            var unused = new FakeRunner(_ => throw new Exception("must not access another VM"));
            Reject(() => VirtualBoxBoundary.Connect(new VirtualBox(unused, "fixture-vbox"), state, binding with { VmId = "22222222-2222-2222-2222-222222222222" }, ["1.1.1.1"], 443, "node.example", CancellationToken.None).GetAwaiter().GetResult(), "foreign binding reached mutation");
            Reject(() => VirtualBoxBoundary.Disconnect(new VirtualBox(unused, "fixture-vbox"), new SessionState(), CancellationToken.None).GetAwaiter().GetResult(), "disconnect created a missing VM");
            Assert(unused.Commands.Count == 0, "invalid binding reached VM command runner");
        }));
        await Check("host-firewall-plan-scoped-to-vm-port-and-program", () => Sync(() => {
            var request = new GatewayFirewallRequest(root, "11111111-1111-1111-1111-111111111111", 49152);
            var plan = GatewayFirewall.Plan(request, @"C:\Program Files\Oracle\VirtualBox");
            Assert(plan.Count == 2 && plan.All(x => x.Port == 49152 && x.Name.Contains(request.VmId) && x.Program.EndsWith(".exe")), "unscoped firewall plan");
            Reject(() => GatewayFirewall.Plan(request with { GuestPort = 53 }, root), "privileged port accepted");
        }));
        await Check("gateway-policy-rejects-private-and-colliding-ports", () => Sync(() => {
            var binding = new GatewayBinding("11111111-1111-1111-1111-111111111111", 49152, 49153, "02:11:22:33:44:55");
            Reject(() => ExternalGateway.Policy(binding, ["127.0.0.1"], 443, "node.example", Guid.NewGuid().ToString("N")), "local node accepted");
            Reject(() => ExternalGateway.Policy(binding with { ListenPort = 49152 }, ["1.1.1.1"], 443, "node.example", Guid.NewGuid().ToString("N")), "self UDP loop accepted");
        }));
        await Check("gateway-guest-config-has-no-direct-dns-bootstrap", () => Sync(() => {
            var node = NetworkImport.Parse("socks5://node.example:1080#Fixture").Nodes.Single();
            using var config = JsonDocument.Parse(NetworkImport.CoreConfig(node, ["1.1.1.1"]));
            var dns = config.RootElement.GetProperty("dns");
            Assert(dns.GetProperty("nameserver")[0].GetString() == "https://1.1.1.1/dns-query#Claude" && dns.GetProperty("proxy-server-nameserver")[0].GetString() == "198.19.250.1", "DNS can fall back to physical direct resolver");
            Assert(config.RootElement.GetProperty("hosts").GetProperty("node.example")[0].GetString() == "1.1.1.1", "node resolution not pinned");
        }));
        await Check("embedded-gateway-control-binding-and-owner-exit", async () => {
            var file = Path.Combine(root, "gateway-fixture.exe"); File.WriteAllBytes(file, Catalog.EmbeddedBytes("ClaudeNetworkGateway.exe"));
            using var one = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); using var two = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var binding = new GatewayBinding("11111111-1111-1111-1111-111111111111", ((IPEndPoint)one.Client.LocalEndPoint!).Port, ((IPEndPoint)two.Client.LocalEndPoint!).Port, "02:11:22:33:44:55"); two.Dispose();
            var policy = ExternalGateway.Policy(binding, ["1.1.1.1"], 443, "node.example", Guid.NewGuid().ToString("N")); var text = JsonSerializer.Serialize(policy);
            var start = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false) };
            using var worker = System.Diagnostics.Process.Start(start)!;
            try {
                await worker.StandardInput.WriteLineAsync(text); await worker.StandardInput.FlushAsync();
                var line = await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)); using var ready = JsonDocument.Parse(line!); var row = ready.RootElement;
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
                Assert(row.GetProperty("ready").GetBoolean() && row.GetProperty("vmId").GetString() == binding.VmId && row.GetProperty("runId").GetString() == policy.runId && row.GetProperty("fingerprint").GetString() == hash && row.GetProperty("udpPort").GetInt32() == binding.ListenPort, "C# / Go binding mismatch");
                worker.StandardInput.Close(); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert(worker.ExitCode == 0, "owner EOF did not stop worker cleanly");
            } finally { if (!worker.HasExited) worker.Kill(true); }
        });
        await Check("isolation-blocks-local-reserved-and-ipv6-destinations", () => Sync(() => {
            foreach (var address in new[] { "0.0.0.0", "10.0.2.2", "127.0.0.1", "100.64.0.1", "169.254.169.254", "172.16.0.1", "172.31.255.255", "192.168.1.1", "192.0.0.1", "198.18.0.1", "224.0.0.1", "255.255.255.255", "::1", "::ffff:127.0.0.1", "fc00::1", "fe80::1" }) Reject(() => IsolationPolicy.ValidateEndpoints([address]), "local or reserved destination allowed: " + address);
            Assert(IsolationPolicy.ValidateEndpoints(["1.1.1.1", "8.8.8.8"]).Length == 2, "public endpoint rejected");
        }));
        await Check("guest-report-cannot-authorize-host-egress", () => Sync(() => {
            var session = new SessionState { NetworkPassed = true, ApplicationAccepted = true, Options = new() { Root = root, NetworkSource = "已有订阅链接" } };
            Reject(() => BoundaryRuntime.Require(session), "guest checks bypassed missing host boundary");
        }));
        await Check("unsupported-provider-cannot-reach-vm-launch", async () => {
            try { await Backends.Open(new SessionState { VmId = "11111111-1111-1111-1111-111111111111", Options = new() { PlatformId = "unknown", Root = root } }, CancellationToken.None); }
            catch (HandoffException ex) { Assert(ex.Action.Id == "isolation-platform", "wrong launch guard"); return; }
            throw new Exception("open entry reached backend before boundary guard");
        });
        await Check("fresh-home-and-pro-platform-recommendations", () => Sync(() => {
            var fresh = new PlatformFacts("Windows", "x64", "Core", true, true, 16, false, false);
            var home = Platforms.Recommend(fresh); Assert(home[0].Id == "virtualbox" && home.Single(x => x.Id == "hyperv").Compatible == false, "home recommendation wrong");
            var pro = Platforms.Recommend(fresh with { Edition = "Professional" }); Assert(pro[0].Id == "hyperv", "Pro should prefer original supported route");
            Assert(Platforms.Recommend(fresh with { Architecture = "arm64" }).All(x => !x.Compatible), "Windows ARM offered unsupported automatic install");
            Assert(Platforms.Recommend(fresh with { VirtualizationReady = false }).All(x => !x.Compatible), "disabled virtualization accepted");
        }));
        await Check("mac-recommendation-does-not-claim-windows-exe-support", () => Sync(() => {
            var mac = new PlatformFacts("macOS", "arm64", "", true, true, 16, false, false);
            Assert(Platforms.Recommend(mac)[0].Id == "fusion" && Platforms.Recommend(mac).All(x => !x.Automatic), "Mac claimed Windows EXE automation");
            Assert(Platforms.Recommend(mac with { ParallelsInstalled = true })[0].Id == "parallels", "existing licensed route not preferred");
        }));
        await Check("appearance-persistence-invalid-color-and-contrast", () => Sync(() => {
            var file = Path.Combine(root, "appearance-fixture.json"); var chosen = new Appearance("fixture", "#8732AC", "#16202C"); chosen.Save(file);
            Assert(Appearance.Load(file) == chosen, "custom appearance did not persist");
            Reject(() => Appearance.Validate(chosen with { Accent = "invalid" }), "invalid color accepted");
            foreach (var preset in Appearance.Presets) { var bg = Appearance.Color(preset.Background); Assert(Appearance.Contrast(bg, Appearance.Legible(bg)) >= 4.5, "illegible foreground"); }
        }));
        await Check("windows-answer-is-well-formed-and-does-not-embed-plaintext-password", () => Sync(() => {
            const string password = "fixture-<>&-password"; var text = WindowsSeed.Answer(new SetupOptions { UserName = "FixtureUser" }, password);
            var xml = System.Xml.Linq.XDocument.Parse(text); System.Xml.Linq.XNamespace ns = "urn:schemas-microsoft-com:unattend";
            Assert(!text.Contains(password) && xml.Descendants(ns + "PlainText").All(x => x.Value == "false"), "plaintext Windows password");
            Assert(xml.Descendants(ns + "DiskID").All(x => x.Value == "0"), "unexpected target disk");
        }));
        await Check("embedded-guest-runtime-helper-present", () => Sync(() => {
            var dll = Catalog.EmbeddedBytes("YamlDotNet.dll"); Assert(dll.Length > 1000 && dll[0] == 'M' && dll[1] == 'Z', "guest YAML runtime missing");
        }));
        await Check("local-path-boundary", () => Sync(() => { Assert(Paths.Under(root, "safe/file.txt").StartsWith(Path.GetFullPath(root)), "valid path rejected"); Reject(() => Paths.Under(root, "../escape.txt"), "path traversal allowed"); Reject(() => Paths.ValidateRoot("C:\\"), "drive root allowed"); Reject(() => Paths.ValidateRoot(@"\\server\share\folder"), "network directory allowed"); }));
        await Check("protected-system-directory", () => Sync(() => Reject(() => Paths.ValidateRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Claude")), "system directory allowed")));
        await Check("workspace-will-not-adopt-unrelated-directory", () => Sync(() =>
        {
            var folder = Path.Combine(root, "unrelated"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "keep.txt"), "fixture");
            Reject(() => Paths.EnsureWorkspace(folder), "unrelated directory adopted");
            var fresh = Path.Combine(root, "owned"); Paths.EnsureWorkspace(fresh); Paths.EnsureWorkspace(fresh); Assert(File.Exists(Path.Combine(fresh, ".claude-setup-workspace.json")), "workspace marker missing");
        }));
        await Check("credential-roundtrip-and-no-plaintext", () => Sync(() =>
        {
            var privateRoot = Path.Combine(root, "private"); var secrets = new Secrets { VmPassword = "  fixture-$#=【pass】  ", SubscriptionUrl = "https://example.invalid/sub?token=fixture" };
            SecretStore.Save(privateRoot, secrets); var read = SecretStore.Load(privateRoot)!;
            Assert(read.VmPassword == secrets.VmPassword && read.SubscriptionUrl == secrets.SubscriptionUrl, "secret changed");
            Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(privateRoot, "credentials.dpapi"))).Contains(secrets.VmPassword), "plaintext store");
            SecretStore.Forget(privateRoot); Assert(SecretStore.Load(privateRoot) is null, "secret not removed");
        }));
        await Check("private-file-disposal", () => Sync(() => { string file; using (var secret = new PrivateFile(Path.Combine(root, "temporary"), "test-only")) { file = secret.FileName; Assert(File.Exists(file), "temporary missing"); } Assert(!File.Exists(file), "temporary remains"); }));
        await Check("clash-config-node-selection", () => Sync(() =>
        {
            var input = "proxies:\n  - name: '我的节点'\n    type: socks5\n    server: 192.0.2.10\n    port: 1080\n    username: test\n    password: '  p#=$  '\nrules: ['MATCH,DIRECT']\n";
            var parsed = NetworkImport.Parse(input); Assert(parsed.Nodes.Count == 1, "node missing");
            var config = NetworkImport.CoreConfig(parsed.Nodes[0]); using var json = JsonDocument.Parse(config);
            File.WriteAllText(Path.Combine(root, "core-test.yaml"), config, new UTF8Encoding(false));
            Assert(json.RootElement.GetProperty("proxies")[0].GetProperty("password").GetString() == "  p#=$  ", "password whitespace changed");
            Assert(json.RootElement.GetProperty("rules")[0].GetString() == "MATCH,Claude", "untrusted source rules used");
        }));
        await Check("uri-http-credentials", () => Sync(() => { var node = NetworkImport.Parse("https://user:p%40ss@192.0.2.11:443#Mine").Nodes.Single(); Assert(node.Type == "http" && node.Port == 443 && node.Json.Contains("p@ss"), "URI credentials changed"); }));
        await Check("uri-vless-websocket", () => Sync(() => { var n = NetworkImport.Parse("vless://11111111-1111-1111-1111-111111111111@node.example:443?security=tls&type=ws&path=%2Fapi&sni=node.example#Node").Nodes.Single(); Assert(n.Type == "vless" && n.Json.Contains("ws-opts"), "VLESS not parsed"); }));
        await Check("base64-node-list", () => Sync(() => { var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("socks5://192.0.2.12:1080#One")); Assert(NetworkImport.Parse(encoded).Nodes.Count == 1, "base64 list failed"); }));
        await Check("external-plugin-rejected", () => Sync(() => Reject(() => NetworkImport.Parse("proxies:\n- {name: x, type: ss, server: example.invalid, port: 443, plugin: arbitrary.exe}"), "external plugin allowed")));
        await Check("subscription-certificate-option-preserved",()=>Sync(()=>{var n=NetworkImport.Parse("proxies:\n- {name: x, type: trojan, server: example.invalid, port: 443, password: sample, skip-cert-verify: true}").Nodes.Single();Assert(n.Json.Contains("\"skip-cert-verify\":true"),"original certificate option lost");}));
        await Check("same-name-but-distinct-routes-are-preserved",()=>Sync(()=>{var list=NetworkImport.Parse("socks5://192.0.2.1:1#same\nsocks5://192.0.2.2:2#same");Assert(list.Nodes.Count==2 && list.Unsupported==0,"distinct routes with the same display name must be retained");}));
        await Check("invalid-port-rejected", () => Sync(() => Reject(() => NetworkImport.Validate(new() { ["name"] = "test", ["type"] = "socks5", ["server"] = "example.invalid", ["port"] = 70000 }), "bad port allowed")));
        await Check("source-state-does-not-serialize-secrets", () => Sync(() =>
        {
            var o = new SetupOptions { Root = root, IsoUrl = "https://example.invalid/secret-query" }; var state = new SessionState { Options = o }; state.Save();
            var text = File.ReadAllText(o.StatePath); Assert(!text.Contains("secret-query") && !text.Contains("VmPassword"), "private data in state");
            Assert(JsonFile.Read<SessionState>(o.StatePath).Status == "待配置", "state roundtrip failed");
        }));
        await Check("vm-list-parser", () => Sync(() => { var list = VirtualBox.ParseList("\"Claude-Workspace\" {11111111-1111-1111-1111-111111111111}\r\n"); Assert(list.Count == 1 && list[0].Name == "Claude-Workspace", "VM inventory parse failed"); }));
        await Check("vm-same-name-collision-does-not-overwrite", async () =>
        {
            var fake = new FakeRunner(args => args.SequenceEqual(new[] { "list", "vms" }) ? "\"Claude-Workspace\" {11111111-1111-1111-1111-111111111111}\n" : "");
            var box = new VirtualBox(fake, "fixture-vbox"); var state = new SessionState { Options = new() { Root = Path.Combine(root, "collision") } };
            await RejectAsync(async () => await box.EnsureMachine(state, CancellationToken.None), "existing VM overwritten");
            Assert(!fake.Commands.Any(a => a[0] == "createvm"), "create called after collision");
        });
        await Check("vm-resume-is-scoped-to-recorded-directory", async () =>
        {
            var id = "11111111-1111-1111-1111-111111111111";
            var state = new SessionState { VmId = id, Options = new() { Root = Path.Combine(root, "resume-vm") } };
            var config = Path.GetFullPath(Path.Combine(state.Options.VmDirectory, state.Options.VmName + ".vbox")).Replace("\\", "\\\\");
            var fake = new FakeRunner(args => args[0] == "list" ? "\"Claude-Workspace\" {" + id + "}\n" : "CfgFile=\"" + config + "\"\n");
            Assert(await new VirtualBox(fake, "fixture-vbox").EnsureMachine(state, CancellationToken.None) == id, "resume rejected");
            Assert(!fake.Commands.Any(a => a[0] == "createvm"), "resume created duplicate");
            var sibling = new FakeRunner(args => args[0] == "list" ? "\"Claude-Workspace\" {" + id + "}\n" : "CfgFile=\"" + config.Replace("Claude-Workspace", "Claude-Workspace-Other") + "\"\n");
            await RejectAsync(async () => await new VirtualBox(sibling, "fixture-vbox").EnsureMachine(state, CancellationToken.None), "similar directory accepted");
            var wrong = new FakeRunner(args => args[0] == "list" ? "\"Claude-Workspace\" {" + id + "}\n" : "CfgFile=\"C:\\\\wrong\\\\Claude.vbox\"\n");
            await RejectAsync(async () => await new VirtualBox(wrong, "fixture-vbox").EnsureMachine(state, CancellationToken.None), "foreign VM directory accepted");
        });
        await Check("guest-password-is-never-command-line-data", async () =>
        {
            var fake = new FakeRunner(_ => "fixture-output"); var box = new VirtualBox(fake, "fixture-vbox");
            var state = new SessionState { VmId = "11111111-1111-1111-1111-111111111111", Options = new() { Root = Path.Combine(root, "guest-call") } };
            var secret = new Secrets { VmPassword = "fixture-$#-secret" };
            await box.GuestRun(state, secret, @"C:\Windows\System32\whoami.exe", [], CancellationToken.None);
            Assert(fake.Commands.Single().Contains("--passwordfile") && !fake.Commands.Single().Any(a => a.Contains(secret.VmPassword)), "password in command args");
            Assert(!Directory.EnumerateFiles(state.Options.Private, "*.txt").Any(), "temporary credential remains");
        });
        await Check("catalog-hashes-and-official-https", () => Sync(() => { var list = Catalog.Resources; Assert(list.Count == 7 && list.Select(r => r.Id).ToHashSet().SetEquals(new[]{"vcredist","vbox","pwsh","adspower","windows-media","clash","bootstrap-core"}), "catalog invalid"); Assert(list.All(r => r.Sha256.Length == 64 && new Uri(r.Url).Scheme == "https" && r.Size > 0), "unverified resource"); }));
        var bytes = Encoding.UTF8.GetBytes("fixture resource for strict resume validation"); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Resource Resource(string name) => new("fixture", "Fixture", name, "https://example.invalid/fixture", hash, bytes.Length, "test-only");
        await Check("download-hash-and-cache", async () =>
        {
            int requests = 0; using var http = new HttpClient(new Handler(request => { requests++; return Reply(request, bytes); })); using var downloader = new Downloader(http);
            var file = await downloader.Get(Resource("download.bin"), root, null, CancellationToken.None); Assert(File.ReadAllBytes(file).SequenceEqual(bytes), "content mismatch");
            var progress = new List<DownloadProgress>(); await downloader.Get(Resource("download.bin"), root, new ProgressSink<DownloadProgress>(progress), CancellationToken.None); Assert(requests == 1, "verified cache redownloaded");
            Assert(progress.Count == 1 && progress[0].FromCache && progress[0].Received == bytes.Length && progress[0].Display.Contains("已核验缓存"), "cache reuse left stale download progress");
        });
        await Check("strict-resume-content-range", async () =>
        {
            var name = "resume.bin"; File.WriteAllBytes(Path.Combine(root, name + ".part"), bytes[..8]); JsonFile.Write(Path.Combine(root, name + ".part.json"), new { Sha256 = hash, ETag = "\"fixture\"" });
            using var http = new HttpClient(new Handler(request => { Assert(request.Headers.Range?.Ranges.Single().From == 8, "Range missing"); var reply = Reply(request, bytes[8..], HttpStatusCode.PartialContent); reply.Content.Headers.ContentRange = new ContentRangeHeaderValue(8, bytes.Length - 1, bytes.Length); return reply; }));
            using var downloader = new Downloader(http); var file = await downloader.Get(Resource(name), root, null, CancellationToken.None); Assert(File.ReadAllBytes(file).SequenceEqual(bytes), "resume corrupt");
        });
        await Check("server-ignoring-range-restarts-safely", async () =>
        {
            var name = "restart.bin"; File.WriteAllBytes(Path.Combine(root, name + ".part"), bytes[..8]); JsonFile.Write(Path.Combine(root, name + ".part.json"), new { Sha256 = hash, ETag = "\"fixture\"" });
            using var http = new HttpClient(new Handler(request => Reply(request, bytes))); using var downloader = new Downloader(http);
            var file = await downloader.Get(Resource(name), root, null, CancellationToken.None); Assert(File.ReadAllBytes(file).SequenceEqual(bytes), "old data appended");
        });
        await Check("incorrect-resume-offset-rejected", async () =>
        {
            var name = "offset.bin"; File.WriteAllBytes(Path.Combine(root, name + ".part"), bytes[..8]); JsonFile.Write(Path.Combine(root, name + ".part.json"), new { Sha256 = hash });
            using var http = new HttpClient(new Handler(request => { var reply = Reply(request, bytes[8..], HttpStatusCode.PartialContent); reply.Content.Headers.ContentRange = new ContentRangeHeaderValue(9, bytes.Length, bytes.Length + 1); return reply; }));
            using var downloader = new Downloader(http); await RejectAsync(async () => await downloader.Get(Resource(name), root, null, CancellationToken.None), "invalid Content-Range accepted");
        });
        await Check("bad-hash-never-published", async () =>
        {
            using var http = new HttpClient(new Handler(request => Reply(request, new byte[bytes.Length]))); using var downloader = new Downloader(http);
            await RejectAsync(async () => await downloader.Get(Resource("bad.bin"), root, null, CancellationToken.None), "bad hash accepted"); Assert(!File.Exists(Path.Combine(root, "bad.bin")), "corrupt final published");
        });
        await Check("existing-corrupt-cache-is-preserved-and-rejected", async () =>
        {
            var path = Path.Combine(root, "existing.bin"); File.WriteAllText(path, "do-not-overwrite");
            using var http = new HttpClient(new Handler(_ => throw new Exception("no network expected"))); using var downloader = new Downloader(http);
            await RejectAsync(async () => await downloader.Get(Resource("existing.bin"), root, null, CancellationToken.None), "corrupt cache accepted");
            Assert(File.ReadAllText(path) == "do-not-overwrite", "existing file overwritten");
        });
        await Check("http-resource-rejected", async () =>
        {
            using var http = new HttpClient(new Handler(_ => throw new Exception("HTTP must not be attempted"))); using var downloader = new Downloader(http);
            await RejectAsync(async () => await downloader.Get(Resource("http.bin") with { Url = "http://example.invalid/fixture" }, root, null, CancellationToken.None), "HTTP allowed");
        });
        await Check("unsafe-resource-name-rejected", async () =>
        {
            using var http = new HttpClient(new Handler(_ => throw new Exception("request must not happen"))); using var downloader = new Downloader(http);
            await RejectAsync(async () => await downloader.Get(Resource("../escape.bin"), root, null, CancellationToken.None), "resource traversal allowed");
        });
        await Check("sensitive-errors-not-echoed", () => Sync(() => { var secret = "not-a-real-password-123"; Assert(!Redaction.SafeError(new IOException(secret)).Contains(secret), "secret leaked"); }));
        await Check("workspace-lock-prevents-busy-stale-overwrite-and-allows-owner-saves", () => Sync(() => {
            var taskRoot = Path.Combine(root, "concurrent-lock-fixture");
            Paths.EnsureWorkspace(taskRoot);
            var owner = new SessionState { VmId = "11111111-1111-1111-1111-111111111111", Stage = "6 / 7  初始阶段", Options = new() { Root = taskRoot } };
            owner.Completed.Add("guest-files");
            owner.Save();
            var stale = JsonFile.Read<SessionState>(owner.Options.StatePath);
            Thread.Sleep(20);
            using (var held = WorkspaceLock.Acquire(owner))
            {
                owner.Stage = "7 / 7  持有锁实例最新阶段";
                owner.Completed.Add("software");
                Assert(SetupWindow.TrySaveTaskState(owner) == TaskSaveOutcome.Saved, "lock owner could not checkpoint");
                Assert(SetupWindow.TrySaveTaskState(stale) == TaskSaveOutcome.LockedByAnotherInstance, "unowned instance bypassed .setup.lock");
                var duringLock = JsonFile.Read<SessionState>(owner.Options.StatePath);
                Assert(duringLock.Completed.Contains("software") && duringLock.Stage.StartsWith("7 / 7"), "stale instance overwrote locked workspace");
            }
            Assert(SetupWindow.TrySaveTaskState(stale) == TaskSaveOutcome.KeptNewer, "stale snapshot overwrote newer unlocked checkpoint");
            Assert(JsonFile.Read<SessionState>(owner.Options.StatePath).Completed.Contains("software"), "software milestone was lost after lock release");
        }));
        await Check("secret-store-save-merged-preserves-hidden-and-unloaded-fields", () => Sync(() => {
            var priv = Path.Combine(root, "secret-merge-fixture", "private");
            SecretStore.Save(priv, new Secrets { VmPassword = "InitialPassword!123", AdsPowerApiKey = "fictional-ads-key", SubscriptionUrl = "https://example.invalid/sub", SelectedNodeJson = "{\"name\":\"N\",\"type\":\"socks5\",\"server\":\"1.1.1.1\",\"port\":1080}" });
            SecretStore.SaveMerged(priv, new Secrets { VmPassword = "UpdatedPassword!456" }, preserveHiddenNetwork: true, preserveUnloadedSecrets: true);
            var merged = SecretStore.Load(priv)!;
            Assert(merged.VmPassword == "UpdatedPassword!456" && merged.AdsPowerApiKey == "fictional-ads-key" && merged.SubscriptionUrl == "https://example.invalid/sub" && merged.SelectedNodeJson.Contains("1.1.1.1"), "hidden or unloaded secrets were wiped");
        }));
        await Check("resource-stamp-ignores-host-only-bootstrap-core-updates", () => Sync(() => {
            var baseline = Catalog.Resources;
            var changedHost = baseline.Select(r => r.Id is "bootstrap-core" or "vbox" or "windows-media" ? r with { Sha256 = new string('f', 64) } : r).ToList();
            var changedGuest = baseline.Select(r => r.Id == "clash" ? r with { Sha256 = new string('e', 64) } : r).ToList();
            var changedRuntime = baseline.Select(r => r.Id == "vcredist" ? r with { Sha256 = new string('d', 64) } : r).ToList();
            Assert(ResumePlan.ComputeResourceStamp(baseline) == ResumePlan.ComputeResourceStamp(changedHost), "host-only resource hash triggered guest file re-transfer");
            Assert(ResumePlan.ComputeResourceStamp(baseline) != ResumePlan.ComputeResourceStamp(changedGuest), "guest resource update was not detected");
            Assert(ResumePlan.ComputeResourceStamp(baseline) != ResumePlan.ComputeResourceStamp(changedRuntime), "guest runtime is missing from the resource checkpoint");
        }));
        await Check("vbox-nem-compatibility-auto-migrates-default-task-to-1-cpu-and-is-idempotent", async () => {
            var state = OwnedVm("nem-auto-migrate");
            state.Options.AcceptChanges = true;
            state.Options.CpuCount = 4;
            var curCpu = "4"; var curParavirt = "minimal"; var curApic = "off"; var curIoApic = "on"; var curX2Apic = "off"; var curHpet = "on";
            string NemInfo() => VmInfo(state, "poweroff", true) + $"cpus={curCpu}\napic=\"{curApic}\"\nioapic=\"{curIoApic}\"\nparavirtprovider=\"{curParavirt}\"\nx2apic=\"{curX2Apic}\"\nhpet=\"{curHpet}\"\n";
            var fake = new FakeRunner(args => {
                if (args[0] == "list") return $"\"Claude-Workspace\" {{{state.VmId}}}\n";
                if (args[0] == "modifyvm") {
                    for (var i = 2; i + 1 < args.Length; i += 2) {
                        if (args[i] == "--cpus") curCpu = args[i + 1];
                        else if (args[i] == "--apic") curApic = args[i + 1];
                        else if (args[i] == "--x86-x2apic") curX2Apic = args[i + 1];
                        else if (args[i] == "--ioapic") curIoApic = args[i + 1];
                        else if (args[i] == "--paravirt-provider") curParavirt = args[i + 1];
                        else if (args[i] == "--x86-hpet") curHpet = args[i + 1];
                    }
                    return "";
                }
                return NemInfo();
            });
            var vbox = new VirtualBox(fake, "fixture-vbox");
            var migrated = await vbox.EnsureNemCompatibility(state, CancellationToken.None, hostNemOverride: true);
            Assert(migrated && curCpu == "1" && curApic == "on" && curIoApic == "on" && curParavirt == "default" && curX2Apic == "on" && curHpet == "off" && state.Options.CpuCount == 1, "legacy default task was not migrated to NEM-safe 1-vCPU profile");
            var modifyCount = fake.Commands.Count(c => c[0] == "modifyvm");
            Assert(!await vbox.EnsureNemCompatibility(state, CancellationToken.None, hostNemOverride: true) && fake.Commands.Count(c => c[0] == "modifyvm") == modifyCount, "already compatible VM was modified again");
        });
        await Check("vbox-nem-compatibility-respects-customer-custom-cpu-configuration", async () => {
            var state = OwnedVm("nem-respects-custom");
            state.Options.AcceptChanges = true;
            state.Options.CpuCount = 4;
            state.Options.ManualCpuConfirmed = true;
            var fake = new FakeRunner(args => args[0] == "list"
                ? $"\"Claude-Workspace\" {{{state.VmId}}}\n"
                : VmInfo(state, "poweroff", true) + "cpus=4\napic=\"on\"\nioapic=\"on\"\nparavirtprovider=\"default\"\nx2apic=\"on\"\nhpet=\"off\"\n");
            var migrated = await new VirtualBox(fake, "fixture-vbox").EnsureNemCompatibility(state, CancellationToken.None, hostNemOverride: true);
            Assert(!migrated && fake.Commands.All(c => c[0] != "modifyvm") && state.Options.CpuCount == 4 && state.Options.ManualCpuConfirmed, "customer custom CPU configuration was unexpectedly overridden");
        });
        await Check("vbox-nem-compatibility-leaves-a-running-vm-with-no-display-alone", async () => {
            var state = OwnedVm("nem-no-automatic-poweroff");
            state.Options.AcceptChanges = true;
            state.Options.CpuCount = 2;
            var fake = new FakeRunner(args => args[0] == "list"
                ? $"\"Claude-Workspace\" {{{state.VmId}}}\n"
                : VmInfo(state, "running", true) + "VideoMode=\"0,0,0\"@0,0 1\ncpus=2\n");
            Assert(!await new VirtualBox(fake, "fixture-vbox").EnsureNemCompatibility(state, CancellationToken.None, hostNemOverride: true),
                "Missing display triggered an automatic VM repair");
            Assert(fake.Commands.All(c => c[0] is "list" or "showvminfo") && state.Options.CpuCount == 2,
                "A running guest was stopped or configured without explicit repair");
        });
        foreach (var scenario in new[] { "saved", "save-unconfirmed", "old-log-only", "gui-log", "wrong-network" })
        await Check("vbox-framebuffer-recovery-" + scenario, async () => {
            var state = OwnedVm("framebuffer-" + scenario);
            var log = Path.Combine(state.Options.VmDirectory, "Logs", "VBox.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            const string error = "00:25:28.383281 ERROR [COM]: aComponent={DisplayWrap} aText={AttachFramebuffer: Framebuffer already attached to 0}";
            var header = "00:00:00.490463 Executable: C:\\Program Files\\Oracle\\VirtualBox\\" +
                (scenario == "gui-log" ? "VirtualBoxVM.exe" : "VBoxHeadless.exe");
            File.WriteAllText(log, header + "\n" + (scenario == "old-log-only" ? "Normal startup" : error));
            if (scenario == "old-log-only") File.WriteAllText(log + ".1", header + "\n" + error);
            var mode = "running";
            var runner = new FakeRunner(args => {
                if (args[0] == "list") return $"\"Claude-Workspace\" {{{state.VmId}}}\n";
                if (args[0] == "controlvm") {
                    Assert(args.SequenceEqual(new[] {"controlvm", state.VmId, "savestate"}), "Unexpected VM state command");
                    mode = scenario == "save-unconfirmed" ? "poweroff" : "saved";
                    return "";
                }
                if (args[0] == "startvm") {
                    Assert(mode == "saved", "Started a powered-off VM");
                    Assert(args.SequenceEqual(new[] {"startvm", state.VmId, "--type", "headless"}), "Wrong VM start parameters");
                    mode = "running";
                    return "";
                }
                var info = VmInfo(state, mode, true);
                return scenario == "wrong-network" ? info.Replace("nic1=\"none\"", "nic1=\"nat\"") : info;
            });
            var vbox = new VirtualBox(runner, "fixture-vbox");
            if (scenario == "saved") {
                await vbox.RecoverFramebufferConflict(state, CancellationToken.None);
                Assert(runner.Commands.Count(a => a[0] == "controlvm") == 1 && runner.Commands.Count(a => a[0] == "startvm") == 1,
                    "Recovery never completed or made duplicate state changes");
            } else {
                await RejectAsync(() => vbox.RecoverFramebufferConflict(state, CancellationToken.None), "Invalid recovery was allowed");
                Assert(runner.Commands.All(a => a[0] != "startvm") &&
                       runner.Commands.Count(a => a[0] == "controlvm") == (scenario == "save-unconfirmed" ? 1 : 0), "Rejected recovery mutated the VM");
            }
        });
        await Check("vbox-force-stop-and-repair-recovers-stuck-running-vm", async () => {
            var state = OwnedVm("nem-force-repair");
            state.Options.CpuCount = 4;
            state.Options.ManualCpuConfirmed = true;
            var curMode = "running"; var curCpu = "4"; var curApic = "off"; var curIoApic = "on"; var curParavirt = "minimal"; var curX2Apic = "off"; var curHpet = "on";
            var fake = new FakeRunner(args => {
                if (args[0] == "list") return $"\"Claude-Workspace\" {{{state.VmId}}}\n";
                if (args[0] == "controlvm" && args[2] == "poweroff") { curMode = "poweroff"; return ""; }
                if (args[0] == "modifyvm") {
                    for (var i = 2; i + 1 < args.Length; i += 2) {
                        if (args[i] == "--cpus") curCpu = args[i + 1];
                        else if (args[i] == "--apic") curApic = args[i + 1];
                        else if (args[i] == "--x86-x2apic") curX2Apic = args[i + 1];
                        else if (args[i] == "--ioapic") curIoApic = args[i + 1];
                        else if (args[i] == "--paravirt-provider") curParavirt = args[i + 1];
                        else if (args[i] == "--x86-hpet") curHpet = args[i + 1];
                    }
                    return "";
                }
                return VmInfo(state, curMode, true) + $"cpus={curCpu}\napic=\"{curApic}\"\nioapic=\"{curIoApic}\"\nparavirtprovider=\"{curParavirt}\"\nx2apic=\"{curX2Apic}\"\nhpet=\"{curHpet}\"\n";
            });
            await new VirtualBox(fake, "fixture-vbox").ForceStopAndRepair(state, CancellationToken.None, hostNemOverride: true);
            Assert(curMode == "poweroff" && curCpu == "1" && curApic == "on" && curX2Apic == "on" && curParavirt == "default" && curHpet == "off" && state.Options.CpuCount == 1 && !state.Options.ManualCpuConfirmed, "stuck running VM was not powered off and repaired to 1-vCPU clean profile");
        });
        await Check("vbox-cleanup-windows-media-ejects-dvd-and-floppy-safely-on-running-vm", async () => {
            var state = OwnedVm("media-cleanup-running");
            var aux = Paths.Under(state.Options.Private, "unattended");
            Directory.CreateDirectory(aux);
            var viso = Path.Combine(aux, "setup-aux-iso.viso");
            var floppy = Path.Combine(aux, "setup-aux-floppy.img");
            var userIso = Path.Combine(state.Options.Root, "Windows.iso");
            File.WriteAllText(viso, "viso"); File.WriteAllText(floppy, "floppy"); File.WriteAllText(userIso, "iso");
            var info = VmInfo(state, "running", true) +
                $"\"SATA-1-0\"=\"{userIso.Replace("\\", "\\\\")}\"\n" +
                $"\"SATA-2-0\"=\"{viso.Replace("\\", "\\\\")}\"\n" +
                $"\"Floppy-0-0\"=\"{floppy.Replace("\\", "\\\\")}\"\n";
            var fake = new FakeRunner(args => args[0] == "showvminfo" ? info : "");
            await new VirtualBox(fake, "fixture-vbox").CleanupWindowsMedia(state, CancellationToken.None);
            var attaches = fake.Commands.Where(c => c[0] == "storageattach").ToList();
            Assert(attaches.Count == 3 && attaches.All(c => c.Contains("emptydrive") && c.Contains("--forceunmount") && !c.Contains("none")), "running VM media cleanup used mutable-only --medium none or missed floppy");
            var closes = fake.Commands.Where(c => c[0] == "closemedium").ToList();
            Assert(closes.Count == 2 && closes.Any(c => c[1] == "dvd" && c[2] == viso) && closes.Any(c => c[1] == "floppy" && c[2] == floppy) && !closes.Any(c => c[2] == userIso), "wrong media closed in VBoxSVC");
            Assert(!Directory.Exists(aux) && File.Exists(userIso), "auxiliary folder remained or user ISO was deleted");
        });
        await Check("node-exit-probe-parses-geoip-and-converts-iana-timezone-while-rejecting-private-ip", () => Sync(() => {
            var ipsb = NodeExitProbe.ParseGeoJson("{\"ip\":\"1.1.1.1\",\"country_code\":\"JP\",\"timezone\":\"Asia/Tokyo\"}", 142);
            Assert(ipsb.ExitIp == "1.1.1.1" && ipsb.CountryCode == "JP" && ipsb.WindowsTimeZoneId == "Tokyo Standard Time" && ipsb.SupportedRegion && ipsb.LatencyMs == 142, "ip.sb GeoIP parse or IANA->Windows timezone conversion failed");
            var ipwho = NodeExitProbe.ParseGeoJson("{\"success\":true,\"ip\":\"8.8.8.8\",\"country_code\":\"KR\",\"timezone\":{\"id\":\"Asia/Seoul\"}}", 85);
            Assert(ipwho.CountryCode == "KR" && ipwho.WindowsTimeZoneId == "Korea Standard Time" && ipwho.SupportedRegion, "ipwho.is nested timezone parse failed");
            var ipinfo = NodeExitProbe.ParseGeoJson("{\"ip\":\"9.9.9.9\",\"country\":\"NL\",\"timezone\":\"Europe/Amsterdam\"}", 190);
            Assert(ipinfo.CountryCode == "NL" && ipinfo.WindowsTimeZoneId == "W. Europe Standard Time" && ipinfo.SupportedRegion, "ipinfo.io parse failed");
            Reject(() => NodeExitProbe.ParseGeoJson("{\"ip\":\"10.0.0.8\",\"country_code\":\"US\",\"timezone\":\"America/Los_Angeles\"}"), "private exit IP accepted");
            Reject(() => NodeExitProbe.ParseGeoJson("{\"ip\":\"127.0.0.1\",\"country_code\":\"US\",\"timezone\":\"America/Los_Angeles\"}"), "loopback exit IP accepted");
            Reject(() => NodeExitProbe.ParseGeoJson("{\"ip\":\"198.51.100.25\",\"country_code\":\"US\",\"timezone\":\"America/Los_Angeles\"}"), "reserved documentation IP accepted");
        }));
        await Check("outbound-interface-classifier-distinguishes-physical-and-virtual-tun", () => Sync(() => {
            var physical = NodeExitProbe.DescribeOutboundInterface("WLAN", "Intel(R) Wi-Fi 6E AX211 160MHz", System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211);
            var ethernet = NodeExitProbe.DescribeOutboundInterface("以太网", "Realtek PCIe GbE Family Controller", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet);
            var metaTun = NodeExitProbe.DescribeOutboundInterface("Meta", "Meta Tunnel", (System.Net.NetworkInformation.NetworkInterfaceType)53);
            var wintun = NodeExitProbe.DescribeOutboundInterface("tun0", "Wintun Userspace Tunnel", System.Net.NetworkInformation.NetworkInterfaceType.Unknown);
            Assert(physical == "物理网卡 WLAN" && ethernet == "物理网卡 以太网", "physical NIC was misclassified");
            Assert(metaTun.Contains("虚拟 TUN/代理接口 Meta") && wintun.Contains("虚拟 TUN/代理接口 tun0"), "virtual TUN adapter was not flagged");
        }));
        await Check("nem-multicore-warning-targets-virtualbox-home-and-nem-hosts", () => Sync(() => {
            var homeWarn = SetupOptions.MultiCoreNemWarning("virtualbox", 2, hostNemOverride: true, hostEditionOverride: "CoreCountrySpecific");
            var proWarn = SetupOptions.MultiCoreNemWarning("virtualbox", 2, hostNemOverride: true, hostEditionOverride: "Professional");
            Assert(homeWarn is not null && homeWarn.Contains("家庭版") && homeWarn.Contains("0x0"), "home NEM multi-core warning missing key details");
            Assert(proWarn is not null && !proWarn.Contains("家庭版"), "non-home host incorrectly showed home edition clause");
            Assert(SetupOptions.MultiCoreNemWarning("virtualbox", 1, true, "CoreCountrySpecific") is null, "1-core config triggered multi-core warning");
            Assert(SetupOptions.MultiCoreNemWarning("virtualbox", 2, false, "CoreCountrySpecific") is null, "non-NEM host triggered NEM warning");
            Assert(SetupOptions.MultiCoreNemWarning("workstation", 4, true, "CoreCountrySpecific") is null, "non-VirtualBox platform triggered VirtualBox warning");
            Assert(!SetupWindow.ShouldOfferMultiCoreBlankRecovery("virtualbox", 2, true, TimeSpan.FromSeconds(24)), "blank recovery offered too early");
            Assert(!SetupWindow.ShouldOfferMultiCoreBlankRecovery("virtualbox", 1, true, TimeSpan.FromSeconds(30)), "blank recovery offered for 1-core VM");
            Assert(!SetupWindow.ShouldOfferMultiCoreBlankRecovery("workstation", 2, true, TimeSpan.FromSeconds(30)), "blank recovery offered for non-VirtualBox VM");
            Assert(SetupWindow.ShouldOfferMultiCoreBlankRecovery("virtualbox", 2, true, TimeSpan.FromSeconds(25)), "blank recovery not offered after 25s on 2-core VirtualBox");
        }));
        await Check("gateway-cleanup-removes-stale-hashed-executables-only", () => Sync(() => {
            var dir = Path.Combine(root, "gateway-cleanup");
            Directory.CreateDirectory(dir);
            var current = Path.Combine(dir, "ClaudeNetworkGateway.aaaaaaaaaaaaaaaa.exe");
            var stale = Path.Combine(dir, "ClaudeNetworkGateway.bbbbbbbbbbbbbbbb.exe");
            var locked = Path.Combine(dir, "ClaudeNetworkGateway.cccccccccccccccc.exe");
            var custom = Path.Combine(dir, "ClaudeNetworkGateway.custom.exe");
            File.WriteAllText(current, "cur");
            File.WriteAllText(stale, "old");
            File.WriteAllText(locked, "busy");
            File.WriteAllText(custom, "keep");
            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                ExternalGateway.CleanupStaleExecutables(dir, current);
            Assert(File.Exists(current) && !File.Exists(stale) && File.Exists(locked) && File.Exists(custom), "stale gateway cleanup deleted wrong files or failed on locked file");
        }));
        await Check("claudome-core-is-cross-platform-and-free-of-wpf-or-registry-dependencies", () => Sync(() => {
            var coreAssembly = typeof(SetupOptions).Assembly;
            var referenced = coreAssembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
            var forbidden = new[] { "PresentationFramework", "PresentationCore", "WindowsBase", "System.Management", "Microsoft.Win32.Registry" };
            Assert(!referenced.Any(name => forbidden.Contains(name, StringComparer.OrdinalIgnoreCase)),
                "Claudome.Core unexpectedly referenced Windows-only assembly: " + string.Join(", ", referenced.Where(name => forbidden.Contains(name, StringComparer.OrdinalIgnoreCase))));
            Assert(HostPlatform.Environment is WindowsHostEnvironment, "WindowsHostEnvironment module initializer did not register");
            Assert(Catalog.Resources.Count >= 6 && Catalog.EmbeddedText("Configure.ps1").StartsWith("#requires -Version 7.0"),
                "Claudome.Core embedded catalog or guest scripts missing");
        }));
        await Check("gateway-heartbeat-survives-transient-host-load-hiccup-and-fails-closed-on-persistent-tamper", async () => {
            var transientCalls = 0;
            await ExternalGateway.RunResilientHeartbeat(_ => {
                if (++transientCalls < 3) throw new SetupException("transient VBoxSVC COM busy");
                return Task.CompletedTask;
            }, () => true, 5, CancellationToken.None);
            Assert(transientCalls == 3, "transient host jitter caused premature gateway disconnect");

            var persistentCalls = 0;
            var closed = false;
            try {
                await ExternalGateway.RunResilientHeartbeat(_ => {
                    persistentCalls++;
                    throw new SetupException("real boundary tamper");
                }, () => true, 5, CancellationToken.None);
            } catch (SetupException) { closed = true; }
            Assert(closed && persistentCalls == 3, "persistent tamper did not fail closed");
        });
        await Check("network-import-accepts-modern-mihomo-fields-while-still-blocking-plugins-and-host-interface", () => Sync(() => {
            var modern = NetworkImport.Parse("proxies:\n- {name: modern-node, type: vless, server: node.example, port: 443, uuid: 11111111-1111-1111-1111-111111111111, tls: true, udp-over-tcp: true, mptcp: false, benchmark-url: https://cp.cloudflare.com}\n").Nodes.Single();
            Assert(modern.Name == "modern-node" && modern.Json.Contains("\"udp-over-tcp\"") && !modern.Json.Contains("benchmark-url"), "modern Mihomo subscription fields were rejected or metadata leaked");
            Reject(() => NetworkImport.Parse("proxies:\n- {name: escape, type: socks5, server: node.example, port: 1080, interface-name: Ethernet}\n"), "host interface-name bypass was allowed");
        }));
        await Check("vbox-usbtablet-sharing-allows-xhci-for-tablet-while-blocking-usb-filters", async () => {
            VirtualBoxBoundary.CheckSharing(SharingInfo);
            VirtualBoxBoundary.CheckSharing(SharingInfo.Replace("xhci=\"off\"", "xhci=\"on\""));
            Reject(() => VirtualBoxBoundary.CheckSharing(SharingInfo.Replace("xhci=\"off\"", "xhci=\"on\"\nUSBFilterActive1=\"on\"")), "active host USB passthrough filter was accepted");
            var state = OwnedVm("pointer-tablet-upgrade");
            state.Options.AcceptChanges = true;
            var curPointing = "ps2mouse";
            var fake = new FakeRunner(args => {
                if (args[0] == "list") return $"\"Claude-Workspace\" {{{state.VmId}}}\n";
                if (args[0] == "modifyvm" && args.Contains("--mouse") && args.Contains("usbtablet")) { curPointing = "usbtablet"; return ""; }
                return VmInfo(state, "poweroff", true) + $"hidpointing=\"{curPointing}\"\n";
            });
            await new VirtualBox(fake, "fixture-vbox").EnsureSharing(state, CancellationToken.None);
            Assert(curPointing == "usbtablet", "powered-off legacy PS/2 mouse VM was not upgraded to absolute usbtablet");
        });
        await Check("vbox-native-clipboard-defaults-on-after-additions-and-respects-task-opt-out", async () => {
            var state = OwnedVm("clipboard-consent");
            Directory.CreateDirectory(state.Options.Root);
            var clipboard = "disabled";
            var additions = 3;
            var fileTransfers = "off";
            var applies = true;
            var fake = new FakeRunner(args =>
            {
                if (args[0] == "list") return $"\"Claude-Workspace\" {{{state.VmId}}}\n";
                Assert(args[1] == state.VmId, "Clipboard was changed on another VM");
                if (args[0] == "showvminfo")
                    return VmInfo(state, "running", true)
                        .Replace("clipboard=\"disabled\"", $"clipboard=\"{clipboard}\"")
                        .Replace("clipboard_file_transfers=\"off\"", $"clipboard_file_transfers=\"{fileTransfers}\"") +
                        $"GuestAdditionsRunLevel={additions}\n";
                Assert(args is ["controlvm", _, "clipboard", "mode", "disabled" or "bidirectional"], "Unexpected clipboard change");
                if (applies) clipboard = args[4];
                return "";
            });
            await using var box = new VirtualBox(fake, "fixture-vbox");
            Assert(VirtualBoxClipboard.WantsSharedClipboard(state) && !VirtualBoxClipboard.IsAllowed(state), "Configured default enabled the sharing policy before activation.");
            additions = 1;
            Assert(!await VirtualBoxClipboard.RestoreDefault(box, state, CancellationToken.None) && clipboard == "disabled",
                "Clipboard enabled before the guest tools were ready.");
            additions = 3;
            fileTransfers = "on";
            await RejectAsync(() => VirtualBoxClipboard.Change(box, state, true, CancellationToken.None), "File transfers were allowed");
            fileTransfers = "off";
            additions = 1;
            await RejectAsync(() => VirtualBoxClipboard.Change(box, state, true, CancellationToken.None), "Clipboard was enabled before Guest Additions started");
            additions = 3;
            applies = false;
            await RejectAsync(() => VirtualBoxClipboard.RestoreDefault(box, state, CancellationToken.None), "No clipboard readback was accepted");
            Assert(!VirtualBoxClipboard.IsAllowed(state) && VirtualBoxClipboard.WantsSharedClipboard(state), "Failed enable retained permission or did not allow retry.");
            applies = true;
            Assert(await VirtualBoxClipboard.RestoreDefault(box, state, CancellationToken.None), "Shared clipboard did not start automatically.");
            Assert(VirtualBoxClipboard.IsAllowed(state) && clipboard == "bidirectional" && fileTransfers == "off", "Clipboard readback or task approval missing");
            var otherTask = OwnedVm("clipboard-consent");
            Assert(!VirtualBoxClipboard.IsAllowed(otherTask), "Consent crossed task identity");
            clipboard = "disabled";
            await VirtualBoxClipboard.RestoreDefault(box, state, CancellationToken.None);
            Assert(clipboard == "bidirectional", "Previously approved clipboard did not reconnect");
            await VirtualBoxClipboard.Change(box, state, false, CancellationToken.None);
            Assert(!VirtualBoxClipboard.IsAllowed(state) && clipboard == "disabled", "Disabling did not revoke both clipboard and approval");
            Assert(!VirtualBoxClipboard.WantsSharedClipboard(state) &&
                !await VirtualBoxClipboard.RestoreDefault(box, state, CancellationToken.None), "Explicit opt-out re-enabled clipboard.");
            Reject(() => VirtualBoxBoundary.CheckSharing(SharingInfo.Replace("clipboard=\"disabled\"", "clipboard=\"bidirectional\"")),
                "Boundary accepted an unapproved bidirectional clipboard");
        });
        await Check("vbox-fitted-window-centers-the-scaled-guest", () => Sync(() => {
            var fit = VirtualBoxDisplayFit.Calculate(new(1024, 720), new(1024, 768))!.Value;
            var fitted = VirtualBoxDisplayFit.FittedWindowSize(fit);
            Assert(fitted.Width < 1024 && fitted.Height <= 720 && fitted.Height - VirtualBoxDisplayFit.ScaledGuestSize(fit).Height >= 51,
                "Native Qt margins reduced the height available for the full guest frame");
        }));
        await Check("windows-seed-detects-iso-ui-language-and-includes-bypassnro-offline-oobe", () => Sync(() => {
            var fakeIso = Path.Combine(root, "fake-win11-zh.iso");
            File.WriteAllText(fakeIso, "PADDING\r\n[Available UI Languages]\r\nzh-cn = 3\r\n\r\n[Fallback Languages]\r\nzh-cn = en-us\r\n", Encoding.ASCII);
            Assert(WindowsSeed.DetectIsoUiLanguage(fakeIso) == "zh-CN", "failed to detect zh-CN media language from lang.ini block");
            var opts = new SetupOptions { UserName = "ClaudeUser", IsoPath = fakeIso, NetworkSource = "已有订阅链接", TimeZoneId = "Pacific Standard Time" };
            var xml = WindowsSeed.Answer(opts, "ValidPass!12345");
            Assert(xml.Contains("<UILanguage>zh-CN</UILanguage>") && xml.Contains("BypassNRO") && xml.Contains("BypassRAMCheck") && xml.Contains("HideOnlineAccountScreens"), "UEFI unattended XML missed UILanguage or BypassNRO OOBE protections");
            Assert(Catalog.EmbeddedText("VBoxSeedBootstrap.cmd").Contains("VBoxWindowsAdditions.exe"), "embedded VirtualBox Guest Additions seed bootstrap script missing");
        }));
        await Check("vbox-guest-stage-auto-mounts-additions-and-distinguishes-auth-mismatch", async () => {
            var state = OwnedVm("guest-stage-probe");
            var fakeVBoxDir = Path.Combine(root, "fake-vbox-bin");
            Directory.CreateDirectory(fakeVBoxDir);
            var fakeExe = Path.Combine(fakeVBoxDir, "VBoxManage.exe");
            var fakeAdditions = Path.Combine(fakeVBoxDir, "VBoxGuestAdditions.iso");
            File.WriteAllText(fakeExe, "stub");
            File.WriteAllText(fakeAdditions, "iso");
            var runLevel = 0;
            var sata2 = "D:\\\\ClaudeWorkspace\\\\private\\\\unattended\\\\setup-aux-iso.viso";
            var authFails = true;
            var setupResponse = "    SystemSetupInProgress    REG_DWORD    0x0\r\n";
            var fake = new FakeRunner(args => {
                if (args[0] == "showvminfo")
                    return VmInfo(state, "running", true) + $"GuestAdditionsRunLevel={runLevel}\n\"SATA-1-0\"=\"D:\\\\Windows.iso\"\n\"SATA-2-0\"=\"{sata2}\"\n\"SATA-3-0\"=\"none\"\n";
                if (args[0] == "storageattach") {
                    Assert(args.Contains("--port") && args[Array.IndexOf(args, "--port") + 1] == "2" && args.Contains("--forceunmount"), "did not hot-swap existing SATA-2-0 DVD slot");
                    sata2 = fakeAdditions.Replace("\\", "\\\\");
                    return "";
                }
                if (args[0] == "guestcontrol") {
                    Assert(args.Contains("--profile") &&
                        args[Array.IndexOf(args, "--exe") + 1].EndsWith("\\cmd.exe", StringComparison.OrdinalIgnoreCase) &&
                        args.Any(value => value.Contains("SystemSetupInProgress", StringComparison.Ordinal)) &&
                        !args.Any(value => value.Contains("whoami", StringComparison.OrdinalIgnoreCase)),
                        "Readiness used a separate direct executable or skipped loading the verified profile");
                    if (authFails)
                        throw new SetupException(CommandFailure.Describe(fakeExe, args, 1, "VBoxManage.exe: error: The specified user was not able to logon on guest\nVBOX_E_IPRT_ERROR"));
                    return setupResponse;
                }
                return "";
            });
            var vbox = new VirtualBox(fake, fakeExe);
            var loginChecks = 0;
            var stage1 = await vbox.ProbeGuestStage(state, new Secrets { VmPassword = "Password!12345" }, CancellationToken.None, () => loginChecks++);
            Assert(stage1.Stage == VirtualBoxGuestStage.WaitingForAdditions && stage1.AdditionsMounted && sata2.EndsWith("VBoxGuestAdditions.iso"), "did not auto-mount Guest Additions ISO onto running VM");
            Assert(loginChecks == 0, "Account verification was reported before Guest Additions");
            runLevel = 2;
            var stage2 = await vbox.ProbeGuestStage(state, new Secrets { VmPassword = "WrongPassword!123" }, CancellationToken.None, () => loginChecks++);
            Assert(stage2.Stage == VirtualBoxGuestStage.AuthMismatch && loginChecks == 1, "Manual OOBE credential mismatch or verification progress was missed");
            authFails = false;
            var stage3 = await vbox.ProbeGuestStage(state, new Secrets { VmPassword = "SyncedPassword!123" }, CancellationToken.None);
            Assert(stage3.Stage == VirtualBoxGuestStage.Ready, "synced guest credentials did not advance to Ready");
            setupResponse = "    SystemSetupInProgress    REG_DWORD    0x00000001\r\n";
            Assert((await vbox.ProbeGuestStage(state, new Secrets(), CancellationToken.None)).Stage == VirtualBoxGuestStage.NotReadyYet,
                "A nonzero setup flag was misread as complete.");
            setupResponse = "    OtherFlag    REG_DWORD    0x0\r\n";
            Assert((await vbox.ProbeGuestStage(state, new Secrets(), CancellationToken.None)).Stage == VirtualBoxGuestStage.SetupStatusUnreadable,
                "An unrelated registry value was accepted.");
            setupResponse = "";
            Assert((await vbox.ProbeGuestStage(state, new Secrets(), CancellationToken.None)).Stage == VirtualBoxGuestStage.SetupStatusUnreadable,
                "An empty guest response was interpreted as a system still installing.");
        });
        await Check("vbox-account-sync-verifies-login-and-uses-actual-profile-before-saving", async () => {
            var state = OwnedVm("guest-account-sync");
            var result = "LOCALAPPDATA=C:\\Users\\Other Folder\\AppData\\Local\r\n";
            var failLogin = false;
            var ready = true;
            var passwordFile = "";
            const string password = "FictionalPassword!42";
            var fake = new FakeRunner(args => {
                if (args[0] == "list") return $"\"Claude-Workspace\" {{{state.VmId}}}\n";
                if (args[0] == "showvminfo") return VmInfo(state, "running", true) + $"GuestAdditionsRunLevel={(ready ? 2 : 0)}\r\n";
                Assert(args[0] == "guestcontrol" && args[1] == state.VmId && args.Contains("--profile") &&
                    args[Array.IndexOf(args, "--username") + 1] == "Example User", "Credential probe did not use the proposed login and original VM");
                passwordFile = args[Array.IndexOf(args, "--passwordfile") + 1];
                Assert(!args.Contains(password) && File.ReadAllText(passwordFile) == password, "Credential passed outside the private password file");
                if (failLogin) throw new SetupException("VERR_LOGON_FAILURE");
                return result;
            });
            var box = new VirtualBox(fake, "fixture-vbox");
            var profile = await box.VerifyGuestLogin(state, "Example User", password, CancellationToken.None);
            Assert(profile == @"C:\Users\Other Folder\AppData\Local" && !File.Exists(passwordFile) &&
                state.Options.UserName == "ClaudeUser" && state.Options.GuestLocalAppData == "", "Login probe changed original state or left the password file");
            state.Options.GuestLocalAppData = profile;
            Assert(state.Options.GuestSetupDirectory == profile + @"\ClaudeEnvironmentSetup", "Verified profile was not used for deployment resources");
            failLogin = true;
            await RejectAsync(() => box.VerifyGuestLogin(state, "Example User", password, CancellationToken.None), "Rejected guest login passed");
            Assert(!File.Exists(passwordFile), "Failed guest login left credential file");
            failLogin = false;
            result = "LOCALAPPDATA=C:\\Windows\\System32\\config\\systemprofile\\AppData\\Local\r\n";
            await RejectAsync(() => box.VerifyGuestLogin(state, "Example User", password, CancellationToken.None), "Unscoped guest profile passed");
            ready = false;
            var probes = fake.Commands.Count(x => x[0] == "guestcontrol");
            await RejectAsync(() => box.VerifyGuestLogin(state, "Example User", password, CancellationToken.None), "Unavailable guest tools passed");
            Assert(fake.Commands.Count(x => x[0] == "guestcontrol") == probes, "Login was attempted without guest tools");
        });
        await Check("windows-sign-in-handoff-retains-only-the-active-vbox-installation-network", () => Sync(() => {
            var state = OwnedVm("retained-signin-network");
            state.Options.NetworkSource = "节点链接 / 本地配置";
            var next = new HandoffException(new("Fixture", [], "guest-auth-sync"));
            Assert(Deployment.CanHoldInstallationNetworkForWindowsSignIn(state, next, false), "Account handoff would cut the installation network");
            Assert(!Deployment.CanHoldInstallationNetworkForWindowsSignIn(state, next, true), "A cancelled installation retained a gateway");
            Assert(!Deployment.CanHoldInstallationNetworkForWindowsSignIn(state, new HandoffException(new("Other", [], "guest-wait")), false), "An unrelated handoff retained a gateway");
            Assert(!Deployment.CanHoldInstallationNetworkForWindowsSignIn(state, new SetupException("network"), false), "A network failure retained a gateway");
            state.Completed.Add("guest-ready");
            Assert(!Deployment.CanHoldInstallationNetworkForWindowsSignIn(state, next, false), "Completed Windows setup retained bootstrap networking");
            state.Completed.Clear();
            state.Options.PlatformId = "hyperv";
            Assert(!Deployment.CanHoldInstallationNetworkForWindowsSignIn(state, next, false), "Non-VirtualBox platform retained bootstrap networking");
            state.Options.PlatformId = "virtualbox";
            state.Options.NetworkSource = "稍后配置";
            Assert(!Deployment.CanHoldInstallationNetworkForWindowsSignIn(state, next, false), "Offline task could acquire a sign-in network");
            Assert(!BoundaryRuntime.TryHoldForWindowsSignIn(state, new VirtualBox(new FakeRunner(_ => ""), "fixture-vbox")), "Missing gateway could be retained");
        }));
        await Check("windows-sign-in-network-deadline-expires-at-the-boundary", () => Sync(() => {
            Assert(!ExternalGateway.WindowsSignInPeriodExpired(0, 60_000), "No pending handoff incorrectly expired");
            Assert(!ExternalGateway.WindowsSignInPeriodExpired(50_000, 49_999), "Active handoff expired before its deadline");
            Assert(ExternalGateway.WindowsSignInPeriodExpired(50_000, 50_000)
                && ExternalGateway.WindowsSignInPeriodExpired(50_000, 50_001), "Expired handoff could renew");
        }));
        await Check("node-exit-probe-respects-retry-after-and-keeps-http-and-transport-separate", async () => {
            var limited = new Uri("https://geo1.example.invalid/check");
            var other = new Uri("https://geo2.example.invalid/check");
            var cooldown = new NodeProbeCooldown();
            var callsToLimited = 0;
            var good = false;
            using var client = new HttpClient(new Handler(request => {
                if (request.RequestUri == limited)
                {
                    callsToLimited++;
                    var blocked = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    blocked.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                    return blocked;
                }
                if (!good) throw new TaskCanceledException("must not disclose raw endpoint details");
                return Reply(request, Encoding.UTF8.GetBytes("{\"ip\":\"8.8.8.8\",\"country\":\"US\",\"timezone\":\"America/New_York\"}"));
            }));
            try
            {
                await NodeExitProbe.ProbeEndpoints(client, [limited, other], cooldown, CancellationToken.None);
                throw new Exception("Geolocation unexpectedly passed");
            }
            catch (NodeProbeException error)
            {
                Assert(error.Message.Contains("HTTP 429") && error.Message.Contains("请求超时") &&
                    error.Message.Contains("继续原任务") && !error.Message.Contains("must not disclose"), "Mixed probe failures were merged or leaked");
            }
            Assert(cooldown.Remaining(limited, DateTimeOffset.UtcNow) > TimeSpan.FromSeconds(60), "HTTP retry-after ignored");
            good = true;
            var result = await NodeExitProbe.ProbeEndpoints(client, [limited, other], cooldown, CancellationToken.None);
            Assert(callsToLimited == 1 && result.CountryCode == "US", "Retry-after request was sent early or fallback failed");
            var now = DateTimeOffset.UtcNow;
            cooldown.Record(other, null, now);
            Assert(cooldown.Remaining(other, now.AddSeconds(59)) > TimeSpan.Zero &&
                cooldown.Remaining(other, now.AddSeconds(60)) == TimeSpan.Zero, "Rate limits without headers had no finite cooldown");
        });
        await Check("virtualbox-microsoft-guest-auth-passes-the-complete-email-without-a-second-domain", async () => {
            var state = OwnedVm("microsoft-login");
            const string account = "SampleUser";
            const string localPath = @"C:\Users\DifferentProfile\AppData\Local";
            const string email = "sample.user@example.invalid";
            const string password = "Fictional1";
            var returnedAlias = account;
            var paths = new List<string>();
            var fake = new FakeRunner(args => {
                if (args[0] == "list") return $"\"Claude-Workspace\" {{{state.VmId}}}\n";
                if (args[0] == "showvminfo") return VmInfo(state, "running", true) + "GuestAdditionsRunLevel=2\n";
                Assert(args[0] == "guestcontrol" && args[1] == state.VmId, "Wrong VM or operation");
                Assert(!args.Contains("--domain") &&
                    args[Array.IndexOf(args, "--username") + 1] == email, "Microsoft identity omitted or lost");
                var file = args[Array.IndexOf(args, "--passwordfile") + 1];
                paths.Add(file);
                Assert(File.ReadAllText(file) == password && !args.Contains(password), "Credential exposed as a CLI argument");
                return args[2] == "run" ? $"fixture\\{returnedAlias}\nLOCALAPPDATA={localPath}\n" : "";
            });
            var vbox = new VirtualBox(fake, "fixture-vbox");
            var profile = await vbox.VerifyGuestLogin(state, account, password, CancellationToken.None, email);
            Assert(profile == localPath && !state.Options.UsesMicrosoftGuestAccount, "Verification persisted or misread the profile");
            returnedAlias = "Unrelated";
            await RejectAsync(() => vbox.VerifyGuestLogin(state, account, password, CancellationToken.None, email), "Wrong Microsoft-linked Windows account accepted");
            state.Options.UsesMicrosoftGuestAccount = true;
            state.Options.GuestLocalAppData = profile;
            state.Options.UserName = account;
            var secret = new Secrets { VmPassword = password, VmMicrosoftAddress = email };
            var stateDoc = JsonSerializer.Serialize(state, JsonFile.Format);
            Assert(!stateDoc.Contains(email) && !stateDoc.Contains(password), "Account details disclosed in public state");
            await vbox.GuestRun(state, secret, @"C:\Windows\System32\whoami.exe", [], CancellationToken.None);
            await vbox.CopyTo(state, secret, Path.Combine(root, "sample.txt"), profile + @"\sample.txt", CancellationToken.None);
            await vbox.CopyFrom(state, secret, profile + @"\sample.txt", Path.Combine(root, "sample.txt"), CancellationToken.None);
            await RejectAsync(() => vbox.GuestRun(state, new Secrets { VmPassword = password }, "guest.exe", [], CancellationToken.None), "Missing Microsoft address fell back to the local username");
            Assert(paths.Count == 5 && paths.All(path => !File.Exists(path)), "Scoped credential calls left a temporary password file");
            SecretStore.SaveMerged(state.Options.Private, secret, false, true);
            Assert(SecretStore.Load(state.Options.Private)?.VmMicrosoftAddress == email, "Microsoft identity not stored with the encrypted secret");
            SecretStore.SaveMerged(state.Options.Private, new Secrets { VmPassword = "DifferentLogin1" }, true, true);
            Assert(SecretStore.Load(state.Options.Private)?.VmMicrosoftAddress == "", "Switching to a local account retained the prior Microsoft identity");
        });
        var report = new { dateUtc = DateTime.UtcNow, type = "isolated local fixtures; no VM installation, real subscription or host network changes", passed = rows.Count - failed, failed, tests = rows };
        JsonFile.Write(Path.Combine(output, "installer-tests.json"), report);
        Console.WriteLine(JsonSerializer.Serialize(new { passed = rows.Count - failed, failed }));
        return failed == 0 ? 0 : 1;
    }
    private static HttpResponseMessage Reply(HttpRequestMessage request, byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) => new(status) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
    private sealed class ProgressSink<T>(List<T> rows) : IProgress<T> { public void Report(T value) => rows.Add(value); }
    private sealed class FakeConsole : IVmConsole
    {
        public int Calls, Attaches;
        public bool TryShow(string machineId) { Calls++; return true; }
        public void Attach(string virtualBoxDirectory, string machineId) { Attaches++; }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }
    private sealed class FakeRunner(Func<string[], string> callback) : ICommandRunner
    {
        public List<string[]> Commands { get; } = [];
        public Task<string> Run(string exe, IEnumerable<string> args, CancellationToken token, int seconds = 120)
        {
            var command = args.ToArray(); Commands.Add(command); return Task.FromResult(callback(command));
        }
    }
}
