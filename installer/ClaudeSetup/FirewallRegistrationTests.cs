namespace ClaudeSetup;

public static class FirewallRegistrationTests
{
    public sealed class Rule
    {
        public string Name{get;set;}="";public string ApplicationName{get;set;}="";public int Protocol{get;set;}public string LocalPorts{get;set;}="";public int Direction{get;set;}public int Action{get;set;}public int Profiles{get;set;}public string Grouping{get;set;}="";public string Description{get;set;}="";public string InterfaceTypes{get;set;}="All";
        public bool RejectRemote;public Func<string,string>? Readback;private bool enabled;private string remote="*";
        public string RemoteAddresses{get=>Readback?.Invoke(remote)??remote;set{if(RejectRemote)throw new ArgumentException("fixture rejected remote range");remote=value;}}
        public bool Enabled{get=>enabled;set{if(value&&(Grouping!=GatewayFirewall.Group||!GatewayFirewall.RemoteScopeMatches(RemoteAddresses)||Profiles!=int.MaxValue))throw new Exception("A rule was enabled before its restrictions were complete");enabled=value;}}
        public Rule Clone()=>(Rule)MemberwiseClone();
    }
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task Run(Func<string,Func<Task>,Task> check)
    {
        Task Sync(Action action){action();return Task.CompletedTask;}
        GatewayFirewallRule Spec()=>new("fixture-owned",@"C:\Fixture\VirtualBoxVM.exe",50001);
        await check("firewall-registration-is-disabled-until-retrieved-and-complete",()=>Sync(()=> {
            var spec=Spec();var template=new Rule();Rule? stored=null;var phases=new List<string>();
            GatewayFirewall.RegisterRule(template,spec,raw=>{var initial=(Rule)raw;Assert(!initial.Enabled&&initial.Protocol==17&&initial.LocalPorts=="50001"&&initial.ApplicationName==spec.Program,"baseline scope incomplete");stored=initial.Clone();return stored;},phases.Add);
            Assert(stored is not null&&stored.Enabled&&stored.Grouping==GatewayFirewall.Group&&stored.RemoteAddresses==GatewayFirewall.RemoteRange,"attached rule not completed");
            Assert(!template.Enabled&&template.RemoteAddresses=="*"&&phases.IndexOf("add-disabled-rule")<phases.IndexOf("set-remote"),"detached template used for live edits");
        }));
        await check("firewall-rejected-field-stays-disabled-and-is-eligible-for-owned-cleanup",()=>Sync(()=> {
            var spec=Spec();Rule? stored=null;
            try{GatewayFirewall.RegisterRule(new Rule(),spec,raw=>{stored=((Rule)raw).Clone();stored.RejectRemote=true;return stored;},_=>{});throw new Exception("Expected field rejection");}
            catch(ArgumentException){Assert(stored is not null&&!stored.Enabled&&GatewayFirewall.IsOwnedDisabledRule(stored,spec,true),"partial rule became active or ownership lost");}
        }));
        await check("firewall-cleanup-cannot-claim-enabled-or-foreign-rules",()=>Sync(()=> {
            var spec=Spec();var owned=new Rule{Name=spec.Name,ApplicationName=spec.Program,Protocol=17,LocalPorts="50001",Direction=1,Action=0,Grouping=GatewayFirewall.Group,Profiles=int.MaxValue,RemoteAddresses=GatewayFirewall.RemoteRange};
            Assert(GatewayFirewall.IsOwnedDisabledRule(owned,spec),"owned disabled rule not recognized");owned.Grouping="OTHER";Assert(!GatewayFirewall.IsOwnedDisabledRule(owned,spec,true),"foreign group accepted");
            owned.Grouping="";Assert(!GatewayFirewall.IsOwnedDisabledRule(owned,spec)&&GatewayFirewall.IsOwnedDisabledRule(owned,spec,true),"uncommitted group needs current-run proof");
            owned.Grouping=GatewayFirewall.Group;owned.Enabled=true;Assert(!GatewayFirewall.IsOwnedDisabledRule(owned,spec,true),"active rule accepted for cleanup");
        }));
    }
}
