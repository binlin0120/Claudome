using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed record RegionOption(string Code, string Label, string DefaultWindowsTimeZone);
public sealed record NodeProbeResult(
    string ExitIp,
    string CountryCode,
    string IanaTimeZone,
    string? WindowsTimeZoneId,
    int LatencyMs,
    bool SupportedRegion,
    string OutboundInterfaceSummary = "",
    string ResolutionSummary = "",
    string HandshakeSummary = "",
    string TargetSummary = "",
    string HostNetworkSummary = "",
    string[]? VerifiedFirstHopAddresses = null,
    string[]? VerifiedExitAddresses = null,
    string? VerifiedConnectedEndpoint = null,
    string? PreProbeEndpoint = null,
    bool ExitVerified = true,
    string? ExitPendingReason = null);

public static class RegionCatalog
{
    public static readonly IReadOnlyList<RegionOption> All =
    [
        new("US", "美国（US）", "Pacific Standard Time"),
        new("GB", "英国（GB）", "GMT Standard Time"),
        new("CA", "加拿大（CA）", "Eastern Standard Time"),
        new("AU", "澳大利亚（AU）", "AUS Eastern Standard Time"),
        new("NZ", "新西兰（NZ）", "New Zealand Standard Time"),
        new("IE", "爱尔兰（IE）", "GMT Standard Time"),
        new("DE", "德国（DE）", "W. Europe Standard Time"),
        new("FR", "法国（FR）", "Romance Standard Time"),
        new("NL", "荷兰（NL）", "W. Europe Standard Time"),
        new("CH", "瑞士（CH）", "W. Europe Standard Time"),
        new("SE", "瑞典（SE）", "W. Europe Standard Time"),
        new("IT", "意大利（IT）", "W. Europe Standard Time"),
        new("ES", "西班牙（ES）", "Romance Standard Time"),
        new("JP", "日本（JP）", "Tokyo Standard Time"),
        new("KR", "韩国（KR）", "Korea Standard Time"),
        new("SG", "新加坡（SG）", "Singapore Standard Time"),
        new("HK", "中国香港（HK）", "China Standard Time"),
        new("TW", "中国台湾（TW）", "Taipei Standard Time"),
        new("MY", "马来西亚（MY）", "Singapore Standard Time"),
        new("TH", "泰国（TH）", "SE Asia Standard Time"),
        new("IN", "印度（IN）", "India Standard Time"),
        new("BR", "巴西（BR）", "E. South America Standard Time")
    ];

    public static bool IsSupported(string code) =>
        All.Any(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    public static string? DefaultWindowsTimeZone(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) return null;
        var match = All.FirstOrDefault(r => r.Code.Equals(countryCode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(match.DefaultWindowsTimeZone); return match.DefaultWindowsTimeZone; }
        catch { return null; }
    }

    public static string? ResolveWindowsTimeZone(string? ianaOrWindows, string? countryCode)
    {
        var systemZones = TimeZoneInfo.GetSystemTimeZones();
        if (!string.IsNullOrWhiteSpace(ianaOrWindows))
        {
            var candidate = ianaOrWindows.Trim();
            var direct = systemZones.FirstOrDefault(z => z.Id.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (direct is not null) return direct.Id;
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(candidate, out var converted) && !string.IsNullOrWhiteSpace(converted))
            {
                var mapped = systemZones.FirstOrDefault(z => z.Id.Equals(converted, StringComparison.OrdinalIgnoreCase));
                if (mapped is not null) return mapped.Id;
            }
        }
        return DefaultWindowsTimeZone(countryCode);
    }
}

internal sealed class NodeProbeException(string message) : SetupException(message);

internal sealed class NodeProbeCooldown
{
    private readonly ConcurrentDictionary<Uri, DateTimeOffset> retryAt = new();

    internal TimeSpan Remaining(Uri endpoint, DateTimeOffset now) =>
        retryAt.TryGetValue(endpoint, out var until) && until > now ? until - now : TimeSpan.Zero;

