using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

/// <summary>Snapshot of macOS host hardware, chip architecture, and installed hypervisors.</summary>
public sealed record MacHostFacts(
    string System,
    string Architecture,
    bool IsAppleSilicon,
    bool RunningUnderRosetta,
    string ChipName,
    string MacOsVersion,
    long TotalMemoryBytes,
    long FreeMemoryBytes,
    int LogicalProcessors,
    string? FusionVmrunPath,
    string? UtmCtlPath,
    bool ParallelsInstalled)
{
    public string GuestArchitecture => IsAppleSilicon ? "arm64" : "x64";
    public string Summary =>
        $"macOS {MacOsVersion} ({(IsAppleSilicon ? "Apple Silicon" : "Intel")} · {ChipName})" +
        (RunningUnderRosetta ? " [当前运行在 Rosetta 转译下]" : "") +
        $"  ·  {TotalMemoryBytes / 1073741824d:F1} GB 内存  ·  {LogicalProcessors} 核\n" +
        $"可用内存约 {FreeMemoryBytes / 1073741824d:F1} GB；" +
        $"VMware Fusion：{(FusionVmrunPath is null ? "未检测到" : "已安装")}  ·  UTM：{(UtmCtlPath is null ? "未检测到" : "已安装")}";
}

/// <summary>macOS host environment queries, Rosetta/Apple Silicon detection, and ISO inspection.</summary>
public sealed class MacHostEnvironment(ICommandRunner? runner = null) : IHostEnvironment
{
    private readonly ICommandRunner commandRunner = runner ?? new CommandRunner();

    public static readonly string[] DefaultFusionVmrunPaths =
    [
        "/Applications/VMware Fusion.app/Contents/Library/vmrun",
        "/Applications/VMware Fusion Tech Preview.app/Contents/Library/vmrun"
    ];

    public static readonly string[] DefaultUtmCtlPaths =
    [
        "/Applications/UTM.app/Contents/MacOS/utmctl"
    ];

    public bool IsHostHypervisorActive => false;
    public string HostEdition => OperatingSystem.IsMacOS() ? "macOS" : RuntimeInformation.OSDescription;
    public bool IsHomeEdition(string? edition) => false;

    public void RestrictPrivateDirectory(string path)
    {
        Paths.RejectLinks(path);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void RequireWindowsIsoFile(string isoPath)
    {
        if (!File.Exists(isoPath))
            throw new SetupException("请选择已获得授权的 Windows 11 官方 ISO 镜像（Apple Silicon 请用 ARM64 版，Intel Mac 请用 x64 版）。");
        if (new FileInfo(isoPath).Length < 1024L * 1024 * 1024 || !isoPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new SetupException("所选文件不是预期大小的 ISO 镜像。");
    }

    /// <summary>Parses sysctl/vm_stat outputs so Apple Silicon is never misclassified under Rosetta.</summary>
    internal static MacHostFacts ParseSysctlFacts(
        string unameM,
        string arm64Flag,
        string procTranslated,
        string chipBrand,
        string swVers,
        long totalMemoryBytes,
        string vmStatOutput,
        int logicalProcessors,
        string? fusionVmrunPath,
        string? utmCtlPath,
        bool parallelsInstalled)
    {
        var isArm64 = arm64Flag.Trim() == "1" || unameM.Trim().Equals("arm64", StringComparison.OrdinalIgnoreCase);
        var rosetta = procTranslated.Trim() == "1";
        var arch = isArm64 ? "arm64" : "x64";
        var freeMemory = EstimateFreeMemoryFromVmStat(vmStatOutput, totalMemoryBytes);
        return new(
            "macOS",
            arch,
            isArm64,
            rosetta,
            string.IsNullOrWhiteSpace(chipBrand) ? (isArm64 ? "Apple Silicon" : "Intel") : chipBrand.Trim(),
            string.IsNullOrWhiteSpace(swVers) ? "未知版本" : swVers.Trim(),
            totalMemoryBytes,
            freeMemory,
            Math.Max(1, logicalProcessors),
            fusionVmrunPath,
            utmCtlPath,
            parallelsInstalled);
    }

    internal static long EstimateFreeMemoryFromVmStat(string vmStatOutput, long fallbackTotalBytes)
    {
        if (string.IsNullOrWhiteSpace(vmStatOutput)) return fallbackTotalBytes / 2;
        long pageSize = 16384;
        var pageMatch = Regex.Match(vmStatOutput, @"page size of (\d+) bytes", RegexOptions.IgnoreCase);
        if (pageMatch.Success && long.TryParse(pageMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var parsedSize))
            pageSize = parsedSize;

        long ReadPages(string label)
        {
            var m = Regex.Match(vmStatOutput, @"^" + Regex.Escape(label) + @":\s+(\d+)\.", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success && long.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var pages) ? pages : 0L;
        }

        var availablePages = ReadPages("Pages free") + ReadPages("Pages inactive") + ReadPages("Pages speculative");
        return availablePages > 0 ? availablePages * pageSize : fallbackTotalBytes / 2;
    }

    public async Task<MacHostFacts> DetectAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new("macOS-Preview", "arm64", true, false, "Apple Silicon (预览模拟)", "15.0",
                16L * 1024 * 1024 * 1024, 10L * 1024 * 1024 * 1024, 8, null, null, false);
        }

        async Task<string> TryRun(string exe, params string[] args)
        {
            try { return await commandRunner.Run(exe, args, token, 10); }
            catch { return ""; }
        }

        var uname = await TryRun("uname", "-m");
        var arm64 = await TryRun("sysctl", "-n", "hw.optional.arm64");
        var translated = await TryRun("sysctl", "-n", "sysctl.proc_translated");
        var chip = await TryRun("sysctl", "-n", "machdep.cpu.brand_string");
        if (string.IsNullOrWhiteSpace(chip))
            chip = await TryRun("system_profiler", "SPHardwareDataType");
        var swVers = await TryRun("sw_vers", "-productVersion");
        var memRaw = await TryRun("sysctl", "-n", "hw.memsize");
        var vmStat = await TryRun("vm_stat");
        var totalMem = long.TryParse(memRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMem)
            ? parsedMem
            : 16L * 1024 * 1024 * 1024;

        var fusion = DefaultFusionVmrunPaths.FirstOrDefault(File.Exists);
        var utm = DefaultUtmCtlPaths.FirstOrDefault(File.Exists);
        var parallels = Directory.Exists("/Applications/Parallels Desktop.app");

        return ParseSysctlFacts(uname, arm64, translated, chip, swVers, totalMem, vmStat,
            Environment.ProcessorCount, fusion, utm, parallels);
    }

