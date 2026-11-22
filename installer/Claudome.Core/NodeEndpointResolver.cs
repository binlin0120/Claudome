using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace ClaudeSetup;

public static class NodeEndpointResolver
{
    private static readonly (string Name,string Prefix,string Suffix)[] Providers=[("Cloudflare","https://cloudflare-dns.com/dns-query?name=","&type=A"),("Google","https://dns.google/resolve?name=","&type=A&edns_client_subnet=0.0.0.0%2F0")];
    internal static string HttpFailure(HttpRequestException error)=>error.StatusCode is { } status?"HTTP_"+(int)status:error.HttpRequestError switch {
        HttpRequestError.NameResolutionError=>"DNS_LOOKUP_FAILED",HttpRequestError.ConnectionError=>"CONNECTION_FAILED",HttpRequestError.SecureConnectionError=>"TLS_FAILED",HttpRequestError.ProxyTunnelError=>"PROXY_TUNNEL_FAILED",HttpRequestError.ResponseEnded=>"RESPONSE_ENDED",HttpRequestError.HttpProtocolError=>"HTTP_PROTOCOL_FAILED",_=>"HTTP_REQUEST_FAILED"
    };
    public static async Task<string[]> Resolve(string server,CancellationToken token,HttpClient? suppliedClient=null,TimeSpan? providerTimeout=null)
    {
        token.ThrowIfCancellationRequested();
        if(IPAddress.TryParse(server,out var literal))return IsolationPolicy.ValidateEndpoints([literal.ToString()]);
        string queryName;
        try {
            queryName=new IdnMapping().GetAscii(server.TrimEnd('.'));
            if(queryName.Length is <1 or >253||Uri.CheckHostName(queryName)!=UriHostNameType.Dns||queryName.Split('.').Any(label=>label.Length is <1 or >63))throw new ArgumentException();
        }catch(ArgumentException){throw new SetupException("节点服务器地址不是有效的域名或 IPv4，请检查节点资料；未改选其他节点。");}
        using var owned=suppliedClient is null?new HttpClient{Timeout=Timeout.InfiniteTimeSpan}:null;
        var client=suppliedClient??owned!;var failures=new List<string>();
        foreach(var provider in Providers) {
            token.ThrowIfCancellationRequested();
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(token);limit.CancelAfter(providerTimeout??TimeSpan.FromSeconds(8));
            try {
                using var request=new HttpRequestMessage(HttpMethod.Get,provider.Prefix+Uri.EscapeDataString(queryName)+provider.Suffix);
                request.Headers.Accept.ParseAdd("application/dns-json");
                using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,limit.Token);
                if(!response.IsSuccessStatusCode){failures.Add(provider.Name+":HTTP_"+(int)response.StatusCode);continue;}
                using var input=await response.Content.ReadAsStreamAsync(limit.Token);using var body=new MemoryStream();
                var buffer=new byte[4096];var tooLarge=false;
                while(true){var read=await input.ReadAsync(buffer.AsMemory(),limit.Token);if(read==0)break;if(body.Length+read>65536){tooLarge=true;break;}body.Write(buffer,0,read);}
                if(tooLarge){failures.Add(provider.Name+":RESPONSE_TOO_LARGE");continue;}
                using var json=JsonDocument.Parse(body.ToArray());var root=json.RootElement;
                if(root.TryGetProperty("Status",out var dnsStatus)&&dnsStatus.ValueKind==JsonValueKind.Number&&dnsStatus.TryGetInt32(out var status)&&status!=0){failures.Add(provider.Name+":DNS_STATUS_"+status);continue;}
                if(!root.TryGetProperty("Answer",out var answers)||answers.ValueKind!=JsonValueKind.Array){failures.Add(provider.Name+":NO_IPV4_ANSWER");continue;}
                var addresses=new List<string>();
                foreach(var answer in answers.EnumerateArray()) {
                    if(answer.ValueKind!=JsonValueKind.Object||!answer.TryGetProperty("type",out var kind)||kind.ValueKind!=JsonValueKind.Number||!kind.TryGetInt32(out var type)||type!=1)continue;
                    if(answer.TryGetProperty("data",out var data)&&data.ValueKind==JsonValueKind.String&&IPAddress.TryParse(data.GetString(),out var ip)&&ip.AddressFamily==AddressFamily.InterNetwork)addresses.Add(ip.ToString());
                }
                if(addresses.Count==0){failures.Add(provider.Name+":NO_IPV4_ANSWER");continue;}
                // A private/reserved result is a hard failure, never a reason to silently try a less restrictive route.
                return IsolationPolicy.ValidateEndpoints(addresses.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            }catch(OperationCanceledException) when(!token.IsCancellationRequested){failures.Add(provider.Name+":TIMEOUT");}
            catch(HttpRequestException error){failures.Add(provider.Name+":"+HttpFailure(error));}
            catch(JsonException){failures.Add(provider.Name+":INVALID_JSON");}
            catch(IOException){failures.Add(provider.Name+":RESPONSE_IO_FAILED");}
        }
        throw new SetupException("节点地址查询未完成（"+string.Join("；",failures)+"）。已保留所选节点和原任务；请检查主机当前网络或代理连接后继续。");
    }
}
