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
public sealed record NodeProbeResult(string ExitIp, string CountryCode, string IanaTimeZone, string? WindowsTimeZoneId, int LatencyMs, bool SupportedRegion, string OutboundInterfaceSummary = "");

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

    public static Task<NodeProbeResult> ProbeAsync(SetupOptions options, ProxyNode selectedNode, ProxyNode? upstreamNode,
        CancellationToken token, IProgress<DownloadProgress>? progress = null) =>
        ProbeThroughCore(options, selectedNode, upstreamNode, token,
            async (client, runtime, addresses) =>
            {
                var first = upstreamNode ?? selectedNode;
                var interfaceTask = ProbeTcpInterface(addresses, first.Port, token);
                var geo = await ProbeEndpoints(client, GeoEndpoints, Cooldown, token, runtime);
                var probe = await interfaceTask;
                return geo with { OutboundInterfaceSummary = probe?.InterfaceLabel ?? "上行接口未识别" };
            }, progress);

    internal static Task<string> MeasureAsync(SetupOptions options, ProxyNode selectedNode, ProxyNode? upstreamNode,
        CancellationToken token) =>
        ProbeThroughCore(options, selectedNode, upstreamNode, token, async (client, runtime, addresses) =>
        {
            var first = upstreamNode ?? selectedNode;
            var tcp = await MeasureEndpoint(addresses, first.Port, token);
            var exit = await ProbeEndpoints(client, GeoEndpoints, Cooldown, token, runtime);
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
            return $"公网出口 {exit.ExitIp} ({exit.CountryCode}) · 主机 TCP {tcp} · " +
                (samples.Count == 0 ? "HTTPS 未能验证 2/2" :
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
        CancellationToken token,
        Func<HttpClient, BootstrapCore, string[], Task<T>> operation,
        IProgress<DownloadProgress>? progress = null)
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
            var firstAddresses = await NodeEndpointResolver.Resolve(firstHop.Server, token);
            var exitAddresses = upstreamNode is null ? firstAddresses : await NodeEndpointResolver.Resolve(selectedNode.Server, token);
            var coreConfig = NetworkImport.CoreConfig(selectedNode, firstAddresses, null, upstreamNode, exitAddresses);

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

            return await operation(client, runtime, firstAddresses);
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
        NodeProbeCooldown cooldown, CancellationToken token, BootstrapCore? runtime = null)
    {
        var limited = 0;
        var timeouts = 0;
        var transportFailures = 0;
        var httpFailures = 0;
        var invalid = 0;
        var nextRetry = TimeSpan.MaxValue;
        var transportHint = "";
        foreach (var endpoint in endpoints)
        {
            token.ThrowIfCancellationRequested();
            var remaining = cooldown.Remaining(endpoint, DateTimeOffset.UtcNow);
            if (remaining > TimeSpan.Zero)
            {
                limited++;
                nextRetry = remaining < nextRetry ? remaining : nextRetry;
                continue;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            runtime?.BeginProbeStep(443);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var now = DateTimeOffset.UtcNow;
                    cooldown.Record(endpoint, response.Headers.RetryAfter, now);
                    remaining = cooldown.Remaining(endpoint, now);
                    limited++;
                    nextRetry = remaining < nextRetry ? remaining : nextRetry;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    httpFailures++;
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
                    continue;
                }

                try
                {
                    return ParseGeoJson(System.Text.Encoding.UTF8.GetString(buffer, 0, total), (int)stopwatch.ElapsedMilliseconds);
                }
                catch (Exception error) when (error is SetupException or JsonException)
                {
                    invalid++;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                timeouts++;
                transportHint = runtime?.FailureHint ?? "";
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                transportFailures++;
                transportHint = runtime?.FailureHint ?? "";
            }
        }

        var lines = new List<string> { "这次未识别出出口 IP、地区和时区。" };
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
        if (limited == 0 && httpFailures == 0 && invalid == 0 && transportHint.Length > 0)
            lines.Add(transportHint.Trim());
        lines.Add("如果已核实节点的实际地区和时区，可手动选择后继续原任务。部署会独立检查安装网络。");
        throw new NodeProbeException(string.Join("\n", lines));
    }
}
