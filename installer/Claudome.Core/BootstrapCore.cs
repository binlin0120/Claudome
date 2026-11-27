using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeSetup;

public sealed record BootstrapProxy(int port,string username,string password);
public sealed class BootstrapCore : IAsyncDisposable
{
    private readonly OwnedChildProcess child;
    private string failureCode="";
    private int diagnosticPort;
    public void BeginProbeStep(int port){Volatile.Write(ref diagnosticPort,port);Volatile.Write(ref failureCode,"");}
    public string FailureCode=>Volatile.Read(ref failureCode);
    public BootstrapProxy Binding { get; }
    public bool IsAlive=>!child.Process.HasExited;
    private BootstrapCore(OwnedChildProcess owned,BootstrapProxy binding){child=owned;Binding=binding;}
    internal static string Configuration(string selectedConfig,BootstrapProxy binding)
    {
        var config=JsonNode.Parse(selectedConfig)?.AsObject()??throw new SetupException("所选节点配置无法用于安装网络。");
        config.Remove("mixed-port");config.Remove("interface-name");
        config["mode"]="rule";config["socks-port"]=binding.port;config["port"]=0;config["allow-lan"]=false;config["bind-address"]="127.0.0.1";
        config["authentication"]=new JsonArray(binding.username+":"+binding.password);
        // Do not rely on an empty/default authentication exemption list.
        config["skip-auth-prefixes"]=new JsonArray("0.0.0.0/32");
        config["tun"]=new JsonObject{["enable"]=false};
        config["dns"]=new JsonObject{["enable"]=false,["use-hosts"]=true};
        config["external-controller"]="";config["external-controller-tls"]="";config["external-controller-pipe"]="";config["external-ui"]="";
        config["profile"]=new JsonObject{["store-selected"]=false,["store-fake-ip"]=false};
        config["geo-auto-update"]=false;config["log-level"]="warning";config["ipv6"]=false;
        // Only the selected node chain and its already validated host pins are retained.
        var proxies=config["proxies"]?.AsArray()??throw new SetupException("安装网络缺少所选节点。");
        if(proxies.Count is <1 or >2)throw new SetupException("安装网络节点链不匹配。");
        var hosts=config["hosts"]?.AsObject()??throw new SetupException("安装网络缺少节点地址绑定。");
        foreach(var host in hosts){var ips=host.Value?.Deserialize<string[]>()??[];IsolationPolicy.ValidateEndpoints(ips);}
        config["proxy-groups"]=new JsonArray(new JsonObject{["name"]="Claude",["type"]="select",["proxies"]=new JsonArray("SelectedNode")});
        config["rules"]=new JsonArray("MATCH,Claude");
        return config.ToJsonString(JsonFile.Format);
    }
    public static async Task<BootstrapCore> Start(SetupOptions options,string selectedConfig,CancellationToken token,IProgress<DownloadProgress>? progress=null,string? archiveCacheDirectory=null)
    {
        var resource=Catalog.Resources.Single(x=>x.Id=="bootstrap-core");
        var cacheDir=string.IsNullOrWhiteSpace(archiveCacheDirectory)?options.Cache:archiveCacheDirectory;
        Directory.CreateDirectory(cacheDir);
        using var downloader=new Downloader();var archive=await downloader.Get(resource,cacheDir,progress,token);
        var folder=Paths.Under(options.Private,"bootstrap-core-1.19.34");HostPlatform.Environment.RestrictPrivateDirectory(options.Private);HostRuntime.ExtractVerified(archive,folder);
        var files=Directory.GetFiles(folder,"*.exe",SearchOption.TopDirectoryOnly);
        if(files.Length!=1)throw new SetupException("安装网络组件内容不匹配。");
        using var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();var port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
        var binding=new BootstrapProxy(port,Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        var config=Configuration(selectedConfig,binding);
        var home=Paths.Under(options.Private,"bootstrap-runtime");HostPlatform.Environment.RestrictPrivateDirectory(home);
        var start=new ProcessStartInfo(files[0]){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false)};
        start.ArgumentList.Add("-d");start.ArgumentList.Add(home);start.ArgumentList.Add("-f");start.ArgumentList.Add("-");
        foreach(var key in start.Environment.Keys.Where(k=>k.StartsWith("CLASH_",StringComparison.OrdinalIgnoreCase)||k.StartsWith("MIHOMO_",StringComparison.OrdinalIgnoreCase)).ToArray())start.Environment.Remove(key);
        var owned=OwnedChildProcess.Start(start);var runtime=new BootstrapCore(owned,binding);
        try {
            _=runtime.Drain(owned.Process.StandardOutput);_=runtime.Drain(owned.Process.StandardError);
            // Config contains credentials: stdin only, never command line, environment, or a config file.
            await owned.Process.StandardInput.WriteAsync(config.AsMemory(),token);owned.Process.StandardInput.Close();
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(15));
            for(var i=0;i<40;i++) {
                if(owned.Process.HasExited)throw new SetupException("安装网络组件提前退出，保留原任务。");
                try{await runtime.VerifyListener(deadline.Token);return runtime;}
                catch(SocketException){await Task.Delay(250,deadline.Token);}
            }
            throw new SetupException("安装网络组件尚未就绪。");
        }catch{await runtime.DisposeAsync();throw;}
    }
    internal static string ClassifyFailure(string record)
    {
        // No substring or captured value is ever returned; raw output is discarded.
        var text=record.ToLowerInvariant();
        if(!text.Contains("[tcp] dial ")||!text.Contains(" error:"))return "";
        if(text.Contains("authentication failed")||text.Contains("proxy authentication required")||text.Contains("unauthorized"))return "PROXY_AUTH_FAILED";
        if(text.Contains("x509:")||text.Contains("certificate verify")||text.Contains("certificate is not valid"))return "PROXY_TLS_CERTIFICATE";
        if(text.Contains("tls:")||text.Contains("tls handshake")||text.Contains("unrecognized name"))return "PROXY_TLS_HANDSHAKE";
        if(text.Contains("no such host")||text.Contains("dns resolve failed"))return "PROXY_NODE_DNS";
        if(text.Contains("connection refused")||text.Contains("10061"))return "PROXY_CONNECTION_REFUSED";
        if(text.Contains("network is unreachable")||text.Contains("10051")||text.Contains("10065"))return "PROXY_NETWORK_UNREACHABLE";
        if(text.Contains("deadline exceeded")||text.Contains("i/o timeout")||text.Contains("timed out"))return "PROXY_TIMEOUT";
        if(text.Contains("bad handshake")||text.Contains("websocket handshake"))return "PROXY_WEBSOCKET_HANDSHAKE";
        if(text.Contains(" eof")||text.Contains("connection reset")||text.Contains("forcibly closed"))return "PROXY_PEER_CLOSED";
        return "";
    }
    public string FailureHint=>FailureCode switch {
        "PROXY_AUTH_FAILED"=>"\n代理内核诊断：上游认证未通过（PROXY_AUTH_FAILED）。",
        "PROXY_TLS_CERTIFICATE"=>"\n代理内核诊断：节点证书校验未通过（PROXY_TLS_CERTIFICATE）。",
        "PROXY_TLS_HANDSHAKE"=>"\n代理内核诊断：节点 TLS 握手未通过（PROXY_TLS_HANDSHAKE）。",
        "PROXY_NODE_DNS"=>"\n代理内核诊断：节点地址解析未完成（PROXY_NODE_DNS）。",
        "PROXY_CONNECTION_REFUSED"=>"\n代理内核诊断：连接被拒绝（PROXY_CONNECTION_REFUSED）。",
        "PROXY_NETWORK_UNREACHABLE"=>"\n代理内核诊断：节点网络不可达（PROXY_NETWORK_UNREACHABLE）。",
        "PROXY_TIMEOUT"=>"\n代理内核诊断：上游连接超时（PROXY_TIMEOUT）。",
        "PROXY_WEBSOCKET_HANDSHAKE"=>"\n代理内核诊断：WebSocket 握手未通过（PROXY_WEBSOCKET_HANDSHAKE）。",
        "PROXY_PEER_CLOSED"=>"\n代理内核诊断：对端关闭了连接（PROXY_PEER_CLOSED）。",_=>""
    };
    private async Task Drain(StreamReader reader)
    {
        try {
            var buffer=new char[2048];var line=new StringBuilder();var overflow=false;int count;
            while((count=await reader.ReadAsync(buffer))>0)for(var i=0;i<count;i++) {
                var value=buffer[i];
                if(value=='\n'){if(!overflow){var record=line.ToString();var port=Volatile.Read(ref diagnosticPort);if(port!=0&&System.Text.RegularExpressions.Regex.IsMatch(record,@"--> [^\s]+:"+port+@" error:")){var code=ClassifyFailure(record);if(code.Length>0)Volatile.Write(ref failureCode,code);}}line.Clear();overflow=false;}
                else if(!overflow){if(line.Length<4096)line.Append(value);else{line.Clear();overflow=true;}}
            }
        }catch{}
    }
    private async Task VerifyListener(CancellationToken token)
    {
        using(var anonymous=new TcpClient()) {
            await anonymous.ConnectAsync(IPAddress.Loopback,Binding.port,token);using var check=anonymous.GetStream();
            await check.WriteAsync(new byte[]{5,1,0},token);var rejected=new byte[2];await check.ReadExactlyAsync(rejected,token);
            if(rejected.SequenceEqual(new byte[]{5,2})) {
                // Mihomo demands password auth even when the client offered only no-auth.
                await check.WriteAsync(new byte[]{1,1,95,1,95},token);await check.ReadExactlyAsync(rejected,token);
                if(!rejected.SequenceEqual(new byte[]{1,1}))throw new SetupException("安装网络组件未拒绝错误认证，已停止。");
            }else if(!rejected.SequenceEqual(new byte[]{5,255}))throw new SetupException("安装网络组件允许匿名连接，已停止。");
        }
        using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,Binding.port,token);using var stream=client.GetStream();
        await stream.WriteAsync(new byte[]{5,1,2},token);var reply=new byte[2];await stream.ReadExactlyAsync(reply,token);
        if(!reply.SequenceEqual(new byte[]{5,2}))throw new SetupException("安装网络组件未要求独立认证，已停止。");
        var user=Encoding.ASCII.GetBytes(Binding.username);var password=Encoding.ASCII.GetBytes(Binding.password);
        await stream.WriteAsync(new byte[]{1,(byte)user.Length}.Concat(user).Concat(new[]{(byte)password.Length}).Concat(password).ToArray(),token);
        await stream.ReadExactlyAsync(reply,token);if(!reply.SequenceEqual(new byte[]{1,0}))throw new SetupException("安装网络组件认证未通过。");
    }
    public void RequireAlive(){if(!IsAlive)throw new SetupException("安装网络组件已停止，保护通道保持关闭。");}
    public ValueTask DisposeAsync(){child.Dispose();return ValueTask.CompletedTask;}
}
