using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace ClaudeSetup;

public static class Platforms
{
    public static string ConsentText(PlatformChoice choice) => $"我已阅读组件许可，同意准备所选 {choice.Name}、创建本任务的虚拟机并配置来宾环境；外层通道所需的主机端口规则会单独确认。";
    [DllImport("kernel32.dll")] private static extern bool IsProcessorFeaturePresent(uint feature);
    public static PlatformFacts Detect()
    {
        var host = Machine.Read();
        var hypervisor = X86Base.IsSupported && (X86Base.CpuId(1, 0).Ecx & unchecked((int)0x80000000)) != 0;
        var firmware = IsProcessorFeaturePresent(21);
        using var vmware = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\VMware, Inc.\VMware Workstation");
        return new("Windows", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), host.Edition, firmware || hypervisor, true, host.TotalMemory / 1073741824, host.VBoxPath is not null, vmware is not null,Build:host.Build);
    }
    public static List<PlatformChoice> Recommend(PlatformFacts host)
    {
        if (host.System == "macOS")
        {
            var arch = host.Architecture == "arm64" ? "Windows 11 ARM64；AdsPower 需先核验 ARM 兼容性" : "Windows 11 x64";
            return new List<PlatformChoice> {new("fusion", "VMware Fusion", host.ParallelsInstalled ? 2 : 1, true, false, "项目的 Mac 默认路线。" + arch, ["按仓库 Mac 流程安装 Fusion 和对应架构 Windows。", "此 Windows EXE 不在 Mac 上运行；Mac 自动安装客户端尚未提供。"]),
                new("parallels", "Parallels Desktop", host.ParallelsInstalled ? 1 : 2, true, false, "已有合法许可时优先复用；" + arch, ["使用已有有效许可，按 Mac 流程创建对应架构 Windows。", "付款、许可确认由本人处理；此 EXE 无法自动部署 Mac。"]),
                new("utm", "UTM", 3, true, false, "开源备选；" + arch, ["按项目 Mac 流程核验 UTM、Windows 架构及应用兼容性。", "按操作指引手动安装。"] )}.OrderBy(x => x.Rank).ToList();
        }
        var supported = HostCompatibility.IsSupported(host.System,host.Architecture,host.Build);
        var pro = host.Edition.StartsWith("Professional") || host.Edition.StartsWith("Enterprise") || host.Edition.StartsWith("Education");
        var hardware = !host.VirtualizationKnown || host.VirtualizationReady;
        var note = !hardware ? "固件虚拟化尚未检测为可用，请先在 BIOS/UEFI 开启并重检。" : host.MemoryGb < 8 ? "总内存不足 8 GB，不建议启动完整 Windows 环境。" : "启动前仍会核对空闲内存和磁盘。";
        var list = new List<PlatformChoice>
        {
            new("hyperv", "Hyper-V", pro ? 1 : 3, supported && pro && hardware, true, (pro ? "符合项目原有完整路线的版本条件；功能启用状态还需确认。" : "家庭版不支持本项目 Hyper-V 路线，优先升级专业版可沿用完整方案。") + note,
                ["打开 Windows 功能，检查 Hyper-V；如需启用，请保存工作并按系统提示自行重启。", "默认允许 Hyper-V 增强会话双向剪贴板，并在来宾策略中禁用宿主机磁盘、USB 与设备重定向；如需严格基本会话隔离，可在桌面左侧一键关闭（切换主机会话会影响同机其他 Hyper-V 虚拟机）。", "程序创建第二代虚拟机、启用安全启动与虚拟 TPM，关闭普通网卡，经专用 VM 总线通道联网，自动准备 Windows 和来宾软件。", "此路线不会被偷偷替换成 VirtualBox，也不会自动购买升级许可。"]),
            new("virtualbox", "Oracle VirtualBox", pro ? 2 : 1, supported && hardware, true, (host.VBoxInstalled ? "检测到已安装，可核验版本后复用。" : "程序自动下载、核验并安装，无需提前安装。") + note,
                ["使用右侧向导继续，程序负责组件和来宾软件安装。"]),
            new("workstation", "VMware Workstation", pro ? 3 : 2, supported && hardware, true, (host.WorkstationInstalled ? "检测到已有安装，可按项目第三方路线核验后使用。" : "Windows 第三方备选。") + "支持自动建机、Windows 应答安装、Tools 与来宾软件传送；首次下载登录和加密 / TPM 设置需本人确认。",
                ["程序会接着获取或识别官方安装器、建机并制作 Windows 安装应答介质。Broadcom 要求登录时由本人配合。", "首次按提示确认加密和虚拟 TPM 后，程序继续安装 Windows、Tools、Clash 和 AdsPower；不绕过 Windows 11 的硬件要求。"])
        };
        if (!supported) return list.Select(x => x with { Compatible = false, Reason = "此 EXE 支持 Windows 10 2004 及以上 / Windows 11 x64；Windows ARM 和其他系统尚未适配。" }).ToList();
        return list.OrderBy(x => x.Compatible ? 0 : 1).ThenBy(x => x.Rank).Select((x, index) => x with { Rank = index + 1 }).ToList();
    }
}
