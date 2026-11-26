using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeSetup;

public sealed record GatewayBinding(string VmId, int GuestPort, int ListenPort, string GuestMac);
public sealed record GatewayPolicy(string vmId, string runId, string guestMac, int guestPort, int listenPort, string[] endpointAddresses, int endpointPort, string nodeHost,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] BootstrapProxy? bootstrap=null);
internal sealed class BootstrapProbeException(string? code, string hint = "")
    : SetupException(ExternalGateway.BootstrapFailureMessage(code) + hint)
{
    internal string? Code { get; } = code;
}

public sealed class ExternalGateway : IAsyncDisposable
{
    private readonly Process worker;
    private readonly string vmId, runId, fingerprint;
    private readonly SemaphoreSlim control = new(1);
    private readonly CancellationTokenSource stopped = new();
    private Task? heartbeat;
    private BootstrapCore? bootstrapCore;
    private long windowsSignInEndsAt;
    private string lastFailure="";
    public string LastFailure=>Volatile.Read(ref lastFailure);
    public string[] EndpointAddresses { get; private init; } = [];
    public int EndpointPort { get; private init; }
    public bool HasBootstrap=>bootstrapCore is not null;
    private Func<CancellationToken, Task>? refresh;
    internal bool IsHeldForWindowsSignIn => Volatile.Read(ref windowsSignInEndsAt) != 0;
    public bool IsAlive => !stopped.IsCancellationRequested && !worker.HasExited &&
        !WindowsSignInPeriodExpired(Volatile.Read(ref windowsSignInEndsAt), Environment.TickCount64);

    internal static bool WindowsSignInPeriodExpired(long expiresAt, long now) => expiresAt != 0 && now >= expiresAt;

    internal bool TryHoldForWindowsSignIn()
    {
        if (!IsAlive || !HasBootstrap) return false;
        Interlocked.CompareExchange(ref windowsSignInEndsAt, Environment.TickCount64 + 600_000, 0);
        return IsAlive;
    }

