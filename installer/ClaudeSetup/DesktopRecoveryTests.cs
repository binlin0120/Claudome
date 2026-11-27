using System.IO;
using System.Text.Json;

namespace ClaudeSetup;

public static class DesktopRecoveryTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task Run(Func<string,Func<Task>,Task> check,string root)
    {
        Task Sync(Action action){action();return Task.CompletedTask;}
        const string id="11111111-1111-1111-1111-111111111111";
        await check("console-identity-supports-vbox-versions-and-exact-executable",()=>Sync(()=>{
            foreach(var command in new[]{"VirtualBoxVM.exe --startvm "+id+" --separate","VirtualBoxVM.exe --startvm={"+id+"}","VirtualBoxVM.exe --comment Fixture --startvm \""+id+"\""})
                Assert(ConsoleWindowLocator.MatchesIdentity(new(1,@"C:\Oracle\VirtualBoxVM.exe",command),id,@"C:\Oracle\VirtualBoxVM.exe"),"valid VBox command rejected");
            Assert(!ConsoleWindowLocator.MatchesIdentity(new(1,@"C:\Other\VirtualBoxVM.exe","--startvm "+id),id,@"C:\Oracle\VirtualBoxVM.exe"),"foreign executable claimed");
            Assert(!ConsoleWindowLocator.MatchesIdentity(new(1,@"C:\Oracle\VirtualBoxVM.exe","--startvm "+id+"0"),id,@"C:\Oracle\VirtualBoxVM.exe"),"UUID prefix claimed");
        }));
        await check("console-identity-hyperv-name-and-vmware-config-are-bounded",()=>Sync(()=>{
            Assert(ConsoleWindowLocator.MatchesIdentity(new(1,@"C:\Windows\System32\vmconnect.exe","vmconnect.exe localhost \"Fixture VM\""),id,@"C:\Windows\System32\vmconnect.exe","Fixture VM"),"Hyper-V quoted name rejected");
            Assert(!ConsoleWindowLocator.MatchesIdentity(new(1,@"C:\Windows\System32\vmconnect.exe","vmconnect.exe localhost \"Fixture VM Copy\""),id,@"C:\Windows\System32\vmconnect.exe","Fixture VM"),"different Hyper-V VM claimed");
            Assert(ConsoleWindowLocator.MatchesIdentity(new(1,@"C:\VMware\vmware.exe","vmware.exe -n \"D:\\Fixture VM\\Fixture.vmx\""),id,@"C:\VMware\vmware.exe","Fixture",@"D:\Fixture VM\Fixture.vmx"),"VMware config path rejected");
            Assert(!ConsoleWindowLocator.MatchesIdentity(new(1,@"C:\VMware\vmware.exe","vmware.exe -n \"D:\\Fixture VM Copy\\Fixture.vmx\""),id,@"C:\VMware\vmware.exe","Fixture",@"D:\Fixture VM\Fixture.vmx"),"foreign VMware path claimed");
            Assert(ConsoleWindowLocator.MatchesVmwareCaption("Fixture - VMware Workstation","Fixture")&&!ConsoleWindowLocator.MatchesVmwareCaption("Fixture Copy - VMware Workstation","Fixture"),"VMware tab caption accepted another VM");
        }));
        await check("hyperv-zoom-matches-only-the-native-view-auto-options", () => Sync(() => {
            Assert(HyperVConsoleZoom.MatchesMenu("查看(&V)", 0), "Chinese View mnemonic was rejected");
            Assert(HyperVConsoleZoom.MatchesMenu("&Zoom Level", 1), "English zoom menu was rejected");
            Assert(HyperVConsoleZoom.MatchesMenu("自动(&A)", 2) && HyperVConsoleZoom.MatchesMenu("&Auto", 2),
                "Automatic zoom options were rejected");
            Assert(!HyperVConsoleZoom.MatchesMenu("增强会话", 0) &&
                !HyperVConsoleZoom.MatchesMenu("100%", 2) && !HyperVConsoleZoom.MatchesMenu("自动连接", 2),
                "Unrelated menu command was accepted");
            Assert(!HyperVConsoleZoom.TrySelectAuto(new(IntPtr.Zero, Environment.ProcessId, @"C:\Windows\System32\vmconnect.exe"), CancellationToken.None),
                "Stale console handle was accepted");
        }));
        await check("desktop-automatic-retry-cannot-reopen-a-closed-console",()=>Sync(()=>{
            var policy=new DesktopConnectionPolicy();policy.ObserveRunning();Assert(policy.TryLaunchFrontend(),"initial launch denied");
            Assert(!policy.TryLaunchFrontend(),"duplicate frontend launch allowed");policy.ObserveWindow();policy.Suspend();
            for(var i=0;i<50;i++){policy.ObserveRunning();Assert(!policy.TryLaunchFrontend()&&policy.Suspended,"periodic status reset a user close");}
            policy.ResumeExplicitly();Assert(policy.TryLaunchFrontend(),"explicit continuation did not reset launch gate");
        }));
        await check("desktop-poweroff-stops-reconnect-without-creating-a-new-vm",()=>Sync(()=>{
            var policy=new DesktopConnectionPolicy();policy.ObserveStopped();Assert(!policy.Suspended,"never-started VM treated as user close");
            policy.ObserveRunning();policy.ObserveStopped();Assert(policy.Suspended&&!policy.TryLaunchFrontend(),"powered-off VM allowed restart");
        }));
        await check("checkpoint-keeps-only-the-previous-valid-task",()=>Sync(()=>{
            var directory=Path.Combine(root,"checkpoint-pair");Paths.EnsureWorkspace(directory);
            var state=new SessionState{VmId=id,Options=new SetupOptions{Root=directory}};state.Completed.Add("machine");state.Save();
            state.Completed.Add("windows-started");state.Save();
            var previous=JsonSerializer.Deserialize<SessionState>(File.ReadAllText(TaskCheckpointStore.BackupPath(state.Options.StatePath)),JsonFile.Format)!;
            Assert(previous.Completed.Contains("machine")&&!previous.Completed.Contains("windows-started"),"previous checkpoint was not retained");
            Assert(Directory.GetFiles(directory,"*.new").Length==0,"unfinished checkpoint leaked");
        }));
        await check("checkpoint-truncated-primary-recovers-last-valid-progress",()=>Sync(()=>{
            var directory=Path.Combine(root,"checkpoint-recovery");Paths.EnsureWorkspace(directory);
            var state=new SessionState{VmId=id,Options=new SetupOptions{Root=directory}};state.Completed.Add("machine");state.Save();state.Completed.Add("windows-started");state.Save();
            File.WriteAllText(state.Options.StatePath,"{\"Options\":");
            var recovered=JsonFile.Read<SessionState>(state.Options.StatePath);
            Assert(recovered.VmId==id&&recovered.Completed.Contains("machine")&&!recovered.Completed.Contains("windows-started")&&recovered.Status.Contains("恢复"),"valid backup not recovered");
            recovered.Save();Assert(JsonFile.Read<SessionState>(state.Options.StatePath).VmId==id,"recovered state not saved");
        }));
        await check("checkpoint-fallback-cannot-import-another-directory",()=>Sync(()=>{
            var directory=Path.Combine(root,"checkpoint-foreign");Paths.EnsureWorkspace(directory);var state=new SessionState{VmId=id,Options=new SetupOptions{Root=directory}};state.Save();
            File.WriteAllText(state.Options.StatePath,"{");
            var foreign=new SessionState{VmId=id,Options=new SetupOptions{Root=Path.Combine(root,"different-task")}};
            JsonFile.Write(TaskCheckpointStore.BackupPath(state.Options.StatePath),foreign);
            try{JsonFile.Read<SessionState>(state.Options.StatePath);throw new Exception("Expected recovery identity failure");}catch(SetupException){}
        }));
        await check("checkpoint-semantically-empty-or-null-primary-recovers-backup",()=>Sync(()=>{
            var directory=Path.Combine(root,"checkpoint-semantic-corrupt");Paths.EnsureWorkspace(directory);
            var state=new SessionState{VmId=id,Options=new SetupOptions{Root=directory}};state.Completed.Add("machine");state.Save();state.Completed.Add("windows-started");state.Save();
            File.WriteAllText(state.Options.StatePath,"{\"schema\":1,\"options\":null}");
            var recoveredFromNull=JsonFile.Read<SessionState>(state.Options.StatePath);
            Assert(recoveredFromNull.VmId==id&&recoveredFromNull.Completed.Contains("machine")&&!recoveredFromNull.Completed.Contains("windows-started")&&recoveredFromNull.Status.Contains("恢复"),"null-options primary did not trigger backup recovery");
            File.WriteAllText(state.Options.StatePath,"{\"schema\":1,\"options\":{\"Root\":\"\",\"VmName\":\"\",\"WorkspaceId\":\"\"}}");
            var recoveredFromBlank=JsonFile.Read<SessionState>(state.Options.StatePath);
            Assert(recoveredFromBlank.VmId==id&&recoveredFromBlank.Completed.Contains("machine")&&recoveredFromBlank.Status.Contains("恢复"),"empty-fields primary did not trigger backup recovery");
            File.WriteAllText(TaskCheckpointStore.BackupPath(state.Options.StatePath),"{\"schema\":1,\"options\":null}");
            try{JsonFile.Read<SessionState>(state.Options.StatePath);throw new Exception("Expected failure when both checkpoints are semantically invalid");}catch(SetupException){}

            // WX02 / C05: Recover from semantically bad primary (empty VmName), save recovered state, then corrupt primary again -> backup MUST still be intact and recoverable!
            var d2 = Path.Combine(root, "checkpoint-double-recovery"); Paths.EnsureWorkspace(d2);
            var s2 = new SessionState { VmId = id, Options = new SetupOptions { Root = d2, VmName = "Win11Box" } };
            s2.Completed.Add("machine"); s2.Save();
            s2.Completed.Add("windows-started"); s2.Save();
            var primaryTxt = File.ReadAllText(s2.Options.StatePath).Replace("\"VmName\": \"Win11Box\"", "\"VmName\": \"\"");
            File.WriteAllText(s2.Options.StatePath, primaryTxt);
            var firstRecovery = JsonFile.Read<SessionState>(s2.Options.StatePath);
            Assert(firstRecovery.Options.VmName == "Win11Box" && firstRecovery.Completed.Contains("machine"), "first recovery failed");
            firstRecovery.Save();
            var backupStillValid = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(TaskCheckpointStore.BackupPath(s2.Options.StatePath)), JsonFile.Format);
            Assert(backupStillValid?.Options.VmName == "Win11Box", "saving recovered state overwrote good backup with semantically invalid primary");
            File.WriteAllText(s2.Options.StatePath, "{\"broken\":");
            var secondRecovery = JsonFile.Read<SessionState>(s2.Options.StatePath);
            Assert(secondRecovery.Options.VmName == "Win11Box" && secondRecovery.Completed.Contains("machine"), "second recovery failed after first save");
        }));
        await check("desktop-16-by-9-frame-computes-centered-max-rect-across-window-sizes", () => Sync(() => {
            var wide = SetupWindow.ComputeCentered16By9Frame(1920, 900);
            Assert(wide.Width == 1600 && wide.Height == 900 && wide.X == 160 && wide.Y == 0,
                $"unexpected wide 16:9 frame: {wide}");
            var tall = SetupWindow.ComputeCentered16By9Frame(1000, 800);
            Assert(tall.Width == 1000 && tall.Height == 562 && tall.X == 0 && tall.Y == 119,
                $"unexpected tall 16:9 frame: {tall}");
            var exact = SetupWindow.ComputeCentered16By9Frame(1280, 720);
            Assert(exact.Width == 1280 && exact.Height == 720 && exact.X == 0 && exact.Y == 0,
                $"unexpected exact 16:9 frame: {exact}");
            var expandedTop = SetupWindow.ComputeCentered16By9Frame(1400, 620);
            var collapsedTop = SetupWindow.ComputeCentered16By9Frame(1400, 740);
            Assert(collapsedTop.Height > expandedTop.Height && collapsedTop.Width > expandedTop.Width &&
                Math.Abs((collapsedTop.Width / collapsedTop.Height) - (16.0 / 9.0)) < 0.02,
                $"collapsing top bar did not grow 16:9 frame properly: {expandedTop} -> {collapsedTop}");
            var invalid = SetupWindow.ComputeCentered16By9Frame(0, 500);
            Assert(invalid.Width == 0 && invalid.Height == 0, "invalid viewport should produce empty rect");
        }));
        await check("hyperv-live-surface-clip-never-blindly-clips-and-fixes-standalone-style", () => Sync(() => {
            var childBounds = new System.Windows.Rect(0, 0, 1300, 800);
            var candidates = new NativeConsoleHost.SurfaceCandidate[]
            {
                new("WindowsForms10.Window.8.app", new(0, 0, 1300, 800), true, 4242),
                new("IHWindowClass", new(4, 62, 1280, 720), true, 9999), // wrong PID
                new("IHWindowClass", new(4, 62, 1280, 720), false, 4242), // hidden
                new("IHWindowClass", new(4, 68, 1280, 720), true, 4242) // credible live surface (non-hardcoded 68px top)
            };
            var selected = NativeConsoleHost.SelectCredibleHyperVGuestSurface(candidates, childBounds, 4242);
            Assert(selected is { } s && s.Left == 4 && s.Top == 68 && s.Width == 1280 && s.Height == 720,
                $"failed to select credible guest render surface: {selected}");

            var plan = NativeConsoleHost.ComputeLiveChromeClipPlan(
                new(1280, 720), new(1300, 818), selected, measuredBottomChromeHeight: 30);
            Assert(plan is { } p && p.ChildSize.Width == 1288 && p.ChildSize.Height == 818 &&
                p.ClipRect.Top == 68 && p.OffsetX == -4 && p.OffsetY == -68,
                $"live chrome clip plan mismatch: {plan}");

            // Missing credible surface must return null (never blindly clip with hardcoded offsets).
            var noSurface = NativeConsoleHost.SelectCredibleHyperVGuestSurface(
                [new("WindowsForms10.Window.8.app", new(0, 0, 1300, 800), true, 4242)], childBounds, 4242);
            Assert(noSurface is null &&
                NativeConsoleHost.ComputeLiveChromeClipPlan(new(1280, 720), new(1300, 800), noSurface) is null,
                "missing surface must not produce a blind clip plan");

            // Embedded style strips titlebar/frame/popup and adds WS_CHILD; detached restores WS_OVERLAPPEDWINDOW without WS_CHILD.
            const long wsOverlappedWindow = 0x00CF0000L;
            const long wsChild = 0x40000000L;
            var embedded = NativeConsoleHost.ComputeEmbeddedChildStyle(wsOverlappedWindow);
            Assert((embedded & wsChild) == wsChild && (embedded & wsOverlappedWindow) == 0,
                $"embedded style did not strip caption/frame: 0x{embedded:X}");
            var detached = NativeConsoleHost.ComputeDetachedStandaloneStyle(embedded);
            Assert((detached & wsChild) == 0 && (detached & wsOverlappedWindow) == wsOverlappedWindow,
                $"detached style did not restore overlapped window controls: 0x{detached:X}");
        }));
        await check("hyperv-route-b-profile-locks-basic-vmrdp-isolation-and-source-aspect-geometry", () => Sync(() => {
            var badRejected = false;
            try { HyperVRdpConnectionProfile.CreateVerifiedBasic("not-a-guid"); }
            catch (SetupException) { badRejected = true; }
            Assert(badRejected, "Route B profile must reject unverified non-GUID VM ID");

            var p = HyperVRdpConnectionProfile.CreateVerifiedBasic(id);
            Assert(p.Server == "localhost" && p.Port == 2179 && p.Pcb == id && !p.Pcb.Contains("EnhancedMode") &&
                p.AuthenticationLevel == 0 && !p.NegotiateSecurityLayer && p.EnableCredSspSupport &&
                p.DisableCredentialsDelegation && p.SmartSizing &&
                !p.RedirectDrives && !p.RedirectPrinters && !p.RedirectPorts && !p.RedirectSmartCards &&
                !p.RedirectPosDevices && !p.RedirectDevices && !p.RedirectDynamicDevices && !p.RedirectDynamicDrives &&
                p.AudioRedirectionMode == 2 && !p.AudioCaptureRedirectionMode && !p.RedirectClipboard,
                $"Route B Basic VMRDP isolation profile violated: {p}");

            Assert(HyperVRdpDisplayGeometry.ShouldUseRouteB("hyperv", guestReady: true, explicitFallbackToA: false) &&
                !HyperVRdpDisplayGeometry.ShouldUseRouteB("hyperv", guestReady: false, explicitFallbackToA: false) &&
                !HyperVRdpDisplayGeometry.ShouldUseRouteB("hyperv", guestReady: true, explicitFallbackToA: true) &&
                !HyperVRdpDisplayGeometry.ShouldUseRouteB("virtualbox", guestReady: true, explicitFallbackToA: false),
                "Route B vs Route A selection policy mismatch");

            var fhd = HyperVRdpDisplayGeometry.ComputeForSource(1920, 900, 1920, 1080);
            Assert(fhd.IsSource16By9 && fhd.Bounds.Width == 1600 && fhd.Bounds.Height == 900 && fhd.StatusText.Contains("16:9"),
                $"16:9 source geometry mismatch: {fhd}");

            var non16By9 = HyperVRdpDisplayGeometry.ComputeForSource(1600, 900, 1024, 768);
            Assert(!non16By9.IsSource16By9 && non16By9.Bounds.Width == 1200 && non16By9.Bounds.Height == 900 &&
                non16By9.StatusText.Contains("非 16:9"),
                $"4:3 source geometry did not preserve full aspect ratio: {non16By9}");
        }));
        await check("confirmed-vmconnect-close-generation-guard-and-mandatory-isolation-no-swallow", async () => {
            var alive = true;
            var closeSent = 0;
            var closedOk = await ConsoleWindowLocator.CloseAndWaitExitAsync(
                () => alive,
                () => { closeSent++; alive = false; },
                TimeSpan.FromMilliseconds(400));
            Assert(closedOk && closeSent == 1, "CloseAndWaitExitAsync must confirm exit when window closes");

            var stillAlive = true;
            var timedOut = await ConsoleWindowLocator.CloseAndWaitExitAsync(
                () => stillAlive,
                () => { },
                TimeSpan.FromMilliseconds(120));
            Assert(!timedOut, "CloseAndWaitExitAsync must return false on timeout without faking release");

            Assert(HyperVRdpConsoleHost.ShouldProcessGenerationEvent(2, 2) &&
                !HyperVRdpConsoleHost.ShouldProcessGenerationEvent(1, 2) &&
                !HyperVRdpConsoleHost.ShouldProcessGenerationEvent(0, 0),
                "stale COM generation events must be ignored");

            var profile = HyperVRdpConnectionProfile.CreateVerifiedBasic(id);
            var threwOnMissingExtendedSettings = false;
            try { HyperVRdpConsoleHost.ApplyProfile(new object(), profile); }
            catch (SetupException) { threwOnMissingExtendedSettings = true; }
            Assert(threwOnMissingExtendedSettings, "ApplyProfile must throw SetupException when mandatory binding/isolation cannot be written");

            // Verify real local MsRdpClient9 COM instance accepts every mandatory Route B binding & isolation property with zero errors.
            var rdpType = Type.GetTypeFromCLSID(new Guid("a3bc03a0-041d-42e3-ad22-882b7865c9c5"));
            if (rdpType is not null)
            {
                Exception? staError = null;
                var sta = new Thread(() =>
                {
                    try
                    {
                        var comObj = Activator.CreateInstance(rdpType) ?? throw new InvalidOperationException("MsRdpClient9 null");
                        try { HyperVRdpConsoleHost.ApplyProfile(comObj, profile); }
                        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(comObj); }
                    }
                    catch (Exception ex) { staError = ex; }
                });
                sta.SetApartmentState(ApartmentState.STA);
                sta.Start();
                sta.Join();
                Assert(staError is null, $"Real MsRdpClient9 ApplyProfile failed: {staError}");
            }
        });
        await check("manual-adspower-readonly-saved-network-and-hyperv-manual-only-new-tasks", () => Sync(() => {
            Assert(Deployment.ShouldUseReadOnlySavedNetworkCheck(checkSavedNetwork: true, manualAdsPower: true, resumeNetwork: true),
                "manual AdsPower checkSavedNetwork must route to strictly read-only path");
            Assert(!Deployment.ShouldUseReadOnlySavedNetworkCheck(checkSavedNetwork: false, manualAdsPower: true, resumeNetwork: true),
                "explicit reconfigure must not be hijacked as read-only");
            Assert(!Deployment.ShouldUseReadOnlySavedNetworkCheck(checkSavedNetwork: true, manualAdsPower: true, resumeNetwork: false),
                "missing saved network checkpoint must not run read-only saved check");

            Assert(SetupOptions.ShouldForceManualAdsPower(isHyperVStandalone: true, platformId: "hyperv", vmId: ""),
                "new Hyper-V standalone task must force ManualAdsPower=true");
            Assert(!SetupOptions.ShouldForceManualAdsPower(isHyperVStandalone: true, platformId: "hyperv", vmId: id),
                "existing saved Hyper-V task must preserve its saved mode without wiping credentials");
            Assert(!SetupOptions.ShouldForceManualAdsPower(isHyperVStandalone: false, platformId: "virtualbox", vmId: ""),
                "non-HyperV-standalone task must not force manual mode");
        }));
        await check("route-b-direct-clipboard-protocol-unicode-only-isolation-and-loop-suppression", async () => {
            var busId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var clipId1 = ClipboardProtocol.DeriveServiceId(busId);
            var clipId2 = ClipboardProtocol.DeriveServiceId(busId);
            Assert(clipId1 == clipId2 && clipId1 != busId, "DeriveServiceId must be deterministic and distinct from bus ServiceId");

            Assert(!ClipboardProtocol.ShouldForwardClipboardSnapshot(hasFileDropList: true, hasUnicodeText: true, "C:\\secret.txt", out _),
                "file drop list must be strictly blocked even when text is also present");
            Assert(!ClipboardProtocol.ShouldForwardClipboardSnapshot(hasFileDropList: false, hasUnicodeText: false, "ignored", out _),
                "image-only or non-text clipboard must be ignored");
            Assert(!ClipboardProtocol.ShouldForwardClipboardSnapshot(hasFileDropList: false, hasUnicodeText: true, "abc\0def", out _),
                "NUL character must be rejected");
            Assert(!ClipboardProtocol.ShouldForwardClipboardSnapshot(hasFileDropList: false, hasUnicodeText: true, new string('x', ClipboardProtocol.MaxTextLength + 1), out _),
                "oversized clipboard text must be rejected");
            Assert(ClipboardProtocol.ShouldForwardClipboardSnapshot(hasFileDropList: false, hasUnicodeText: true, "Hello 世界 123", out var clean) && clean == "Hello 世界 123",
                "valid Unicode plain text must be forwarded");

            var dedup = new ClipboardSyncDeduplicator();
            var t0 = DateTime.UtcNow;
            dedup.Reset("task-a", 1, 10);
            Assert(dedup.ShouldSendLocalChange("task-a", 1, 11, "host copy", t0), "new local copy should be sent");
            Assert(!dedup.ShouldSendLocalChange("task-a", 1, 11, "host copy", t0), "same clipboard sequence must not resend");
            Assert(dedup.TryBeginApplyRemote("task-a", 1, "guest copy", t0.AddMilliseconds(500), out var remoteHash), "remote update should apply");
            dedup.CompleteApplyRemote("task-a", 1, 12, remoteHash, t0.AddMilliseconds(510));
            Assert(!dedup.ShouldSendLocalChange("task-a", 1, 12, "guest copy", t0.AddMilliseconds(600)), "echo of applied remote text must be suppressed by seq");
            Assert(!dedup.ShouldSendLocalChange("task-a", 1, 13, "guest copy", t0.AddMilliseconds(700)), "echo of applied remote text must be suppressed by hash window");
            // Switching task/generation immediately blocks old task events and prevents replay.
            dedup.Reset("task-b", 2, 13);
            Assert(!dedup.ShouldSendLocalChange("task-a", 1, 14, "old task", t0.AddSeconds(1)), "old task local change must be ignored");
            Assert(!dedup.TryBeginApplyRemote("task-a", 1, "old remote", t0.AddSeconds(1), out _), "old task remote change must be ignored");

            // End-to-end mutual HMAC handshake (CLDCLP01) + framed plain-text round-trip over paired duplex pipes.
            using var c2s = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
            using var c2sRead = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, c2s.GetClientHandleAsString());
            using var s2c = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
            using var s2cRead = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, s2c.GetClientHandleAsString());
            await using var clientStream = new DuplexTestStream(s2cRead, c2s);
            await using var serverStream = new DuplexTestStream(c2sRead, s2c);
            var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var serverAuth = ClipboardProtocol.AuthenticateAsync(serverStream, key, server: true, CancellationToken.None);
            var clientAuth = ClipboardProtocol.AuthenticateAsync(clientStream, key, server: false, CancellationToken.None);
            await Task.WhenAll(serverAuth, clientAuth);

            await ClipboardProtocol.WriteFrameAsync(clientStream, new(ClipboardFrameType.TextUpdate, 7, 2, "双向直贴测试 ✓"), CancellationToken.None);
            var received = await ClipboardProtocol.ReadFrameAsync(serverStream, CancellationToken.None);
            Assert(received is { Type: ClipboardFrameType.TextUpdate, Sequence: 7, Generation: 2, Text: "双向直贴测试 ✓" },
                $"ClipboardProtocol round-trip mismatch: {received}");
        });
    }

    private sealed class DuplexTestStream(System.IO.Stream reader, System.IO.Stream writer) : System.IO.Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => writer.Flush();
        public override Task FlushAsync(CancellationToken token) => writer.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => reader.ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => writer.WriteAsync(buffer, token);
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
