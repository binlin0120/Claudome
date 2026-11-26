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

internal enum HyperVWindowsInstallState
{
    Empty,
    ImageReady,
    Ready,
    NeedsRecovery
}

internal sealed record HyperVWindowsInstallDiagnosis(
    HyperVWindowsInstallState State,
    string Reason,
    int SnapshotCount,
    bool ChainMatchesBase,
    string BaseDiskPath);

// A single elevated, current-user-only pipe session avoids repeated UAC prompts.
// Passwords travel in the protected pipe; command lines contain only the pipe name.
public sealed class HyperV : IVmBackend
{
    private readonly HyperV? launcher;
    private readonly Func<SetupOptions, CancellationToken, Task<Stream>>? connectFixture;
    private HyperV? verifier;
    private readonly IProgress<InstallationProgress>? progress;
    public HyperV(IProgress<InstallationProgress>? progress = null) { this.progress = progress; }
    internal HyperV(Func<SetupOptions, CancellationToken, Task<Stream>> connectFixture, IProgress<InstallationProgress>? progress = null)
    {
        this.connectFixture = connectFixture;
        this.progress = progress;
    }
    private HyperV(HyperV launcher) { this.launcher=launcher; }
    private Stream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private Process? worker;
    private PrivateFile? workerFile;
    private readonly SemaphoreSlim serial = new(1, 1);
    private int disposed;
    private volatile bool faultedOrAborted;
    private readonly List<string> disposeTrace = [];
    internal IReadOnlyList<string> DisposeTrace => disposeTrace;
    internal int ConnectionSequence { get; private set; }
    internal bool HasManagementConnection => Volatile.Read(ref disposed) == 0 && !faultedOrAborted && pipe is { CanRead: true, CanWrite: true } && (pipe is not NamedPipeServerStream nps || nps.IsConnected) && writer is not null;

    private static bool IsCancellableReadOperation(string operation) => operation is
        "ready" or "running" or "windows-install-state" or "windows-install-diagnose" or
        "clipboard-status" or "boundary-verify" or "ensure-checkpoint-policy";

    private static TimeSpan? TimeoutForOperation(string operation) => operation switch
    {
        "ready" => TimeSpan.FromSeconds(30),
        "repair-guest-session" => TimeSpan.FromSeconds(90),
        _ when IsCancellableReadOperation(operation) => TimeSpan.FromSeconds(30),
        _ => null
    };

    private void AbortPipeUnsafely()
    {
        faultedOrAborted = true;
        var p = Interlocked.Exchange(ref pipe, null);
        var r = Interlocked.Exchange(ref reader, null);
        var w = Interlocked.Exchange(ref writer, null);
        // Dispose the underlying stream FIRST so any blocked ReadLineAsync unblocks immediately
        // without deadlocking on StreamReader / StreamWriter synchronous Flush locks.
        if (p is not null) try { p.Dispose(); } catch { }
        if (r is not null) try { r.Dispose(); } catch { }
        if (w is not null) try { w.Dispose(); } catch { }
    }

