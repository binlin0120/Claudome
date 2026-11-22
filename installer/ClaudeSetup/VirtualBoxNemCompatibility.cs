using System.IO;

namespace ClaudeSetup;

public sealed partial class VirtualBox
{
    public async Task<bool> EnsureNemCompatibility(SessionState state, CancellationToken token, IProgress<string>? messages = null, bool? hostNemOverride = null, bool resetManualCpu = false)
    {
        if (resetManualCpu) state.Options.ManualCpuConfirmed = false;
        // When a customer explicitly selected their own custom VM resources, never auto-override them.
        if (state.Options.ManualCpuConfirmed) return false;
        if (!(hostNemOverride ?? Machine.IsHostHypervisorActive) || !state.Options.AcceptChanges) return false;

        await EnsureMachine(state, token);
        var info = await Run(token, "showvminfo", state.VmId, "--machinereadable");
        var mode = VmInventory.VBoxValue(info, "VMState");

        bool NeedsUpdate(string vmInfo, out int cpus, out string pv, out string ap, out string ioap, out string x2ap, out string hp)
        {
            pv = VmInventory.VBoxValue(vmInfo, "paravirtprovider");
            ap = VmInventory.VBoxValue(vmInfo, "apic");
            ioap = VmInventory.VBoxValue(vmInfo, "ioapic");
            x2ap = VmInventory.VBoxValue(vmInfo, "x2apic");
            hp = VmInventory.VBoxValue(vmInfo, "hpet");
            cpus = int.TryParse(VmInventory.VBoxValue(vmInfo, "cpus"), out var parsed) && parsed >= 1 ? parsed : state.Options.CpuCount;
            return cpus != 1 || ap != "on" || ioap != "on" || x2ap != "on" || hp != "off" || !pv.Equals("default", StringComparison.OrdinalIgnoreCase);
        }

        var needsHardwareUpdate = NeedsUpdate(info, out var actualCpus, out var paravirt, out var apic, out var ioapic, out var x2apic, out var hpet);

        if (mode == "running" && needsHardwareUpdate)
        {
            messages?.Report("当前虚拟机仍在运行。兼容参数可在来宾正常关机后调整；卡死时可打开黑屏诊断。");
            return false;
        }

        if (mode is not ("poweroff" or "aborted")) return false;
        const int targetCpus = 1;
        if (!needsHardwareUpdate && state.Options.CpuCount == targetCpus) return false;

        if (needsHardwareUpdate)
        {
            Paths.EnsureWorkspace(state.Options.Root);
            JsonFile.Write(Paths.Under(state.Options.Root, "nem-compat-before-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json"),
                new { state.VmId, state.Options.WorkspaceId, cpu = actualCpus, paravirt, apic, ioapic, x2apic, hpet, dateUtc = DateTime.UtcNow });
            // On Windows Hyper-V NEM, VirtualBox uses user-mode APIC emulation where multi-vCPU SMP can lose cross-CPU IPI wakeups.
            // 1 vCPU with standard Windows 11 EFI64 settings eliminates all cross-vCPU spinlock/IPI deadlocks cleanly.
            await Run(token, "modifyvm", state.VmId, "--cpus", "1", "--apic", "on", "--x86-x2apic", "on", "--ioapic", "on", "--x86-hpet", "off", "--paravirt-provider", "default");
            var readback = await Run(token, "showvminfo", state.VmId, "--machinereadable");
            if (VmInventory.VBoxValue(readback, "cpus") != "1" ||
                VmInventory.VBoxValue(readback, "apic") != "on" ||
                VmInventory.VBoxValue(readback, "ioapic") != "on" ||
                VmInventory.VBoxValue(readback, "x2apic") != "on" ||
                VmInventory.VBoxValue(readback, "hpet") != "off" ||
                !VmInventory.VBoxValue(readback, "paravirtprovider").Equals("default", StringComparison.OrdinalIgnoreCase))
                throw new SetupException("VirtualBox 防卡死兼容参数回读未通过，请确认虚拟机已完全关机后重试。");
        }

        state.Options.CpuCount = targetCpus;
        state.Save();
        messages?.Report("检测到当前电脑启用了 Windows 虚拟化安全（Hyper-V 兼容模式），已为默认配置自动启用 1 核防死锁兼容模式（避免多核中断丢失卡死；若需自定义核数可在关机后调整），原磁盘与安装进度完整保留。");
        return true;
    }

    public async Task ForceStopAndRepair(SessionState state, CancellationToken token, IProgress<string>? messages = null, bool? hostNemOverride = null)
    {
        if (!Guid.TryParseExact(state.VmId, "D", out _)) throw new SetupException("请先载入需要修复的原虚拟机任务。");
        await EnsureMachine(state, token);
        var info = await Run(token, "showvminfo", state.VmId, "--machinereadable");
        var mode = VmInventory.VBoxValue(info, "VMState");
        if (mode == "saved")
        {
            messages?.Report("正在清除已卡死的休眠状态并保留虚拟磁盘…");
            await Run(token, "discardstate", state.VmId);
        }
        else if (mode is not ("poweroff" or "aborted"))
        {
            messages?.Report("正在强制关闭无响应的本任务虚拟机…");
            await Run(token, "controlvm", state.VmId, "poweroff");
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(testExecutable is null ? 500 : 5, token);
                mode = VmInventory.VBoxValue(await Run(token, "showvminfo", state.VmId, "--machinereadable"), "VMState");
                if (mode is "poweroff" or "aborted") break;
            }
        }
        ConsoleWindowLocator.Forget(state.VmId);
        info = await Run(token, "showvminfo", state.VmId, "--machinereadable");
        mode = VmInventory.VBoxValue(info, "VMState");
        if (mode is not ("poweroff" or "aborted"))
            throw new SetupException("虚拟机进程尚未退出（当前状态：" + mode + "）。请稍候再试，或检查任务管理器中的 VBoxHeadless 进程。");

        var previousConsent = state.Options.AcceptChanges;
        state.Options.AcceptChanges = true;
        try
        {
            await EnsureSharing(state, token);
            await EnsureNemCompatibility(state, token, messages, hostNemOverride ?? true, resetManualCpu: true);
        }
        finally
        {
            state.Options.AcceptChanges = previousConsent;
        }
        state.RestartPassed = false;
        state.RestartBootBefore = "";
        state.Status = "已完成强制关机与单核防卡死修复，可继续原任务";
        state.Save();
    }
}
