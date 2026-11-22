using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ClaudeSetup;

public sealed record HostInfo(string Edition, long TotalMemory, long FreeMemory, int Processors, bool Is64Bit, int Build, string? VBoxPath)
{
    public string EditionName => (Build>=22000?"11 ":"10 ") + (Edition switch { "CoreCountrySpecific" => "家庭中文版", "Core" or "CoreSingleLanguage" => "家庭版", "Professional" => "专业版", "Enterprise" => "企业版", "Education" => "教育版", _ => Edition });
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
        options.Validate(requireIso);
        var host = Read();
        HostCompatibility.Require("Windows",host.Is64Bit?"x64":"other",host.Build);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.Root))!);
        if (drive.AvailableFreeSpace < (Directory.Exists(options.VmDirectory)?2L:options.DiskGb + 15L) * 1024 * 1024 * 1024) throw new SetupException("所选磁盘空间不足。需保留虚拟磁盘容量与至少 15 GB 安装余量。");
        if (startingVm && host.FreeMemory < (options.MemoryMb + 2048L) * 1024 * 1024) throw new SetupException($"启动前建议至少空出 {(options.MemoryMb + 2048) / 1024d:F1} GB 内存。当前约 {host.FreeMemory / 1073741824d:F1} GB，请关闭闲置应用后继续。");
    }
    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static bool IsHostHypervisorActive => System.Runtime.Intrinsics.X86.X86Base.IsSupported && (System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0).Ecx & unchecked((int)0x80000000)) != 0;
    internal static bool IsHomeEdition(string? edition) => edition is "Core" or "CoreCountrySpecific" or "CoreSingleLanguage";
    public static void CheckVmStart(SetupOptions options)
    {
        Paths.ValidateRoot(options.Root);
        var host = Read();
        HostCompatibility.Require("Windows",host.Is64Bit?"x64":"other",host.Build);
        if (options.MemoryMb < 4096 || options.MemoryMb > 32768) throw new SetupException("虚拟机内存设置不在支持范围内。");
        if (host.FreeMemory < (options.MemoryMb + 2048L) * 1024 * 1024) throw new SetupException($"请先空出约 {(options.MemoryMb + 2048) / 1024d:F1} GB 内存，再打开虚拟机。当前约 {host.FreeMemory / 1073741824d:F1} GB。");
        if (new DriveInfo(Path.GetPathRoot(options.Root)!).AvailableFreeSpace < 2L * 1024 * 1024 * 1024) throw new SetupException("虚拟机所在磁盘的可用空间不足 2 GB，请先释放空间。");
    }
}
