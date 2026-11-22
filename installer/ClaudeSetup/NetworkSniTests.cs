using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSetup;

public static class NetworkSniTests
{
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task Run(Func<string,Func<Task>,Task> check,string root)
    {
        Task Sync(Action action){action();return Task.CompletedTask;}
        await check("trojan-uri-keeps-sni-in-the-field-read-by-the-native-adapter",()=>Sync(()=>{
            foreach(var parameter in new[]{"sni","peer"}) {
                var node=NetworkImport.Parse("trojan://fictional-trojan-password@node.example:443?"+parameter+"=trojan-fixture.invalid&allowInsecure=0#Fixture").Nodes.Single();
                using var doc=JsonDocument.Parse(node.Json);var value=doc.RootElement;
                Assert(value.GetProperty("sni").GetString()=="trojan-fixture.invalid"&&!value.TryGetProperty("servername",out _),"Trojan TLS name would be ignored by the native adapter");
                Assert(!value.GetProperty("skip-cert-verify").GetBoolean(),"certificate checking was disabled");
                File.WriteAllText(Path.Combine(root,"trojan-sni-native-fixture.json"),node.Json);
            }
        }));
        await check("legacy-saved-trojan-json-is-normalized-for-both-chain-hops",()=>Sync(()=>{
            ProxyNode Legacy(string name,string server,string sni)=>new(name,"trojan",server,443,JsonSerializer.Serialize(new Dictionary<string,object?>{{"name",name},{"type","trojan"},{"server",server},{"port",443},{"password","fictional-password"},{"servername",sni},{"skip-cert-verify",false}}));
            var final=Legacy("Final","final.example","final-sni.example");var first=Legacy("First","first.example","first-sni.example");
            using var doc=JsonDocument.Parse(NetworkImport.CoreConfig(final,["1.1.1.1"],null,first,["8.8.8.8"]));
            var proxies=doc.RootElement.GetProperty("proxies");
            Assert(proxies[0].GetProperty("sni").GetString()=="final-sni.example"&&proxies[1].GetProperty("sni").GetString()=="first-sni.example","legacy chain kept an ignored TLS name");
            Assert(!proxies[0].TryGetProperty("servername",out _)&&!proxies[1].GetProperty("skip-cert-verify").GetBoolean()&&proxies[0].GetProperty("password").GetString()=="fictional-password","normalization changed credentials or certificate policy");
        }));
        await check("trojan-conflicting-sni-fields-are-not-silently-overwritten",()=>Sync(()=>{
            var input=new Dictionary<string,object?>{{"name","Fixture"},{"type","trojan"},{"server","node.example"},{"port",443},{"sni","one.example"},{"servername","two.example"}};
            try{NetworkImport.Validate(input);throw new Exception("Conflicting SNI accepted");}catch(SetupException){}
            input["sni"]="TWO.EXAMPLE";var node=NetworkImport.Validate(input);using var json=JsonDocument.Parse(node.Json);
            Assert(json.RootElement.GetProperty("sni").GetString()=="TWO.EXAMPLE","valid explicit SNI replaced");
        }));
        await check("vless-and-vmess-keep-their-own-servername-field",()=>Sync(()=>{
            var vless=NetworkImport.Parse("vless://11111111-1111-4111-8111-111111111111@node.example:443?security=tls&sni=vless.example#Fixture").Nodes.Single();
            using var doc=JsonDocument.Parse(vless.Json);Assert(doc.RootElement.GetProperty("servername").GetString()=="vless.example"&&!doc.RootElement.TryGetProperty("sni",out _),"Trojan mapping was incorrectly applied to VLESS");
            var vmess=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{ps="Fixture",add="node.example",port=443,id="11111111-1111-4111-8111-111111111111",tls="tls",sni="vmess.example"})));
            using var v=JsonDocument.Parse(NetworkImport.Parse("vmess://"+vmess).Nodes.Single().Json);Assert(v.RootElement.GetProperty("servername").GetString()=="vmess.example","VMess TLS name changed");
        }));
        await check("native-proxy-error-classifier-emits-only-fixed-codes",()=>Sync(()=>{
            foreach(var item in new[]{("x509: certificate invalid for secret.example","PROXY_TLS_CERTIFICATE"),("remote error: tls: unrecognized name","PROXY_TLS_HANDSHAKE"),("dial tcp fictional: connection refused","PROXY_CONNECTION_REFUSED"),("i/o timeout token=fictional","PROXY_TIMEOUT"),("auth: authentication failed password=fictional","PROXY_AUTH_FAILED")})
                Assert(BootstrapCore.ClassifyFailure("[TCP] dial Claude error: "+item.Item1)==item.Item2,"native error category missing");
            Assert(BootstrapCore.ClassifyFailure("https://secret.example/?token=fictional")==""&&BootstrapCore.ClassifyFailure("[SOCKS] authentication failed")=="","raw or listener-health-check log was misclassified as upstream failure");
        }));
    }
}
