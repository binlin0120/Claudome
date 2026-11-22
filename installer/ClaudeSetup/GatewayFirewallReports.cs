using System.IO;
using System.Runtime.InteropServices;

namespace ClaudeSetup;

internal sealed record GatewayFirewallReport(int Schema,string VmId,string RunId,int ExitCode,string Step,string Component,string ErrorKind,int HResult,int? NativeErrorCode);
public static partial class GatewayFirewall
{
    private static readonly Dictionary<string,string> Stages=new(){["validate-request"]="核验任务请求",["locate-components"]="查找虚拟机组件",["open-policy"]="连接防火墙接口",["check-policy"]="核验组织策略",["check-profiles"]="核验网络类型开关",["lookup-rule"]="查询任务规则",["create-rule"]="创建规则对象",["set-name"]="设置规则名称",["set-group"]="设置规则归属",["set-description"]="设置规则说明",["set-program"]="绑定虚拟机程序",["set-protocol"]="设置 UDP 协议",["set-port"]="绑定任务端口",["set-remote"]="设置远端地址范围",["verify-remote"]="核验远端地址覆盖",["set-direction"]="设置入站方向",["set-action"]="设置阻止动作",["set-profiles"]="设置适用网络",["set-interfaces"]="设置接口类型",["set-enabled"]="启用任务规则",["add-rule"]="提交任务规则",["add-disabled-rule"]="登记禁用规则",["attach-rule"]="读取已登记规则",["rollback-rule"]="清理本次未完成规则",["verify-rules"]="回读任务规则",["done"]="完成规则核验"};
    private static string ErrorKind(Exception? error)=>error switch {null=>"NONE",COMException=>"COM",System.ComponentModel.Win32Exception=>"WIN32",UnauthorizedAccessException=>"ACCESS",FileNotFoundException=>"FILE",ArgumentException=>"ARGUMENT",InvalidOperationException=>"OPERATION",Microsoft.CSharp.RuntimeBinder.RuntimeBinderException=>"BINDING",IOException=>"IO",_=>"OTHER"};
    private static string ReportPath(GatewayFirewallRequest request)
    {
        if(!Guid.TryParseExact(request.RunId,"N",out _))throw new SetupException("防护诊断请求身份无效。");
        return Paths.Under(request.Root,"private/gateway-firewall-result-"+request.RunId+".json");
    }
    internal static string? FormatReport(GatewayFirewallReport report,GatewayFirewallRequest request,int exitCode)
    {
        if(report.Schema!=1||report.VmId!=request.VmId||report.RunId!=request.RunId||report.ExitCode!=exitCode)return null;
        var stage=Stages.GetValueOrDefault(report.Step,"未识别阶段");
        var component=report.Component is "VirtualBoxVM.exe" or "VBoxHeadless.exe"?"，"+report.Component:"";
        var kind=report.ErrorKind is "NONE" or "COM" or "WIN32" or "ACCESS" or "FILE" or "ARGUMENT" or "OPERATION" or "BINDING" or "IO"?report.ErrorKind:"OTHER";
        return ElevatedFailure(exitCode)+$"\n诊断：{stage}{component}；类型 {kind}；HRESULT 0x{unchecked((uint)report.HResult):X8}"+(report.NativeErrorCode is int native?$"；系统码 {native}":"")+"。";
    }
    private static string FailureFromReport(GatewayFirewallRequest request,int exitCode)
    {
        try {
            var path=ReportPath(request);Paths.RejectLinks(path);
            if(File.Exists(path)&&new FileInfo(path).Length<=8192) {
                var detail=FormatReport(JsonFile.Read<GatewayFirewallReport>(path),request,exitCode);
                if(detail is not null)return detail;
            }
        }catch{}
        return ElevatedFailure(exitCode)+" 管理员进程未返回匹配的脱敏诊断记录。";
    }
}