    /// <summary>Recommends Mac virtualization backends for the detected Apple Silicon or Intel host.</summary>
    public static List<PlatformChoice> Recommend(MacHostFacts facts)
    {
        var guestArchNote = facts.IsAppleSilicon
            ? "Apple Silicon（M 系列）需使用 Windows 11 ARM64 镜像；AdsPower 将经 Win11 内置 x64 转译运行，需在来宾内实机核验。"
            : "Intel Mac 需使用 Windows 11 x64 镜像。";
        var memoryOk = facts.TotalMemoryBytes >= 8L * 1024 * 1024 * 1024;
        var memNote = memoryOk ? "" : " 当前总内存不足 8 GB，同时运行宿主与虚拟机可能明显卡顿。";

        return
        [
            new("fusion", "VMware Fusion", 1, memoryOk || facts.TotalMemoryBytes >= 4L * 1024 * 1024 * 1024, true,
                (facts.FusionVmrunPath is not null ? "检测到已安装 VMware Fusion，可直接复用。" : "Mac 首选免费商业方案（需从 Broadcom 官方下载安装）。") +
                guestArchNote + memNote,
                ["基于 .vmx 与 vmrun -T fusion 自动建机、配置安全启动与虚拟 TPM。",
                 "Mac 端不强行跨进程嵌套窗口，而是并排唤起 Fusion 原生窗口操作。",
                 "配合 Claudome Mac 网关与 pfctl 外层门禁维持双层防泄露保护。"]),
            new("utm", "UTM (QEMU 开源方案)", 2, facts.IsAppleSilicon && memoryOk, true,
                (facts.UtmCtlPath is not null ? "检测到已安装 UTM，可直接复用。" : "开源免费备选，无需登录 Broadcom 账号下载。") +
                (facts.IsAppleSilicon ? guestArchNote : " Intel Mac 推荐优先使用 VMware Fusion。") + memNote,
                ["生成 .utm/config.plist、qcow2 磁盘与 ARM64 无人值守应答盘。",
                 "通过 utmctl 管理启停，并注入来宾受控代理通道以完成软件安装与四维验收。"])
        ];
    }

    /// <summary>Inspects a Windows ISO on macOS via hdiutil read-only attach and checks ARM64 vs x64 EFI boot loaders.</summary>
    public async Task<string> InspectIsoArchitectureAsync(string isoPath, bool expectArm64, CancellationToken token)
    {
        RequireWindowsIsoFile(isoPath);
        if (!OperatingSystem.IsMacOS())
            return expectArm64 ? "arm64" : "x64";

        var mountPoint = Path.Combine(Path.GetTempPath(), "claudome-iso-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(mountPoint);
        try
        {
            await commandRunner.Run("hdiutil", ["attach", "-readonly", "-nobrowse", "-mountpoint", mountPoint, isoPath], token, 60);
            var hasArm64 = File.Exists(Path.Combine(mountPoint, "efi", "boot", "bootaa64.efi"));
            var hasX64 = File.Exists(Path.Combine(mountPoint, "efi", "boot", "bootx64.efi"));
            return RequireMatchingIsoBootFiles(expectArm64, hasArm64, hasX64);
        }
        finally
        {
            try { await commandRunner.Run("hdiutil", ["detach", mountPoint, "-force"], CancellationToken.None, 20); } catch { }
            try { if (Directory.Exists(mountPoint)) Directory.Delete(mountPoint, false); } catch { }
        }
    }

    internal static string RequireMatchingIsoBootFiles(bool expectArm64, bool hasArm64, bool hasX64)
    {
        if (expectArm64 && !hasArm64)
            throw new SetupException(hasX64
                ? "你选择的是 x64 版 Windows ISO，不能在 Apple Silicon（M 系列）上原生安装。请改选 Windows 11 ARM64 官方 ISO。"
                : "未在 ISO 中识别到 ARM64 UEFI 引导文件（efi/boot/bootaa64.efi）。");
        if (!expectArm64 && !hasX64)
            throw new SetupException(hasArm64
                ? "你选择的是 ARM64 版 Windows ISO，与当前 Intel Mac 不匹配。请改选 Windows 11 x64 官方 ISO。"
                : "未在 ISO 中识别到 x64 UEFI 引导文件（efi/boot/bootx64.efi）。");
        return expectArm64 ? "arm64" : "x64";
    }
}