    private async Task Connect(SetupOptions o, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (pipe is not null && !faultedOrAborted) return;
        AbortPipeUnsafely();
        if (worker is { } staleWorker)
        {
            worker = null;
            try { if (!staleWorker.HasExited) staleWorker.Kill(entireProcessTree: true); } catch { }
            try { staleWorker.Dispose(); } catch { }
        }
        workerFile?.Dispose();
        workerFile = null;
        faultedOrAborted = false;

        if (connectFixture is not null)
        {
            pipe = await connectFixture(o, token);
            reader = new(pipe, Encoding.UTF8, false, 4096, true);
            writer = new(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            ConnectionSequence++;
            await CallLocked(o, "init", new { options = o, verificationOnly = launcher is not null }, token);
            return;
        }

        var resource = Catalog.Resources.Single(r => r.Id == "pwsh");
        var archive = Paths.Under(o.Cache, resource.FileName);
        if (!File.Exists(archive) || await Downloader.Hash(archive, token) != resource.Sha256) throw new SetupException("请先下载并核验 PowerShell 组件，再使用 Hyper-V 自动部署。");
        var runtime = Paths.Under(o.Private, "host-pwsh");
        SecretStore.RestrictDirectory(o.Private);
        // Always extract from the verified archive; do not trust an unrelated system pwsh.
        Paths.RejectLinks(runtime);
        HostRuntime.ExtractVerified(archive, runtime);
        var name = "ClaudeSetup-" + Guid.NewGuid().ToString("N");
        var serverPipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        pipe = serverPipe;
        workerFile = new PrivateFile(o.Private, Catalog.EmbeddedText("HyperVWorker.ps1"), ".ps1");
        var start = new ProcessStartInfo(Path.Combine(runtime, "pwsh.exe")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-File", workerFile.FileName, "-PipeName", name }) start.ArgumentList.Add(arg);
        if(launcher is null) worker = Process.Start(start) ?? throw new SetupException("未启动 Hyper-V 管理组件，请确认管理员提示。");
        else {
            var id=await launcher.Call(o,"launch-verifier",new {pipeName=name,scriptPath=workerFile.FileName},token);
            worker=Process.GetProcessById(int.Parse(id));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await serverPipe.WaitForConnectionAsync(timeout.Token);
        reader = new(pipe, Encoding.UTF8, false, 4096, true); writer = new(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        ConnectionSequence++;
        await CallLocked(o, "init", new { options = o, verificationOnly = launcher is not null }, token);
    }
    private async Task<string> Call(SetupOptions options, string operation, object? data, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await serial.WaitAsync(token);
        try {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (pipe is null || faultedOrAborted) await Connect(options, token);
            return await CallLocked(options, operation, data, token);
        } finally {
            try { if (Volatile.Read(ref disposed) == 0) serial.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task<string> CallLocked(SetupOptions options, string operation, object? data, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var cancellableRead = IsCancellableReadOperation(operation);
        var opTimeout = TimeoutForOperation(operation);
        var activeWriter = writer ?? throw new SetupException("Hyper-V 管理通道未就绪，请重试。");
        var activeReader = reader ?? throw new SetupException("Hyper-V 管理通道未就绪，请重试。");
        var payload = JsonSerializer.Serialize(new { operation, data });
        try
        {
            await activeWriter.WriteLineAsync(payload.AsMemory(), cancellableRead ? token : CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellableRead ? token : CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellableRead && token.IsCancellationRequested)
        {
            AbortPipeUnsafely();
            throw new OperationCanceledException("已暂停当前 Hyper-V 检查，原任务保留。", token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
        {
            AbortPipeUnsafely();
            throw new SetupException("Hyper-V 管理通道发送请求失败，原任务已保留，请继续部署。");
        }

        JsonDocument result;
        while (true) {
            string? line;
            using var timeoutCts = opTimeout is { } d ? new CancellationTokenSource(d) : null;
            using var linkedCts = cancellableRead
                ? (timeoutCts is not null ? CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token) : CancellationTokenSource.CreateLinkedTokenSource(token))
                : (timeoutCts is not null ? CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token) : null);
            try { line = await activeReader.ReadLineAsync(linkedCts?.Token ?? CancellationToken.None); }
            catch (OperationCanceledException) when (Volatile.Read(ref disposed) != 0)
            {
                AbortPipeUnsafely();
                throw new OperationCanceledException("Hyper-V 管理通道已关闭。");
            }
            catch (OperationCanceledException) when (cancellableRead && token.IsCancellationRequested)
            {
                AbortPipeUnsafely();
                throw new OperationCanceledException("已暂停当前 Hyper-V 检查，原任务保留。", token);
            }
            catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
            {
                AbortPipeUnsafely();
                if (operation is "ready" or "repair-guest-session")
                    throw new HandoffException(new("Hyper-V 来宾初始化检查超时",
                        [operation == "ready" ? "来宾 PowerShell Direct 未在 30 秒内返回状态。" : "来宾会话修复未在 90 秒内返回结果。",
                         "请保留原虚拟机和当前安装进度，确认 Windows 桌面及初始化状态后继续原任务。"], "guest-wait"));
                throw new HandoffException(new("Hyper-V 状态查询超时",
                    ["宿主机未在 30 秒内完成当前只读状态回读，已安全断开本次通道。",
                     "原虚拟机与磁盘保留完好，请确认 Hyper-V 管理器状态后继续原任务。"], "guest-wait"));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                AbortPipeUnsafely();
                if (Volatile.Read(ref disposed) != 0 || (cancellableRead && token.IsCancellationRequested))
                    throw new OperationCanceledException("已暂停当前 Hyper-V 检查，原任务保留。", token);
                throw new SetupException("Hyper-V 管理通道已结束，原任务会保留，请继续部署。");
            }
            if (line is null)
            {
                AbortPipeUnsafely();
                if (Volatile.Read(ref disposed) != 0 || (cancellableRead && token.IsCancellationRequested))
                    throw new OperationCanceledException("已暂停当前 Hyper-V 检查，原任务保留。", token);
                throw new SetupException("Hyper-V 管理通道已结束，原任务会保留，请继续部署。");
            }
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
                    "VM_CHECKPOINT_POLICY" => "Hyper-V 未能关闭当前虚拟机的自动检查点设置，请确认虚拟机已正常关机且无未合并检查点后重试。",
                    "HYPERV_CHECKPOINT_ACTIVE" => "当前虚拟机存在尚未合并的检查点或差分盘（.avhdx）。请保持原盘不要删除，先正常关机并在 Hyper-V 管理器合并检查点后继续。",
                    "DISK_SCOPE" => "虚拟磁盘身份校验失败，未执行分区或格式化。",
                    "HYPERV_NOT_READY" => "Hyper-V 尚未就绪。请在宿主机排查：1) 进入主板 BIOS 确认开启 VT-x / AMD-V 与 SLAT 虚拟化；2) 在「启用或关闭 Windows 功能」勾选全部 Hyper-V 项并重启电脑；3) 以管理员身份重新运行 Claudome。",
                    "HYPERV_MODULE_UNAVAILABLE" => "已启用 Hyper-V，但未能加载 PowerShell Hyper-V 模块。请在宿主机打开「控制面板 → 程序 → 启用或关闭 Windows 功能」，勾选「Hyper-V 管理工具（Hyper-V Module for Windows PowerShell）」并重启后继续。",
                    "HYPERV_VMMS_MISSING" => "找不到 Hyper-V 虚拟机管理服务（vmms）。请确认宿主机系统为 Windows 11 专业版/企业版（非家庭版），并在「Windows 功能」完整安装「Hyper-V 平台」。",
                    "HYPERV_VMMS_DISABLED" => "Hyper-V 虚拟机管理服务（vmms）已被禁用。请在宿主机运行 services.msc 找到“Hyper-V 虚拟机管理”，将启动类型改为“自动”后启动该服务。",
                    "HYPERV_VMMS_STOPPED" => "Hyper-V 虚拟机管理服务（vmms）当前未运行。请在管理员 PowerShell 执行 Start-Service vmms 启动服务后重试。",
                    "HYPERV_FEATURE_NOT_FOUND" => "当前系统找不到 Windows 可选功能「Microsoft-Hyper-V-All」。请在本机打开「控制面板 → 启用或关闭 Windows 功能」核对 Hyper-V 项是否存在；若缺失，请使用微软官方 Windows 11 专业版 ISO 执行保留应用与文件的原位修复安装。",
                    "HYPERV_FEATURE_QUERY_FAILED" => "查询本机 Hyper-V 可选功能状态失败。请确认已通过管理员授权、DISM 服务未被安全软件拦截，并查看 C:\\Windows\\Logs\\DISM\\dism.log。",
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
                    "HYPERV_STEP_FAILED" => FallbackOperationDetail(operation),
                    _ => FallbackOperationDetail(operation)
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
                else if (code is "HYPERV_STEP_FAILED" or "HYPERV_FEATURE_NOT_FOUND" or "HYPERV_FEATURE_QUERY_FAILED")
                {
                    detail += FormatSafeHyperVDiagnostic(result.RootElement);
                }
                throw new SetupException(CommandFailure.HyperV(operation, code, detail));
            }
            return result.RootElement.GetProperty("value").GetString() ?? "";
    }
    internal static string FallbackOperationDetail(string operation) => operation switch
    {
        "init" or "prepare" => "宿主机检查或启用 Hyper-V 功能未完成。请核对：1) 本机系统为完整 Windows 11 专业版/企业版且硬件虚拟化已开启；2) 已通过管理员确认且未被安全软件拦截；3) 若下方附带错误码，请据此排查系统功能或 DISM 日志。",
        "ensure" or "windows-install-state" => "在宿主机创建虚拟机或核对系统盘检查点时未完成。请检查目标工作区目录权限、剩余空间及 Hyper-V 虚拟机管理服务（vmms）状态。",
        "configure" => "在配置虚拟机的 CPU、静态内存、安全启动或虚拟 TPM 时未完成。请确认虚拟机处于关机状态、可用内存充足，并检查 Hyper-V-VMMS 管理日志。",
        "open" or "running" or "shutdown" => "在读取、启动或关闭虚拟机状态时未完成。请在宿主机确认 Hyper-V 虚拟机管理服务（vmms）和虚拟机当前状态后继续。",
        "boundary-prepare" or "boundary-verify" or "boundary-offline" => "在配置或核验 Hyper-V 受控总线边界时未完成。请核对宿主机管理员权限及 Hyper-V 服务状态。",
        "set-clipboard" or "clipboard-status" or "console-restore" => "在读写宿主机 Hyper-V 会话或剪贴板设置时未完成。请确认管理员权限与虚拟机状态后重试。",
        "windows" => "在写入 Windows 系统盘或配置启动引导时未完成。请核对目标盘剩余空间、ISO 完整性与宿主机 DISM 日志，原任务已保留。",
        "guest" or "ready" or "repair-guest-session" or "copy-to" or "copy-from" or "file-exists" => "在虚拟机内执行命令或传输资源时未完成。请查看来宾中的管理员提示、Windows 登录状态与磁盘空间后再继续。",
        _ => "该 Hyper-V 管理操作未完成。请核对宿主机管理员权限、vmms 服务与当前任务状态后继续。"
    };

    internal static string FormatSafeHyperVDiagnostic(JsonElement report)
    {
        var parts = new List<string>();
        if (report.TryGetProperty("errorSubstage", out var substageEl) && substageEl.ValueKind == JsonValueKind.String)
        {
            var label = CommandFailure.HyperVSubstageLabel(substageEl.GetString());
            if (!string.IsNullOrEmpty(label)) parts.Add($"子步骤「{label}」");
        }
        if (report.TryGetProperty("errorCategory", out var catEl) && catEl.ValueKind == JsonValueKind.String &&
            catEl.GetString() is { Length: > 0 and <= 32 } cat && cat.All(char.IsAsciiLetter))
        {
            parts.Add(cat);
        }
        if (report.TryGetProperty("errorHResult", out var hrEl) && hrEl.ValueKind == JsonValueKind.Number && hrEl.TryGetInt32(out var hr))
        {
            parts.Add($"0x{unchecked((uint)hr):X8}");
        }
        if (report.TryGetProperty("errorNativeCode", out var nativeEl) && nativeEl.ValueKind == JsonValueKind.Number && nativeEl.TryGetInt32(out var native))
        {
            parts.Add($"退出码 {native}");
        }
        return parts.Count > 0 ? " 诊断：" + string.Join(" · ", parts) + "。" : "";
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
    internal sealed record HyperVClipboardHostStatus(bool HostEnhanced, bool HvSocket, bool Valid = false);

    internal static HyperVClipboardHostStatus ParseClipboardHostStatus(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(false, false);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(false, false);
            if (!root.TryGetProperty("hostEnhanced", out var hostEnhanced) ||
                !root.TryGetProperty("hvSocket", out var hvSocket) ||
                hostEnhanced.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                hvSocket.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return new(false, false);
            return new(hostEnhanced.GetBoolean(), hvSocket.GetBoolean(), Valid: true);
        }
        catch (JsonException)
        {
            return new(false, false);
        }
    }

    internal async Task<HyperVClipboardHostStatus> SetEnhancedSessionClipboard(SessionState s, bool enabled, CancellationToken token) =>
        ParseClipboardHostStatus(await Call(s.Options, "set-clipboard", new { vmId = s.VmId, enabled }, token));

    internal async Task<HyperVClipboardHostStatus> ReadClipboardHostStatus(SessionState s, CancellationToken token) =>
        HasManagementConnection
            ? ParseClipboardHostStatus(await Call(s.Options, "clipboard-status", new { vmId = s.VmId }, token))
            : new(false, false);
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
    internal Task<string> EnsureCheckpointPolicy(SessionState s, CancellationToken token) =>
        Call(s.Options, "ensure-checkpoint-policy", Context(s), token);
    internal async Task<HyperVWindowsInstallState> ProbeWindowsInstallState(SessionState s, CancellationToken token) =>
        ParseWindowsInstallState(await Call(s.Options, "windows-install-state", Context(s), token));

    internal async Task<HyperVWindowsInstallDiagnosis> DiagnoseWindowsInstallState(SessionState s, CancellationToken token)
    {
        var raw = await Call(s.Options, "windows-install-diagnose", Context(s), token);
        return ParseWindowsInstallDiagnosis(raw);
    }

    internal static HyperVWindowsInstallDiagnosis ParseWindowsInstallDiagnosis(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new(HyperVWindowsInstallState.NeedsRecovery, "empty-response", 0, false, "");
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var state = root.TryGetProperty("state", out var st) ? ParseWindowsInstallState(st.GetString() ?? "") : HyperVWindowsInstallState.NeedsRecovery;
            var reason = root.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
            var snaps = root.TryGetProperty("snapshotCount", out var sc) && sc.TryGetInt32(out var sVal) ? sVal : 0;
            var match = root.TryGetProperty("chainMatchesBase", out var cm) && cm.ValueKind == JsonValueKind.True;
            var basePath = root.TryGetProperty("baseDiskPath", out var bp) ? bp.GetString() ?? "" : "";
            return new(state, reason, snaps, match, basePath);
        }
        catch (JsonException)
        {
            return new(ParseWindowsInstallState(raw), "", 0, false, "");
        }
    }

    internal static HyperVWindowsInstallState ParseWindowsInstallState(string value) => value switch
    {
        "empty" => HyperVWindowsInstallState.Empty,
        "image-ready" => HyperVWindowsInstallState.ImageReady,
        "ready" => HyperVWindowsInstallState.Ready,
        _ => HyperVWindowsInstallState.NeedsRecovery
    };
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
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var totalSw = Stopwatch.StartNew();
        try
        {
            if (verifier is not null)
            {
                await verifier.DisposeAsync();
                verifier = null;
                disposeTrace.Add($"verifier:{totalSw.ElapsedMilliseconds}ms");
            }
            var canSendExit = !faultedOrAborted && serial.CurrentCount > 0 &&
                pipe is { CanWrite: true } && (pipe is not NamedPipeServerStream nps || nps.IsConnected) &&
                writer is not null && worker is not { HasExited: true };
            if (canSendExit && writer is { } activeWriter)
            {
                var exitSw = Stopwatch.StartNew();
                try
                {
                    await activeWriter.WriteLineAsync("{\"operation\":\"exit\"}").WaitAsync(TimeSpan.FromMilliseconds(400));
                    await activeWriter.FlushAsync().WaitAsync(TimeSpan.FromMilliseconds(400));
                    disposeTrace.Add($"exit-sent:{exitSw.ElapsedMilliseconds}ms");
                }
                catch
                {
                    faultedOrAborted = true;
                    disposeTrace.Add($"exit-aborted:{exitSw.ElapsedMilliseconds}ms");
                }
            }
            else
            {
                disposeTrace.Add($"exit-skipped(faulted={faultedOrAborted},busy={serial.CurrentCount == 0})");
            }

            var pipeSw = Stopwatch.StartNew();
            AbortPipeUnsafely();
            disposeTrace.Add($"pipe-closed:{pipeSw.ElapsedMilliseconds}ms");

            if (worker is { } activeWorker)
            {
                var waitSw = Stopwatch.StartNew();
                var waitMs = canSendExit && !faultedOrAborted ? 2500 : 400;
                using var timeout = new CancellationTokenSource(waitMs);
                try { await activeWorker.WaitForExitAsync(timeout.Token); } catch { }
                disposeTrace.Add($"wait-worker:{waitSw.ElapsedMilliseconds}ms(exited={activeWorker.HasExited})");
                if (!activeWorker.HasExited)
                {
                    try
                    {
                        activeWorker.Kill(entireProcessTree: true);
                        disposeTrace.Add("worker-killed");
                    }
                    catch (Exception ex)
                    {
                        disposeTrace.Add($"worker-kill-skipped:0x{unchecked((uint)ex.HResult):X8}");
                    }
                }
                activeWorker.Dispose();
                worker = null;
            }
            workerFile?.Dispose();
            workerFile = null;
        }
        finally
        {
            disposeTrace.Add($"total:{totalSw.ElapsedMilliseconds}ms");
        }
    }
}
