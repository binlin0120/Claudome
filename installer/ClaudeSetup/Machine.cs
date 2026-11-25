using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ClaudeSetup;

public sealed record HostInfo(string Edition, long TotalMemory, long FreeMemory, int Processors, bool Is64Bit, int Build, string? VBoxPath)
{
    public string EditionName => (Build>=22000?"11 ":"10 ") + (Edition switch { "CoreCountrySpecific" => "家庭中文版", "Core" or "CoreSingleLanguage" => "家庭版", "CoreN" => "家庭版 N", "Professional" => "专业版", "Enterprise" => "企业版", "Education" => "教育版", _ => Edition });
    public string Summary => $"Windows {EditionName}  ·  {TotalMemory / 1073741824d:F1} GB 内存  ·  {Processors} 个逻辑处理器\n当前空闲 {FreeMemory / 1073741824d:F1} GB；" + (VBoxPath is null ? "尚未安装 VirtualBox" : "检测到 VirtualBox，可复用");
}
public static class Machine
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatus>(); public uint Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);
    public static HostInfo Read()
    {
        var memory = new MemoryStatus(); if (!GlobalMemoryStatusEx(memory)) throw new SetupException("无法读取当前内存状态。");
        using var registry = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var edition = registry?.GetValue("EditionID")?.ToString() ?? "未知版本";
        return new(edition, (long)memory.TotalPhysical, (long)memory.AvailablePhysical, Environment.ProcessorCount, RuntimeInformation.OSArchitecture == Architecture.X64, Environment.OSVersion.Version.Build, VBoxPath());
    }
    public static string? VBoxPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Oracle\VirtualBox");
        var registered = key?.GetValue("InstallDir") as string;
        var candidates = new[] { registered is null ? "" : Path.Combine(registered, "VBoxManage.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Oracle", "VirtualBox", "VBoxManage.exe") };
        return candidates.FirstOrDefault(path => Path.IsPathFullyQualified(path) && File.Exists(path));
    }
    public static bool VcRuntimeInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
        return key?.GetValue("Installed") is int installed && installed == 1;
    }
    public static void CheckRun(SetupOptions options, bool startingVm,bool requireIso=true)
    {
        RequireHyperVStandaloneHostEligible();
        options.Validate(requireIso);
        var host = Read();
        HostCompatibility.Require("Windows",host.Is64Bit?"x64":"other",host.Build);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.Root))!);
        if (drive.AvailableFreeSpace < (Directory.Exists(options.VmDirectory)?2L:options.DiskGb + 15L) * 1024 * 1024 * 1024) throw new SetupException("所选磁盘空间不足。需保留虚拟磁盘容量与至少 15 GB 安装余量。");
        if (startingVm && host.FreeMemory < (options.MemoryMb + 2048L) * 1024 * 1024) throw new SetupException($"启动前建议至少空出 {(options.MemoryMb + 2048) / 1024d:F1} GB 内存。当前约 {host.FreeMemory / 1073741824d:F1} GB，请关闭闲置应用后继续。");
    }
    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static bool IsHostHypervisorActive => System.Runtime.Intrinsics.X86.X86Base.IsSupported && (System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0).Ecx & unchecked((int)0x80000000)) != 0;
    internal const string HomeEditionHyperVNotice =
        "当前电脑使用 Windows 家庭版。新建 Hyper-V 环境，请先在「设置 → 系统 → 激活」通过 Microsoft Store 升级，或输入有效的专业版产品密钥，然后返回重新检查。已有 VirtualBox 任务可继续使用。";

    internal static bool IsHomeEdition(string? edition) => edition is "Core" or "CoreN" or "CoreCountrySpecific" or "CoreSingleLanguage";
    internal static string? HyperVStandaloneIneligibilityReason(HostInfo? hostOverride = null, PlatformFacts? factsOverride = null)
    {
        if (!ProductProfile.IsHyperVStandalone) return null;
        var host = hostOverride ?? Read();
        if (!host.Is64Bit || host.Build < 22000)
            return "Claudome Hyper-V 独立版仅支持 Windows 11 x64（专业版或企业版）；当前系统版本或架构不满足要求，已阻止下载或建机操作。";
        if (IsHomeEdition(host.Edition) || !Platforms.IsHyperVSupportedEdition(host.Edition))
            return $"当前电脑系统为 Windows {host.EditionName}，不符合 Hyper-V 独立版支持范围。请在「设置 → 系统 → 激活」升级至 Windows 11 专业版或企业版，或使用符合要求的电脑；旧任务请继续使用原版 Claudome 打开。";
        var facts = factsOverride ?? Platforms.Detect();
        if (facts.VirtualizationKnown && !facts.VirtualizationReady)
            return "未检测到可用的固件虚拟化（VT-x / AMD-V / SLAT），请先在 BIOS/UEFI 开启后重新检查。";
        return null;
    }

    public static void RequireHyperVStandaloneHostEligible(HostInfo? hostOverride = null, PlatformFacts? factsOverride = null)
    {
        if (HyperVStandaloneIneligibilityReason(hostOverride, factsOverride) is { } reason)
            throw new SetupException(reason);
    }

    public static void CheckVmStart(SetupOptions options)
    {
        RequireHyperVStandaloneHostEligible();
        Paths.ValidateRoot(options.Root);
        var host = Read();
        HostCompatibility.Require("Windows",host.Is64Bit?"x64":"other",host.Build);
        if (options.MemoryMb < 4096 || options.MemoryMb > 32768) throw new SetupException("虚拟机内存设置不在支持范围内。");
        if (host.FreeMemory < (options.MemoryMb + 2048L) * 1024 * 1024) throw new SetupException($"请先空出约 {(options.MemoryMb + 2048) / 1024d:F1} GB 内存，再打开虚拟机。当前约 {host.FreeMemory / 1073741824d:F1} GB。");
        if (new DriveInfo(Path.GetPathRoot(options.Root)!).AvailableFreeSpace < 2L * 1024 * 1024 * 1024) throw new SetupException("虚拟机所在磁盘的可用空间不足 2 GB，请先释放空间。");
    }

    public sealed record FixedNtfsDrive(string Name, string RootPath, string VolumeLabel, long FreeBytes, long TotalBytes, bool MeetsRecommendedSpace, string SuggestedWorkspacePath)
    {
        public string DisplayLabel => $"{Name.TrimEnd('\\')} ({(string.IsNullOrWhiteSpace(VolumeLabel) ? "本地磁盘" : VolumeLabel)}) · 可用 {FreeBytes / 1073741824d:F1} GB / {TotalBytes / 1073741824d:F0} GB" +
            (MeetsRecommendedSpace ? " · 推荐" : " · 空间偏紧");
    }

    public static List<FixedNtfsDrive> GetFixedNtfsDrives(int requiredFreeGb = 95)
    {
        var list = new List<FixedNtfsDrive>();
        var threshold = requiredFreeGb * 1024L * 1024 * 1024;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed ||
                    !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    continue;
                var rootPath = drive.RootDirectory.FullName;
                var suggested = Path.Combine(rootPath, ProductProfile.DefaultWorkspaceFolderName);
                list.Add(new(drive.Name, rootPath, drive.VolumeLabel ?? "", drive.AvailableFreeSpace, drive.TotalSize, drive.AvailableFreeSpace >= threshold, suggested));
            }
            catch { }
        }
        return list.OrderByDescending(d => d.MeetsRecommendedSpace).ThenByDescending(d => d.FreeBytes).ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private static string QueryVmmsState()
    {
        using var reg = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\vmms");
        if (reg is null) return "missing";
        if (reg.GetValue("Start") is int start && start == 4) return "disabled";
        const uint ScManagerConnect = 0x0001;
        const uint ServiceQueryStatus = 0x0004;
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return "unknown";
        try
        {
            var svc = OpenServiceW(scm, "vmms", ServiceQueryStatus);
            if (svc == IntPtr.Zero) return "missing";
            try
            {
                if (!QueryServiceStatus(svc, out var status)) return "unknown";
                return status.CurrentState == 4 ? "running" : "stopped";
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public sealed record HyperVHostPreflight(
        bool EditionEligible,
        string EditionSummary,
        bool VirtualizationReady,
        string VirtualizationSummary,
        string VmmsState,
        string VmmsSummary,
        bool RebootPending,
        bool AllReady,
        bool CanOfferEnableHyperV,
        string SummaryLine);

    public static HyperVHostPreflight ReadHyperVPreflight(HostInfo? hostOverride = null, PlatformFacts? factsOverride = null)
    {
        var host = hostOverride ?? Read();
        var facts = factsOverride ?? Platforms.Detect();
        var editionOk = host.Is64Bit && host.Build >= 22000 && !IsHomeEdition(host.Edition) && Platforms.IsHyperVSupportedEdition(host.Edition);
        var editionMsg = editionOk
            ? $"系统符合：Windows {host.EditionName}（Build {host.Build}）"
            : $"系统受限：Windows {host.EditionName}（需 Win11 x64 专业版/企业版）";
        var virtOk = !facts.VirtualizationKnown || facts.VirtualizationReady;
        var virtMsg = virtOk ? "硬件虚拟化：已就绪" : "硬件虚拟化：未在 BIOS/UEFI 开启";

        using var cbsReboot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        using var wuReboot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        var rebootPending = cbsReboot is not null || wuReboot is not null;

        var vmmsState = QueryVmmsState();
        var vmmsMsg = vmmsState switch
        {
            "running" => "Hyper-V vmms 服务：运行中",
            "stopped" => rebootPending ? "Hyper-V 等待重启生效" : "Hyper-V vmms 服务：未启动",
            "disabled" => "Hyper-V vmms 服务：被禁用",
            "missing" => rebootPending ? "Hyper-V 功能等待重启完成" : "Hyper-V 功能：尚未启用",
            _ => "Hyper-V 服务状态：未知"
        };
        var allReady = editionOk && virtOk && vmmsState == "running" && !rebootPending;
        var canEnable = editionOk && virtOk && !allReady && !rebootPending && vmmsState is "missing" or "stopped";
        var summary = $"{editionMsg} · {virtMsg} · {vmmsMsg} · {(IsAdmin ? "当前为管理员权限" : "需要时将请求管理员授权")}";
        return new(editionOk, editionMsg, virtOk, virtMsg, vmmsState, vmmsMsg, rebootPending, allReady, canEnable, summary);
    }
}
