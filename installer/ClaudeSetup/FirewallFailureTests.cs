using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ClaudeSetup;

public static class FirewallFailureTests
{
    private static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    public static async Task Run(Func<string,Func<Task>,Task> check)
    {
        Task Sync(Action action){action();return Task.CompletedTask;}
        await check("firewall-missing-rule-CLR-FileNotFound-is-a-create-candidate",()=>Sync(()=> {
            Assert(GatewayFirewall.LookupRule(()=>throw new FileNotFoundException("PRIVATE_FIXTURE")) is null,"CLR-mapped absence aborted first rule creation");
        }));
        await check("firewall-missing-rule-COM-HRESULT-is-a-create-candidate",()=>Sync(()=> {
            Assert(GatewayFirewall.LookupRule(()=>throw new COMException("PRIVATE_FIXTURE",unchecked((int)0x80070002))) is null,"COM absence not recognized");
        }));
        await check("firewall-rule-access-denial-is-never-treated-as-absence",()=>Sync(()=> {
            var rejected=0;
            foreach(var error in new Exception[]{new UnauthorizedAccessException("PRIVATE_FIXTURE"),new COMException("PRIVATE_FIXTURE",unchecked((int)0x80070005)),new COMException("PRIVATE_FIXTURE",unchecked((int)0x80004005)),new IOException("PRIVATE_FIXTURE")}) {
                try{GatewayFirewall.LookupRule(()=>throw error);}catch(Exception actual){Assert(ReferenceEquals(actual,error),"native error replaced");rejected++;}
            }
            Assert(rejected==4,"unrelated error was treated as missing rule");
        }));
        await check("firewall-existing-rule-object-is-reused",()=>Sync(()=> {
            var existing=new object();Assert(ReferenceEquals(existing,GatewayFirewall.LookupRule(()=>existing)),"existing rule was replaced");
        }));
        await check("real-firewall-missing-rule-read-only-lookup-matches-CLR-contract",()=>Sync(()=> {
            object? policy=null;
            try {
                policy=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);dynamic fw=policy!;
                // Read a unique nonexistent name only. No Rules.Add/Remove or profile write.
                Assert(GatewayFirewall.LookupRule(()=>(object)fw.Rules.Item("Claudome read-only lookup fixture "+Guid.NewGuid().ToString("N"))) is null,"real absent rule not recognized");
            }finally{if(policy is not null&&Marshal.IsComObject(policy))Marshal.ReleaseComObject(policy);}
        }));
        await check("firewall-disabled-domain-produces-specific-preflight-handoff",()=>Sync(()=> {
            try{GatewayFirewall.RequireProfiles(0,new Dictionary<int,bool>{{1,false},{2,true},{4,true}});throw new Exception("Expected domain handoff");}
            catch(HandoffException error){Assert(error.Action.Id=="gateway-firewall-profile"&&error.Action.Title.Contains("域网络")&&!error.Action.Title.Contains("公用网络"),"disabled profile not identified");}
        }));
        await check("firewall-all-profiles-enabled-continues-without-global-changes",()=>Sync(()=> {
            GatewayFirewall.RequireProfiles(0,new Dictionary<int,bool>{{1,true},{2,true},{4,true}});
        }));
        await check("firewall-elevated-failure-and-UAC-cancel-remain-distinct",()=>Sync(()=> {
            Assert(GatewayFirewall.ElevatedFailure(13).Contains("组件")&&GatewayFirewall.ElevatedFailure(17).Contains("拒绝访问"),"worker causes hidden");
            Assert(GatewayFirewall.ElevationFailure(new Win32Exception(1223,"PRIVATE_FIXTURE")).Message.Contains("取消"),"UAC cancellation hidden");
            Assert(!GatewayFirewall.ElevationFailure(new Win32Exception(5,"PRIVATE_FIXTURE")).Message.Contains("PRIVATE_FIXTURE"),"raw native message escaped");
        }));
        await check("firewall-worker-report-is-bound-to-run-and-task",()=>Sync(()=> {
            var request=new GatewayFirewallRequest(@"C:\Fixture","11111111-1111-1111-1111-111111111111",50001,Guid.NewGuid().ToString("N"));
            var result=new GatewayFirewallReport(1,request.VmId,request.RunId,18,"add-rule","VirtualBoxVM.exe","ARGUMENT",unchecked((int)0x80070057),null);
            var detail=GatewayFirewall.FormatReport(result,request,18);
            Assert(detail is not null&&detail.Contains("提交任务规则")&&detail.Contains("80070057"),"worker stage lost");
            Assert(GatewayFirewall.FormatReport(result with {RunId=Guid.NewGuid().ToString("N")},request,18) is null,"stale report accepted");
            Assert(GatewayFirewall.FormatReport(result with {VmId=Guid.NewGuid().ToString()},request,18) is null,"other task report accepted");
        }));
        await check("firewall-worker-report-rejects-untrusted-text",()=>Sync(()=> {
            var request=new GatewayFirewallRequest(@"C:\Fixture","11111111-1111-1111-1111-111111111111",50001,Guid.NewGuid().ToString("N"));
            var result=new GatewayFirewallReport(1,request.VmId,request.RunId,18,"PRIVATE_FIXTURE","PRIVATE_FIXTURE","PRIVATE_FIXTURE",0,null);
            Assert(!GatewayFirewall.FormatReport(result,request,18)!.Contains("PRIVATE_FIXTURE"),"raw diagnostic fields escaped");
        }));
        await check("unclassified-errors-keep-safe-diagnostic-category",()=>Sync(()=> {
            var file=Redaction.SafeError(new FileNotFoundException("PRIVATE_FIXTURE",@"C:\PRIVATE_FIXTURE\private.json"));
            var json=Redaction.SafeError(new System.Text.Json.JsonException("PRIVATE_FIXTURE"));
            var unknown=Redaction.SafeError(new InvalidOperationException("PRIVATE_FIXTURE"));
            Assert(file.Contains("FILE_NOT_FOUND")&&json.Contains("JSON_INVALID")&&unknown.Contains("INVALID_OPERATION"),"diagnostic category missing");
            Assert(!file.Contains("PRIVATE_FIXTURE")&&!json.Contains("PRIVATE_FIXTURE")&&!unknown.Contains("PRIVATE_FIXTURE"),"private native error escaped");
        }));
    }
}
