using System.IO;

namespace ClaudeSetup;

public sealed partial class VirtualBox
{
    public async Task EnsureRunning(SessionState state,CancellationToken token,int attempts=60,int delayMilliseconds=500)
    {
        await EnsureMachine(state,token);
        var mode=VmInventory.VBoxValue(await Run(token,"showvminfo",state.VmId,"--machinereadable"),"VMState");
        if(mode is "poweroff" or "saved" or "aborted") {
            (startCheck??Machine.CheckVmStart)(state.Options);
            await Run(token,"startvm",state.VmId,"--type","headless");
        } else if(mode=="paused")await Run(token,"controlvm",state.VmId,"resume");
        else if(mode!="running" && !IsTransition(mode))throw new SetupException("未识别虚拟机运行状态，未重复启动。请运行虚拟机状态检查。");
        for(var i=0;i<attempts;i++) {
            token.ThrowIfCancellationRequested();
            mode=VmInventory.VBoxValue(await Run(token,"showvminfo",state.VmId,"--machinereadable"),"VMState");
            if(mode=="running")return;
            if(mode is "poweroff" or "aborted" or "stuck")break;
            await Task.Delay(Math.Max(1,delayMilliseconds),token);
        }
        throw new HandoffException(new("等待虚拟机启动未完成",["原虚拟机和安装进度已保留；当前状态："+mode,"在程序内打开虚拟机桌面，检查是否有启动提示。不要新建或反复重启；稍后继续会复查原实例。"],"vm-start-wait"));
    }
    internal async Task ShutdownOriginal(SessionState state, CancellationToken token,
        int attempts = 75, int intervalMilliseconds = 1000)
    {
        if (!Guid.TryParseExact(state.VmId, "D", out _))
            throw new SetupException("请先载入本任务的虚拟机。");
        await EnsureMachine(state, token);
        async Task<string> Mode() => VmInventory.VBoxValue(
            await Run(token, "showvminfo", state.VmId, "--machinereadable"), "VMState");
        var mode = await Mode();
        if (mode == "poweroff") return;
        if (mode is not ("running" or "stopping"))
            throw new SetupException("当前状态无法直接正常关机。请先回到原虚拟机桌面确认来宾状态。");
        if (mode == "running")
        {
            try { await Run(token, "controlvm", state.VmId, "acpipowerbutton"); }
            catch (SetupException)
            {
                if (await Mode() == "poweroff") return;
                throw;
            }
        }
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (await Mode() == "poweroff") return;
            await Task.Delay(intervalMilliseconds, token);
        }
        throw new SetupException("尚未确认 Windows 已关机。请回到虚拟机桌面查看正在关闭的应用。");
    }

    internal const string FramebufferConflict = "AttachFramebuffer: Framebuffer already attached to 0";

    internal static bool HasFramebufferConflict(SessionState state)
    {
        try
        {
            var path = Paths.Under(state.Options.VmDirectory, @"Logs\VBox.log");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The current log identifies the VM process. Rotated logs describe previous runs.
            var header = new byte[(int)Math.Min(4096, stream.Length)];
            stream.ReadExactly(header);
            using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(header));
            var headless = false;
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains(" Executable: ", StringComparison.Ordinal)) continue;
                headless = line.TrimEnd().EndsWith(@"\VBoxHeadless.exe", StringComparison.OrdinalIgnoreCase);
                break;
            }
            if (!headless) return false;

            var tail = new byte[(int)Math.Min(131_072, stream.Length)];
            stream.Position = stream.Length - tail.Length;
            stream.ReadExactly(tail);
            return System.Text.Encoding.UTF8.GetString(tail).Contains(FramebufferConflict, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SetupException) { return false; }
    }

    internal async Task RecoverFramebufferConflict(SessionState state, CancellationToken token)
    {
        if (state.Options.PlatformId != "virtualbox" || !Guid.TryParseExact(state.VmId, "D", out _))
            throw new SetupException("请先载入本任务的 VirtualBox 虚拟机。");

        using var repairLock = WorkspaceLock.Acquire(state);
        await EnsureMachine(state, token);
        var vmId = state.VmId;
        Task<string> ReadInfo() => Run(token, "showvminfo", vmId, "--machinereadable");

        var info = await ReadInfo();
        if (VmInventory.VBoxValue(info, "VMState") != "running" || !HasFramebufferConflict(state))
            throw new SetupException("原虚拟机当前未同时满足运行中与本次日志的显示缓冲冲突条件。请先查看黑屏诊断。");
        await VerifyDisplayRecoveryBoundary(state, info, token);

        if (testExecutable is null)
        {
            var root = Path.GetPathRoot(state.Options.VmDirectory)!;
            var available = new DriveInfo(root).AvailableFreeSpace;
            if (available < (state.Options.MemoryMb + 512L) * 1024 * 1024)
                throw new SetupException("虚拟机磁盘所在分区没有足够空间保存运行状态。请释放空间后重试。");
        }

        await Run(token, "controlvm", vmId, "savestate");
        var interval = TimeSpan.FromMilliseconds(testExecutable is null ? 500 : 1);
        var mode = "";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            token.ThrowIfCancellationRequested();
            info = await ReadInfo();
            mode = VmInventory.VBoxValue(info, "VMState");
            if (mode is "saved" or "poweroff" or "aborted" or "stuck") break;
            await Task.Delay(interval, token);
        }
        if (mode != "saved")
            throw new SetupException("VirtualBox 未确认已保存运行状态，恢复已停止。请检查本任务的虚拟机状态。");

        if (testExecutable is null || startCheck is not null)
            (startCheck ?? Machine.CheckVmStart)(state.Options);
        await VerifyDisplayRecoveryBoundary(state, info, token);
        if (VmInventory.VBoxValue(await ReadInfo(), "VMState") != "saved")
            throw new SetupException("虚拟机状态已经变化，已停止恢复。请核对当前状态。");

        ConsoleWindowLocator.Forget(vmId);
        await Run(token, "startvm", vmId, "--type", "headless");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            token.ThrowIfCancellationRequested();
            mode = VmInventory.VBoxValue(await ReadInfo(), "VMState");
            if (mode == "running") return;
            if (mode is "poweroff" or "aborted" or "stuck") break;
            await Task.Delay(interval, token);
        }
        throw new SetupException("尚未确认虚拟机从保存状态恢复。请检查当前状态后继续原任务。");
    }

    private async Task VerifyDisplayRecoveryBoundary(SessionState state, string info, CancellationToken token)
    {
        VirtualBoxBoundary.CheckSharing(info, VirtualBoxClipboard.IsAllowed(state));
        if (Enumerable.Range(1, 8).All(i => VmInventory.VBoxValue(info, "nic" + i) == "none")) return;
        var path = Paths.Under(state.Options.Private, "gateway-binding.json");
        if (!File.Exists(path)) throw new SetupException("缺少原外层通道的绑定记录，无法确认安全恢复条件。");
        var binding = JsonFile.Read<GatewayBinding>(path);
        await VirtualBoxBoundary.Verify(this, state, binding, token);
    }

    public static bool IsTransition(string mode)=>mode is "starting" or "restoring" or "teleporting" or "live-snapshotting" or "online-snapshotting" or "saving" or "stopping" or "restoring-snapshot";
    public async Task EnsureSharing(SessionState state,CancellationToken token)
    {
        var info=await Run(token,"showvminfo",state.VmId,"--machinereadable");
        var mode=VmInventory.VBoxValue(info,"VMState");
        var sharingOk=false;
        try{VirtualBoxBoundary.CheckSharing(info, VirtualBoxClipboard.IsAllowed(state));sharingOk=true;}catch(HandoffException){}
        if(sharingOk)
        {
            if((mode is "poweroff" or "aborted") && state.Options.AcceptChanges &&
               VmInventory.VBoxValue(info,"hidpointing").Equals("ps2mouse",StringComparison.OrdinalIgnoreCase))
            {
                await EnsureMachine(state,token);
                await Run(token,"modifyvm",state.VmId,"--usb-xhci","on","--mouse","usbtablet");
            }
            return;
        }
        if((mode is "poweroff" or "aborted") && state.Options.AcceptChanges) {
            await EnsureMachine(state,token);
            await Run(token,"modifyvm",state.VmId,"--clipboard-mode","disabled","--clipboard-file-transfers","disabled","--drag-and-drop","disabled","--usb-ohci","off","--usb-ehci","off","--usb-xhci","on","--mouse","usbtablet","--vrde","off");
            info=await Run(token,"showvminfo",state.VmId,"--machinereadable");
        }
        VirtualBoxBoundary.CheckSharing(info, VirtualBoxClipboard.IsAllowed(state));
    }
    public async Task ChangePoweredOffResources(SessionState state,int cpuCount,int memoryMb,CancellationToken token,bool customConfirmed=true)
    {
        await EnsureMachine(state,token);
        var info=await Run(token,"showvminfo",state.VmId,"--machinereadable");
        if(VmInventory.VBoxValue(info,"VMState")!="poweroff")throw new SetupException("请先在来宾中正常关机；本入口不会强制关机或丢弃保存状态。");
        if(cpuCount<1 || cpuCount>Environment.ProcessorCount || memoryMb<4096 || memoryMb>32768)throw new SetupException("请填写有效的 CPU 数量和内存；至少 1 核、4096 MB。");
        JsonFile.Write(Paths.Under(state.Options.Root,"resource-change-before-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+".json"),new{state.VmId,state.Options.WorkspaceId,cpu=VmInventory.VBoxValue(info,"cpus"),memory=VmInventory.VBoxValue(info,"memory"),dateUtc=DateTime.UtcNow});
        await Run(token,"modifyvm",state.VmId,"--cpus",cpuCount.ToString(),"--memory",memoryMb.ToString());
        info=await Run(token,"showvminfo",state.VmId,"--machinereadable");
        if(VmInventory.VBoxValue(info,"cpus")!=cpuCount.ToString()||VmInventory.VBoxValue(info,"memory")!=memoryMb.ToString())throw new SetupException("资源修改回读未通过，原值已记录，请检查虚拟机配置。");
        state.Options.CpuCount=cpuCount;state.Options.ManualCpuConfirmed=customConfirmed;state.Options.MemoryMb=memoryMb;state.RestartPassed=false;state.RestartBootBefore="";state.Status="资源已修改，等待启动后检查";state.Save();
    }
}
