using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Claudome.Transport;

namespace ClaudeSetup;

internal enum HyperVGuestStatus
{
    StatusUnavailable, Ready, WindowsSetup, SessionUnavailable, CredentialsRejected, AccessDenied, BootstrapMissing,
    Initializing, PreparingBootstrap, PreparingRemoting, PreparingLoopback, VerifyingEndpoint, EndpointUnavailable,
    FailedInitializing, FailedPreparingBootstrap, FailedPreparingRemoting, FailedPreparingLoopback, FailedVerifyingEndpoint, RepairFailed
}

// A single elevated, current-user-only pipe session avoids repeated UAC prompts.
// Passwords travel in the protected pipe; command lines contain only the pipe name.
public sealed class HyperV : IVmBackend
{
    private readonly HyperV? launcher;
    private HyperV? verifier;
    private readonly IProgress<InstallationProgress>? progress;
    public HyperV(IProgress<InstallationProgress>? progress = null) { this.progress = progress; }
    private HyperV(HyperV launcher) { this.launcher=launcher; }
    private NamedPipeServerStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private Process? worker;
    private PrivateFile? workerFile;
    private readonly SemaphoreSlim serial = new(1, 1);
    private async Task Connect(SetupOptions o, CancellationToken token)
    {
        if (pipe is not null) return;
        var resource = Catalog.Resources.Single(r => r.Id == "pwsh");
        var archive = Paths.Under(o.Cache, resource.FileName);
        if (!File.Exists(archive) || await Downloader.Hash(archive, token) != resource.Sha256) throw new SetupException("请先下载并核验 PowerShell 组件，再使用 Hyper-V 自动部署。");
        var runtime = Paths.Under(o.Private, "host-pwsh");
        SecretStore.RestrictDirectory(o.Private);
        // Always extract from the verified archive; do not trust an unrelated system pwsh.
        Paths.RejectLinks(runtime);
        HostRuntime.ExtractVerified(archive, runtime);
        var name = "ClaudeSetup-" + Guid.NewGuid().ToString("N");
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        workerFile = new PrivateFile(o.Private, Catalog.EmbeddedText("HyperVWorker.ps1"), ".ps1");
        var start = new ProcessStartInfo(Path.Combine(runtime, "pwsh.exe")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-File", workerFile.FileName, "-PipeName", name }) start.ArgumentList.Add(arg);
        if(launcher is null) worker = Process.Start(start) ?? throw new SetupException("未启动 Hyper-V 管理组件，请确认管理员提示。");
        else {
            var id=await launcher.Call(o,"launch-verifier",new {pipeName=name,scriptPath=workerFile.FileName},token);
            worker=Process.GetProcessById(int.Parse(id));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await pipe.WaitForConnectionAsync(timeout.Token);
        reader = new(pipe, Encoding.UTF8, false, 4096, true); writer = new(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await Call(o, "init", new { options = o, verificationOnly = launcher is not null }, token);
    }
    private async Task<string> Call(SetupOptions options, string operation, object? data, CancellationToken token)
    {
        if (pipe is null) await Connect(options, token);
        await serial.WaitAsync(token);
        try {
            token.ThrowIfCancellationRequested();
            await writer!.WriteLineAsync(JsonSerializer.Serialize(new { operation, data }));
            // Do not interrupt disk application, feature servicing or the guest installer.
            using var probeTimeout = operation is "ready" or "repair-guest-session"
                ? new CancellationTokenSource(TimeSpan.FromSeconds(operation == "ready" ? 30 : 90)) : null;
            JsonDocument result;
            while (true) {
                string? line;
                try { line = await reader!.ReadLineAsync(probeTimeout?.Token ?? CancellationToken.None); }
                catch (OperationCanceledException) when (probeTimeout?.IsCancellationRequested == true)
                {
                    throw new HandoffException(new("Hyper-V 来宾初始化检查超时",
                        [operation == "ready" ? "来宾 PowerShell Direct 未在 30 秒内返回状态。" : "来宾会话修复未在 90 秒内返回结果。",
                         "请保留原虚拟机和当前安装进度，确认 Windows 桌面及初始化状态后继续原任务。"], "guest-wait"));
                }
                if (line is null) throw new SetupException("Hyper-V 管理通道已结束，原任务会保留，请继续部署。");
                result = JsonDocument.Parse(line);
                if (!result.RootElement.TryGetProperty("event", out var kind)) break;
                using (result) {
                    if (kind.GetString() != "install-progress" || operation is not ("windows" or "cleanup-recovery")) throw new SetupException("安装进度与当前操作不符。");
                    progress?.Report(InstallationProgress.FromWorker(result.RootElement));
                }
            }
            using var completedResponse = result;
            if (!result.RootElement.GetProperty("ok").GetBoolean()) {
                var code = result.RootElement.GetProperty("code").GetString();
                if(code is "HYPERV_DISK_RECOVERY" or "HYPERV_DISK_MOUNTED") throw new HandoffException(new("原安装磁盘需要确认",["程序已支持安装检查点恢复，并保留中断磁盘；本次检测到无法自动接管的挂载、附加关系或文件缺失。","请确认本任务虚拟机已正常关机，并在磁盘管理中分离本任务仍被主机挂载的 VHDX（不要删除文件）。","核对并恢复原磁盘/任务文件后，回到助手继续；不会格式化原磁盘或覆盖已有系统。"],"hyperv-disk-recovery"));
                if(code=="GUEST_LOGIN_REQUIRED") throw new HandoffException(new("先登录虚拟机 Windows 桌面",["打开本任务的虚拟机窗口，用保存的虚拟机账号登录 Windows 桌面。","保持桌面登录状态，再回到助手继续；程序会在来宾桌面完成应用配置，不需要开启增强会话。"],"guest-login"));
                if(code=="HYPERV_CONSOLE_CONSENT") throw new HandoffException(new("确认 Hyper-V 基本会话保护",["Hyper-V 增强会话可能共享主机剪贴板和设备，本项目使用基本会话。","继续将关闭这台主机的增强会话策略；这会影响主机上的其他 Hyper-V 虚拟机。程序保存原值，在所有受保护环境正常关机后，可从安装环境最后一页恢复。","如果不接受此项主机设置变化，请选择 VirtualBox 或 VMware；不会自动替你切换。"],"hyperv-console"));
                if(code=="HYPERV_CONSOLE_SHUTDOWN") throw new HandoffException(new("正常关机后调整会话保护",["请保存虚拟机内的工作，关闭其连接窗口并正常关机。","关机后回到助手继续，程序不会强制关机或丢弃保存的数据。"],"hyperv-console-shutdown"));
                var detail = code switch {
                    "HYPERV_RESTART" => "Hyper-V 功能变更需要重启。请保存工作并自行重启电脑，再载入此任务继续。",
                    "HYPERV_EDITION" => "当前 Windows 版本不支持 Hyper-V，请升级专业版或选择其他方案。",
                    "HYPERV_SWITCH" => "未找到 Windows 默认虚拟交换机，请确认 Hyper-V 启用并重启后重试；程序没有修改物理网卡。",
                    "VM_COLLISION" => "同名虚拟机或磁盘目录已存在，未覆盖。请载入原任务或另选名称和目录。",
                    "VM_SCOPE" => "Hyper-V 虚拟机身份或目录与任务不符，已停止。",
                    "VM_SECURITY" => "Hyper-V 安全启动或虚拟 TPM 回读未通过，未继续安装。",
                    "VM_KEY_PROTECTOR_READ_FAILED" => "无法读取本任务的 Hyper-V 密钥保护器。请查看同一时间的 Hyper-V-VMMS 管理日志。",
                    "VM_KEY_PROTECTOR_SETUP_FAILED" => "未能创建或回读本任务的本地密钥保护器。请保留原任务并查看 Hyper-V-VMMS 管理日志。",
                    "VM_TPM_ENABLE_FAILED" => "Hyper-V 未能启用虚拟 TPM。已保留原密钥保护器，请查看 Hyper-V-VMMS 管理日志。",
                    "DISK_SCOPE" => "虚拟磁盘身份校验失败，未执行分区或格式化。",
                    "HYPERV_NOT_READY" => "Hyper-V 尚未运行，请核对固件虚拟化、功能启用状态和系统重启。",
                    "HYPERV_MODULE_UNAVAILABLE" => "已启用 Hyper-V，但未能加载管理模块。请检查 Hyper-V 管理工具，安装完成或重启后继续。",
                    "HYPERV_VMMS_MISSING" => "找不到 Hyper-V 虚拟机管理服务 vmms，请核对 Windows 功能安装状态。",
                    "HYPERV_VMMS_DISABLED" => "Hyper-V 虚拟机管理服务 vmms 已被禁用，请由电脑管理员检查本机策略。",
                    "HYPERV_VMMS_STOPPED" => "Hyper-V 虚拟机管理服务 vmms 未运行，请由电脑管理员检查服务状态。",
                    "GUEST_SHUTDOWN_PENDING" => "尚未确认 Windows 已关机，请检查来宾状态和正在关闭的应用。",
                    "BOUNDARY_NICS" => "Hyper-V 仍存在普通虚拟网卡，请正常关机后继续，由程序核对专用通道。",
                    "BOUNDARY_SERVICE" => "Hyper-V 专用通道注册与当前任务不符，未开放联网。",
                    "BOUNDARY_METADATA" => "Hyper-V 数据交换服务禁用状态无法确认，未开放联网。",
                    "HYPERV_CONSOLE_POLICY" => "Hyper-V 基本会话保护发生变化，当前通道保持关闭。",
                    "WINDOWS_MEDIA_UNSUPPORTED" => "所选 Windows 镜像不支持此安装流程，请重新核对镜像版本。",
                    "WINDOWS_IMAGE_APPLY_FAILED" => "系统映像写入未完成，保留原磁盘与安装检查点，请查看原任务后继续。",
                    "WINDOWS_BOOT_FILES_FAILED" => "Windows 启动文件写入未完成，保留原磁盘，请查看原任务后继续。",
                    "GUEST_COMMAND_FAILED" => "来宾命令未完成，请查看来宾中的管理员提示和软件状态。",
                    "GUEST_BRIDGE_20" => "来宾网络组件的管理员确认已取消，请重试并确认本次授权。",
                    "GUEST_BRIDGE_21" => "来宾网络组件安装前校验或暂存失败，请检查组件文件和来宾磁盘空间。",
                    "GUEST_BRIDGE_22" => "来宾网络组件旧进程未能正常结束。",
                    "GUEST_BRIDGE_23" => "来宾网络组件文件切换失败，请检查文件占用。",
                    "GUEST_BRIDGE_24" => "来宾网络组件计划任务注册失败。",
                    "GUEST_BRIDGE_25" => "来宾网络组件计划任务启动失败。",
                    "GUEST_BRIDGE_26" => "来宾网络组件启动后未完成与宿主机的握手。",
                    "GUEST_BRIDGE_27" => "来宾网络组件未能自动恢复，已保留原组件文件备份。",
                    "GUEST_BRIDGE_28" => "另一轮来宾网络组件安装仍在进行，请等待结束后重试。",
                    "GUEST_BRIDGE_29" => "来宾网络组件文件未通过 SHA-256 核验，请重新传送该组件。",
                    "GUEST_BRIDGE_30" => "来宾网络组件的任务或虚拟机身份未通过校验。",
                    "GUEST_BRIDGE_31" => "来宾网络组件提权启动失败，请核对原来宾账号权限。",
                    "GUEST_COPY_INVALID" => "来宾资源传输清单不完整。",
                    "GUEST_COPY_SOURCE" => "宿主机上的传送源文件已不存在，请重新下载并核验后重试。",
                    "GUEST_COPY_DIRECTORY" => "来宾资源目录不存在，请核对来宾账号和初始化状态。",
                    "GUEST_COPY_DISK_FULL" => "来宾磁盘空间不足，旧文件已保留。",
                    "GUEST_COPY_HASH" => "收到的来宾资源未通过大小或 SHA-256 核验，旧文件已保留。",
                    "HYPERV_STEP_FAILED" when operation == "windows" => "请核对客户机同一时间的 Hyper-V 或 DISM 日志，原任务已保留。",
                    _ => "请查看虚拟机中的许可、首次设置或管理员提示，再继续原任务。"
                };
                if (operation == "windows") detail = CommandFailure.HyperVWindowsStage(result.RootElement) + detail;
                if (code == "VM_TPM_ENABLE_FAILED" && result.RootElement.TryGetProperty("tpmErrorHResult", out var tpmResult) &&
                    tpmResult.TryGetInt32(out var tpmNativeCode))
                    detail += $" TPM 诊断：0x{tpmNativeCode:X8}。";
                if ((operation is "copy-to" or "copy-from") &&
                    result.RootElement.TryGetProperty("copyErrorCategory", out var copyCategory) &&
                    copyCategory.GetString() is { Length: > 0 and <= 32 } category &&
                    category.All(char.IsAsciiLetter) &&
                    result.RootElement.TryGetProperty("copyErrorHResult", out var copyHResult) &&
                    copyHResult.TryGetInt32(out var nativeCode))
                {
                    var phase = result.RootElement.TryGetProperty("copyErrorStage", out var copyStage) ? copyStage.GetString() switch
                    {
                        "session" => "建立来宾会话",
                        "source" => "核对宿主源文件",
                        "destination" => "核对目标目录或可用空间",
                        "verify" => "核验并保存来宾文件",
                        _ => "传送文件"
                    } : "传送文件";
                    detail = $"在「{phase}」未完成。{detail} 诊断：{category} / 0x{nativeCode:X8}。";
                }
                throw new SetupException(CommandFailure.HyperV(operation, code, detail));
            }
            return result.RootElement.GetProperty("value").GetString() ?? "";
        } finally { serial.Release(); }
    }
    public Task Prepare(SetupOptions options, CancellationToken token) => Call(options, "prepare", null, token);
    public async Task<string> EnsureMachine(SessionState state, CancellationToken token)
    {
        var intentPath = Paths.Under(state.Options.Private, "pending-hyperv-create.json");
        if (string.IsNullOrEmpty(state.VmId))
        {
            Directory.CreateDirectory(state.Options.Private);
            JsonFile.Write(intentPath, new
            {
                schema = 1,
                vmName = state.Options.VmName,
                workspaceId = state.Options.WorkspaceId,
                machinePath = state.Options.VmDirectory,
                createdUtc = DateTime.UtcNow
            });
        }
        state.VmId = await Call(state.Options, "ensure", new { vmId = state.VmId }, token);
        state.Save();
        if (File.Exists(intentPath)) try { File.Delete(intentPath); } catch { }
        return state.VmId;
    }
    private object Context(SessionState s, Secrets? secrets = null) => new { vmId = s.VmId, password = secrets?.VmPassword };
    public Task Configure(SessionState s, CancellationToken token) => Call(s.Options, "configure", Context(s), token);
    public Task PrepareBoundary(SessionState s, BridgeSpec spec, CancellationToken token) => Call(s.Options,"boundary-prepare",new {vmId=s.VmId,serviceId=spec.ServiceId,workspaceId=spec.WorkspaceId},token);
    public Task VerifyBoundary(SessionState s, BridgeSpec spec, CancellationToken token)
    {
        verifier ??= new HyperV(this);
        return verifier.Call(s.Options,"boundary-verify",new {vmId=s.VmId,serviceId=spec.ServiceId,workspaceId=spec.WorkspaceId},token);
    }
    public Task VerifyOffline(SessionState s, CancellationToken token) => Call(s.Options,"boundary-offline",Context(s),token);
    public Task<string> RestoreConsole(SessionState s,CancellationToken token)=>Call(s.Options,"console-restore",Context(s),token);
    internal Task<string> SetEnhancedSessionClipboard(SessionState s, bool enabled, CancellationToken token) =>
        Call(s.Options, "set-clipboard", new { vmId = s.VmId, enabled }, token);
    public async Task InstallWindows(SessionState s, Secrets secrets, CancellationToken token)
    {
        Machine.CheckRun(s.Options, true);
        using var initialize = new PrivateFile(s.Options.Private, Catalog.EmbeddedText("HyperVInitialize.ps1"), ".ps1");
        await Call(s.Options, "windows", new { vmId = s.VmId, password = secrets.VmPassword, pwshFile = Catalog.Resources.Single(r => r.Id == "pwsh").FileName, bootstrapScript = initialize.FileName }, token);
        await Open(s,token);
    }
    internal Task ShutdownOriginal(SessionState state, CancellationToken token) =>
        Call(state.Options, "shutdown", Context(state), token);
    public async Task<bool> IsRunning(SessionState s, CancellationToken token) => await Call(s.Options, "running", Context(s), token) == "True";
    public async Task<bool> GuestReady(SessionState s, Secrets secrets, CancellationToken token) =>
        await ReadGuestStatus(s, secrets, token) == HyperVGuestStatus.Ready;

    internal async Task<HyperVGuestStatus> ReadGuestStatus(SessionState s, Secrets secrets, CancellationToken token) =>
        ParseGuestStatus(await Call(s.Options, "ready", Context(s, secrets), token));

    internal async Task<HyperVGuestStatus> TryRepairGuestSession(SessionState s, Secrets secrets, CancellationToken token) =>
        ParseGuestStatus(await Call(s.Options, "repair-guest-session", new
        {
            vmId = s.VmId,
            password = secrets.VmPassword,
            bootstrap = Catalog.EmbeddedText("HyperVInitialize.ps1")
        }, token));

    internal static HyperVGuestStatus ParseGuestStatus(string value) =>
        Enum.TryParse<HyperVGuestStatus>(value, out var status) && status.ToString() == value
            ? status : HyperVGuestStatus.StatusUnavailable;

    internal static bool CanRepairGuestStatus(HyperVGuestStatus status) => status is
        HyperVGuestStatus.EndpointUnavailable or HyperVGuestStatus.FailedInitializing or
        HyperVGuestStatus.FailedPreparingBootstrap or HyperVGuestStatus.FailedPreparingRemoting or
        HyperVGuestStatus.FailedPreparingLoopback or HyperVGuestStatus.FailedVerifyingEndpoint;

    internal static string DescribeGuestStatus(HyperVGuestStatus status) => status switch
    {
        HyperVGuestStatus.Ready => "来宾 PowerShell.7 会话已就绪",
        HyperVGuestStatus.WindowsSetup => "Windows 仍在安装或首次设置",
        HyperVGuestStatus.SessionUnavailable => "等待首次登录或 PowerShell Direct 会话可用",
        HyperVGuestStatus.CredentialsRejected => "来宾 Windows 登录凭据未通过验证",
        HyperVGuestStatus.AccessDenied => "来宾拒绝建立 PowerShell Direct 会话，请核对账号权限",
        HyperVGuestStatus.BootstrapMissing => "来宾缺少任务初始化文件，请核对原虚拟机",
        HyperVGuestStatus.Initializing => "正在初始化虚拟机",
        HyperVGuestStatus.PreparingBootstrap => "正在初始化虚拟机：准备来宾管理组件",
        HyperVGuestStatus.PreparingRemoting => "正在初始化虚拟机：配置来宾本地 PowerShell 管理",
        HyperVGuestStatus.PreparingLoopback => "正在初始化虚拟机：核验来宾回环监听",
        HyperVGuestStatus.VerifyingEndpoint => "正在初始化虚拟机：核验 PowerShell.7 会话",
        HyperVGuestStatus.EndpointUnavailable => "Windows 已就绪，PowerShell.7 会话尚不可用",
        HyperVGuestStatus.FailedPreparingBootstrap => "来宾初始化失败：准备管理组件",
        HyperVGuestStatus.FailedPreparingRemoting => "来宾初始化失败：配置本地 PowerShell 管理",
        HyperVGuestStatus.FailedPreparingLoopback => "来宾初始化失败：设置或核验来宾回环监听",
        HyperVGuestStatus.FailedVerifyingEndpoint => "来宾初始化失败：PowerShell.7 命名会话核验",
        HyperVGuestStatus.FailedInitializing or HyperVGuestStatus.RepairFailed => "来宾初始化或会话修复未完成",
        _ => "暂时无法读取来宾初始化状态"
    };

    public async Task<RecoveryCleanupResult> CleanupRecovery(SessionState s, Secrets secrets, CancellationToken token) =>
        JsonSerializer.Deserialize<RecoveryCleanupResult>(await Call(s.Options, "cleanup-recovery", Context(s,secrets), token), JsonFile.Format) ?? throw new SetupException("旧盘清理结果缺失。");
    public Task CleanupWindowsMedia(SessionState s, CancellationToken token) => Call(s.Options, "cleanup", Context(s), token);
    public Task<string> GuestRun(SessionState s, Secrets secrets, string executable, IEnumerable<string> args, CancellationToken token, int seconds = 300)
    {
        var arguments=args.ToArray(); var interactive=arguments.Any(a=>new[]{"Configure.ps1","BridgeInstall.ps1","Rollback.ps1","Validate.ps1"}.Contains(Path.GetFileName(a)));
        return Call(s.Options,"guest",new{vmId=s.VmId,password=secrets.VmPassword,executable,arguments,interactiveScript=interactive?Catalog.EmbeddedText("InteractiveRun.ps1"):null,seconds=Math.Clamp(seconds,30,1800)},token);
    }
    public async Task<bool> GuestFileExists(SessionState s, Secrets secrets, string file, CancellationToken token)
    {
        var value = await Call(s.Options, "file-exists", new { vmId = s.VmId, password = secrets.VmPassword, file }, token);
        return bool.TryParse(value, out var exists) ? exists : throw new SetupException("未取得来宾文件核对结果。");
    }
    public Task CopyTo(SessionState s, Secrets secrets, string local, string destination, CancellationToken token) => Call(s.Options, "copy-to", new { vmId = s.VmId, password = secrets.VmPassword, source = local, destination }, token);
    internal Task CopyVerifiedResource(SessionState s, Secrets secrets, string local, string destination, Resource resource, CancellationToken token) =>
        Call(s.Options, "copy-to", new { vmId = s.VmId, password = secrets.VmPassword, source = local, destination, sha256 = resource.Sha256, size = resource.Size }, token);
    public Task CopyFrom(SessionState s, Secrets secrets, string guest, string destination, CancellationToken token) => Call(s.Options, "copy-from", new { vmId = s.VmId, password = secrets.VmPassword, source = guest, destination }, token);
    public async Task Open(SessionState s, CancellationToken token)
    {
        if (!await IsRunning(s, token)) Machine.CheckVmStart(s.Options);
        await Call(s.Options, "open", Context(s), token);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmconnect.exe")) { UseShellExecute = true };
        start.ArgumentList.Add("localhost"); start.ArgumentList.Add(s.Options.VmName);
        using var window = Process.Start(start) ?? throw new SetupException("未打开 Hyper-V 虚拟机连接窗口。");
        await PlatformConsole.Confirm(start.FileName,s.Options.VmName,()=>IsRunning(s,token),token);
    }
    public async ValueTask DisposeAsync()
    {
        if(verifier is not null) {await verifier.DisposeAsync();verifier=null;}
        try { if (writer is not null) await writer.WriteLineAsync("{\"operation\":\"exit\"}"); } catch { }
        reader?.Dispose(); writer?.Dispose(); pipe?.Dispose();
        if (worker is not null) { using var timeout = new CancellationTokenSource(5000); try { await worker.WaitForExitAsync(timeout.Token); } catch { } worker.Dispose(); }
        workerFile?.Dispose(); serial.Dispose();
    }
}
