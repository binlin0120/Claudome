using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

/// <summary>
/// Manages a scoped macOS Packet Filter (pfctl) anchor so guest bridge traffic can reach
/// only the host Claudome gateway port and cannot leak directly out of the Mac host.
/// </summary>
public sealed class MacGatewayFirewall(ICommandRunner? runner = null) : IHostFirewall
{
    private readonly ICommandRunner commandRunner = runner ?? new CommandRunner();

    internal static string AnchorName(string workspaceId)
    {
        var sanitized = Regex.Replace(workspaceId ?? "", "[^a-zA-Z0-9]", "");
        if (sanitized.Length < 8) throw new SetupException("任务标识无效，无法生成 pf 防火墙锚点。");
        return "claudome_" + sanitized[..Math.Min(16, sanitized.Length)].ToLowerInvariant();
    }

    /// <summary>Builds fail-closed pf anchor rules scoping the VM bridge interface strictly to the host gateway.</summary>
    internal static string BuildAnchorRules(string guestInterface, string gatewayBindIp, int gatewayPort, string[] endpointIps, int endpointPort)
    {
        if (!Regex.IsMatch(guestInterface, "^[a-zA-Z0-9._-]{2,24}$"))
            throw new SetupException("虚拟机桥接网卡名称无效。");
        if (!IPAddress.TryParse(gatewayBindIp, out var bindIp) || bindIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new SetupException("宿主网关监听 IP 无效。");
        if (gatewayPort is < 1024 or > 65535 || endpointPort is < 1 or > 65535)
            throw new SetupException("网关或节点端口范围无效。");
        var validatedEndpoints = IsolationPolicy.ValidateEndpoints(endpointIps);

        var sb = new StringBuilder();
        sb.AppendLine($"# Claudome scoped macOS pf anchor ({DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ})");
        sb.AppendLine($"pass in quick on {guestInterface} proto udp from any to {bindIp} port {gatewayPort}");
        sb.AppendLine($"pass in quick on {guestInterface} proto tcp from any to {bindIp} port {gatewayPort}");
        sb.AppendLine($"block drop quick on {guestInterface} all");
        foreach (var endpoint in validatedEndpoints)
            sb.AppendLine($"pass out quick proto tcp from any to {endpoint} port {endpointPort} user root");
        return sb.ToString();
    }

    internal static void VerifyAnchorReadback(string pfctlOutput, string guestInterface, string gatewayBindIp, int gatewayPort, string[] endpointIps, int endpointPort)
    {
        var validated = IsolationPolicy.ValidateEndpoints(endpointIps);
        var hasBlockAll = Regex.IsMatch(pfctlOutput, $@"block drop quick on {Regex.Escape(guestInterface)}\b.*\ball\b", RegexOptions.IgnoreCase);
        var hasGatewayUdp = Regex.IsMatch(pfctlOutput,
            $@"pass in quick on {Regex.Escape(guestInterface)}\b.*\bproto udp\b.*\bto {Regex.Escape(gatewayBindIp)} port (?:= )?{gatewayPort}\b",
            RegexOptions.IgnoreCase);
        var hasEndpoint = validated.Any(ip =>
            Regex.IsMatch(pfctlOutput, $@"\bto {Regex.Escape(ip)} port (?:= )?{endpointPort}\b", RegexOptions.IgnoreCase));
        if (!hasBlockAll || !hasGatewayUdp || !hasEndpoint)
            throw new SetupException("macOS pfctl 防火墙规则回读不匹配，已阻止启动虚拟机。");
    }

    public async Task ApplyAsync(string workspaceId, string guestInterface, string[] endpointIps, int endpointPort, int gatewayPort, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var anchor = AnchorName(workspaceId);
        var rules = BuildAnchorRules(guestInterface, "127.0.0.1", gatewayPort, endpointIps, endpointPort);
        var tempFile = Path.Combine(Path.GetTempPath(), $"claudome-pf-{Guid.NewGuid():N}.conf");
        await File.WriteAllTextAsync(tempFile, rules, new UTF8Encoding(false), token);
        try
        {
            await commandRunner.Run("osascript", ["-e", $"do shell script \"pfctl -a {anchor} -f '{tempFile}' && pfctl -e || true\" with administrator privileges"], token, 60);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
        await VerifyAsync(workspaceId, endpointIps, endpointPort, gatewayPort, token);
    }

    public async Task VerifyAsync(string workspaceId, string[] endpointIps, int endpointPort, int gatewayPort, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var anchor = AnchorName(workspaceId);
        var output = await commandRunner.Run("pfctl", ["-a", anchor, "-sr"], token, 15);
        VerifyAnchorReadback(output, "bridge100", "127.0.0.1", gatewayPort, endpointIps, endpointPort);
    }

    public async Task ClearAsync(string workspaceId, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var anchor = AnchorName(workspaceId);
        await commandRunner.Run("pfctl", ["-a", anchor, "-F", "all"], token, 15);
    }
}
