using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ClaudeSetup;

public sealed record ProxyNode(string Name, string Type, string Server, int Port, string Json)
{
    public ProxyNode? Upstream { get; init; }
    public override string ToString() => $"{Name}  ·  {Type.ToUpperInvariant()}  ·  {Server}:{Port}" + (Upstream is null ? "" : $"  ·  前置 {Upstream.Name}");
}
public sealed record NodeImportIssue(int Index,string Reason);
public sealed record NodeList(List<ProxyNode> Nodes, int Unsupported) { public IReadOnlyList<NodeImportIssue> Issues { get; init; } = []; }

public static class NetworkImport
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "http", "socks5", "ss", "vmess", "vless", "trojan" };
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "name","type","server","port","username","password","cipher","uuid","alterId","tls","udp","network","servername","sni","skip-cert-verify","client-fingerprint","alpn","flow","reality-opts","ws-opts","grpc-opts","h2-opts","http-opts","ech-opts","packet-encoding","ip-version","tfo","mptcp","smux","fingerprint","udp-over-tcp","udp-over-tcp-version","GlobalPadding","authenticated-length"
    };
    private static readonly string[] IgnoredSubscriptionMetadataKeys = ["benchmark-url", "benchmark-timeout", "test-url", "test-timeout"];
    public static async Task<string> Fetch(string address, CancellationToken token)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) throw new SetupException("订阅链接请使用 HTTPS，认证内容保留在链接原有参数中。");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new SetupException("订阅重定向到了不安全地址。");
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new SetupException("订阅文件过大。");
        using var stream = await response.Content.ReadAsStreamAsync(token); using var output = new MemoryStream(); var buffer = new byte[16384];
        while (true) { var n = await stream.ReadAsync(buffer, token); if (n == 0) break; if (output.Length + n > MaximumBytes) throw new SetupException("订阅文件过大。"); output.Write(buffer, 0, n); }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    public static NodeList Parse(string source)
    {
        if (Encoding.UTF8.GetByteCount(source) > MaximumBytes) throw new SetupException("配置文件超过 8 MB。");
        var text = source.Trim(); if (text.Length == 0) throw new SetupException("请先填写订阅、节点链接或导入配置文件。");
        var nodes = new List<ProxyNode>(); int unsupported = 0;
        var issues = new List<NodeImportIssue>();
        var routeDefinitions = new HashSet<(string Json, string Link)>();
        bool Add(ProxyNode node, int index, string link = "")
        {
            if (!routeDefinitions.Add((node.Json, link)))
            {
                unsupported++;
                issues.Add(new(index, "完全相同的节点配置已保留一份。"));
                return false;
            }
            nodes.Add(node);
            return true;
        }
        void Failed(int index,SetupException error) { unsupported++;issues.Add(new(index,error.Message)); }
        if (!text.Contains("proxies:") && !text.StartsWith('{') && !text.Contains("://"))
        {
            try { text = Decode(text); } catch { throw new SetupException("无法识别该格式。首版支持 Clash proxies 配置及常见节点链接列表。"); }
        }
        if (text.StartsWith('{') || text.Contains("proxies:"))
        {
            try
            {
                var yaml = new YamlStream(); yaml.Load(new StringReader(text));
                if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root) throw new SetupException("配置必须包含一份节点清单。");
                if (!root.Children.TryGetValue(new YamlScalarNode("proxies"), out var proxies) || proxies is not YamlSequenceNode sequence) throw new SetupException("配置中没有 proxies 节点列表。远程 provider 请先转为本地节点清单。");
                if (sequence.Children.Count > 1000) throw new SetupException("一次最多导入 1000 个节点。");
                int budget = 30000;
                var links = new Dictionary<ProxyNode, string>(ReferenceEqualityComparer.Instance);
                foreach (var entry in sequence.Children)
                {
                    var index=nodes.Count+unsupported+1;
                    try {
                        var map = ConvertNode(entry, 0, ref budget) as Dictionary<string, object?> ?? throw new SetupException("节点格式不正确。");
                        if (!Types.Contains(Value(map, "type"))) throw new SetupException("此节点协议暂未适配");
                        map.Remove("dialer-proxy",out var dialer);
                        var candidate = Validate(map);
                        var upstreamName = System.Convert.ToString(dialer) ?? "";
                        if (Add(candidate, index, upstreamName) && upstreamName.Length > 0) links[candidate] = upstreamName;
                    }catch(SetupException error){Failed(index,error);}
                }
                var byName = nodes.ToLookup(n => n.Name, StringComparer.Ordinal);
                foreach (var (candidate, upstreamName) in links)
                {
                    var upstream = byName[upstreamName].ToArray();
                    var index = nodes.FindIndex(n => ReferenceEquals(n, candidate));
                    if (index < 0) continue;
                    if (upstream.Length != 1 || ReferenceEquals(candidate, upstream[0]) || links.ContainsKey(upstream[0]))
                    {
                        Failed(index + 1, new SetupException("链式前置缺失、重名或包含额外跳数，请分别导入并明确选择。"));
                        nodes.RemoveAt(index);
                        continue;
                    }
                    nodes[index] = candidate with { Upstream = upstream[0] };
                }
            }
            catch (SetupException) { throw; }
            catch { throw new SetupException("配置内容无法安全解析，请检查 YAML 格式及重复字段。"); }
        }
        else
        {
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (nodes.Count + unsupported >= 1000) throw new SetupException("一次最多导入 1000 个节点。");
                var item = line.Trim();
                if (!new[] { "ss://", "vmess://", "vless://", "trojan://", "socks5://", "http://", "https://" }.Any(p => item.StartsWith(p, StringComparison.OrdinalIgnoreCase))) { unsupported++;issues.Add(new(nodes.Count+unsupported,"此节点协议或链接格式暂未适配"));continue; }
                try{Add(ParseUri(item),nodes.Count+unsupported+1);}catch(SetupException error){Failed(nodes.Count+unsupported+1,error);}
            }
        }
        if (nodes.Count == 0) throw new SetupException("没有可使用的节点。支持 HTTP、SOCKS5、SS、VMess、VLESS 和 Trojan；外部插件需另行适配。");
        return new(nodes, unsupported){Issues=issues};
    }
    private static object? ConvertNode(YamlNode node, int depth, ref int budget)
    {
        if (depth > 15 || --budget < 0) throw new SetupException("配置结构过于复杂。");
        if (node is YamlScalarNode scalar)
        {
            var value = scalar.Value ?? "";
            if (scalar.Style == ScalarStyle.Plain)
            {
                if (bool.TryParse(value, out var b)) return b;
                if (long.TryParse(value, out var number)) return number;
            }
            return value;
        }
        if (node is YamlSequenceNode sequence) { var list = new List<object?>(); foreach (var item in sequence.Children) list.Add(ConvertNode(item, depth + 1, ref budget)); return list; }
        if (node is YamlMappingNode mapping)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in mapping.Children)
            {
                if (pair.Key is not YamlScalarNode key || key.Value is null || !map.TryAdd(key.Value, ConvertNode(pair.Value, depth + 1, ref budget))) throw new SetupException("配置包含重复或不支持的字段。");
            }
            return map;
        }
        throw new SetupException("配置包含不支持的引用或标签。");
    }
    private static string Value(Dictionary<string, object?> node, string key) => node.TryGetValue(key, out var value) ? System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "" : "";
    internal static bool SameDefinition(ProxyNode? left, ProxyNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Name != right.Name || left.Type != right.Type || left.Port != right.Port ||
            !left.Server.Equals(right.Server, StringComparison.OrdinalIgnoreCase)) return false;
        if (left.Json == right.Json) return true;
        using var first = JsonDocument.Parse(left.Json);
        using var second = JsonDocument.Parse(right.Json);
        var a = first.RootElement.EnumerateObject().Where(p => p.Name is not ("server" or "port")).ToArray();
        var b = second.RootElement.EnumerateObject().Where(p => p.Name is not ("server" or "port")).ToArray();
        return a.Length == b.Length && a.All(p => second.RootElement.TryGetProperty(p.Name, out var value) &&
            JsonElement.DeepEquals(p.Value, value));
    }

    internal static bool SameSavedDefinition(string leftJson, string rightJson)
    {
        if (leftJson == rightJson) return true;
        if (leftJson.Length == 0 || rightJson.Length == 0) return false;
        return SameDefinition(Parse("{\"proxies\":[" + leftJson + "]}").Nodes.Single(),
            Parse("{\"proxies\":[" + rightJson + "]}").Nodes.Single());
    }

    public static ProxyNode Validate(Dictionary<string, object?> node)
    {
        var name = Value(node, "name"); var type = Value(node, "type"); var server = Value(node, "server");
        if (!Types.Contains(type) || string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.Any(char.IsControl) || string.IsNullOrWhiteSpace(server) || server.Length > 253 || server.Any(char.IsWhiteSpace) || server.Contains('/') || server.Contains('\\')) throw new SetupException("节点名称、类型或服务器地址不正确。");
        if (!int.TryParse(Value(node, "port"), out var port) || port is < 1 or > 65535) throw new SetupException("节点端口应为 1–65535。");
        foreach (var metaKey in IgnoredSubscriptionMetadataKeys) node.Remove(metaKey);
        if (node.Keys.Any(k => !Keys.Contains(k))) throw new SetupException("所选节点包含未支持的字段或外部插件。前置节点请在链式配置中明确选择。");
        // Mihomo's Trojan adapter reads sni, while VMess/VLESS read servername.
        // Normalize both newly imported links and older saved node JSON without
        // changing the actual hostname or certificate-verification choice.
        if(type=="trojan") {
            var canonical=Value(node,"sni");var legacy=Value(node,"servername");
            if(canonical.Length>0&&legacy.Length>0&&!canonical.Equals(legacy,StringComparison.OrdinalIgnoreCase))throw new SetupException("Trojan 节点的 TLS 服务器名字段互相冲突，请核对原配置。");
            if(canonical.Length==0&&legacy.Length>0)node["sni"]=legacy;
            node.Remove("servername");
        }
        if(node.ContainsKey("skip-cert-verify")) {
            var flag=Value(node,"skip-cert-verify");
            if(flag is "1" || flag.Equals("true",StringComparison.OrdinalIgnoreCase))node["skip-cert-verify"]=true;
            else if(flag is "0" || flag.Equals("false",StringComparison.OrdinalIgnoreCase))node["skip-cert-verify"]=false;
            else throw new SetupException("节点证书选项应为布尔值");
        }
        return new(name, type, server, port, JsonSerializer.Serialize(node));
    }
    private static ProxyNode ParseUri(string text)
    {
        try
        {
            var map = new Dictionary<string, object?>();
            if (text.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(Decode(text[8..])); var json = doc.RootElement;
                string Get(string key) => json.TryGetProperty(key, out var value) ? value.ToString() : "";
                map["name"] = Get("ps"); map["type"] = "vmess"; map["server"] = Get("add"); map["port"] = Get("port"); map["uuid"] = Get("id"); map["alterId"] = int.TryParse(Get("aid"), out var aid) ? aid : 0; map["cipher"] = string.IsNullOrEmpty(Get("scy")) ? "auto" : Get("scy");
                map["tls"] = Get("tls") == "tls";
                if(Get("allowInsecure").Length>0)map["skip-cert-verify"]=Get("allowInsecure");
                if (!string.IsNullOrEmpty(Get("sni"))) map["servername"] = Get("sni");
                if (Get("net") == "ws") { map["network"] = "ws"; map["ws-opts"] = new Dictionary<string, object?> { ["path"] = Get("path"), ["headers"] = new Dictionary<string, string> { ["Host"] = Get("host") } }; }
                else if (Get("net") is not ("" or "tcp")) throw new SetupException("此 VMess 传输方式暂不支持，请导入兼容的 Clash 节点配置。");
            }
            else
            {
                var uri = new Uri(text); var type = uri.Scheme.ToLowerInvariant(); var auth = Uri.UnescapeDataString(uri.UserInfo); var name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
                map["name"] = string.IsNullOrEmpty(name) ? "我的节点" : name; map["server"] = uri.Host; map["port"] = uri.Port;
                if (type == "ss") { var decoded = auth.Contains(':') ? auth : Decode(auth); var colon = decoded.IndexOf(':'); if (colon <= 0) throw new FormatException(); map["type"] = "ss"; map["cipher"] = decoded[..colon]; map["password"] = decoded[(colon + 1)..]; }
                else if (type is "http" or "https" or "socks5")
                {
                    map["type"] = type == "https" ? "http" : type;
                    if (auth.Length > 0) { var pair = auth.Split(':', 2); map["username"] = pair[0]; map["password"] = pair.Length > 1 ? pair[1] : ""; }
                    if (type == "https") map["tls"] = true;
                    foreach(var pair in uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Split('=',2))) {
                        if(pair.Length==2 && new[]{"allowInsecure","skip-cert-verify","insecure"}.Contains(Uri.UnescapeDataString(pair[0])))map["skip-cert-verify"]=Uri.UnescapeDataString(pair[1]);
                    }
                }
                else
                {
                    map["type"] = type;
                    if (type == "vless") map["uuid"] = auth; else map["password"] = auth;
                    var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => x.Length > 1 ? Uri.UnescapeDataString(x[1]) : "");
                    string Get(string key) => query.GetValueOrDefault(key, "");
                    map["tls"] = Get("security") is "tls" or "reality" || type == "trojan";
                    var sni=Get("sni");if(sni.Length==0)sni=Get("peer");
                    if(sni.Length>0) map[type=="trojan"?"sni":"servername"]=sni;
                    if (!string.IsNullOrEmpty(Get("fp"))) map["client-fingerprint"] = Get("fp");
                    if (!string.IsNullOrEmpty(Get("flow"))) map["flow"] = Get("flow");
                    if (Get("security") == "reality") map["reality-opts"] = new Dictionary<string, string> { ["public-key"] = Get("pbk"), ["short-id"] = Get("sid") };
                    if (Get("type") == "ws") { map["network"] = "ws"; map["ws-opts"] = new Dictionary<string, object?> { ["path"] = Get("path"), ["headers"] = new Dictionary<string, string> { ["Host"] = Get("host") } }; }
                    else if (Get("type") == "grpc") { map["network"] = "grpc"; map["grpc-opts"] = new Dictionary<string, string> { ["grpc-service-name"] = Get("serviceName") }; }
                    else if (Get("type") is not ("" or "tcp")) throw new SetupException("此节点传输方式暂不支持。");
                    var certificate=Get("allowInsecure");if(certificate.Length==0)certificate=Get("skip-cert-verify");if(certificate.Length==0)certificate=Get("insecure");
                    if(certificate.Length>0)map["skip-cert-verify"]=certificate;
                }
            }
            return Validate(map);
        }
        catch (SetupException) { throw; }
        catch { throw new SetupException("节点链接格式不正确，未显示含密码的原始链接。"); }
    }
    private static string Decode(string text)
    {
        var value = text.Replace('-', '+').Replace('_', '/').Trim(); value = value.PadRight((value.Length + 3) / 4 * 4, '=');
        return new UTF8Encoding(false, true).GetString(System.Convert.FromBase64String(value));
    }
    public static string CoreConfig(ProxyNode proxy, string[]? endpointAddresses = null, string? physicalInterface = null, ProxyNode? upstream = null, string[]? exitAddresses = null)
    {
        Dictionary<string,JsonElement> RuntimeNode(ProxyNode value)=>JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(Validate(JsonSerializer.Deserialize<Dictionary<string,object?>>(value.Json)!).Json)!;
        var node = RuntimeNode(proxy);
        node["name"] = JsonSerializer.SerializeToElement("SelectedNode");
        upstream ??= proxy.Upstream;
        if (upstream?.Upstream is not null) throw new SetupException("只支持明确的前置节点 → 最终出口两跳配置。");
        var proxies = new List<Dictionary<string,JsonElement>> { node };
        var hosts = new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
        if (upstream is not null) {
            var first = RuntimeNode(upstream);
            first["name"] = JsonSerializer.SerializeToElement("UpstreamNode");
            node["dialer-proxy"] = JsonSerializer.SerializeToElement("UpstreamNode"); proxies.Add(first);
            if (endpointAddresses is not null) {
                if (exitAddresses is null) throw new SetupException("最终出口地址尚未核验。");
                hosts[proxy.Server] = IsolationPolicy.ValidateEndpoints(exitAddresses);
                hosts[upstream.Server] = IsolationPolicy.ValidateEndpoints(endpointAddresses);
                if (proxy.Server.Equals(upstream.Server,StringComparison.OrdinalIgnoreCase) && !endpointAddresses.Order().SequenceEqual(exitAddresses.Order())) throw new SetupException("同名节点服务器的地址绑定不一致。");
            }
        } else if(endpointAddresses is not null) hosts[proxy.Server] = endpointAddresses;
        var external = endpointAddresses is not null;
        if (external) IsolationPolicy.ValidateEndpoints(endpointAddresses!);
        var config = new Dictionary<string, object?>
        {
            ["hosts"] = hosts,
            ["mixed-port"] = 17897, ["allow-lan"] = false, ["mode"] = "rule", ["log-level"] = "silent", ["ipv6"] = false,
            ["tun"] = new Dictionary<string, object?> { ["enable"] = true, ["device"] = "ClaudeTun", ["stack"] = "mixed", ["auto-route"] = true, ["strict-route"] = true, ["auto-detect-interface"] = true, ["dns-hijack"] = new[] { "any:53" } },
            ["dns"] = new Dictionary<string, object?> { ["enable"] = true, ["listen"] = "127.0.0.1:15353", ["ipv6"] = false, ["enhanced-mode"] = "fake-ip", ["fake-ip-range"] = "198.18.0.1/16", ["use-hosts"] = true, ["default-nameserver"] = new[] { external ? "198.19.250.1" : "1.1.1.1" }, ["proxy-server-nameserver"] = new[] { external ? "198.19.250.1" : "https://1.1.1.1/dns-query" }, ["nameserver"] = new[] { external ? "https://1.1.1.1/dns-query#Claude" : "https://cloudflare-dns.com/dns-query" } },
            ["proxies"] = proxies, ["proxy-groups"] = new[] { new { name = "Claude", type = "select", proxies = new[] { "SelectedNode" } } }, ["rules"] = new[] { "MATCH,Claude" }
        };
        if (physicalInterface is not null) {
            if (physicalInterface != "ClaudomeBridge") throw new SetupException("来宾外层网卡名称不符合受控通道配置。");
            config["interface-name"] = physicalInterface;
            ((Dictionary<string,object?>)config["tun"]!)["auto-detect-interface"] = false;
        }
        return JsonSerializer.Serialize(config, JsonFile.Format);
    }
}