    internal void Record(Uri endpoint, RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
    {
        var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - now : TimeSpan.FromMinutes(1));
        var until = delay <= TimeSpan.Zero ? now :
            delay >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now.Add(delay);
        retryAt.AddOrUpdate(endpoint, until, (_, previous) => previous > until ? previous : until);
    }
}

public static class NodeExitProbe
{
    private static readonly NodeProbeCooldown Cooldown = new();
    private static readonly Uri[] GeoEndpoints =
    [
        new("https://api.ip.sb/geoip"),
        new("https://ipwho.is/"),
        new("https://ipinfo.io/json")
    ];
    private static readonly Uri[] ProtocolProbeEndpoints =
    [
        new("https://cp.cloudflare.com/generate_204"),
        new("https://www.gstatic.com/generate_204")
    ];

    public static NodeProbeResult ParseGeoJson(string json, int latencyMs = 0)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new SetupException("出口检测服务返回格式无效。");
        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new SetupException("出口检测服务未能识别当前地址。");

        string PickString(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!root.TryGetProperty(key, out var val)) continue;
                if (val.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(val.GetString()))
                    return val.GetString()!.Trim();
                if (val.ValueKind == JsonValueKind.Object && val.TryGetProperty("id", out var nested) && nested.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(nested.GetString()))
                    return nested.GetString()!.Trim();
            }
            return "";
        }

        var ip = PickString("ip", "query");
        if (string.IsNullOrEmpty(ip)) throw new SetupException("未从检测响应中读取到出口 IP。");
        // Verify public IPv4 boundary; private/reserved/IPv6 addresses are rejected.
        _ = IsolationPolicy.ValidateEndpoints([ip]);

        var country = PickString("country_code", "countryCode", "country").ToUpperInvariant();
        if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
            throw new SetupException("检测响应未包含有效的两位国家/地区代码。");

        var ianaZone = PickString("timezone", "time_zone");
        var windowsZone = RegionCatalog.ResolveWindowsTimeZone(ianaZone, country);
        return new(ip, country, ianaZone, windowsZone, Math.Max(1, latencyMs), RegionCatalog.IsSupported(country));
    }

    internal static EndpointCandidatePlan ExpandSingleEndpointPlan(
        EndpointCandidatePlan plan,
        Func<string[], bool>? isPreferredAddressSet = null)
    {
        var preferredIntactGroups = new List<EndpointCandidateGroup>();
        var singleEntries = new List<(bool Preferred, EndpointCandidateGroup Group)>();
        var seenSingle = new HashSet<string>(StringComparer.Ordinal);
        var seenIntact = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in plan.Groups)
        {
            if (isPreferredAddressSet is not null && group.Addresses.Length > 1)
            {
                var normalizedGroup = group.Addresses.Order(StringComparer.Ordinal).ToArray();
                var intactKey = string.Join(",", normalizedGroup);
                if (seenIntact.Add(intactKey) && isPreferredAddressSet(normalizedGroup))
                {
                    preferredIntactGroups.Add(new EndpointCandidateGroup(
                        $"{group.Source} · 原任务可信绑定组", normalizedGroup, group.Reachability));
                }
            }

            var orderedIps = new List<string>(group.Addresses.Length);
            var preProbeIp = group.Reachability?.ConnectedIp;
            if (!string.IsNullOrEmpty(preProbeIp) && group.Addresses.Contains(preProbeIp, StringComparer.Ordinal))
                orderedIps.Add(preProbeIp);
            foreach (var ip in group.Addresses)
            {
                if (!orderedIps.Contains(ip, StringComparer.Ordinal))
                    orderedIps.Add(ip);
            }
            foreach (var ip in orderedIps)
            {
                if (!seenSingle.Add(ip)) continue;
                var preferred = isPreferredAddressSet?.Invoke([ip]) == true;
                var sourceLabel = group.Addresses.Length > 1 ? $"{group.Source} · 单点 {ip}" : group.Source;
                singleEntries.Add((preferred, new EndpointCandidateGroup(sourceLabel, [ip], group.Reachability)));
            }
        }
        var ordered = preferredIntactGroups
            .Concat(singleEntries
                .OrderByDescending(entry => entry.Preferred)
                .Select(entry => entry.Group))
            .ToArray();
        return new EndpointCandidatePlan(ordered, plan.Attempts, plan.HostNetwork);
    }

    internal static async Task<T> ExecuteAcrossCandidatePlanAsync<T>(
        EndpointCandidatePlan plan,
        Func<EndpointResolutionReport, Task<T>> executeCandidate,
        CancellationToken token)
    {
        var groupFailures = new List<string>();
        Exception? lastError = null;
        for (var i = 0; i < plan.Groups.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var group = plan.Groups[i];
            var priorNotes = groupFailures.Count > 0 ? string.Join("；", groupFailures) + " → " : "";
            var summary = $"{priorNotes}候选组 {i + 1}/{plan.Groups.Length}（{group.Source} · {group.Addresses.Length} 个公网 IPv4）";
            var report = new EndpointResolutionReport(group.Addresses, group.Source, summary, plan.Attempts, plan.HostNetwork, group.Reachability);
            try
            {
                return await executeCandidate(report);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is NodeProbeException or SetupException)
            {
                lastError = error;
                var tcpNote = group.Reachability?.Reachable == true
                    ? $"TCP 已通({group.Reachability.LatencyMs}ms)"
                    : "TCP 未通";
                groupFailures.Add($"{group.Source}({tcpNote}，协议/出口失败)");
            }
        }
        if (lastError is NodeProbeException npe && groupFailures.Count > 1)
            throw new NodeProbeException($"已依次核验 {groupFailures.Count} 组候选地址均未通过（{string.Join("；", groupFailures)}）。\n{npe.Message}");
        throw lastError ?? new NodeProbeException("未取得可核验的候选节点地址。");
    }

    public static Task<NodeProbeResult> ProbeAsync(SetupOptions options, ProxyNode selectedNode, ProxyNode? upstreamNode,
        CancellationToken token, IProgress<DownloadProgress>? progress = null,
        EndpointResolutionStrategy strategy = EndpointResolutionStrategy.Default,
        bool allowUnverifiedExit = false,
        string? preferredCoreConfigSha256 = null,
        string? physicalInterfaceForConfigHash = null) =>
        ProbeThroughCore(options, selectedNode, upstreamNode, strategy, token,
            async (client, runtime, report, exitAddresses) =>
            {
                var first = upstreamNode ?? selectedNode;
                var reach = report.FirstHopReachability;
                var preProbeEndpoint = reach is { Reachable: true, ConnectedIp: { Length: > 0 } preIp }
                    ? $"{preIp}:{first.Port}"
                    : null;
                var singleBoundEndpoint = report.Addresses.Length == 1
                    ? $"{report.Addresses[0]}:{first.Port}"
                    : null;
                var preProbeMatchesBound = reach?.Reachable == true && report.Addresses.Length == 1 &&
                    string.Equals(reach.ConnectedIp, report.Addresses[0], StringComparison.Ordinal);
                var interfaceTask = preProbeMatchesBound
                    ? Task.FromResult<(long ElapsedMs, string InterfaceLabel)?>(null)
                    : ProbeTcpInterface(report.Addresses, first.Port, token);
                var geo = await ProbeEndpoints(client, GeoEndpoints, Cooldown, token, runtime, report, first.Port,
                    allowUnverifiedExit, ProtocolProbeEndpoints);
                var probe = await interfaceTask;
                var ifaceLabel = preProbeMatchesBound
                    ? reach!.OutboundInterfaceLabel
                    : probe?.InterfaceLabel ?? (reach?.Reachable == true ? $"预探测 {reach.OutboundInterfaceLabel}" : "宿主出站路径待核对");
                var tcpLatency = preProbeMatchesBound ? reach!.LatencyMs : (probe is { } h ? (int)Math.Max(1, h.ElapsedMs) : (int?)null);
                var boundNote = singleBoundEndpoint is not null
                    ? $"单点绑定代理对端 {singleBoundEndpoint}"
                    : preProbeEndpoint is not null
                        ? $"TCP 预探测 {preProbeEndpoint}（多 IP 组未单点绑定）"
                        : "多 IP 候选组";
                var handshakeText = tcpLatency is { } ms
                    ? $"{ifaceLabel} · TCP {ms} ms · {boundNote} · 节点协议与 TLS 握手成功"
                    : $"{ifaceLabel} · {boundNote} · 节点协议与 TLS 握手成功";
                return geo with
                {
                    OutboundInterfaceSummary = ifaceLabel,
                    ResolutionSummary = report.Summary,
                    HandshakeSummary = handshakeText,
                    HostNetworkSummary = report.HostNetwork.Summary,
                    VerifiedFirstHopAddresses = [.. report.Addresses],
                    VerifiedExitAddresses = [.. exitAddresses],
                    VerifiedConnectedEndpoint = singleBoundEndpoint,
                    PreProbeEndpoint = preProbeEndpoint
                };
            }, progress, preferredCoreConfigSha256, physicalInterfaceForConfigHash);

    internal static Task<string> MeasureAsync(SetupOptions options, ProxyNode selectedNode, ProxyNode? upstreamNode,
        CancellationToken token, EndpointResolutionStrategy strategy = EndpointResolutionStrategy.Default) =>
        ProbeThroughCore(options, selectedNode, upstreamNode, strategy, token, async (client, runtime, report, _) =>
        {
            var first = upstreamNode ?? selectedNode;
            var tcp = report.FirstHopReachability?.Reachable == true
                ? $"{report.FirstHopReachability.LatencyMs} ms（{report.FirstHopReachability.OutboundInterfaceLabel}）"
                : await MeasureEndpoint(report.Addresses, first.Port, token);
            var exit = await ProbeEndpoints(client, GeoEndpoints, Cooldown, token, runtime, report, first.Port);
            var samples = new List<(double Latency, double Megabits)>();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        "https://speed.cloudflare.com/__down?bytes=262144&nonce=" + Guid.NewGuid().ToString("N"));
                    request.Headers.CacheControl = new() { NoCache = true };
                    var watch = Stopwatch.StartNew();
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                    response.EnsureSuccessStatusCode();
                    var latency = watch.Elapsed.TotalMilliseconds;
                    await using var body = await response.Content.ReadAsStreamAsync(limit.Token);
                    var buffer = new byte[16384];
                    var bytes = 0;
                    watch.Restart();
                    while (bytes < 262144)
                    {
                        var size = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 262144 - bytes)), limit.Token);
                        if (size == 0) break;
                        bytes += size;
                    }
                    if (bytes == 262144) samples.Add((latency, bytes * 8d / Math.Max(.001, watch.Elapsed.TotalSeconds) / 1e6));
                }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                }
            }
            return $"公网出口 {exit.ExitIp} ({exit.CountryCode}) · 解析 {report.SelectedSource} · 主机 TCP {tcp} · " +
                (samples.Count == 0 ? "HTTPS 测速未能验证 2/2" :
                    $"HTTPS 首字节 {samples.Average(s => s.Latency):F0} ms · 短下载 {samples.Average(s => s.Megabits):F2} Mbps · 失败 {2-samples.Count}/2");
        });

    internal static bool IsVirtualTunnelAdapter(string? name, string? description, NetworkInterfaceType type)
    {
        if (type is (NetworkInterfaceType)53 or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp or NetworkInterfaceType.Unknown)
            return true;
        var combined = $"{name} {description}".ToLowerInvariant();
        string[] tunnelMarkers = ["meta", "mihomo", "wintun", "clash", "sing-tun", "sing-box", "tailscale", "wireguard", "zerotier", "openvpn", "tunnel"];
        return tunnelMarkers.Any(combined.Contains) || Regex.IsMatch(combined, @"\b(?:tun|tap|vpn)\d*\b");
    }

    internal static string DescribeOutboundInterface(string? name, string? description, NetworkInterfaceType type)
    {
        if (string.IsNullOrWhiteSpace(name)) return "上行接口未识别";
        return IsVirtualTunnelAdapter(name, description, type)
            ? $"虚拟 TUN/代理接口 {name}（可能存在本机 TUN 接管）"
            : $"物理网卡 {name}";
    }

    private static async Task<(long ElapsedMs, string InterfaceLabel)?> ProbeTcpInterface(string[] addresses, int port, CancellationToken token)
    {
        if (port is < 1 or > 65535 || addresses.Length == 0) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        foreach (var address in addresses)
        {
            using var socket = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
            var watch = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(IPAddress.Parse(address), port, deadline.Token);
                var local = (socket.Client.LocalEndPoint as IPEndPoint)?.Address;
                var adapter = local is null ? null : NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local)));
                var label = adapter is null
                    ? "上行接口未识别"
                    : DescribeOutboundInterface(adapter.Name, adapter.Description, adapter.NetworkInterfaceType);
                return (watch.ElapsedMilliseconds, label);
            }
            catch (System.Net.Sockets.SocketException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
        }
        return null;
    }

    private static async Task<string> MeasureEndpoint(string[] addresses, int port, CancellationToken token)
    {
        var probe = await ProbeTcpInterface(addresses, port, token);
        return probe is { } hit ? $"{hit.ElapsedMs} ms（{hit.InterfaceLabel}）" : "未能验证";
    }

    private static async Task<T> ProbeThroughCore<T>(
        SetupOptions options,
        ProxyNode selectedNode,
        ProxyNode? upstreamNode,
        EndpointResolutionStrategy strategy,
        CancellationToken token,
        Func<HttpClient, BootstrapCore, EndpointResolutionReport, string[], Task<T>> operation,
        IProgress<DownloadProgress>? progress = null,
        string? preferredCoreConfigSha256 = null,
        string? physicalInterfaceForConfigHash = null)
    {
        Paths.ValidateRoot(options.Root);
        var workspaceInitialized = File.Exists(Paths.Under(options.Root, ".claude-setup-workspace.json"));
        var tempProbeRoot = workspaceInitialized ? null : Path.Combine(Path.GetTempPath(), "ClaudeSetup-probe-" + Guid.NewGuid().ToString("N")[..12]);
        var probeOptions = workspaceInitialized
            ? options
            : new SetupOptions { Root = tempProbeRoot! };

        try
        {
            if (tempProbeRoot is not null) Paths.EnsureWorkspace(tempProbeRoot);
            Directory.CreateDirectory(probeOptions.Cache);
            HostPlatform.Environment.RestrictPrivateDirectory(probeOptions.Private);

            var firstHop = upstreamNode ?? selectedNode;
            var candidatePlan = await NodeEndpointResolver.ResolveCandidateGroupsAsync(
                firstHop.Server, firstHop.Port, strategy, token,
                stopOnFirstTcpReachable: strategy == EndpointResolutionStrategy.Default);
            // In two-hop chain mode, host resolves exit hop candidates without connecting directly to the second hop.
            var secondHopAddresses = upstreamNode is null
                ? null
                : (await NodeEndpointResolver.ResolveDetailedAsync(selectedNode.Server, 0, strategy, token)).Addresses;

            if (strategy == EndpointResolutionStrategy.HyperVStandalone)
            {
                Func<string[], bool>? matchesSavedConfig = null;
                if (!string.IsNullOrWhiteSpace(preferredCoreConfigSha256))
                {
                    matchesSavedConfig = candidateAddrs =>
                    {
                        try
                        {
                            var normalizedFirst = IsolationPolicy.ValidateEndpoints(candidateAddrs).Order(StringComparer.Ordinal).ToArray();
                            var sameServer = upstreamNode is not null &&
                                selectedNode.Server.Equals(upstreamNode.Server, StringComparison.OrdinalIgnoreCase);
                            var exitForHash = sameServer
                                ? normalizedFirst
                                : (secondHopAddresses is { Length: > 0 }
                                    ? IsolationPolicy.ValidateEndpoints(secondHopAddresses).Order(StringComparer.Ordinal).ToArray()
                                    : normalizedFirst);
                            var candidateConfig = NetworkImport.CoreConfig(selectedNode, normalizedFirst, physicalInterfaceForConfigHash, upstreamNode, exitForHash);
                            var candidateHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(candidateConfig)));
                            return candidateHash.Equals(preferredCoreConfigSha256, StringComparison.OrdinalIgnoreCase);
                        }
                        catch { return false; }
                    };
                }
                candidatePlan = ExpandSingleEndpointPlan(candidatePlan, matchesSavedConfig);
            }

            return await ExecuteAcrossCandidatePlanAsync(candidatePlan, async report =>
            {
                var sameServer = upstreamNode is not null &&
                    selectedNode.Server.Equals(upstreamNode.Server, StringComparison.OrdinalIgnoreCase);
                var exitAddresses = sameServer ? report.Addresses : (secondHopAddresses ?? report.Addresses);
                var coreConfig = NetworkImport.CoreConfig(selectedNode, report.Addresses, null, upstreamNode, exitAddresses);
                await using var runtime = await BootstrapCore.Start(probeOptions, coreConfig, token, progress);
                using var handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://127.0.0.1:{runtime.Binding.port}", false, [], new NetworkCredential(runtime.Binding.username, runtime.Binding.password)),
                    UseProxy = true,
                    AllowAutoRedirect = false,
                    ConnectTimeout = TimeSpan.FromSeconds(8),
                    PooledConnectionLifetime = TimeSpan.Zero
                };
                using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Claudome/0.4");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

                return await operation(client, runtime, report, exitAddresses);
            }, token);
        }
        finally
        {
            if (tempProbeRoot is not null && Directory.Exists(tempProbeRoot))
            {
                try { Directory.Delete(tempProbeRoot, true); } catch { }
            }
        }
    }

    internal static async Task<NodeProbeResult> ProbeEndpoints(HttpClient client, IReadOnlyList<Uri> endpoints,
        NodeProbeCooldown cooldown, CancellationToken token, BootstrapCore? runtime = null,
        EndpointResolutionReport? resolutionReport = null, int firstHopPort = 443,
        bool allowUnverifiedExit = false, IReadOnlyList<Uri>? protocolProbeEndpoints = null,
        TimeSpan? requestTimeout = null)
    {
        var limited = 0;
        var timeouts = 0;
        var transportFailures = 0;
        var httpFailures = 0;
        var invalid = 0;
        var liveHttpResponses = 0;
        var liveHttpLatencyMs = 0;
        var nextRetry = TimeSpan.MaxValue;
        var transportHint = "";
        var targetNotes = new List<string>();
        var perRequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        foreach (var endpoint in endpoints)
        {
            token.ThrowIfCancellationRequested();
            var remaining = cooldown.Remaining(endpoint, DateTimeOffset.UtcNow);
            if (remaining > TimeSpan.Zero)
            {
                limited++;
                nextRetry = remaining < nextRetry ? remaining : nextRetry;
                targetNotes.Add($"{endpoint.Host}:冷却限流(HTTP 429)");
                continue;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(perRequestTimeout);
            runtime?.BeginProbeStep(443);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                liveHttpResponses++;
                liveHttpLatencyMs = liveHttpLatencyMs == 0 ? (int)Math.Max(1, stopwatch.ElapsedMilliseconds) : liveHttpLatencyMs;
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var now = DateTimeOffset.UtcNow;
                    cooldown.Record(endpoint, response.Headers.RetryAfter, now);
                    remaining = cooldown.Remaining(endpoint, now);
                    limited++;
                    nextRetry = remaining < nextRetry ? remaining : nextRetry;
                    targetNotes.Add($"{endpoint.Host}:HTTP 429");
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    httpFailures++;
                    targetNotes.Add($"{endpoint.Host}:HTTP {(int)response.StatusCode}");
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                var buffer = new byte[16_384];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), deadline.Token);
                    if (read == 0) break;
                    total += read;
                }
                if (total is 0 or >= 16_384)
                {
                    invalid++;
                    targetNotes.Add($"{endpoint.Host}:响应内容无效");
                    continue;
                }

                try
                {
                    var parsed = ParseGeoJson(System.Text.Encoding.UTF8.GetString(buffer, 0, total), (int)stopwatch.ElapsedMilliseconds);
                    return parsed with
                    {
                        TargetSummary = $"{endpoint.Host} · {parsed.LatencyMs} ms · 出口 {parsed.ExitIp} ({parsed.CountryCode})",
                        ExitVerified = true
                    };
                }
                catch (Exception error) when (error is SetupException or JsonException)
                {
                    invalid++;
                    targetNotes.Add($"{endpoint.Host}:格式不符");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                timeouts++;
                transportHint = runtime?.FailureHint ?? "";
                targetNotes.Add($"{endpoint.Host}:请求超时");
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                transportFailures++;
                transportHint = runtime?.FailureHint ?? "";
                targetNotes.Add($"{endpoint.Host}:连接失败");
            }
        }

        // Separate proxy protocol reachability from 3rd-party geo API availability when caller permits
        // unverified exit (deploy/resume/daily reconnect), while real node protocol failures still block.
        var hasProxyFailure = !string.IsNullOrEmpty(runtime?.FailureCode);
        if (allowUnverifiedExit && !hasProxyFailure)
        {
            var protocolReachable = liveHttpResponses > 0;
            var protocolLatencyMs = liveHttpLatencyMs;
            if (!protocolReachable && (limited > 0 || timeouts > 0) && protocolProbeEndpoints is { Count: > 0 })
            {
                var probeTimeout = requestTimeout ?? TimeSpan.FromSeconds(6);
                foreach (var probeUri in protocolProbeEndpoints)
                {
                    token.ThrowIfCancellationRequested();
                    using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    probeDeadline.CancelAfter(probeTimeout);
                    runtime?.BeginProbeStep(probeUri.Port > 0 ? probeUri.Port : 443);
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        using var probeResp = await client.GetAsync(probeUri, HttpCompletionOption.ResponseHeadersRead, probeDeadline.Token);
                        if (string.IsNullOrEmpty(runtime?.FailureCode))
                        {
                            protocolReachable = true;
                            protocolLatencyMs = (int)Math.Max(1, watch.ElapsedMilliseconds);
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) { }
                    catch (Exception error) when (error is HttpRequestException or IOException) { }
                }
            }

            if (protocolReachable && string.IsNullOrEmpty(runtime?.FailureCode))
            {
                var pendingReason = limited > 0
                    ? "第三方地理接口限流（HTTP 429）"
                    : timeouts > 0
                        ? "第三方地理接口超时"
                        : httpFailures > 0
                            ? "第三方地理接口返回异常状态"
                            : "第三方地理接口暂不可用";
                return new NodeProbeResult(
                    ExitIp: "",
                    CountryCode: "",
                    IanaTimeZone: "",
                    WindowsTimeZoneId: null,
                    LatencyMs: Math.Max(1, protocolLatencyMs),
                    SupportedRegion: false,
                    TargetSummary: $"出口待核验（{pendingReason}；节点协议连通性已确认，按现有边界与真实来宾证据核验）",
                    ExitVerified: false,
                    ExitPendingReason: pendingReason);
            }
        }

        var lines = new List<string> { "这次未识别出出口 IP、地区和时区。" };
        if (resolutionReport is not null)
        {
            lines.Add($"1) 节点地址解析：{resolutionReport.Summary}");
            var reach = resolutionReport.FirstHopReachability;
            var tcpNote = reach is null ? "未单独测 TCP" :
                reach.Reachable ? $"端点端口 :{firstHopPort} TCP 已连通（{reach.LatencyMs} ms · {reach.OutboundInterfaceLabel}）" :
                $"候选端点 TCP 均不可达（{reach.OutboundInterfaceLabel}）";
            lines.Add($"2) 节点连接／握手：{tcpNote}" + (transportHint.Length > 0 ? $" · {transportHint.Trim()}" : ""));
            lines.Add($"3) 出口目标验证：{(targetNotes.Count > 0 ? string.Join("；", targetNotes) : "无可用目标响应")}");
            lines.Add($"4) 宿主网络路径：{resolutionReport.HostNetwork.Summary}");
        }
        if (limited > 0)
        {
            var retry = nextRetry > TimeSpan.FromHours(1) ? "请按检测服务的等待时间稍后再测。" :
                nextRetry > TimeSpan.Zero ? $"最早约 {Math.Ceiling(nextRetry.TotalSeconds):F0} 秒后可重试受限服务。" : "";
            lines.Add($"{limited} 个出口检测服务已限流（HTTP 429）。{retry}");
        }
        if (timeouts > 0) lines.Add($"{timeouts} 次检测请求超时。");
        if (transportFailures > 0) lines.Add($"{transportFailures} 次检测请求连接失败。");
        if (httpFailures > 0) lines.Add($"{httpFailures} 个检测服务返回其他 HTTP 错误。");
        if (invalid > 0) lines.Add($"{invalid} 个检测服务的响应缺少有效出口资料。");
        if (resolutionReport is null && limited == 0 && httpFailures == 0 && invalid == 0 && transportHint.Length > 0)
            lines.Add(transportHint.Trim());
        lines.Add("如果已核实节点的实际地区和时区，可手动选择后继续原任务。部署会独立检查安装网络。");
        throw new NodeProbeException(string.Join("\n", lines));
    }
}
