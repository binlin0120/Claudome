using System.IO;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed record VmDiagnostic(string State,bool NemMode,bool ResetSeen,bool GuestToolsReady,int CpuCount,int MemoryMb,string[] Actions);
public static class VmDiagnostics
{
    public static VmDiagnostic Parse(string info,string log)
    {
        var mode=VmInventory.VBoxValue(info,"VMState");var nem=log.Contains("Snail execution mode",StringComparison.OrdinalIgnoreCase);
        var reset=log.Contains("to 'RESETTING'",StringComparison.Ordinal);
        int.TryParse(VmInventory.VBoxValue(info,"cpus"),out var cpu);int.TryParse(VmInventory.VBoxValue(info,"memory"),out var memory);
        var tools=int.TryParse(VmInventory.VBoxValue(info,"GuestAdditionsRunLevel"),out var level)&&level>0;
        var actions=new List<string>{"运行中只表示虚拟机进程启动，不代表 Windows 或安装器正常。持续黑屏时先保留原盘，不反复创建任务。"};
        if (mode == "running" && log.Contains(VirtualBox.FramebufferConflict, StringComparison.Ordinal))
            actions.Add("日志记录了 VirtualBox 显示缓冲重复占用。当前仍黑屏时，在检查与维护中选择“保存状态并恢复显示”。");
        else if(nem)actions.Add("发现 Hyper-V 兼容运行模式（NEM）。若画面定格、白屏无响应或单核满载，请点击“强制关闭无响应虚拟机并修复”自动切换为 1 核防死锁兼容配置（宿主安全功能保持原设置；若客户已指定自定义资源，平时启动不会自动覆盖客户配置；切换后仍属待验证恢复项，请按实际启动结果确认）。");
        if(!tools)actions.Add("来宾工具尚未就绪；Windows 完成后需安装 Guest Additions，助手才能继续传送与配置软件。");
        if(reset)actions.Add("日志记录过重置。安装重启时让硬盘继续启动；首启 ISO 的按键提示应在程序内桌面操作。");
        return new(mode,nem,reset,tools,cpu,memory,actions.ToArray());
    }
    public static async Task<VmDiagnostic> Read(SessionState state,CancellationToken token)
    {
        if(state.Options.PlatformId!="virtualbox")throw new SetupException("此详细诊断入口当前适用于 VirtualBox；其他平台请使用通用诊断。");
        var box=new VirtualBox(new CommandRunner());await box.EnsureMachine(state,token);
        var info=await box.Run(token,"showvminfo",state.VmId,"--machinereadable");
        var path=Paths.Under(state.Options.VmDirectory,@"Logs\VBox.log");var log="";
        if(File.Exists(path)) {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            if(stream.Length>4*1024*1024)stream.Seek(-4*1024*1024,SeekOrigin.End);
            using var reader=new StreamReader(stream);log=await reader.ReadToEndAsync(token);
        }
        return Parse(info,log);
    }
}
