using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ClaudeSetup;

public sealed record GatewayFirewallRequest(string Root, string VmId, int GuestPort,string RunId="");
public sealed record GatewayFirewallRule(string Name, string Program, int Port);
public static partial class GatewayFirewall
{
    public const string Group = "ClaudeEnvironmentSetup.ExternalBoundary";
    // Nonzero host bits keep subnet literals valid; masking preserves the exact old scope.
    public const string RemoteRange = "0.0.0.1/2,64.0.0.0/3,96.0.0.0/4,112.0.0.0/5,120.0.0.0/6,124.0.0.0/7,126.0.0.0/8,128.0.0.0/1,::2/1,8000::/1";
    public static List<GatewayFirewallRule> Plan(GatewayFirewallRequest request, string vboxDirectory)
    {
        if (!Guid.TryParseExact(request.VmId, "D", out _) || request.GuestPort is < 1024 or > 65535) throw new SetupException("外层网关绑定无效。");
        return new[] { "VirtualBoxVM.exe", "VBoxHeadless.exe" }.Select(file => new GatewayFirewallRule($"Claude gateway {request.VmId} {request.GuestPort} {file}", Path.Combine(vboxDirectory, file), request.GuestPort)).ToList();
    }
    public static void Verify(GatewayFirewallRequest request)
    {
        var executable = Machine.VBoxPath() ?? throw new SetupException("VirtualBox 尚未安装。");
        object? policy = null;
        try {
            policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!); dynamic fw = policy!;
            ReadHostProfiles(policy!);
            foreach (var rule in Plan(request, Path.GetDirectoryName(executable)!)) {
                object? raw = null;
                try {
                    raw = LookupRule(()=>(object)fw.Rules.Item(rule.Name));
                    if(raw is null)throw new GatewayRuleMissingException(Path.GetFileName(rule.Program));
                    dynamic r = raw;
                    if(IsOwnedDisabledRule(raw,rule))throw new GatewayRuleMissingException(Path.GetFileName(rule.Program));
                    if (!(bool)r.Enabled || (int)r.Action != 0 || (int)r.Direction != 1 || (int)r.Protocol != 17 || (int)r.Profiles != int.MaxValue ||
                        !string.Equals((string)r.ApplicationName, rule.Program, StringComparison.OrdinalIgnoreCase) || (string)r.LocalPorts != rule.Port.ToString() ||
                        !RemoteScopeMatches((string)r.RemoteAddresses) || (string)r.Grouping != Group || (string)r.InterfaceTypes != "All") throw new SetupException("外层通道防护规则回读不匹配，保持断网。");
                } finally { if (raw is not null && Marshal.IsComObject(raw)) Marshal.ReleaseComObject(raw); }
            }
        } catch (SetupException) { throw; }
        catch { throw new SetupException("未能确认主机的外层通道防护规则，保持断网。"); }
        finally { if (policy is not null && Marshal.IsComObject(policy)) Marshal.ReleaseComObject(policy); }
    }
    // Called only by the explicit elevated mode. No global firewall, proxy or
    // existing application rule is changed. No real invocation in offline tests.
    public static int Elevated(string requestFile)
    {
        GatewayFirewallRequest? accepted=null;Exception? failure=null;
        var step="validate-request";var component="";var exit=18;
        int Finish(int code){exit=code;return code;}
        try {
            using var identity=WindowsIdentity.GetCurrent();
            if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))return Finish(11);
            Paths.RejectLinks(requestFile);
            if(!File.Exists(requestFile)||new FileInfo(requestFile).Length>8192)return Finish(12);
            var request=JsonFile.Read<GatewayFirewallRequest>(requestFile);Paths.ValidateRoot(request.Root);
            if(!Guid.TryParseExact(request.RunId,"N",out _)||!Path.GetFullPath(requestFile).Equals(Paths.Under(request.Root,"private/gateway-firewall-request.json"),StringComparison.OrdinalIgnoreCase)||!File.Exists(Paths.Under(request.Root,".claude-setup-workspace.json")))return Finish(12);
            accepted=request;
            step="locate-components";
            var executable=Machine.VBoxPath()??throw new SetupException("VirtualBox 未安装。");
            var planned=Plan(request,Path.GetDirectoryName(executable)!);
            if(planned.Any(x=>!File.Exists(x.Program)))return Finish(13);
            object? policy=null;
            try {
                step="open-policy";policy=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);dynamic fw=policy!;
                step="check-policy";if((int)fw.LocalPolicyModifyState!=0)return Finish(14);
                step="check-profiles";foreach(var profile in new[]{1,2,4})if(!(bool)fw.FirewallEnabled[profile])return Finish(15);
                foreach(var spec in planned) {
                    component=Path.GetFileName(spec.Program);step="lookup-rule";
                    var existing=LookupRule(()=>(object)fw.Rules.Item(spec.Name));
                    if(existing is not null) {
                        try{if(IsOwnedDisabledRule(existing,spec))fw.Rules.Remove(spec.Name);else continue;}
                        finally{Marshal.ReleaseComObject(existing);}
                    }
                    step="create-rule";object raw=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
                    object? attached=null;var created=false;
                    try {
                        RegisterRule(raw,spec,template=>{
                            fw.Rules.Add(template);created=true;step="attach-rule";
                            attached=(object)fw.Rules.Item(spec.Name);return attached;
                        },value=>step=value);
                    }catch {
                        var failedStep=step;
                        if(created) {
                            object? current=null;
                            try{step="rollback-rule";current=LookupRule(()=>(object)fw.Rules.Item(spec.Name));if(current is not null&&IsOwnedDisabledRule(current,spec,true))fw.Rules.Remove(spec.Name);}
                            catch{}
                            finally{if(current is not null&&Marshal.IsComObject(current))Marshal.ReleaseComObject(current);}
                        }
                        step=failedStep;throw;
                    }finally{if(attached is not null&&Marshal.IsComObject(attached))Marshal.ReleaseComObject(attached);Marshal.ReleaseComObject(raw);}
                }
            }finally{if(policy is not null)Marshal.ReleaseComObject(policy);}
            step="verify-rules";Verify(request);step="done";return Finish(0);
        }catch(UnauthorizedAccessException error){failure=error;return Finish(17);}
        catch(COMException error) when(unchecked((uint)error.HResult)==0x80070005){failure=error;return Finish(17);}
        catch(SetupException error){failure=error;return Finish(16);}
        catch(Exception error){failure=error;return Finish(18);}
        finally {
            if(accepted is not null) {
                try {
                    var path=ReportPath(accepted);Paths.RejectLinks(path);
                    JsonFile.Write(path,new GatewayFirewallReport(1,accepted.VmId,accepted.RunId,exit,step,component,ErrorKind(failure),failure?.HResult??0,(failure as System.ComponentModel.Win32Exception)?.NativeErrorCode));
                }catch{}
            }
        }
    }
    public static async Task Ensure(GatewayFirewallRequest request, CancellationToken token)
    {
        request=request with {RunId=Guid.NewGuid().ToString("N")};
        try { Verify(request); return; } catch (GatewayRuleMissingException) { }
        SecretStore.RestrictDirectory(Paths.Under(request.Root, "private"));
        var file = Paths.Under(request.Root, "private/gateway-firewall-request.json"); JsonFile.Write(file, request);
        try {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--gateway-firewall"); start.ArgumentList.Add(file);
            Process process;
            try{process=Process.Start(start)??throw new SetupException("外层通道防护未启动。");}
            catch(System.ComponentModel.Win32Exception error){throw ElevationFailure(error);}
            using(process){await process.WaitForExitAsync(token);if(process.ExitCode!=0)throw new SetupException(FailureFromReport(request,process.ExitCode));}
            Verify(request);
        } finally { File.Delete(file); }
    }
}
