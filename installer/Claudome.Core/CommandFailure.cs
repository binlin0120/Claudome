using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

// Only fixed operation labels and known public error identifiers leave this boundary.
// Raw stderr, arguments, paths and arbitrary vendor strings are never returned.
public static class CommandFailure
{
    private static readonly string[] NativeCodes=["VERR_AUTHENTICATION_FAILURE","VERR_LOGON_FAILURE","VERR_ACCOUNT_RESTRICTED","VERR_PASSWORD_EXPIRED","VBOX_E_INVALID_VM_STATE","VBOX_E_INVALID_OBJECT_STATE","VBOX_E_OBJECT_IN_USE","VBOX_E_OBJECT_NOT_FOUND","VBOX_E_VM_ERROR","VBOX_E_IPRT_ERROR","E_ACCESSDENIED","E_INVALIDARG","E_FAIL","VERR_INVALID_PARAMETER","VERR_VM_DRIVER_NOT_INSTALLED","VERR_NEM_NOT_AVAILABLE","VERR_NEM_INIT_FAILED","VERR_VMX_NO_VMX","VERR_SVM_NO_SVM","VERR_NO_MEMORY","VERR_DISK_FULL","VERR_FILE_NOT_FOUND","VERR_PATH_NOT_FOUND"];
    private static string NativeCode(string error)=>NativeCodes.FirstOrDefault(code=>Regex.IsMatch(error,@"\b"+code+@"\b",RegexOptions.IgnoreCase))??"";
    public static string Describe(string executable,IReadOnlyList<string> arguments,int exitCode,string error)
    {
        var file=Path.GetFileName(executable);var operation=arguments.FirstOrDefault()??"";
        var isBox=file.Equals("VBoxManage.exe",StringComparison.OrdinalIgnoreCase);
        var isVmware=new[]{"vmrun.exe","vmware-vdiskmanager.exe"}.Contains(file,StringComparer.OrdinalIgnoreCase);
        var isShell=file.Equals("pwsh.exe",StringComparison.OrdinalIgnoreCase);
        var tool=isBox?"VirtualBox":isVmware?"VMware":isShell?"PowerShell":"外部组件";
        var label=isBox?operation switch {
            "--version"=>"核对组件版本","list"=>"读取虚拟机列表","showvminfo"=>"读取原虚拟机信息","modifyvm"=>"更新原虚拟机设置","startvm"=>"启动原虚拟机","controlvm"=>"调整原虚拟机运行状态","guestcontrol"=>GuestControlLabel(arguments),"unattended"=>"准备 Windows 安装","storageattach" or "storagectl"=>"配置原虚拟机存储","createmedium"=>"创建任务磁盘","modifynvram"=>"配置任务固件","createvm"=>"创建任务虚拟机",_=>"执行组件命令"
        }:isVmware?operation switch {"list"=>"读取虚拟机列表","stop"=>"请求原虚拟机正常关机","-c" or "-r"=>"准备任务磁盘",_=>"执行组件命令"}:isShell?"执行管理脚本":"执行组件命令";
        var code=NativeCode(error);
        if(isBox&&operation=="guestcontrol"&&code is "" or "VBOX_E_IPRT_ERROR"&&Regex.IsMatch(error,@"(?i)not able to logon|authentication failure|logon failure|account restriction"))
            code="VERR_AUTHENTICATION_FAILURE";
        var hint=code switch {
            "VERR_AUTHENTICATION_FAILURE" or "VERR_LOGON_FAILURE" or "VERR_ACCOUNT_RESTRICTED" or "VERR_PASSWORD_EXPIRED"=>"虚拟机 Windows 账号或密码与助手记录不一致，请点击“同步虚拟机账号密码”后继续。",
            "E_ACCESSDENIED"=>"组件报告权限不足，请检查本任务所需的管理员确认。",
            "VBOX_E_INVALID_VM_STATE" or "VBOX_E_INVALID_OBJECT_STATE" or "VBOX_E_OBJECT_IN_USE"=>"当前状态不允许这项操作；保留原任务，待状态稳定后继续。",
            "VBOX_E_OBJECT_NOT_FOUND"=>"组件未找到记录中的对象，请核对原任务与当前用户的虚拟机登记。",
            "VERR_DISK_FULL"=>"磁盘空间不足，请释放空间后继续原任务。",
            "VERR_NO_MEMORY"=>"可用内存不足，请关闭闲置应用后继续。",
            "E_INVALIDARG" or "VERR_INVALID_PARAMETER"=>"组件拒绝了命令参数，请使用修正版后继续原任务。",
            _=>Regex.IsMatch(error,@"(?im)(?:error:|syntax error:)[^\r\n]*(?:invalid|unknown|unrecognized|syntax)")?"组件拒绝了命令参数，请使用修正版后继续原任务。":"原任务已保留，请检查组件和虚拟机状态后继续。"
        };
        var guestExit = isBox && operation == "guestcontrol" && arguments.ElementAtOrDefault(2) == "run" &&
            exitCode is >= 33 and <= 125 ? $"，来宾程序退出码 {exitCode - 32}" : "";
        return $"{tool} 在“{label}”时失败（退出码 {exitCode}{guestExit}{(code.Length>0?"，"+code:"")}）。{hint}";
    }
    private static string GuestControlLabel(IReadOnlyList<string> arguments) => arguments.ElementAtOrDefault(2) switch
    {
        "mkdir" => "创建来宾目录",
        "stat" => "核对来宾文件",
        "copyto" => "向来宾传送文件",
        "copyfrom" => "从来宾读取文件",
        "run" when arguments.Contains(@"C:\Windows\System32\tar.exe", StringComparer.OrdinalIgnoreCase) => "解压来宾安装资源",
        "run" when arguments.Any(argument => Path.GetFileName(argument).Equals("Configure.ps1", StringComparison.OrdinalIgnoreCase)) => "执行来宾配置",
        _ => "执行来宾操作"
    };
    private static string HyperVOperation(string operation)=>operation switch {
        "init" or "prepare"=>"准备管理组件","ensure"=>"核对原虚拟机身份","configure"=>"配置原虚拟机","windows"=>"安装 Windows","ready"=>"检查来宾就绪状态","running"=>"读取原虚拟机状态","shutdown"=>"请求原虚拟机正常关机","guest"=>"执行来宾命令","copy-to" or "copy-from"=>"传送来宾文件","cleanup" or "cleanup-recovery"=>"清理任务安装介质","open"=>"连接原虚拟机","boundary-prepare" or "boundary-verify" or "boundary-offline"=>"核对任务网络通道","console-restore"=>"恢复会话设置",_=>"执行管理操作"
    };
    internal static string HyperVWindowsStage(JsonElement report)
    {
        if (!report.TryGetProperty("windowsStage", out var stage) || stage.ValueKind != JsonValueKind.String) return "";
        var label = stage.GetString() switch
        {
            "checkpoint" => "核对安装检查点",
            "new-vhd" => "创建虚拟硬盘",
            "mount-vhd" => "挂载虚拟硬盘",
            "partitions" => "创建并格式化虚拟硬盘分区",
            "mount-iso" => "挂载并定位安装镜像",
            "read-image" => "读取安装索引",
            "apply-image" => "写入 Windows 系统映像",
            "bootstrap" => "准备来宾首次启动文件",
            "bcdboot" => "写入 Windows 引导文件",
            "attach-vhd" => "将系统盘接入原虚拟机",
            "boot-config" => "设置原虚拟机的启动顺序",
            "start-vm" => "启动原虚拟机",
            _ => null
        };
        if (label is null) return "";
        var nativeCode = report.TryGetProperty("windowsNativeCode", out var native) &&
            native.ValueKind == JsonValueKind.Number && native.TryGetInt32(out var number)
                ? $"（Windows 代码 0x{unchecked((uint)number):X8}）" : "";
        return $"失败子步骤为「{label}」{nativeCode}。";
    }