    internal void EndWindowsSignInHold() => Interlocked.Exchange(ref windowsSignInEndsAt, 0);
    internal void RevokeWindowsSignInHold() { if (IsHeldForWindowsSignIn) Stop(); }
    private ExternalGateway(Process process, string vm, string run, string hash) { worker = process; vmId = vm; runId = run; fingerprint = hash; }
    public static GatewayPolicy Policy(GatewayBinding binding, string[] addresses, int port, string nodeHost, string runId)
    {
        if (!Guid.TryParseExact(binding.VmId, "D", out _) || !Guid.TryParseExact(runId, "N", out _) || binding.ListenPort is < 1024 or > 65535 || binding.GuestPort is < 1024 or > 65535 || binding.ListenPort == binding.GuestPort || port is < 1 or > 65535 || addresses.Length > 32) throw new SetupException("网关绑定参数无效。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(binding.GuestMac, "^(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$") || (Convert.ToByte(binding.GuestMac[..2], 16) & 1) != 0) throw new SetupException("网关 MAC 绑定无效。");
        if (nodeHost.Length > 253 || nodeHost.Any(x => !(char.IsAsciiLetterOrDigit(x) || x is '.' or '-'))) throw new SetupException("节点主机名无效。");
        return new(binding.VmId, runId, binding.GuestMac, binding.GuestPort, binding.ListenPort, IsolationPolicy.ValidateEndpoints(addresses), port, nodeHost);
    }
    public static GatewayBinding Binding(SessionState state)
    {
        var file = Paths.Under(state.Options.Private, "gateway-binding.json");
        if (File.Exists(file)) { var saved = JsonFile.Read<GatewayBinding>(file); if (saved.VmId != state.VmId) throw new SetupException("网关记录属于其他虚拟机。"); return saved; }
        using var first = ReserveLoopbackPort();
        using var second = ReserveLoopbackPort();
        var bytes = RandomNumberGenerator.GetBytes(6); bytes[0] = 2;
        var binding = new GatewayBinding(state.VmId, ((IPEndPoint)first.Client.LocalEndPoint!).Port, ((IPEndPoint)second.Client.LocalEndPoint!).Port, string.Join(":", bytes.Select(x => x.ToString("x2"))));
        HostPlatform.Environment.RestrictPrivateDirectory(state.Options.Private); JsonFile.Write(file, binding); return binding;
    }
    /// <summary>Deletes superseded hashed gateway binaries in the private folder, skipping any locked file.</summary>
    internal static void CleanupStaleExecutables(string privateDirectory, string currentExecutable)
    {
        if (!Directory.Exists(privateDirectory)) return;
        var currentFull = Path.GetFullPath(currentExecutable);
        foreach (var path in Directory.EnumerateFiles(privateDirectory, "ClaudeNetworkGateway.*.exe", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFullPath(path).Equals(currentFull, StringComparison.OrdinalIgnoreCase)) continue;
            var fileName = Path.GetFileName(path);
            if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^ClaudeNetworkGateway\.[0-9a-f]{16}\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private static UdpClient ReserveLoopbackPort()
    {
        try { return new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); }
        catch (SocketException error) { throw Redaction.SocketStartupError("分配宿主回环 UDP 端口", error); }
    }
    public static async Task<ExternalGateway> Start(SessionState state, GatewayBinding binding, string[] addresses, int port, string nodeHost, Func<CancellationToken, Task> verifyAdapter, CancellationToken token,BootstrapCore? bootstrap=null)
    {
        var policy = Policy(binding, addresses, port, nodeHost, Guid.NewGuid().ToString("N")) with {bootstrap=bootstrap?.Binding};
        var json = JsonSerializer.Serialize(policy); var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        var data = Catalog.EmbeddedBytes("ClaudeNetworkGateway.exe");
        var executable = Paths.Under(state.Options.Private, "ClaudeNetworkGateway." + Convert.ToHexString(SHA256.HashData(data))[..16].ToLowerInvariant() + ".exe");
        HostPlatform.Environment.RestrictPrivateDirectory(state.Options.Private);
        CleanupStaleExecutables(state.Options.Private, executable);
        if (File.Exists(executable)) {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(executable)), SHA256.HashData(data))) throw new SetupException("本地网关文件与程序内置版本不一致，未启动。");
        } else File.WriteAllBytes(executable, data);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false) };
        var process = Process.Start(start) ?? throw new SetupException("网关组件未启动。");
        var gateway = new ExternalGateway(process, policy.vmId, policy.runId, hash){bootstrapCore=bootstrap, EndpointAddresses=policy.endpointAddresses, EndpointPort=policy.endpointPort};
        try {
            _ = process.StandardError.ReadToEndAsync(); // never log packet data or node addresses
            await process.StandardInput.WriteLineAsync(json.AsMemory(), token); await process.StandardInput.FlushAsync(token);
            using var ready = await gateway.Read(token); var root = ready.RootElement;
            gateway.CheckBinding(root);
            if (!root.GetProperty("ready").GetBoolean() || root.GetProperty("version").GetInt32() != 1 || root.GetProperty("udpPort").GetInt32() != binding.ListenPort) throw new SetupException("网关启动回读不匹配。");
            // No renewal until both host firewall and actual adapter were read back.
            await gateway.VerifyAndRenew(state, binding, verifyAdapter, token);
            gateway.refresh = ct => gateway.VerifyAndRenew(state, binding, verifyAdapter, ct);
            gateway.heartbeat = Task.Run(() => gateway.Monitor(state, binding, verifyAdapter)); return gateway;
        } catch { await gateway.DisposeAsync(); throw; }
    }
    public async Task ProbeBootstrap(CancellationToken token, IProgress<string>? messages = null)
    {
        if (bootstrapCore is null) return;
        foreach (var (operation, port, start, complete) in new[] {
            ("probe-bootstrap-dns", 443, "1 / 2 检查经所选节点的 HTTPS 与 DNS", "HTTPS 与 DNS 已通过，继续检测 Windows 联网页面。"),
            ("probe-bootstrap-http", 80, "2 / 2 检查 Windows 联网页面，尝试已核验的多个地址", "Windows 联网页面已通过，安装网络准备完成。")
        })
        {
            messages?.Report("安装网络：" + start);
            await RetryBootstrapTransport(async ct =>
            {
                // Every attempt renews the verified boundary and uses the same selected node.
                await Recheck(ct);
                await control.WaitAsync(ct);
                try
                {
                    bootstrapCore?.BeginProbeStep(port);
                    await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { operation, fingerprint }).AsMemory(), ct);
                    await worker.StandardInput.FlushAsync(ct);
                    using var reply = await Read(ct, 11);
                    CheckBinding(reply.RootElement);
                    if (!reply.RootElement.GetProperty("ok").GetBoolean() || reply.RootElement.GetProperty("operation").GetString() != operation)
                    {
                        var code = reply.RootElement.TryGetProperty("code", out var status) ? status.GetString() : null;
                        throw new BootstrapProbeException(code, bootstrapCore?.FailureHint ?? "");
                    }
                }
                finally { control.Release(); }
            }, attempt => messages?.Report($"安装网络：连接中断，正在用原节点重试（{attempt}/3）。"), token);
            messages?.Report("安装网络：" + complete);
        }
        await Recheck(token);
    }

    internal static async Task RetryBootstrapTransport(Func<CancellationToken, Task> probe, Action<int> retry, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await probe(token);
                return;
            }
            catch (BootstrapProbeException ex) when (attempt < 3 && ex.Code is
                "BOOTSTRAP_DNS_TRANSPORT_FAILED" or "BOOTSTRAP_TIMEOUT" or "BOOTSTRAP_HTTP_FAILED" or "BOOTSTRAP_HTTP_TIMEOUT")
            {
                retry(attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt), token);
            }
        }
    }

    internal static string BootstrapFailureMessage(string? code)
    {
        var message=code switch {
            "BOOTSTRAP_AUTH_REQUIRED" or "BOOTSTRAP_AUTH_FAILED"=>"本机安装网络组件的认证未通过。",
            "BOOTSTRAP_CONNECT_FAILED"=>"安装网络的代理连接阶段未完成。",
            "BOOTSTRAP_DNS_TLS_FAILED"=>"经所选节点访问 DNS 服务时，HTTPS 证书验证未通过。",
            "BOOTSTRAP_DNS_TRANSPORT_FAILED"=>"经所选节点访问 DNS 服务时，HTTPS 连接中断。",
            "BOOTSTRAP_DOH_HTTP_FAILED"=>"DNS 服务的 HTTPS 请求未成功，已尝试备用服务。",
            "BOOTSTRAP_DOH_RESPONSE_INVALID"=>"DNS 服务返回内容未通过校验，已尝试备用服务。",
            "BOOTSTRAP_DNS_EMPTY"=>"DNS 响应中未取得联网检测所需的公网地址。",
            "BOOTSTRAP_TIMEOUT"=>"安装网络检测超时，尚未确认所选节点可访问 Internet。",
            "BOOTSTRAP_DNS_FAILED"=>"安装网络的 DNS 检测未通过。",
            "BOOTSTRAP_HTTP_REDIRECT"=>"DNS 已取得地址，但 Windows 联网检测被重定向，未按联网成功处理。",
            "BOOTSTRAP_HTTP_TIMEOUT"=>"DNS 已通过，但 Windows 联网页面在多个地址上均未及时响应。",
            "BOOTSTRAP_DNS_REQUIRED"=>"Windows 联网检测缺少本次已核验的 DNS 结果。",
            "BOOTSTRAP_HTTP_FAILED"=>"DNS 已取得地址，但 Windows 联网检测的网页连接未成功。",
            "BOOTSTRAP_HTTP_MISMATCH"=>"Windows 联网检测返回内容不符，尚未确认 Internet 可用。",
            "EGRESS_CLOSED"=>"安装网络保护通道已关闭。",
            _=>"本机安装网络组件尚未就绪。"
        };
        if(code is not ("BOOTSTRAP_AUTH_REQUIRED" or "BOOTSTRAP_AUTH_FAILED" or "BOOTSTRAP_CONNECT_FAILED" or "BOOTSTRAP_DNS_TLS_FAILED" or "BOOTSTRAP_DNS_TRANSPORT_FAILED" or "BOOTSTRAP_DOH_HTTP_FAILED" or "BOOTSTRAP_DOH_RESPONSE_INVALID" or "BOOTSTRAP_DNS_EMPTY" or "BOOTSTRAP_TIMEOUT" or "BOOTSTRAP_DNS_FAILED" or "BOOTSTRAP_HTTP_REDIRECT" or "BOOTSTRAP_HTTP_TIMEOUT" or "BOOTSTRAP_DNS_REQUIRED" or "BOOTSTRAP_HTTP_FAILED" or "BOOTSTRAP_HTTP_MISMATCH" or "EGRESS_CLOSED"))code="BOOTSTRAP_UNAVAILABLE";
        var hostHint = code switch
        {
            "BOOTSTRAP_AUTH_REQUIRED" or "BOOTSTRAP_AUTH_FAILED" => "宿主排查：请回到「节点」页核对节点用户名、密码或认证方式是否填写正确；原任务进度与磁盘已保留。",
            "BOOTSTRAP_CONNECT_FAILED" => "宿主排查：请检查宿主机代理服务是否已开启、节点服务器地址与端口是否正确且允许本机防火墙出站；原任务配置与磁盘已保留。",
            "BOOTSTRAP_DNS_TLS_FAILED" => "宿主排查：请校准宿主机系统时间与时区，并排查节点/机场或本地安全软件是否开启了 HTTPS 证书中间人解密；原任务进度已保留。",
            "BOOTSTRAP_DNS_TRANSPORT_FAILED" or "BOOTSTRAP_DOH_HTTP_FAILED" or "BOOTSTRAP_DOH_RESPONSE_INVALID" or "BOOTSTRAP_DNS_EMPTY" or "BOOTSTRAP_DNS_FAILED" =>
                "宿主排查：请确认宿主机所选代理节点可正常解析 DoH（DNS over HTTPS），或切换一条稳定节点后重试原任务。",
            "BOOTSTRAP_TIMEOUT" or "BOOTSTRAP_HTTP_TIMEOUT" or "BOOTSTRAP_HTTP_FAILED" =>
                "宿主排查：请在宿主机用同一节点测试访问网页是否超时或丢包；当前原任务与磁盘均已保留，节点恢复后可直接重试。",
            _ => "宿主排查：请检查宿主机当前代理节点连通性与防火墙放行设置；原任务与磁盘完整保留，排查后可继续。"
        };
        return message + "（" + code + "）\n" + hostHint;
    }
    public async Task FinishBootstrap(CancellationToken token)
    {
        if(bootstrapCore is null)return;
        await control.WaitAsync(token);
        try {
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{operation="finish-bootstrap",fingerprint}).AsMemory(),token);await worker.StandardInput.FlushAsync(token);
            using var reply=await Read(token);CheckBinding(reply.RootElement);
            if(!reply.RootElement.GetProperty("ok").GetBoolean()||reply.RootElement.GetProperty("operation").GetString()!="finish-bootstrap")throw new SetupException("安装网络尚未退出，未完成最终网络验收。");
            await bootstrapCore.DisposeAsync();bootstrapCore=null;
        }finally{control.Release();}
    }
    private void CheckBinding(JsonElement root)
    {
        if (root.GetProperty("vmId").GetString() != vmId || root.GetProperty("runId").GetString() != runId || root.GetProperty("fingerprint").GetString() != fingerprint) throw new SetupException("网关回读与当前虚拟机或节点不匹配。");
    }
    private async Task<JsonDocument> Read(CancellationToken token,int seconds=5)
    {
        var line = await worker.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(seconds), token);
        if (line is null || line.Length > 8192) throw new SetupException("网关控制通道中断。"); return JsonDocument.Parse(line);
    }
    private async Task VerifyAndRenew(SessionState state, GatewayBinding binding, Func<CancellationToken, Task> verify, CancellationToken token)
    {
        await control.WaitAsync(token);
        try {
            await verify(token);
            if (WindowsSignInPeriodExpired(Volatile.Read(ref windowsSignInEndsAt), Environment.TickCount64))
                throw new SetupException("等待 Windows 登录的临时安装网络已超时，继续原任务可重新开启。");
            bootstrapCore?.RequireAlive();
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { operation = "renew", fingerprint, seconds = 20 }).AsMemory(), token); await worker.StandardInput.FlushAsync(token);
            using var ack = await Read(token); CheckBinding(ack.RootElement);
            if (!ack.RootElement.GetProperty("ok").GetBoolean() || ack.RootElement.GetProperty("operation").GetString() != "renew") throw new SetupException("网关拒绝联网授权。");
        } finally { control.Release(); }
    }
    internal static async Task RunResilientHeartbeat(Func<CancellationToken, Task> verifyOnce, Func<bool> canRetry, int retryDelayMs, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await verifyOnce(token);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch when (attempt < 3 && canRetry())
            {
                await Task.Delay(Math.Max(1, retryDelayMs), token);
            }
        }
    }
    private async Task Monitor(SessionState state, GatewayBinding binding, Func<CancellationToken, Task> verify)
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                await Task.Delay(5000, stopped.Token);
                await RunResilientHeartbeat(
                    ct => VerifyAndRenew(state, binding, verify, ct),
                    () => IsAlive,
                    1200,
                    stopped.Token);
            }
        }
        catch(Exception error) { Volatile.Write(ref lastFailure,Redaction.SafeError(error));Stop(); } // Persistent or real boundary failure closes the worker fail-closed.
    }
    public async Task Recheck(CancellationToken token)
    {
        if (!IsAlive || refresh is null) throw new SetupException("外层网关已停止，请继续原任务重新建立保护。");
        try { await refresh(token); } catch { Stop(); throw; }
    }
    private void Stop()
    {
        stopped.Cancel(); try { worker.StandardInput.Close(); } catch { }
        try { if (!worker.HasExited) worker.Kill(true); } catch { }
    }
    public async ValueTask DisposeAsync()
    {
        Stop(); if (heartbeat is not null) await heartbeat;
        try { await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        worker.Dispose();if(bootstrapCore is not null){await bootstrapCore.DisposeAsync();bootstrapCore=null;} stopped.Dispose(); control.Dispose();
    }
}
