using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace ClaudeSetup;

public enum EndpointResolutionStrategy
{
    Default,
    HyperVStandalone
}

public sealed record HostNetworkContext(
    bool SystemHttpProxyEnabled,
    bool HostTunnelDetected,
    string[] TunnelAdapterNames,
    string Summary);

public sealed record TcpReachabilityResult(
    bool Reachable,
    string? ConnectedIp,
    int? LatencyMs,
    string OutboundInterfaceLabel);

public sealed record EndpointResolutionAttempt(
    string Source,
    string Outcome,
    string[] ValidatedAddresses,
    bool TcpReachable = false,
    string? ConnectedIp = null,
    int? TcpLatencyMs = null,
    string OutboundInterfaceLabel = "");

public sealed record EndpointResolutionReport(
    string[] Addresses,
    string SelectedSource,
    string Summary,
    EndpointResolutionAttempt[] Attempts,
    HostNetworkContext HostNetwork,
    TcpReachabilityResult? FirstHopReachability);

public static class NodeEndpointResolver
{
    private static readonly (string Name, string Prefix, string Suffix)[] DefaultProviders =
    [
        ("Cloudflare", "https://cloudflare-dns.com/dns-query?name=", "&type=A"),
        ("Google", "https://dns.google/resolve?name=", "&type=A&edns_client_subnet=0.0.0.0%2F0")
    ];

    private static readonly (string Name, string Prefix, string Suffix)[] HyperVStandaloneProviders =
    [
        ("AliDNS", "https://dns.alidns.com/resolve?name=", "&type=1"),
        ("DNSPod", "https://doh.pub/resolve?name=", "&type=1"),
        ("Cloudflare", "https://cloudflare-dns.com/dns-query?name=", "&type=A"),
        ("Google", "https://dns.google/resolve?name=", "&type=A&edns_client_subnet=0.0.0.0%2F0")
    ];