    public static string HyperV(string operation,string? code,string message)
    {
        var known=new[]{"HYPERV_STEP_FAILED","HYPERV_RESTART","HYPERV_EDITION","HYPERV_SWITCH","HYPERV_NOT_READY","VM_SCOPE","VM_COLLISION","DISK_SCOPE","VM_SECURITY","VM_KEY_PROTECTOR_READ_FAILED","VM_KEY_PROTECTOR_SETUP_FAILED","VM_TPM_ENABLE_FAILED","BOUNDARY_NICS","BOUNDARY_SERVICE","BOUNDARY_METADATA","HYPERV_CONSOLE_POLICY","WINDOWS_MEDIA_UNSUPPORTED","WINDOWS_IMAGE_APPLY_FAILED","WINDOWS_BOOT_FILES_FAILED","GUEST_COMMAND_FAILED","GUEST_SHUTDOWN_PENDING","UNSUPPORTED_OPERATION"};
        return $"Hyper-V 在“{HyperVOperation(operation)}”时未完成（{(known.Contains(code)?code:"HYPERV_STEP_FAILED")}）。{message}";
    }
    public static string Vix(string operation,ulong code)
    {
        var label=operation switch {"connect"=>"连接管理服务","open"=>"打开原虚拟机","login"=>"登录来宾系统","run"=>"执行来宾命令","copy"=>"传送来宾文件",_=>"等待组件操作"};
        return $"VMware 在“{label}”时失败（VIX 错误码 {code}）。请检查来宾工具、虚拟机解锁和操作权限后继续原任务。";
    }
    public static string GuestExit(long code)=>$"VMware 来宾命令未完成（退出码 {code}）。请查看来宾中的管理员提示和软件状态。";
}
