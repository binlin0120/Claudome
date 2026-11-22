using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ClaudeSetup;

internal sealed class GatewayRuleMissingException(string component):SetupException("本任务的外层通道规则尚未完成创建："+component);

public static partial class GatewayFirewall
{
    // INetFwRules.Item documents ERROR_FILE_NOT_FOUND; CLR may translate it to
    // FileNotFoundException rather than COMException. Other failures must propagate.
    internal static object? LookupRule(Func<object> lookup)
    {
        try{return lookup();}
        catch(Exception error) when(error is FileNotFoundException or COMException && unchecked((uint)error.HResult)==0x80070002){return null;}
    }
    internal static void RequireProfiles(int policyState,IReadOnlyDictionary<int,bool> enabled)
    {
        if(policyState!=0)throw new SetupException("主机防火墙受组织策略限制，请由管理员核对本任务所需的规则权限。");
        var labels=new Dictionary<int,string>{{1,"域网络"},{2,"专用网络"},{4,"公用网络"}};
        var missing=labels.Where(p=>!enabled.TryGetValue(p.Key,out var on)||!on).Select(p=>p.Value).ToArray();
        if(missing.Length>0)throw new HandoffException(new("外层防护需要启用防火墙："+string.Join("、",missing),[
            "尚未确认开启："+string.Join("、",missing)+"。此时不会请求创建规则或启动虚拟机。",
            "请本人在 Windows 安全中心 → 防火墙和网络保护中，核对上述网络类型的 Microsoft Defender 防火墙。",
            "开启后回到原任务继续；助手不会自动更改主机的防火墙开关。"],"gateway-firewall-profile"));
    }
    private static void ReadHostProfiles(object policy)
    {
        dynamic fw=policy;
        RequireProfiles((int)fw.LocalPolicyModifyState,new Dictionary<int,bool>{{1,(bool)fw.FirewallEnabled[1]},{2,(bool)fw.FirewallEnabled[2]},{4,(bool)fw.FirewallEnabled[4]}});
    }
    internal static string ElevatedFailure(int code)=>code switch {
        11=>"外层防护需要管理员权限；请确认本任务的系统管理员提示后继续。",
        12=>"外层防护请求未通过任务身份或文件校验；请载入完整的原任务后继续。",
        13=>"外层防护所需的 VirtualBox 窗口或后台组件缺失，请修复组件安装后继续。",
        14=>"主机防火墙受组织策略限制，管理员进程未创建规则。",
        15=>"主机有网络类型的防火墙尚未开启；请核对域、专用和公用网络后继续。",
        16=>"管理员进程未能完成外层规则回读，请保留原任务并查看具体检查结果。",
        17=>"Windows 拒绝访问本任务的防火墙规则，请核对管理员权限或组织策略。",
        18=>"调用 Windows 防火墙接口时发生异常，外层规则未确认完成。",
        _=>$"外层防护管理员进程未正常完成（退出码 {code}），原任务已保留。"
    };
    internal static SetupException ElevationFailure(Win32Exception error)=>new(error.NativeErrorCode switch {
        1223=>"管理员确认已取消，本次未完成外层防护。需要时继续原任务，并由本人确认系统提示。",
        5=>"Windows 拒绝启动外层防护管理员进程，请核对权限或组织策略。",
        740=>"外层防护管理员权限未就绪，请确认系统提示后继续。",
        _=>$"外层防护管理员进程未启动（系统错误码 {error.NativeErrorCode}），原任务已保留。"
    });
}
