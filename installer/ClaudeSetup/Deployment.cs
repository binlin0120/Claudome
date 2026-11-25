using System.IO;
using Claudome.Transport;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed partial class Deployment(IProgress<string> messages, IProgress<DownloadProgress> downloads, IProgress<InstallationProgress>? installation = null)
{
    private readonly CommandRunner runner = new();
    public async Task PrepareWindowsMedia(SetupOptions o, CancellationToken token)
    {
        Machine.RequireHyperVStandaloneHostEligible();
        Paths.EnsureWorkspace(o.Root);
        using var downloader = new Downloader();
        var resource = Catalog.Resources.Single(r => r.Id == "windows-media");
        messages.Report("正在获取并核验微软官方媒体创建工具");
        var path = await downloader.Get(resource, o.Cache, downloads, token);
        token.ThrowIfCancellationRequested();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" }); }
        catch { throw new SetupException("微软媒体创建工具未打开，请确认管理员提示后重试。"); }
    }
    public async Task<string> DownloadIso(SetupOptions o, CancellationToken token)
    {
        Machine.RequireHyperVStandaloneHostEligible();
        Paths.EnsureWorkspace(o.Root);
        if (!Uri.TryCreate(o.IsoUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !new[] { "microsoft.com", "microsoftonline.com", "windowsupdate.com" }.Any(d => uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))) throw new SetupException("请输入微软官方下载的 HTTPS 直链。链接过期时请重新获取，也可以选择已有 ISO。");
        if (!Regex.IsMatch(o.IsoSha256, "^[a-fA-F0-9]{64}$")) throw new SetupException("请填写微软公布的 SHA-256，再下载镜像。");
        using var downloader = new Downloader();
        return await downloader.Get(new("windows", "Windows 11 x64 镜像", "Windows11-x64.iso", o.IsoUrl, o.IsoSha256, 0, "Microsoft Windows license"), o.Cache, downloads, token);
    }
    public async Task DownloadResources(SetupOptions o, CancellationToken token)
    {
        Machine.RequireHyperVStandaloneHostEligible();
        Paths.EnsureWorkspace(o.Root);
        using var downloader = new Downloader();
        foreach (var resource in Catalog.Resources)
        {
            if (resource.Id == "windows-media" || resource.Id=="bootstrap-core" && o.NetworkSource=="稍后配置") continue;
            if (resource.Id == "vbox" && (o.PlatformId != "virtualbox" || Machine.VBoxPath() is not null)) continue;
            messages.Report("准备并核验：" + resource.Name);
            await downloader.Get(resource, o.Cache, downloads, token);
        }
    }
    private void Stage(SessionState state, string title) { state.Stage = title; state.Status = "进行中"; state.Save(); messages.Report(title); installation?.Report(new(title)); }
    public async Task Run(SessionState state, Secrets secrets, CancellationToken token, bool preserveUnloadedSecrets = false, bool checkSavedNetwork = false, bool reconfigureGuestNetwork = false)
    {
        Machine.RequireHyperVStandaloneHostEligible();
        var o = state.Options;
        try { IsolationPolicy.RequireSupportedPlatform(state); }
        catch (HandoffException handoff) { Paths.EnsureWorkspace(o.Root); using var handoffLock = WorkspaceLock.Acquire(state); state.PendingActions = [handoff.Action]; state.Status = "所选平台尚不支持受控通道"; state.Save(); throw; }
        var previousBackend=BoundaryRuntime.BackendFor(state);
        var box = previousBackend ?? Backends.Create(o.PlatformId, installation); var retained = previousBackend is not null; BridgeSpec? bridge = null; BusPacketRelay? relay = null;
        var plan=ResumePlan.For(state);
        var browserCheckpoint = ResumePlan.CanResumeBrowser(state, checkSavedNetwork);
        var recheckNetworkFirst = checkSavedNetwork || o.ManualAdsPower || state.PendingActions.Any(ResumePlan.IsGuestNetworkProbe);
        o.Validate(plan.InstallWindows);
        if(plan.InstallWindows) WindowsMedia.RequireSelected(o,o.IsoSha256);
        if (!o.AcceptChanges || (plan.InstallWindows && !o.AcceptWindowsLicense)) throw new SetupException("开始前请确认安装范围，以及所选 Windows 的许可与授权。");
        if (o.UsesMicrosoftGuestAccount && (!SetupOptions.IsMicrosoftGuestAddress(secrets.VmMicrosoftAddress) || !o.HasUsableGuestPassword(secrets.VmPassword)))
            throw new SetupException("请在原虚拟机桌面重新同步微软账号邮箱和登录密码。");
        if (!o.HasUsableGuestPassword(secrets.VmPassword)) throw new SetupException("请填写至少 10 位的独立虚拟机密码。");
        if (!string.IsNullOrEmpty(secrets.ExpectedExitIp) && (!IPAddress.TryParse(secrets.ExpectedExitIp, out var expected) || expected.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)) throw new SetupException("预期出口请填写有效 IPv4，或留空。");
        if (o.NetworkSource != "稍后配置" && string.IsNullOrEmpty(secrets.SelectedNodeJson)) throw new SetupException("请先解析并选择一个节点；程序不会自动替你挑选。");
        Paths.EnsureWorkspace(o.Root); Paths.RejectLinks(o.StatePath);
        using var workspaceLock = WorkspaceLock.Acquire(state);
        if (!state.DeferCheckpoint)
        {
            if (o.Remember) SecretStore.SaveMerged(o.Private, secrets, o.NetworkSource == "稍后配置", preserveUnloadedSecrets, state.CredentialsRevision);
            else SecretStore.Forget(state);
        }
        state.Options = o;
        // Runtime connectivity is refreshed; completed installation milestones are retained.
        state.NetworkPassed=false;state.Save();
        try
        {
            Machine.CheckRun(o,false,plan.InstallWindows);
            if(plan.InstallWindows)Stage(state,"1 / 7  核验 Windows 镜像");
            else { Stage(state,browserCheckpoint ? (recheckNetworkFirst ? "恢复任务：核对已配置的专用网络" : "恢复任务：核对专用网络，准备继续 AdsPower") : plan.ResumeTitle);messages.Report("恢复原任务，已完成的创建和安装步骤保留。"); }
            if (plan.InstallWindows && !string.IsNullOrEmpty(o.IsoSha256) && await Downloader.Hash(o.IsoPath, token) != o.IsoSha256.ToLowerInvariant()) throw new SetupException("Windows ISO 校验不匹配，未启动安装。");
            if (plan.InstallWindows && string.IsNullOrEmpty(o.IsoSha256)) { o.IsoSha256 = await Downloader.Hash(o.IsoPath, token); state.Save(); }
            if(plan.InstallWindows) WindowsMedia.RequireSelected(o,o.IsoSha256);
            var resourceStamp=ResumePlan.ResourceStamp();
            var hostPackages = Catalog.Resources.Where(r =>
                (r.Id == "vcredist" && !Machine.VcRuntimeInstalled()) ||
                (r.Id == "vbox" && o.PlatformId == "virtualbox" && Machine.VBoxPath() is null)).ToArray();
            if (hostPackages.Length > 0)
            {
                Stage(state, "2 / 7  准备缺失的主机组件");
                using var hostDownloader = new Downloader();
                foreach (var package in hostPackages)
                    await hostDownloader.Get(package, o.Cache, downloads, token);
            }
            if (!Machine.VcRuntimeInstalled())
            {
                Stage(state, "2 / 7  准备微软运行库，请确认管理员提示");
                var vc = Catalog.Resources.Single(r => r.Id == "vcredist");
                var code = await WindowsHostEnvironment.ElevatedInstaller(Path.Combine(o.Cache, vc.FileName), ["/install", "/quiet", "/norestart"]);
                if (code is 3010 or 1641) throw new SetupException("微软运行库安装需要重启。请保存工作并自行重启，再载入此任务继续。");
                if (code != 0 || !Machine.VcRuntimeInstalled()) throw new SetupException("微软运行库未安装完成，请检查管理员提示后继续。");
            }
            if(plan.CreateMachine)Stage(state,"2 / 7  准备所选虚拟机组件，请确认管理员提示");else messages.Report("恢复检查：核对所选虚拟机组件。");
            await box.Prepare(o, token);
            if(plan.CreateMachine)Stage(state,"3 / 7  创建专用虚拟机");else messages.Report("复用本任务的原虚拟机。");
            await box.EnsureMachine(state, token);
            if (!state.Completed.Contains("machine")) { await box.Configure(state, token); state.Completed.Add("machine"); state.Save(); }
            if (box is VirtualBox compatVBox) await compatVBox.EnsureNemCompatibility(state, token, messages);
            var addresses = Array.Empty<string>(); var port = 0; var config = "";
            if (o.NetworkSource != "稍后配置")
            {
                var node = NetworkImport.Validate(JsonSerializer.Deserialize<Dictionary<string, object?>>(secrets.SelectedNodeJson)!.ToDictionary(p => p.Key, p => p.Value is JsonElement e ? Unwrap(e) : p.Value));
                var upstream = string.IsNullOrEmpty(secrets.UpstreamNodeJson) ? null : NetworkImport.Parse("{\"proxies\":["+secrets.UpstreamNodeJson+"]}").Nodes.Single();
                if (o.ChainEnabled && upstream is null) throw new SetupException("链式配置需要明确选择前置节点。");
                if (!o.ChainEnabled && upstream is not null) throw new SetupException("前置节点与当前链式选项不一致，请重新选择。");
                var firstHop = upstream ?? node;
                messages.Report("恢复检查：解析所选节点地址，准备专用网络。");
                port = firstHop.Port; addresses = await ResolveEndpoints(firstHop.Server, token);
                var exitAddresses = upstream is null ? addresses : await ResolveEndpoints(node.Server, token);
                config = NetworkImport.CoreConfig(node, addresses, o.PlatformId == "virtualbox" ? null : "ClaudomeBridge", upstream, exitAddresses);
                var configHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(config))).ToLowerInvariant();
                var reuseGateway=previousBackend is not null && state.CoreConfigSha256==configHash;
                if(previousBackend is not null && !reuseGateway) {
                    await BoundaryRuntime.Replace(null,"");box=Backends.Create(o.PlatformId,installation);retained=false;previousBackend=null;
                    await box.Prepare(o,token);await box.EnsureMachine(state,token);
                }
                if (state.CoreConfigSha256 != configHash) { state.RestartPassed=false;state.RestartBootBefore="";state.BrowserRuntimePassed=false;state.BrowserReopenPassed=false;state.TranslationPassed=false; }
                state.CoreConfigSha256=configHash;state.Save();
                messages.Report("恢复检查：建立并回读本任务的外层通道防护。");
                var binding = ExternalGateway.Binding(state);
                if (reuseGateway)
                {
                    await BoundaryRuntime.VerifyCurrent(state, token);
                    BoundaryRuntime.EndWindowsSignInHold(state);
                }
                else if (box is VirtualBox vbox) {
                    // A previous timed-out login channel may still own this task's UDP port.
                    await BoundaryRuntime.Replace(null, "");
                    messages.Report(state.Completed.Contains("guest-ready")?"正在连接原虚拟机网络。":"正在自动连接 Windows 安装网络，经所选节点核验 Internet。");
                    var gateway = await VirtualBoxBoundary.Connect(vbox, state, binding, addresses, port, firstHop.Server, token,state.Completed.Contains("guest-ready")?null:config,downloads,messages);
                    await BoundaryRuntime.Replace(gateway, state.VmId);
                } else {
                    bridge = BusBoundary.Configuration(state, binding); await BusBoundary.Prepare(box, state, bridge, token);
                    relay = new BusPacketRelay(bridge, binding); var channel = relay; var identity = bridge;
                    try {
                        var gateway = await ExternalGateway.Start(state, binding, addresses, port, firstHop.Server, async ct => {
                            if (!channel.IsAlive) throw new SetupException("虚拟机总线通道已停止。");
                            await BusBoundary.Verify(box, state, identity, ct);
                        }, token);
                        await BoundaryRuntime.Replace(gateway, state.VmId, relay);
                    } catch { await relay.DisposeAsync(); relay = null; throw; }
                }
            }
            else {
                await BoundaryRuntime.Replace(null, "");
                if (box is VirtualBox vbox) await VirtualBoxBoundary.Disconnect(vbox, state, token);
                else await BusBoundary.VerifyOffline(box, state, token);
            }
            var settingsStamp = ResumePlan.SettingsStamp(o, state.CoreConfigSha256);
            var resumeNetwork = !reconfigureGuestNetwork && !state.DeferCheckpoint && state.ClashConfigured && state.GuestSettingsStamp == settingsStamp;
            var resumeBrowser = state.BrowserProfileConfigured && state.GuestSettingsStamp == settingsStamp;
            if (checkSavedNetwork && (!browserCheckpoint || !resumeNetwork))
                throw new SetupException("本任务缺少可回读的网络检查点，请使用「重新配置本任务网络」。");
            var continueBrowser = browserCheckpoint && resumeNetwork;
            var refreshGuestScripts = continueBrowser || state.GuestFilesStamp != resourceStamp || !state.Completed.Contains("guest-files");
            if (plan.InstallWindows)
            {
                if (!await box.IsRunning(state, token)) { Stage(state, "4 / 7  安装 Windows"); await box.InstallWindows(state, secrets, token); }
                state.Completed.Add("windows-started"); state.Save();
            }
            else if(box is VirtualBox resumedVBox)await resumedVBox.EnsureRunning(state,token);
            else if(!await box.IsRunning(state,token))await box.Open(state,token);
            var cleanup = new RecoveryCleanupResult(0,0);
            if (!continueBrowser)
            {
                state.AwaitingGuestTools = !state.Completed.Contains("guest-ready");
                if(state.AwaitingGuestTools)Stage(state,"4 / 7  等待 Windows 和来宾工具；可在程序内虚拟机桌面查看进度");
                else messages.Report("复核原 Windows 与来宾工具连接，保留已完成的安装。");
                var ready = false; var waiting = System.Diagnostics.Stopwatch.StartNew();
                var runningLoss = 0; var authFailures = 0; var unreadableStatusChecks = 0;
                var additionsNotified = false; var loginCheckReported = false;
                var repairAttempted = false;
                var hyperVStatus = HyperVGuestStatus.StatusUnavailable;
                var postRepairChecks = 0;
                for (var i = 0; i < 240 && (box is not HyperV || waiting.Elapsed < TimeSpan.FromMinutes(40)); i++)
                {
                    token.ThrowIfCancellationRequested();
                    if(o.NetworkSource!="稍后配置")BoundaryRuntime.Require(state);
                    if (await box.IsRunning(state, token))
                    {
                        runningLoss = 0;
                        if (box is VirtualBox vboxStage)
                        {
                            var probe = await vboxStage.ProbeGuestStage(state, secrets, token, () =>
                            {
                                if (loginCheckReported) return;
                                loginCheckReported = true;
                                if (state.AwaitingGuestTools)
                                    Stage(state, "4 / 7  来宾工具已在线，正在读取 Windows 安装状态");
                            });
                            if (probe.Stage == VirtualBoxGuestStage.Ready) { ready = true; break; }
                            if (probe.Stage == VirtualBoxGuestStage.SetupStatusUnreadable)
                            {
                                authFailures = 0;
                                if (++unreadableStatusChecks == 4)
                                    throw new HandoffException(new("VirtualBox 未能从来宾读回 Windows 状态",
                                        state.AwaitingGuestTools
                                            ? ["在右侧桌面处理 Windows 安装或重启提示，然后继续原任务。"]
                                            : ["原任务已记录 Windows 和来宾工具安装完成。本次回读失败，外网会保持阻断。",
                                               "请先保存右侧桌面的工作，在虚拟机内通过「开始 → 电源 → 重启」重启 Windows。进入来宾桌面后点击「继续原任务并连接」。"],
                                        "guest-status-unreadable"));
                                var check = state.AwaitingGuestTools ? "4 / 7  正在重试 Windows 安装状态读取" : "恢复检查：正在重试 VirtualBox 来宾控制回读";
                                installation?.Report(new($"{check}（{unreadableStatusChecks}/4）"));
                                await Task.Delay(TimeSpan.FromSeconds(10), token);
                                continue;
                            }
                            unreadableStatusChecks = 0;
                            if (probe.Stage == VirtualBoxGuestStage.AuthMismatch)
                            {
                                if (++authFailures == 1)
                                {
                                    Stage(state, "4 / 7  来宾拒绝了后台登录，可点击左侧同步虚拟机账号密码");
                                    await Task.Delay(TimeSpan.FromSeconds(4), token);
                                    continue;
                                }
                                else
                                    throw new HandoffException(new("需要同步虚拟机 Windows 登录账号与密码",
                                    [
                                        "检测到 VirtualBox 来宾工具已就绪，但手动完成 Windows 设置时创建的账号或密码与助手当前记录不一致。",
                                        "先在来宾 Windows 完成登录。若微软账号需要联网，请使用当前已核验的安装网络，必要时关闭登录提示后重新打开。",
                                        "进入来宾桌面后，点击左侧「同步虚拟机账号密码」，填写实际 Windows 用户名和密码，验证后再继续第 5/7 步。"
                                    ], "guest-auth-sync"));
                            }
                            else authFailures = 0;

                            if (probe.Stage == VirtualBoxGuestStage.WaitingForAdditions)
                            {
                                loginCheckReported = false;
                                if (probe.AdditionsMounted && !additionsNotified)
                                {
                                    additionsNotified = true;
                                    messages.Report("已自动挂载 VirtualBox 来宾工具光盘。若右侧已进入 Windows 桌面，请打开「此电脑」双击 CD 驱动器 VirtualBox Guest Additions 运行 VBoxWindowsAdditions.exe 安装。");
                                }
                                if (i % 2 == 0)
                                    installation?.Report(new($"4 / 7  等待 Windows 与来宾工具 · 已等待 {waiting.Elapsed.TotalMinutes:F1} 分钟（若已进桌面，请在虚拟机「此电脑」双击 VirtualBox Guest Additions 安装）"));
                                await Task.Delay(TimeSpan.FromSeconds(10), token);
                                continue;
                            }
                            if (i % 3 == 0)
                                installation?.Report(new($"4 / 7  来宾工具已在线，正在确认后台账号与 Windows 状态 · 已等待 {waiting.Elapsed.TotalMinutes:F1} 分钟"));
                            await Task.Delay(TimeSpan.FromSeconds(10), token);
                            continue;
                        }
                        else if (box is HyperV hyperV)
                        {
                            hyperVStatus = await hyperV.ReadGuestStatus(state, secrets, token);
                            if (hyperVStatus == HyperVGuestStatus.Ready) { ready = true; break; }
                            var detail = HyperV.DescribeGuestStatus(hyperVStatus);
                            installation?.Report(new($"4 / 7  {detail} · 已等待 {waiting.Elapsed.TotalMinutes:F1} 分钟"));

                            if (hyperVStatus == HyperVGuestStatus.CredentialsRejected)
                                throw new HandoffException(new("需要同步虚拟机 Windows 登录账号与密码",
                                    [detail + "。请在来宾内核对原 Windows 登录账号，再使用左侧「同步虚拟机账号密码」。"], "guest-auth-sync"));
                            if (hyperVStatus is HyperVGuestStatus.AccessDenied or HyperVGuestStatus.BootstrapMissing)
                                break;

                            if (HyperV.CanRepairGuestStatus(hyperVStatus))
                            {
                                if (!repairAttempted)
                                {
                                    repairAttempted = true;
                                    installation?.Report(new("4 / 7  正在修复原虚拟机的 PowerShell.7 会话"));
                                    hyperVStatus = await hyperV.TryRepairGuestSession(state, secrets, token);
                                    if (hyperVStatus == HyperVGuestStatus.CredentialsRejected)
                                        throw new HandoffException(new("需要同步虚拟机 Windows 登录账号与密码",
                                            ["来宾 Windows 登录凭据未通过验证，请使用左侧「同步虚拟机账号密码」。"], "guest-auth-sync"));
                                    if (hyperVStatus is not (HyperVGuestStatus.EndpointUnavailable or HyperVGuestStatus.Initializing))
                                        break;
                                }
                                else if (++postRepairChecks >= 2)
                                {
                                    break;
                                }
                            }
                            else if (!plan.InstallWindows && hyperVStatus == HyperVGuestStatus.SessionUnavailable &&
                                     waiting.Elapsed >= TimeSpan.FromMinutes(2))
                            {
                                break;
                            }
                        }
                        else if (await box.GuestReady(state, secrets, token)) { ready = true; break; }
                    }
                    else if (++runningLoss >= 3)
                        throw new OperationCanceledException("连续三次未检测到虚拟机运行，任务已保存并暂停。");
                    else
                        installation?.Report(new("4 / 7  暂未检测到虚拟机运行，正在等待重新启动或继续。"));
                    if(box is not HyperV && i%3==0) installation?.Report(new($"4 / 7  等待 Windows 和来宾工具 · 已等待 {waiting.Elapsed.TotalMinutes:F1} 分钟，可在虚拟机窗口查看安装进度"));
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
                state.AwaitingGuestTools = false;
                if (!ready && box is HyperV)
                    throw new HandoffException(new("Hyper-V 来宾初始化尚未完成",
                        [HyperV.DescribeGuestStatus(hyperVStatus) + "。",
                         "请在原虚拟机中确认 Windows 首次设置及来宾 PowerShell Direct 状态，再继续原任务。"], "guest-wait"));
                if(!ready)throw new HandoffException(new("Windows 或来宾工具尚未就绪",["打开程序内的虚拟机桌面，确认 Windows 安装是否完成。安装中继续等待，不要反复重建虚拟机。","如已进入桌面但尚未安装来宾工具，请在虚拟机内打开「此电脑」双击 VirtualBox Guest Additions 运行 VBoxWindowsAdditions.exe。","如手动设置了不同的账号密码，点击左侧「同步虚拟机账号密码」后继续。"],"guest-wait"));
                state.PendingActions.RemoveAll(a => a.Id is "guest-wait" or "guest-auth-sync" or "guest-status-unreadable");
                if (!state.Completed.Contains("guest-ready")) state.Completed.Add("guest-ready"); state.Save();
                if(o.NetworkSource!="稍后配置"&&BoundaryRuntime.HasInstallationNetwork) {
                    messages.Report("Windows 首次设置已完成，正在切换到来宾专用网络。安装资源从本机传送，随后自动配置 Clash。");
                    await BoundaryRuntime.FinishInstallationNetwork(state,token);
                }
                if(!state.Completed.Contains("media-cleanup")) {
                    await box.CleanupWindowsMedia(state,token);
                    if(!Directory.Exists(Paths.Under(o.Private,"unattended"))){state.Completed.Add("media-cleanup");state.Save();}
                }
                try {
                    if(!state.Completed.Contains("recovery-cleanup")) {
                    messages.Report("新 Windows 已就绪，清理本任务被替换的旧半成品磁盘。");
                    if(box is HyperV hyperv) cleanup=await hyperv.CleanupRecovery(state,secrets,token);
                    else if(box is Workstation workstation) cleanup=await workstation.CleanupRecovery(state,secrets,token);
                    messages.Report($"旧半成品磁盘清理：已删除 {cleanup.Deleted} 个，待处理 {cleanup.Remaining} 个。");
                    if(cleanup.Remaining==0){state.Completed.Add("recovery-cleanup");state.Save();}
                    }
                } catch (OperationCanceledException) { throw; }
                catch { cleanup=new(0,1); messages.Report("旧半成品磁盘清理未完成，已保留待办；新环境继续安装。"); }
            }
            if (continueBrowser) Stage(state, recheckNetworkFirst ? "核对原来宾网络，沿用已安装的软件" : "继续 AdsPower：复核本机 API 与专用环境");
            else Stage(state, "5 / 7  核对来宾已有资源，按需补传");
            var guestRoot = o.GuestSetupDirectory;
            var guestPwsh = guestRoot + @"\pwsh\pwsh.exe";
            var resources = await PrepareGuestPackages(box, state, secrets, guestRoot, continueBrowser, token);
            if (bridge is not null) {
                Stage(state, "5 / 7  安装并验证来宾受控网络通道");
                if (await BusBoundary.TryReuseConnectedGuest(box, state, secrets, () => relay?.IsConnected == true, token))
                    messages.Report("已核验并复用原来宾网络组件。");
                else
                {
                    try { await BusBoundary.InstallGuest(box, state, secrets, bridge, token); }
                    catch (HandoffException) { throw; }
                    catch (SetupException error)
                    {
                        var detail = Redaction.SafeError(error);
                        messages.Report(detail);
                        throw new HandoffException(new("来宾网络组件安装未完成",
                            [detail, "请按提示检查原虚拟机，处理完成后继续原任务。"], "guest-bridge"));
                    }
                }
                for (var i=0; relay?.IsConnected != true && i<15; i++) await Task.Delay(1000,token);
                if (relay?.IsConnected != true) throw new SetupException("来宾网络通道尚未完成双向认证，未继续配置代理。");
                await BoundaryRuntime.VerifyCurrent(state, token);
            }
            TimeZoneInfo.TryConvertWindowsIdToIanaId(o.TimeZoneId, o.Country, out var ianaTimeZone);
            if(state.GuestSettingsStamp!=settingsStamp || reconfigureGuestNetwork) { state.OutagePassed=false;state.RestartPassed=false;state.RestartBootBefore=""; }
            if (reconfigureGuestNetwork)
            {
                state.SetupFinished = false;
                state.ApplicationAccepted = false;
                state.Save();
            }
            var runId=Guid.NewGuid().ToString("N");
            var guestPayload = guestRoot + $@"\payload-{runId}.json";
            var payload = JsonSerializer.Serialize(new { runId, platformId = o.PlatformId, workspaceId = o.WorkspaceId, sharedClipboardEnabled = o.SharedClipboardEnabled, manualAdsPower = o.ManualAdsPower, adsPowerApiKey = o.ManualAdsPower ? "" : secrets.AdsPowerApiKey, ianaTimeZone, vmId = state.VmId, country = o.Country, timeZoneId = o.TimeZoneId, networkEnabled = o.NetworkSource != "稍后配置", externalGateway = true, transportKind = o.PlatformId == "virtualbox" ? "ethernet" : "bus", coreConfig = config, endpointAddresses = addresses, endpointPort = port, expectedExitIp = secrets.ExpectedExitIp, testOutage = o.TestOutage && !state.OutagePassed, resumeNetwork, resumeBrowser, browserOnly = continueBrowser, resumeReopenPassed=state.BrowserReopenPassed, coreConfigSha256=state.CoreConfigSha256, resources = resources.Select(r => new { id = r.Id, fileName = r.FileName, sha256 = r.Sha256 }) });
            using var input = new PrivateFile(o.Private, payload, ".json");
            if (refreshGuestScripts)
            {
                foreach (var name in new[] { "Configure.ps1", "BrowserAudit.ps1", "NetworkAudit.ps1", "Integrations.ps1" })
                {
                    using var script = new PrivateFile(o.Private, Catalog.EmbeddedText(name), ".ps1");
                    await box.CopyTo(state, secrets, script.FileName, guestRoot + @"\" + name, token);
                }
                var yamlPath = Paths.Under(o.Private, "YamlDotNet.dll");
                File.WriteAllBytes(yamlPath, Catalog.EmbeddedBytes("YamlDotNet.dll"));
                await box.CopyTo(state, secrets, yamlPath, guestRoot + @"\YamlDotNet.dll", token);
            }
            if (!continueBrowser)
            {
                state.GuestFilesStamp = resourceStamp;
                if (!state.Completed.Contains("guest-files")) state.Completed.Add("guest-files");
                state.Save();
                Stage(state, resumeNetwork ? "6 / 7  回读已有环境，继续未完成的配置" : "6 / 7  配置来宾环境；如出现管理员提示，请在虚拟机中确认");
            }
            await box.CopyTo(state, secrets, input.FileName, guestPayload, token);
            SetupException? guestCommandFailure = null;
            // A current report takes precedence over the launcher exit code. Preserve the sanitized
            // command error so a missing report can be diagnosed without opening an old one.
            try { _ = await box.GuestRun(state, secrets, guestPwsh, ["-NoLogo", "-NoProfile", "-File", guestRoot + @"\Configure.ps1", "-PayloadPath", guestPayload], CancellationToken.None, 1800); }
            catch (HandoffException) { throw; }
            catch (SetupException ex) { guestCommandFailure = ex; }
            catch (OperationCanceledException) { guestCommandFailure = new SetupException("等待来宾配置结束超时（GUEST_CONFIG_TIMEOUT）。"); }
            Stage(state, continueBrowser && !recheckNetworkFirst ? "继续 AdsPower：回读本次浏览器配置与验收结果" : "7 / 7  回读安装与网络结果");
            using var report = await GuestEvidence.CollectConfiguration(state, runId, guestCommandFailure,
                (destination, receiveToken) => box.CopyFrom(state, secrets, guestRoot + $@"\guest-result-{runId}.json", destination, receiveToken));
            var result = report.RootElement;
            state.NetworkPassed = result.GetProperty("networkPassed").GetBoolean();
            if(!state.NetworkPassed){state.OutagePassed=false;state.RestartPassed=false;}
            if(result.GetProperty("outagePassed").GetBoolean())state.OutagePassed=true;
            state.BrowserRuntimePassed = result.GetProperty("browserRuntimePassed").GetBoolean();
            state.BrowserReopenPassed = result.GetProperty("browserReopenPassed").GetBoolean();
            state.TranslationPassed = result.GetProperty("translationPassed").GetBoolean();
            state.ClashConfigured = result.GetProperty("clashConfigured").GetBoolean() || resumeNetwork;
            state.BrowserProfileConfigured = result.GetProperty("browserProfileConfigured").GetBoolean() || resumeBrowser;
            if(state.ClashConfigured || state.BrowserProfileConfigured)state.GuestSettingsStamp=settingsStamp;
            state.PendingActions = result.GetProperty("pendingActions").Deserialize<List<UserAction>>(JsonFile.Format) ?? [];
            var guestCode = result.GetProperty("code").GetString();
            if (o.ManualAdsPower && state.NetworkPassed) state.PendingActions.Add(CustomerGuide.ManualAdsPower());
            if(guestCode is "GUEST_RESOURCE_HASH_MISMATCH" or "VC_RUNTIME_RESOURCE_MISSING" or "GUEST_RESOURCE_MISSING") {
                state.GuestFilesStamp="";state.Completed.Remove("guest-files");
                state.PendingActions.Add(new("补传来宾文件",["部分来宾安装文件缺失或校验不一致，已只撤销文件传送检查点。","继续原任务将重新传送需要的文件，不重装 Windows 或重新创建虚拟机。"],"guest-files"));
            }
            if(cleanup.Remaining>0)state.PendingActions.Add(new("旧半成品磁盘清理待处理",["新系统已就绪；部分旧盘可能仍被占用或引用，程序不会强制删除。","关闭占用旧盘的软件，保持原任务文件完整，再点击继续部署重试清理。"],"recovery-cleanup"));
            state.Save();
            if (!result.GetProperty("softwareInstalled").GetBoolean() || guestCode != "NEEDS_BROWSER_ACCEPTANCE")
            {
                var reason = guestCode switch
                {
                    "GUEST_INPUT_UNREADABLE" or "GUEST_INPUT_MISMATCH" => "来宾未能读取本轮部署输入",
                    "TARGET_VM_IDENTITY_MISMATCH" => "当前来宾身份与原任务不符，配置未执行",
                    "GUEST_CONFIG_BUSY" => "上一轮来宾配置仍在执行，请等待结束后继续原任务",
                    "GUEST_ELEVATION_CANCELLED" => "来宾的管理员确认已取消或超时，请继续原任务并查看虚拟机内的提示",
                    "GUEST_ELEVATION_FAILED" => "无法启动来宾管理员配置进程，请检查虚拟机内的管理员权限",
                    "GUEST_ELEVATED_REPORT_MISSING" => "来宾管理员配置进程已退出，本轮结果文件未生成",
                    "VC_RUNTIME_RESOURCE_MISSING" => "来宾缺少微软 VC++ x64 安装包，继续时会重新传送所需文件",
                    "VC_RUNTIME_RESTART_REQUIRED" => "来宾已安装微软 VC++ x64 运行库，保存工作并仅重启虚拟机里的 Windows，再继续部署",
                    "VC_RUNTIME_INSTALL_WAITING" => "来宾微软 VC++ x64 安装尚未结束，请等安装完成后继续",
                    "VC_RUNTIME_INSTALL_FAILED" => "来宾微软 VC++ x64 运行库安装失败",
                    "VC_RUNTIME_READBACK_FAILED" => "来宾微软 VC++ x64 安装已返回，但运行库回读仍未通过",
                    "CLASH_SERVICE_INSTALL_WAITING" => "Clash 后台服务登记尚未结束，请检查虚拟机内的提示",
                    "RESUME_NETWORK_REVIEW_REQUIRED" => "已保存的来宾网络需要检查，请查看待办中的具体原因",
                    "CLASH_SERVICE_INSTALLER_MISSING" => "已检测到 Clash Verge，但其后台服务安装组件缺失",
                    "CLASH_SERVICE_INSTALL_REQUIRED" => "应用安装已完成，Clash 后台服务登记失败",
                    "CLASH_SERVICE_START_FAILED" or "CLASH_SERVICE_NOT_RUNNING" => "Clash 后台服务未进入运行状态",
                    "CLASH_SERVICE_CORE_REQUIRED" => "Clash 未切换到已校验的服务核心，虚拟机网络未通过",
                    "CLASH_APP_LAUNCH_FAILED" => "Clash 客户端未能启动",
                    "CLASH_CORE_NOT_READY" or "PROXY_PORT_COLLISION" => "Clash 本机代理监听未就绪或端口被占用",
                    "CLASH_CORE_HASH_MISMATCH" => "Clash 服务核心文件与安装包不一致",
                    "CLASH_CONFIG_REVIEW_REQUIRED" => "已有 Clash 配置格式无法安全读取或写回",
                    "CLASH_CONFIG_WRITE_FAILED" => "来宾无法写入 Clash 配置文件，请检查文件权限或占用",
                    "CLASH_CONFIG_BACKUP_FAILED" => "无法备份来宾原有 Clash 配置，部署已停止",
                    "CLASH_STARTUP_REGISTRATION_FAILED" => "Clash 配置已写入，但来宾的开机启动项未登记完成",
                    "CLASH_STARTUP_VALUE_CONFLICT" => "来宾已有不兼容的 Claudome 启动项，原设置已保留",
                    "CLASH_CONTROLLER_UNAVAILABLE" => "Clash 服务核心已找到，但控制接口未通过回读",
                    "RUNTIME_CONTROLLER_NOT_LOCAL" => "Clash 控制接口未限制在来宾本机",
                    "CLASH_TUN_REQUIRED" => "Clash 的 ClaudeTun 虚拟网卡未就绪",
                    "RUNTIME_NETWORK_SETTINGS_MISMATCH" or "RUNTIME_ROUTE_MISMATCH" or
                        "RUNTIME_CONFIG_OUT_OF_SCOPE" or "RUNTIME_CONFIG_NOT_LOCATED" => "Clash 实际运行配置未通过核对",
                    "PHYSICAL_INTERFACE_AMBIGUOUS" => "无法确认唯一的来宾上行网卡，请检查虚拟机的受控网卡连接",
                    "VBOX_DHCP_RULE_FAILED" => "来宾防火墙的受控 DHCP 规则未通过回读",
                    "VBOX_DHCP_NOT_ENABLED" => "来宾受控网卡未启用 DHCP",
                    "VBOX_DHCP_LEASE_NOT_READY" => "来宾 DHCP 续租后仍未取得受控地址和默认路由",
                    "TRACE_DNS_FAILED" => "Clash 本地检查已通过，来宾 DNS 解析失败",
                    "TRACE_TLS_FAILED" => "Clash 本地检查已通过，来宾 HTTPS 握手失败",
                    "TRACE_CONNECT_FAILED" => "Clash 本地检查已通过，来宾连接代理出口超时或中断",
                    "TRACE_HTTP_FAILED" or "TRACE_HTTP_PROTOCOL_FAILED" or "TRACE_INCOMPLETE" => "来宾出口检测网页没有返回有效结果",
                    "TRACE_PROXY_TUNNEL_FAILED" => "来宾代理未能建立出口检测隧道",
                    "EXIT_COUNTRY_MISMATCH" or "EXIT_IP_MISMATCH" => "来宾可访问出口检测网页，但实际出口与任务设置不一致",
                    _ => "来宾配置未完成"
                };
                if (guestCode == "TRACE_TLS_FAILED" && result.TryGetProperty("traceTlsPath", out var tracePath) &&
                    tracePath.ValueKind == JsonValueKind.String)
                    reason += tracePath.GetString() switch
                    {
                        "LOOPBACK_TLS_PASSED" => "。同一节点经 Clash 本机代理可握手，请复查 TUN 路由和 DNS",
                        "LOOPBACK_TLS_FAILED" => "。同一节点经 Clash 本机代理也握手失败",
                        "LOOPBACK_TUNNEL_FAILED" => "。Clash 本机代理也未能建立隧道",
                        _ => ""
                    };
                if (guestCode == "TRACE_TLS_FAILED" && result.TryGetProperty("traceComparison", out var comparison) &&
                    comparison.ValueKind == JsonValueKind.String)
                    reason += comparison.GetString() switch
                    {
                        "COMPARISON_HTTPS_RESPONDED" => "。同一代理可以访问另一 HTTPS 检测地址，请复测节点到原检测站的连接",
                        "COMPARISON_TLS_FAILED" => "。另一 HTTPS 检测地址也握手失败，请核对来宾时钟、证书和代理节点",
                        _ => ""
                    };
                var installerExitCode = result.TryGetProperty("installerExitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number &&
                    exitCode.TryGetInt32(out var number) ? $"，安装器退出码 {number}" : "";
                if (guestCode == "GUEST_STEP_FAILED" && result.TryGetProperty("step", out var reportedStep) &&
                    reportedStep.ValueKind == JsonValueKind.String && reportedStep.GetString() is
                        "核对 Clash 后台服务与已登记核心" or "备份来宾 Clash 配置" or "写入专用 Clash 配置" or
                        "登记 Clash 开机启动项" or "等待 Clash 服务核心与 TUN 网卡" or "核对来宾身份" or
                        "配置来宾出站防火墙" or "核对 Clash 生效配置与出站规则" or "检查来宾 HTTPS 并核对代理出口")
                    reason = $"来宾在「{reportedStep.GetString()}」停下";
                throw new SetupException($"{reason}（{guestCode}{installerExitCode}）。原环境已保留。");
            }
            installation?.Report(new(o.ManualAdsPower && state.NetworkPassed ? CustomerGuide.ManualAdsPowerStage : continueBrowser ? "AdsPower 本轮回读已结束，请查看待办" : "自动安装步骤已结束，浏览器验收与待处理项请按下方指引完成", 100));
            if (!state.Completed.Contains("software")) state.Completed.Add("software");
            if (o.NetworkSource != "稍后配置")
            {
                await BoundaryRuntime.VerifyCurrent(state, token);
                ApplyProtection(state, result, DateTime.UtcNow, guestOnly: o.ManualAdsPower);
                if (state.Protection.Boundary.Status == CheckStatus.Failed ||
                    state.Protection.Exit.Status == CheckStatus.Failed || state.Protection.Dns.Status == CheckStatus.Failed)
                {
                    state.NetworkPassed = false;
                    if (!state.DeferCheckpoint) WriteProtectionReport(state);
                    throw new SetupException("防护或出口检查失败，已暂停外层连接。");
                }
            }
            state.Status = "需要本人完成浏览器验收";
            var adsNext = state.PendingActions.FirstOrDefault(action => action.Id is "adspower" or "browser-active");
            state.Stage = o.NetworkSource == "稍后配置" ? "应用已安装，网络待配置" :
                o.ManualAdsPower ? CustomerGuide.ManualAdsPowerStage :
                state.BrowserProfileConfigured ? "专用环境已创建，请在浏览器内验收" :
                adsNext?.Title ?? "软件和网络已配置，请完成 AdsPower 登录后继续";
            state.Save();
            if (!state.DeferCheckpoint && o.NetworkSource != "稍后配置") WriteProtectionReport(state);
            if (!state.DeferCheckpoint) WriteAcceptanceReport(state);
            messages.Report(!o.ManualAdsPower && state.BrowserProfileConfigured
                ? state.Stage + "。请在专用浏览器中完成实际联网和出口核验。"
                : state.Stage + "。请按下方待办处理。");
            if (o.NetworkSource != "稍后配置") { BoundaryRuntime.RetainBackend(box, state); retained = true; }
            else retained = false;
        }
        catch (Exception ex)
        {
            state.AwaitingGuestTools = false;
            if (CanHoldInstallationNetworkForWindowsSignIn(state, ex, token.IsCancellationRequested) &&
                BoundaryRuntime.TryHoldForWindowsSignIn(state, box))
            {
                retained = true;
                messages.Report("请先在虚拟机内完成 Windows 登录。此期间暂保留已核验的安装网络（最长 10 分钟）。");
            }
            else await BoundaryRuntime.Replace(null, "");
            if (ex is HandoffException handoff) state.PendingActions = [handoff.Action];
            state.Status = ex is OperationCanceledException ? "已暂停" : "需要处理"; state.Save(); throw;
        }
        finally { if (!state.DeferCheckpoint && !o.Remember) SecretStore.Forget(state); if (!retained) await box.DisposeAsync(); }
    }
    internal static bool CanHoldInstallationNetworkForWindowsSignIn(SessionState state, Exception error, bool cancelled) =>
        !cancelled && state.Options.PlatformId == "virtualbox" &&
        state.Options.NetworkSource != "稍后配置" && !state.Completed.Contains("guest-ready") &&
        error is HandoffException { Action.Id: "guest-auth-sync" };

    private static object? Unwrap(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.Object => item.EnumerateObject().ToDictionary(p => p.Name, p => Unwrap(p.Value)),
        JsonValueKind.Array => item.EnumerateArray().Select(Unwrap).ToList(),
        JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Number => item.TryGetInt64(out var n) ? n : item.GetDouble(),
        JsonValueKind.String => item.GetString(), _ => null
    };
    public async Task RollbackNetwork(SessionState state, Secrets secrets, CancellationToken token)
    {
        await BoundaryRuntime.Replace(null, "");
        await using var box = Backends.Create(state.Options.PlatformId);
        if (string.IsNullOrEmpty(state.VmId) || !state.Options.HasUsableGuestPassword(secrets.VmPassword)) throw new SetupException("请载入原任务并填写该虚拟机的密码后撤回。");
        using var workspaceLock = WorkspaceLock.Acquire(state);
        var guestRoot = state.Options.GuestSetupDirectory;
        var runId=Guid.NewGuid().ToString("N");
        using var input = new PrivateFile(state.Options.Private, JsonSerializer.Serialize(new { runId, vmId = state.VmId, workspaceId = state.Options.WorkspaceId, platformId = state.Options.PlatformId }), ".json");
        using var script = new PrivateFile(state.Options.Private, Catalog.EmbeddedText("Rollback.ps1"), ".ps1");
        await box.CopyTo(state, secrets, input.FileName, guestRoot + @"\rollback-input.json", token);
        await box.CopyTo(state, secrets, script.FileName, guestRoot + @"\Rollback.ps1", token);
        messages.Report("正在撤回本工具设置的来宾网络保护，请在虚拟机内处理管理员提示。");
        try { _ = await box.GuestRun(state, secrets, guestRoot + @"\pwsh\pwsh.exe", ["-NoProfile", "-File", guestRoot + @"\Rollback.ps1", "-PayloadPath", guestRoot + @"\rollback-input.json"], CancellationToken.None); } catch (HandoffException) { throw; } catch (SetupException) { }
        var reportPath = Paths.Under(state.Options.Private, "rollback-result.json"); if (File.Exists(reportPath)) File.Delete(reportPath);
        await box.CopyFrom(state, secrets, guestRoot + @"\rollback-result.json", reportPath, CancellationToken.None);
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        GuestEvidence.RequireCurrent(report.RootElement,state,runId);
        if (!report.RootElement.GetProperty("rollbackApplied").GetBoolean()) throw new SetupException("撤回未完成，代码：" + report.RootElement.GetProperty("code").GetString());
        state.NetworkPassed = false; state.OutagePassed = false; state.ApplicationAccepted = false; state.SetupFinished = false; state.Protection = new(); state.Status = "网络保护已撤回"; state.Stage = "来宾防火墙已恢复；外层通道已关闭，重新使用前需配置与验收"; state.Save();
        if (!state.DeferCheckpoint && !string.IsNullOrWhiteSpace(state.Options.Root) && Directory.Exists(state.Options.Root))
        {
            WriteProtectionReport(state);
            WriteAcceptanceReport(state);
        }
        messages.Report(state.Stage);
    }
    public async Task RestoreHyperVConsole(SessionState state,CancellationToken token)
    {
        if(state.Options.PlatformId!="hyperv"||!Guid.TryParse(state.VmId,out _))throw new SetupException("请先载入本任务的 Hyper-V 虚拟机。");
        await BoundaryRuntime.Replace(null,"");await using var backend=new HyperV();await backend.EnsureMachine(state,token);
        var result=await backend.RestoreConsole(state,token);state.Options.HyperVBasicConsoleConfirmed=false;state.Status="基本会话保护已请求撤回";state.Save();
        messages.Report(result=="kept-for-other-environments"?"其他受保护环境仍在使用基本会话策略，主机设置暂时保留。":"已按记录恢复主机增强会话设置；再次使用受保护环境前会重新确认。");
    }
    public static Task<string[]> ResolveEndpoints(string server,CancellationToken token)=>NodeEndpointResolver.Resolve(server,token);
}
