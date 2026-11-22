using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSetup;

public static class BootstrapTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task Run(Func<string,Func<Task>,Task> check,string root)
    {
        Task Sync(Action action){action();return Task.CompletedTask;}
        BootstrapProxy Binding()=>new(40001,new string('a',32),new string('b',64));
        var node=NetworkImport.Parse("trojan://fixture-secret@node.example:443?allowInsecure=1#Fixture").Nodes.Single();
        var selected=NetworkImport.CoreConfig(node,["1.1.1.1"]);
        await check("bootstrap-core-config-cannot-enable-host-tun-lan-or-controller",()=>Sync(()=>{
            using var config=JsonDocument.Parse(BootstrapCore.Configuration(selected,Binding()));var value=config.RootElement;
            Assert(!value.GetProperty("tun").GetProperty("enable").GetBoolean()&&!value.GetProperty("allow-lan").GetBoolean()&&value.GetProperty("bind-address").GetString()=="127.0.0.1","host networking scope widened");
            Assert(value.GetProperty("external-controller").GetString()==""&&!value.GetProperty("dns").GetProperty("enable").GetBoolean(),"host DNS/controller listener enabled");
            Assert(value.GetProperty("rules")[0].GetString()=="MATCH,Claude"&&!value.TryGetProperty("mixed-port",out _),"selected proxy route was replaced");
        }));
        await check("bootstrap-core-preserves-node-host-pins-and-certificate-choice",()=>Sync(()=>{
            using var original=JsonDocument.Parse(selected);using var config=JsonDocument.Parse(BootstrapCore.Configuration(selected,Binding()));
            Assert(config.RootElement.GetProperty("hosts").GetProperty("node.example")[0].GetString()=="1.1.1.1","selected node IP pin changed");
            Assert(JsonElement.DeepEquals(config.RootElement.GetProperty("proxies"),original.RootElement.GetProperty("proxies")),"node credentials or certificate option changed");
            Assert(config.RootElement.GetProperty("authentication").GetArrayLength()==1,"isolated proxy auth missing");
        }));
        await check("bootstrap-core-rejects-unpinned-private-server-addresses",()=>Sync(()=>{
            var invalid=JsonNode.Parse(selected)!;invalid["hosts"]!["node.example"]=new JsonArray("127.0.0.1");
            try{BootstrapCore.Configuration(invalid.ToJsonString(),Binding());throw new Exception("Expected private node rejection");}catch(SetupException){}
        }));
        await check("bootstrap-errors-name-the-stage-without-echoing-private-details",()=>Sync(()=>{
            foreach(var code in new[]{"BOOTSTRAP_HTTP_REDIRECT","BOOTSTRAP_HTTP_TIMEOUT","BOOTSTRAP_DNS_REQUIRED","BOOTSTRAP_TIMEOUT","BOOTSTRAP_DNS_TLS_FAILED","BOOTSTRAP_DNS_TRANSPORT_FAILED","BOOTSTRAP_DOH_HTTP_FAILED","BOOTSTRAP_DOH_RESPONSE_INVALID","BOOTSTRAP_CONNECT_FAILED"}) {
                var message=ExternalGateway.BootstrapFailureMessage(code);Assert(message.Contains(code)&&message.Contains("原任务"),"failure stage hidden");
            }
            var unknown=ExternalGateway.BootstrapFailureMessage("https://private.example/?token=fictional");
            Assert(unknown.Contains("BOOTSTRAP_UNAVAILABLE")&&!unknown.Contains("private.example")&&!unknown.Contains("fictional"),"untrusted failure code echoed");
        }));
        await check("bootstrap-control-fingerprint-and-completion-roundtrip",async()=>{
            using var first=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));using var second=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
            var binding=new GatewayBinding("11111111-1111-1111-1111-111111111111",((IPEndPoint)first.Client.LocalEndPoint!).Port,((IPEndPoint)second.Client.LocalEndPoint!).Port,"02:11:22:33:44:55");second.Dispose();
            var policy=ExternalGateway.Policy(binding,["1.1.1.1"],443,"node.example",Guid.NewGuid().ToString("N")) with{bootstrap=Binding() with {port=Enumerable.Range(40001,3).First(x=>x!=binding.GuestPort&&x!=binding.ListenPort)}};
            var json=JsonSerializer.Serialize(policy);var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            var exe=Path.Combine(root,"bootstrap-control-fixture.exe");File.WriteAllBytes(exe,Catalog.EmbeddedBytes("ClaudeNetworkGateway.exe"));
            using var worker=new Process{StartInfo=new(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false)}};
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            worker.Start();_=worker.StandardError.ReadToEndAsync();
            try {
                await worker.StandardInput.WriteLineAsync(json.AsMemory(),deadline.Token);await worker.StandardInput.FlushAsync(deadline.Token);
                using(var ready=JsonDocument.Parse((await worker.StandardOutput.ReadLineAsync(deadline.Token))!))Assert(ready.RootElement.GetProperty("fingerprint").GetString()==hash,"C#/Go bootstrap policy fingerprint mismatch");
                await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{operation="renew",fingerprint=hash,seconds=15}).AsMemory(),deadline.Token);await worker.StandardInput.FlushAsync(deadline.Token);
                using(var renewed=JsonDocument.Parse((await worker.StandardOutput.ReadLineAsync(deadline.Token))!))Assert(renewed.RootElement.GetProperty("ok").GetBoolean(),"probe fixture renewal failed");
                await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{operation="probe-bootstrap-http",fingerprint=hash}).AsMemory(),deadline.Token);await worker.StandardInput.FlushAsync(deadline.Token);
                using(var missing=JsonDocument.Parse((await worker.StandardOutput.ReadLineAsync(deadline.Token))!))Assert(!missing.RootElement.GetProperty("ok").GetBoolean()&&missing.RootElement.GetProperty("code").GetString()=="BOOTSTRAP_DNS_REQUIRED","HTTP probe accepted without a current DNS result");
                await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{operation="finish-bootstrap",fingerprint=hash}).AsMemory(),deadline.Token);await worker.StandardInput.FlushAsync(deadline.Token);
                using(var reply=JsonDocument.Parse((await worker.StandardOutput.ReadLineAsync(deadline.Token))!))Assert(reply.RootElement.GetProperty("ok").GetBoolean()&&reply.RootElement.GetProperty("operation").GetString()=="finish-bootstrap","bootstrap completion was not confirmed");
                worker.StandardInput.Close();await worker.WaitForExitAsync(deadline.Token);Assert(worker.ExitCode==0,"fixture gateway did not exit on owner EOF");
            }finally{if(!worker.HasExited)worker.Kill(true);}
        });
    }
}
