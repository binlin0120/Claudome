using System.Text.Json;

namespace ClaudeSetup;

public static class AlignmentTests
{
    public static async Task Run(Func<string,Func<Task>,Task> check,string root)
    {
        Task Sync(Action action){action();return Task.CompletedTask;}
        void Assert(bool value,string message){if(!value)throw new Exception(message);}
        void Reject(Action action){try{action();}catch(SetupException){return;}throw new Exception("invalid chain accepted");}
        await check("two-hop-import-and-pinned-dialer-route",()=>Sync(()=> {
            var route=NetworkImport.Parse("proxies:\n  - {name: First, type: socks5, server: first.example, port: 1080}\n  - {name: Home, type: http, server: home.example, port: 8080, dialer-proxy: First}").Nodes.Single(n=>n.Name=="Home");
            Assert(route.Upstream?.Name=="First","upstream lost on import");
            using var json=JsonDocument.Parse(NetworkImport.CoreConfig(route,["1.1.1.1"],null,null,["8.8.8.8"]));
            var config=json.RootElement;var proxies=config.GetProperty("proxies");
            Assert(proxies.GetArrayLength()==2&&proxies[0].GetProperty("dialer-proxy").GetString()=="UpstreamNode","wrong chain");
            Assert(config.GetProperty("hosts").GetProperty("home.example")[0].GetString()=="8.8.8.8","final endpoint not pinned");
            Assert(config.GetProperty("hosts").GetProperty("first.example")[0].GetString()=="1.1.1.1","first endpoint not pinned");
            var binding=new GatewayBinding("11111111-1111-1111-1111-111111111111",49152,49153,"02:11:22:33:44:55");
            var policy=ExternalGateway.Policy(binding,["1.1.1.1"],route.Upstream!.Port,route.Upstream.Server,Guid.NewGuid().ToString("N"));
            Assert(policy.endpointAddresses.SequenceEqual(new[]{"1.1.1.1"})&&policy.endpointPort==1080&&policy.nodeHost=="first.example","outer route leaked final endpoint");
            Assert(!config.GetProperty("rules").ToString().Contains("DIRECT"),"direct fallback");
        }));
        foreach(var (name,yaml) in new[]{
            ("missing","proxies:\n - {name: Home,type: http,server: home.example,port: 80,dialer-proxy: Missing}"),
            ("cycle","proxies:\n - {name: Home,type: http,server: home.example,port: 80,dialer-proxy: Home}"),
            ("multi-hop-only","proxies:\n - {name: B,type: http,server: b.example,port: 80,dialer-proxy: C}\n - {name: C,type: http,server: c.example,port: 80,dialer-proxy: B}")
        })await check("reject-chain-"+name,()=>Sync(()=>Reject(()=>NetworkImport.Parse(yaml))));
        await check("chain-cannot-start-with-unresolved-final-endpoint",()=>Sync(()=> {
            var final=NetworkImport.Parse("http://home.example:80#Home").Nodes.Single();var first=NetworkImport.Parse("socks5://first.example:1080#First").Nodes.Single();
            Reject(()=>NetworkImport.CoreConfig(final,["1.1.1.1"],null,first));
        }));
        await check("restart-evidence-requires-new-boot-and-live-checks",()=>Sync(()=> {
            var before="2026-11-17T01:00:00Z";
            foreach(var (boot,live,expected) in new[]{(before,true,false),("2026-11-16T01:00:00Z",true,false),("2026-11-17T02:00:00Z",false,false),("2026-11-17T02:00:00Z",true,true)}) {
                using var doc=JsonDocument.Parse(JsonSerializer.Serialize(new{bootUtc=boot,clashRuntimePassed=true,networkPassed=true,browserRuntimePassed=live,translationPassed=true}));
                Assert(Deployment.RestartEvidencePasses(doc.RootElement,before)==expected,"stale/failed restart evidence accepted");
            }
        }));
    }
}