    internal static string HttpFailure(HttpRequestException error) => error.StatusCode is { } status ? "HTTP_" + (int)status : error.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "DNS_LOOKUP_FAILED",
        HttpRequestError.ConnectionError => "CONNECTION_FAILED",
        HttpRequestError.SecureConnectionError => "TLS_FAILED",
        HttpRequestError.ProxyTunnelError => "PROXY_TUNNEL_FAILED",
        HttpRequestError.ResponseEnded => "RESPONSE_ENDED",
        HttpRequestError.HttpProtocolError => "HTTP_PROTOCOL_FAILED",
        _ => "HTTP_REQUEST_FAILED"
    };

    public static HostNetworkContext InspectHostNetworkContext()
    {
        var proxyEnabled = false;
        try
        {
            var probeUri = new Uri("https://api.ip.sb/geoip");
            var proxyUri = HttpClient.DefaultProxy?.GetProxy(probeUri);
            proxyEnabled = proxyUri is not null && !string.Equals(proxyUri.AbsoluteUri, probeUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        }
        catch { }

        var tunnels = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (NodeExitProbe.IsVirtualTunnelAdapter(nic.Name, nic.Description, nic.NetworkInterfaceType) &&
                    !nic.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) &&
                    !nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
                {
                    tunnels.Add(nic.Name);
                }
            }
        }
        catch { }

        var distinctTunnels = tunnels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hasTunnel = distinctTunnels.Length > 0;
        var summary = (hasTunnel, proxyEnabled) switch
        {
            (true, true) => $"检测到宿主 TUN（{string.Join("、", distinctTunnels)}）与 HTTP 系统代理；独立 DoH 已绕过 HTTP 代理，网关 TCP 仍随当前宿主路由",
            (true, false) => $"检测到宿主 TUN（{string.Join("、", distinctTunnels)}）；网关出站随当前宿主路由（未自动关闭）",
            (false, true) => "检测到宿主 HTTP 系统代理；独立 DoH 已显式绕过，网关 TCP 直连宿主默认路由",
            _ => "未检测到宿主 TUN 或 HTTP 系统代理"
        };
        return new(proxyEnabled, hasTunnel, distinctTunnels, summary);
    }

    public static Task<string[]> Resolve(string server, CancellationToken token, HttpClient? suppliedClient = null, TimeSpan? providerTimeout = null) =>
        ResolveCoreAsync(server, DefaultProviders, token, suppliedClient, providerTimeout);

    public static async Task<EndpointResolutionReport> ResolveDetailedAsync(
        string server,
        int port,
        EndpointResolutionStrategy strategy,
        CancellationToken token,
        HttpClient? suppliedDirectClient = null,
        TimeSpan? providerTimeout = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? systemDnsLookup = null,
        Func<string[], int, CancellationToken, Task<TcpReachabilityResult>>? tcpProbe = null,
        HostNetworkContext? hostNetworkOverride = null)
    {
        token.ThrowIfCancellationRequested();
        var hostNet = hostNetworkOverride ?? InspectHostNetworkContext();
        var probe = tcpProbe ?? ProbeEndpointConnectivityAsync;

        if (IPAddress.TryParse(server, out var literal))
        {
            var literalAddrs = IsolationPolicy.ValidateEndpoints([literal.ToString()]);
            TcpReachabilityResult? literalReach = port is >= 1 and <= 65535 ? await probe(literalAddrs, port, token) : null;
            var attempt = new EndpointResolutionAttempt("IPv4 字面量", "公网地址有效", literalAddrs,
                literalReach?.Reachable == true, literalReach?.ConnectedIp, literalReach?.LatencyMs, literalReach?.OutboundInterfaceLabel ?? "");
            return new(literalAddrs, "IPv4 字面量", "IPv4 字面量（无需 DNS 解析）", [attempt], hostNet, literalReach);
        }

        var queryName = ValidateHostname(server);
        if (strategy == EndpointResolutionStrategy.Default)
        {
            var addrs = await ResolveCoreAsync(server, DefaultProviders, token, suppliedDirectClient, providerTimeout);
            TcpReachabilityResult? reach = port is >= 1 and <= 65535 ? await probe(addrs, port, token) : null;
            var attempt = new EndpointResolutionAttempt("默认 DoH", $"已解析 {addrs.Length} 个公网 IPv4", addrs,
                reach?.Reachable == true, reach?.ConnectedIp, reach?.LatencyMs, reach?.OutboundInterfaceLabel ?? "");
            return new(addrs, "默认 DoH", $"默认 DoH · {addrs.Length} 个公网 IPv4", [attempt], hostNet, reach);
        }

        using var directHandler = suppliedDirectClient is null
            ? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) }
            : null;
        using var ownedDirectClient = directHandler is null ? null : new HttpClient(directHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = suppliedDirectClient ?? ownedDirectClient!;
        var dnsLookup = systemDnsLookup ?? ((host, ct) => Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct));

        var attempts = new List<EndpointResolutionAttempt>();
        var failures = new List<string>();
        string[]? fallbackValidAddresses = null;
        string fallbackSource = "";
        TcpReachabilityResult? fallbackReach = null;

        async Task<EndpointResolutionReport?> EvaluateGroupAsync(string sourceName, string[] candidateAddresses)
        {
            var reach = port is >= 1 and <= 65535 ? await probe(candidateAddresses, port, token) : null;
            var outcome = reach is null
                ? $"已解析 {candidateAddresses.Length} 个公网 IPv4"
                : reach.Reachable
                    ? $"已解析 {candidateAddresses.Length} 个公网 IPv4 · TCP 已连通 ({reach.LatencyMs} ms · {reach.OutboundInterfaceLabel})"
                    : $"已解析 {candidateAddresses.Length} 个公网 IPv4 · TCP 不可达 ({reach.OutboundInterfaceLabel})";
            attempts.Add(new EndpointResolutionAttempt(sourceName, outcome, candidateAddresses,
                reach?.Reachable == true, reach?.ConnectedIp, reach?.LatencyMs, reach?.OutboundInterfaceLabel ?? ""));
            if (fallbackValidAddresses is null)
            {
                fallbackValidAddresses = candidateAddresses;
                fallbackSource = sourceName;
                fallbackReach = reach;
            }
            if (reach is null || reach.Reachable)
            {
                var summary = BuildAttemptSummary(sourceName, candidateAddresses.Length, attempts);
                return new(candidateAddresses, sourceName, summary, [.. attempts], hostNet, reach);
            }
            return null;
        }

        // 1. Try Host System DNS first (must pass strict IsolationPolicy validation across the whole answer set).
        try
        {
            using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            dnsTimeout.CancelAfter(providerTimeout ?? TimeSpan.FromSeconds(4));
            var hostIps = await dnsLookup(queryName, dnsTimeout.Token);
            var ipv4Strings = hostIps.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).Select(ip => ip.ToString()).Distinct(StringComparer.Ordinal).ToArray();
            if (ipv4Strings.Length == 0)
            {
                attempts.Add(new("宿主机系统 DNS", "无 IPv4 A 记录", []));
                failures.Add("SystemDNS:NO_IPV4_ANSWER");
            }
            else
            {
                try
                {
                    var validated = IsolationPolicy.ValidateEndpoints(ipv4Strings).Order(StringComparer.Ordinal).ToArray();
                    if (await EvaluateGroupAsync("宿主机系统 DNS", validated) is { } hit) return hit;
                    failures.Add("SystemDNS:TCP_UNREACHABLE");
                }
                catch (SetupException)
                {
                    attempts.Add(new("宿主机系统 DNS", "含私网/Fake-IP/保留地址（整组已拒绝）", []));
                    failures.Add("SystemDNS:REJECTED_NON_PUBLIC");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            attempts.Add(new("宿主机系统 DNS", "查询超时", []));
            failures.Add("SystemDNS:TIMEOUT");
        }
        catch (Exception)
        {
            attempts.Add(new("宿主机系统 DNS", "解析失败", []));
            failures.Add("SystemDNS:LOOKUP_FAILED");
        }

        // 2. Fallback to independent DoH sources (UseProxy = false, AllowAutoRedirect = false).
        foreach (var provider in HyperVStandaloneProviders)
        {
            token.ThrowIfCancellationRequested();
            var (addresses, errorCode) = await QueryDoHProviderAsync(client, queryName, provider, providerTimeout ?? TimeSpan.FromSeconds(6), token);
            if (errorCode is not null)
            {
                attempts.Add(new($"直连 DoH ({provider.Name})", DescribeProviderError(errorCode), []));
                failures.Add($"{provider.Name}:{errorCode}");
                continue;
            }
            if (await EvaluateGroupAsync($"直连 DoH ({provider.Name})", addresses!) is { } hit) return hit;
            failures.Add($"{provider.Name}:TCP_UNREACHABLE");
        }

        if (fallbackValidAddresses is not null)
        {
            var summary = BuildAttemptSummary(fallbackSource, fallbackValidAddresses.Length, attempts) + "（所有候选组 TCP 均未连通，保留首个合法候选集供代理内核继续诊断）";
            return new(fallbackValidAddresses, fallbackSource, summary, [.. attempts], hostNet, fallbackReach);
        }

        throw new SetupException("节点地址查询未完成（" + string.Join("；", failures) + "）。已保留所选节点和原任务；请检查主机当前网络或代理连接后继续。");
    }

    private static string BuildAttemptSummary(string selectedSource, int count, IReadOnlyList<EndpointResolutionAttempt> attempts)
    {
        var prior = attempts.Where(a => !string.Equals(a.Source, selectedSource, StringComparison.Ordinal))
            .Select(a => $"{a.Source}:{a.Outcome}").ToArray();
        var prefix = prior.Length > 0 ? string.Join(" → ", prior) + " → " : "";
        return $"{prefix}采用 {selectedSource}（{count} 个公网 IPv4）";
    }

    private static string DescribeProviderError(string code) => code switch
    {
        "REJECTED_NON_PUBLIC" => "含私网/保留地址（整组已拒绝）",
        "TIMEOUT" => "请求超时",
        "NO_IPV4_ANSWER" => "无 IPv4 记录",
        _ => code
    };

    private static string ValidateHostname(string server)
    {
        try
        {
            var queryName = new IdnMapping().GetAscii(server.TrimEnd('.'));
            if (queryName.Length is < 1 or > 253 || Uri.CheckHostName(queryName) != UriHostNameType.Dns || queryName.Split('.').Any(label => label.Length is < 1 or > 63))
                throw new ArgumentException();
            return queryName;
        }
        catch (ArgumentException)
        {
            throw new SetupException("节点服务器地址不是有效的域名或 IPv4，请检查节点资料；未改选其他节点。");
        }
    }

    private static async Task<(string[]? Addresses, string? ErrorCode)> QueryDoHProviderAsync(
        HttpClient client,
        string queryName,
        (string Name, string Prefix, string Suffix) provider,
        TimeSpan timeout,
        CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, provider.Prefix + Uri.EscapeDataString(queryName) + provider.Suffix);
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!response.IsSuccessStatusCode) return (null, "HTTP_" + (int)response.StatusCode);
            using var input = await response.Content.ReadAsStreamAsync(limit.Token);
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), limit.Token);
                if (read == 0) break;
                if (body.Length + read > 65536) return (null, "RESPONSE_TOO_LARGE");
                body.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(body.ToArray());
            var root = json.RootElement;
            if (root.TryGetProperty("Status", out var dnsStatus) && dnsStatus.ValueKind == JsonValueKind.Number && dnsStatus.TryGetInt32(out var status) && status != 0)
                return (null, "DNS_STATUS_" + status);
            if (!root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
                return (null, "NO_IPV4_ANSWER");
            var addresses = new List<string>();
            foreach (var answer in answers.EnumerateArray())
            {
                if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.Number || !kind.TryGetInt32(out var type) || type != 1)
                    continue;
                if (answer.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String && IPAddress.TryParse(data.GetString(), out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    addresses.Add(ip.ToString());
            }
            if (addresses.Count == 0) return (null, "NO_IPV4_ANSWER");
            try
            {
                return (IsolationPolicy.ValidateEndpoints(addresses).Order(StringComparer.Ordinal).ToArray(), null);
            }
            catch (SetupException)
            {
                return (null, "REJECTED_NON_PUBLIC");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return (null, "TIMEOUT"); }
        catch (HttpRequestException error) { return (null, HttpFailure(error)); }
        catch (JsonException) { return (null, "INVALID_JSON"); }
        catch (IOException) { return (null, "RESPONSE_IO_FAILED"); }
    }

    private static async Task<TcpReachabilityResult> ProbeEndpointConnectivityAsync(string[] addresses, int port, CancellationToken token)
    {
        if (port is < 1 or > 65535 || addresses.Length == 0)
            return new(false, null, null, "宿主出站路径待核对");

        foreach (var address in addresses.Take(3))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(2500));
            using var socket = new TcpClient(AddressFamily.InterNetwork);
            var watch = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(IPAddress.Parse(address), port, deadline.Token);
                watch.Stop();
                var local = (socket.Client.LocalEndPoint as IPEndPoint)?.Address;
                var adapter = local is null ? null : NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local)));
                var label = adapter is null
                    ? "宿主出站路径待核对"
                    : NodeExitProbe.DescribeOutboundInterface(adapter.Name, adapter.Description, adapter.NetworkInterfaceType);
                return new(true, address, (int)Math.Max(1, watch.ElapsedMilliseconds), label);
            }
            catch (SocketException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        return new(false, null, null, "宿主出站路径待核对");
    }

    private static async Task<string[]> ResolveCoreAsync(
        string server,
        (string Name, string Prefix, string Suffix)[] providers,
        CancellationToken token,
        HttpClient? suppliedClient,
        TimeSpan? providerTimeout)
    {
        token.ThrowIfCancellationRequested();
        if (IPAddress.TryParse(server, out var literal)) return IsolationPolicy.ValidateEndpoints([literal.ToString()]);
        var queryName = ValidateHostname(server);
        using var owned = suppliedClient is null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : null;
        var client = suppliedClient ?? owned!;
        var failures = new List<string>();
        foreach (var provider in providers)
        {
            token.ThrowIfCancellationRequested();
            var (addresses, errorCode) = await QueryDoHProviderAsync(client, queryName, provider, providerTimeout ?? TimeSpan.FromSeconds(8), token);
            if (errorCode is null && addresses is not null) return addresses;
            // A private/reserved result in Default strategy is a hard failure, never silently fall through to a backup provider.
            if (errorCode == "REJECTED_NON_PUBLIC")
                _ = IsolationPolicy.ValidateEndpoints(["10.0.0.1"]);
            failures.Add(provider.Name + ":" + errorCode);
        }
        throw new SetupException("节点地址查询未完成（" + string.Join("；", failures) + "）。已保留所选节点和原任务；请检查主机当前网络或代理连接后继续。");
    }
}
