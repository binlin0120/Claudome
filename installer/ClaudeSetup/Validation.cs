using System.IO;
using System.Text.Json;

namespace ClaudeSetup;

public sealed partial class Deployment
{
    public static bool RestartEvidencePasses(JsonElement report,string bootBefore,bool manualAdsPower = false) =>
        DateTimeOffset.TryParse(bootBefore,out var before) &&
        report.TryGetProperty("bootUtc",out var boot) && DateTimeOffset.TryParse(boot.GetString(),out var after) && after > before &&
        report.GetProperty("clashRuntimePassed").GetBoolean() && report.GetProperty("networkPassed").GetBoolean() &&
        (manualAdsPower || (report.GetProperty("browserRuntimePassed").GetBoolean() && report.GetProperty("translationPassed").GetBoolean()));

    public async Task ValidateExisting(SessionState state,Secrets secrets,bool restart,bool outageTest,CancellationToken token)
    {
        if(string.IsNullOrEmpty(state.VmId)||!state.Options.HasUsableGuestPassword(secrets.VmPassword)||state.CoreConfigSha256.Length!=64)
            throw new SetupException("请载入已完成配置的原任务，并填写虚拟机密码。旧版本任务需先继续部署一次。");
        var box=BoundaryRuntime.BackendFor(state)??throw new SetupException("请保持本次助手与外层保护开启。若刚重开助手，请先继续原任务恢复连接，再重新做重启验收。");
        await BoundaryRuntime.VerifyCurrent(state,token);
        using var workspaceLock=WorkspaceLock.Acquire(state);
        messages.Report("先检查当前来宾配置与实际运行参数；本步骤不会重新配置 Clash 或 AdsPower。");
        if(restart||state.RestartBootBefore.Length>0)state.RestartPassed=false;
        if(outageTest)state.OutagePassed=false;
        state.Save();
        try {
            using var initial=await ReadValidation(box,state,secrets,outageTest,token);
            var result=initial.RootElement;
            ApplyValidation(state,result);
            ApplyProtection(state, result, DateTime.UtcNow, guestOnly: false);
            if (state.Protection.Outage.Status == CheckStatus.Failed ||
                state.Protection.Boundary.Status == CheckStatus.Failed ||
                state.Protection.Exit.Status == CheckStatus.Failed ||
                state.Protection.Dns.Status == CheckStatus.Failed)
            {
                state.NetworkPassed = false;
                await BoundaryRuntime.StopFor(state);
                state.Save();
                WriteProtectionReport(state);
                throw new SetupException("保护检查失败，已暂停外层连接。");
            }
            if(restart && string.IsNullOrEmpty(state.RestartBootBefore)) {
                if(!result.GetProperty("clashRuntimePassed").GetBoolean()||!result.GetProperty("networkPassed").GetBoolean())
                    throw new SetupException("重启前检查未通过，未请求重启。请查看待处理项，修复后重新验收。");
                var boot=result.GetProperty("bootUtc").GetString()??"";
                if(!DateTimeOffset.TryParse(boot,out _))throw new SetupException("没有有效的来宾启动时间，未请求重启。");
                state.RestartBootBefore=boot;state.Status="等待来宾重启并重新登录";state.Save();
                // A normal guest restart is explicitly requested in the UI; no forced process close.
                _=await box.GuestRun(state,secrets,@"C:\Windows\System32\shutdown.exe",["/r","/t","0"],token);
                messages.Report("已请求正常重启虚拟机。请在虚拟机中重新登录 Windows；保持助手开启。");
                await Task.Delay(TimeSpan.FromSeconds(12),token);
                for(var i=0;i<60;i++) {
                    token.ThrowIfCancellationRequested();
                    if(await box.GuestReady(state,secrets,token))break;
                    if(i%6==0)messages.Report("等待虚拟机重启和来宾工具；如有保存工作提示，请在虚拟机中处理。");
                    await Task.Delay(TimeSpan.FromSeconds(5),token);
                }
                await box.Open(state,token);
                await BoundaryRuntime.VerifyCurrent(state,token);
                using var after=await ReadValidation(box,state,secrets,false,token);
                result=after.RootElement;
                ApplyValidation(state,result);
                ApplyProtection(state, result, DateTime.UtcNow, guestOnly: false);
                if (!state.NetworkPassed && state.Protection.Exit.Status == CheckStatus.Failed)
                    await BoundaryRuntime.StopFor(state);
                state.RestartPassed=RestartEvidencePasses(result,state.RestartBootBefore,state.Options.ManualAdsPower);
            } else if(!string.IsNullOrEmpty(state.RestartBootBefore)) {
                // Continue the pending readback without requesting another restart.
                state.RestartPassed=RestartEvidencePasses(result,state.RestartBootBefore,state.Options.ManualAdsPower);
            }
            if(state.RestartPassed)state.RestartBootBefore="";
            state.Stage=state.Options.ManualAdsPower && state.NetworkPassed ? CustomerGuide.ManualAdsPowerStage :
                state.RestartPassed?"重启后的运行检查通过，请在浏览器中核对检测页和登录":
                state.RestartBootBefore.Length>0?"重启验收待完成；重新登录后点检查已配置环境继续回读":
                state.NetworkPassed&&state.BrowserRuntimePassed&&state.TranslationPassed?"当前网络和浏览器运行参数检查通过；请继续检测页验收":"运行检查有待处理项，请查看指引";
            state.Status="需要本人完成浏览器验收";state.Save();
            WriteValidationAcceptance(state);
            WriteProtectionReport(state);
            messages.Report(state.Stage);
        }catch(Exception ex) {
            state.NetworkPassed=false;state.OutagePassed=false;state.BrowserRuntimePassed=false;state.BrowserReopenPassed=false;state.TranslationPassed=false;state.RestartPassed=false;
            if(ex is HandoffException handoff)state.PendingActions.Add(handoff.Action);
            state.Status="运行检查待继续";state.Save();WriteValidationAcceptance(state);throw;
        }
    }
    private async Task<JsonDocument> ReadValidation(IVmBackend box,SessionState state,Secrets secrets,bool outageTest,CancellationToken token,bool guestOnly = false,bool quick = false,bool measure = false,bool openBrowser = false,bool elevate = false,bool enableBlockAudit = false)
    {
        var o=state.Options;var guestRoot=o.GuestSetupDirectory;
        TimeZoneInfo.TryConvertWindowsIdToIanaId(o.TimeZoneId,o.Country,out var zone);
        var runId=Guid.NewGuid().ToString("N");
        using var input=new PrivateFile(o.Private,JsonSerializer.Serialize(new{runId,vmId=state.VmId,workspaceId=o.WorkspaceId,platformId=o.PlatformId,transportKind=o.PlatformId=="virtualbox"?"ethernet":"bus",country=o.Country,timeZoneId=o.TimeZoneId,ianaTimeZone=zone,coreConfigSha256=state.CoreConfigSha256,endpointAddresses=BoundaryRuntime.EndpointsFor(state),endpointPort=BoundaryRuntime.PortFor(state),expectedExitIp=secrets.ExpectedExitIp,manualAdsPower=o.ManualAdsPower,adsPowerApiKey=o.ManualAdsPower?"":secrets.AdsPowerApiKey,testOutage=outageTest,guestOnly,quick,measure,openBrowser,enableBlockAudit}),".json");
        foreach(var name in new[]{"Validate.ps1","Integrations.ps1","BrowserAudit.ps1","NetworkAudit.ps1"}) {
            using var script=new PrivateFile(o.Private,Catalog.EmbeddedText(name),".ps1");
            await box.CopyTo(state,secrets,script.FileName,guestRoot+"\\"+name,token);
        }
        await box.CopyTo(state,secrets,input.FileName,guestRoot+@"\validation-input.json",token);
        SetupException? guestRunFailure = null;
        try{_=await box.GuestRun(state,secrets,guestRoot+@"\pwsh\pwsh.exe",["-NoProfile","-File",guestRoot+@"\Validate.ps1","-PayloadPath",guestRoot+@"\validation-input.json" , ..(quick && !elevate ? new[] { "-Quick" } : [])],token,quick && !elevate ? 125 : 600);}
        catch(HandoffException){throw;}catch(SetupException ex){ guestRunFailure = ex; /* Only a fresh identity-bound report is accepted below. */ }
        var path=Paths.Under(o.Private,"validation-result.json");if(File.Exists(path))File.Delete(path);
        try { await box.CopyFrom(state,secrets,guestRoot+@"\validation-result.json",path,token); }
        catch (SetupException) when (guestRunFailure is not null) { throw guestRunFailure; }
        if(new FileInfo(path).Length>128*1024)throw new SetupException("来宾检查报告过大，未接受。");
        var report=JsonDocument.Parse(File.ReadAllText(path));
        try{GuestEvidence.RequireCurrent(report.RootElement,state,runId);return report;}
        catch (SetupException staleError)
        {
            report.Dispose();
            if (guestRunFailure is not null)
                throw new SetupException($"{staleError.Message}（来宾命令错误：{Redaction.SafeError(guestRunFailure)}）");
            throw;
        }
        catch{report.Dispose();throw;}
    }
    private static void ApplyValidation(SessionState state,JsonElement result)
    {
        state.NetworkPassed=result.GetProperty("networkPassed").GetBoolean();
        state.BrowserRuntimePassed=result.GetProperty("browserRuntimePassed").GetBoolean();
        state.TranslationPassed=result.GetProperty("translationPassed").GetBoolean();
        if(!state.NetworkPassed||!state.BrowserRuntimePassed||!state.TranslationPassed){state.RestartPassed=false;state.BrowserReopenPassed=false;}
        if(!state.NetworkPassed)state.OutagePassed=false;
        if(result.GetProperty("browserReopenTested").GetBoolean())state.BrowserReopenPassed=result.GetProperty("browserReopenPassed").GetBoolean();
        if(result.GetProperty("outageTested").GetBoolean())state.OutagePassed=result.GetProperty("outagePassed").GetBoolean();
        state.PendingActions.RemoveAll(a=>new[]{"runtime-validation","browser-runtime","browser-reopen","outage-validation"}.Contains(a.Id));
        if (state.Options.ManualAdsPower)
        {
            state.PendingActions.RemoveAll(ResumePlan.IsBrowserHandoff);
            if (state.NetworkPassed) state.PendingActions.Add(CustomerGuide.ManualAdsPower());
        }
        state.PendingActions.AddRange(result.GetProperty("pendingActions").Deserialize<List<UserAction>>(JsonFile.Format)??[]);
    }
    private static void WriteValidationAcceptance(SessionState state) => Deployment.WriteAcceptanceReport(state);
}
