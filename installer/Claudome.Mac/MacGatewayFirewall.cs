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
    private string lastAppliedInterface = MacDeployment.DefaultMacBridgeInterface;

    internal static string AnchorName(string workspaceId)
    {
        var sanitized = Regex.Replace(workspaceId ?? "", "[^a-zA-Z0-9]", "");
        if (sanitized.Length < 8) throw new SetupException("任务标识无效，无法生成 pf 防火墙锚点。");
        return "claudome_" + sanitized[..Math.Min(16, sanitized.Length)].ToLowerInvariant();
    }

    internal static string NormalizeLines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    internal static string BuildMainRulesetWithAnchor(string existingMainFilterRules, string anchor)
    {
        var preamble = new List<string>();
        var filters = new List<string>();
        foreach (var raw in NormalizeLines(existingMainFilterRules).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsAnchorLine(raw, anchor)) continue;
            if (Regex.IsMatch(raw, @"^(?:set|table|scrub|altq|queue|dummynet|nat|rdr|binat)\b", RegexOptions.IgnoreCase))
                preamble.Add(raw);
            else
                filters.Add(raw);
        }
        var combined = new List<string>(preamble) { $"anchor \"{anchor}\"" };
        combined.AddRange(filters);
        return string.Join("\n", combined) + "\n";
    }

    internal static string RemoveAnchorFromMainRuleset(string existingMainFilterRules, string anchor)
    {
        var lines = NormalizeLines(existingMainFilterRules)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !IsAnchorLine(l, anchor))
            .ToArray();
        return lines.Length == 0 ? "" : string.Join("\n", lines) + "\n";
    }

    private static bool IsAnchorLine(string line, string anchor) =>
        Regex.IsMatch(line.Trim(), $@"^anchor\s+""?{Regex.Escape(anchor)}""?(?:\s|$)", RegexOptions.IgnoreCase);

    /// <summary>Detects an active Fusion/UTM NAT bridge interface on macOS hosts, falling back to bridge100.</summary>
    public async Task<string> DetectGuestBridgeInterfaceAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) return MacDeployment.DefaultMacBridgeInterface;
        try
        {
            var output = await commandRunner.Run("ifconfig", ["-l"], token, 10);
            var interfaces = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var preferred in new[] { "bridge100", "vmnet8", "bridge101" })
            {
                if (interfaces.Contains(preferred, StringComparer.Ordinal)) return preferred;
            }
        }
        catch { }
        return MacDeployment.DefaultMacBridgeInterface;
    }

    /// <summary>Builds fail-closed pf anchor rules scoping the VM bridge interface strictly to the host gateway and verified proxy endpoints.</summary>
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
        sb.AppendLine($"pass in quick on {guestInterface} proto udp from any to any port 67");
        sb.AppendLine($"pass in quick on {guestInterface} proto tcp from any to 1.1.1.1 port 443");
        foreach (var endpoint in validatedEndpoints)
        {
            sb.AppendLine($"pass in quick on {guestInterface} proto tcp from any to {endpoint} port {endpointPort}");
            sb.AppendLine($"pass in quick on {guestInterface} proto udp from any to {endpoint} port {endpointPort}");
        }
        sb.AppendLine($"block drop quick on {guestInterface} all");
        foreach (var endpoint in validatedEndpoints)
        {
            sb.AppendLine($"pass out quick proto tcp from any to {endpoint} port {endpointPort}");
            sb.AppendLine($"pass out quick proto udp from any to {endpoint} port {endpointPort}");
        }
        return sb.ToString();
    }

    internal static void VerifyPfEnabled(string pfInfoOutput)
    {
        if (!Regex.IsMatch(NormalizeLines(pfInfoOutput), @"^\s*Status:\s*Enabled\b", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            throw new SetupException("macOS Packet Filter (pf) 当前未处于启用状态（Status: Enabled），已阻止启动虚拟机。");
    }

    internal static void VerifyMainRulesetHasAnchor(string pfMainOutput, string anchor)
    {
        var attached = NormalizeLines(pfMainOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => IsAnchorLine(line, anchor));
        if (!attached)
            throw new SetupException($"macOS PF 主规则集未挂载本任务锚点（{anchor}），已阻止启动虚拟机。");
    }

    internal static void VerifyAnchorReadback(string pfctlOutput, string guestInterface, string gatewayBindIp, int gatewayPort, string[] endpointIps, int endpointPort)
    {
        var normalized = NormalizeLines(pfctlOutput);
        var validated = IsolationPolicy.ValidateEndpoints(endpointIps);
        var hasBlockAll = Regex.IsMatch(normalized, $@"block drop quick on {Regex.Escape(guestInterface)}\b.*\ball\b", RegexOptions.IgnoreCase);
        var hasGatewayUdp = Regex.IsMatch(normalized,
            $@"pass in quick on {Regex.Escape(guestInterface)}\b.*\bproto udp\b.*\bto {Regex.Escape(gatewayBindIp)} port (?:= )?{gatewayPort}\b",
            RegexOptions.IgnoreCase);
        var hasEndpoint = validated.Any(ip =>
            Regex.IsMatch(normalized, $@"\bto {Regex.Escape(ip)} port (?:= )?{endpointPort}\b", RegexOptions.IgnoreCase));
        if (!hasBlockAll || !hasGatewayUdp || !hasEndpoint)
            throw new SetupException("macOS pfctl 防火墙规则回读不匹配，已阻止启动虚拟机。");
    }

    internal static void VerifyCombinedReadback(string combinedOutput, string anchor, string guestInterface, string gatewayBindIp, int gatewayPort, string[] endpointIps, int endpointPort)
    {
        var normalized = NormalizeLines(combinedOutput);
        const string infoMarker = "===PF_INFO===";
        const string mainMarker = "===PF_MAIN===";
        const string anchorMarker = "===PF_ANCHOR===";
        var infoIdx = normalized.IndexOf(infoMarker, StringComparison.Ordinal);
        var mainIdx = normalized.IndexOf(mainMarker, StringComparison.Ordinal);
        var anchorIdx = normalized.IndexOf(anchorMarker, StringComparison.Ordinal);
        if (infoIdx < 0 || mainIdx <= infoIdx || anchorIdx <= mainIdx)
            throw new SetupException("未能完整回读 macOS PF 启用状态、主规则集与锚点规则。");

        var infoSection = normalized[(infoIdx + infoMarker.Length)..mainIdx];
        var mainSection = normalized[(mainIdx + mainMarker.Length)..anchorIdx];
        var anchorSection = normalized[(anchorIdx + anchorMarker.Length)..];
        VerifyPfEnabled(infoSection);
        VerifyMainRulesetHasAnchor(mainSection, anchor);
        VerifyAnchorReadback(anchorSection, guestInterface, gatewayBindIp, gatewayPort, endpointIps, endpointPort);
    }

    public Task ApplyAsync(string workspaceId, string guestInterface, string[] endpointIps, int endpointPort, int gatewayPort, CancellationToken token) =>
        ApplyCoreAsync(workspaceId, guestInterface, endpointIps, endpointPort, gatewayPort, token, isMacOverride: null);

    internal async Task ApplyCoreAsync(string workspaceId, string guestInterface, string[] endpointIps, int endpointPort, int gatewayPort, CancellationToken token, bool? isMacOverride)
    {
        lastAppliedInterface = guestInterface;
        if (!(isMacOverride ?? OperatingSystem.IsMacOS())) return;
        var anchor = AnchorName(workspaceId);
        var rules = BuildAnchorRules(guestInterface, "127.0.0.1", gatewayPort, endpointIps, endpointPort);
        var tempFile = Path.Combine(Path.GetTempPath(), $"claudome-pf-{Guid.NewGuid():N}.conf");
        var origMainFile = Path.Combine(Path.GetTempPath(), $"claudome-pf-orig-{Guid.NewGuid():N}.conf");
        var origAnchorFile = Path.Combine(Path.GetTempPath(), $"claudome-pf-orig-anchor-{Guid.NewGuid():N}.conf");
        var mainTempFile = Path.Combine(Path.GetTempPath(), $"claudome-pf-main-{Guid.NewGuid():N}.conf");
        var helperSh = Path.Combine(Path.GetTempPath(), $"claudome-pf-apply-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(tempFile, rules, new UTF8Encoding(false), token);
        var shellBody = $$"""
            #!/bin/sh
            set -eu
            if ! pfctl -sr > '{{origMainFile}}' 2>/dev/null; then
              echo 'PF_READ_MAIN_FAILED' >&2
              exit 21
            fi
            had_orig_anchor=0
            if grep -qE '^anchor[[:space:]]+"?{{anchor}}"?([[:space:]]|$)' '{{origMainFile}}'; then
              had_orig_anchor=1
            fi
            if [ "$had_orig_anchor" -eq 1 ]; then
              if ! pfctl -a '{{anchor}}' -sr > '{{origAnchorFile}}' 2>/dev/null; then
                echo 'PF_READ_ANCHOR_FAILED' >&2
                exit 28
              fi
            else
              pfctl -a '{{anchor}}' -sr > '{{origAnchorFile}}' 2>/dev/null || : > '{{origAnchorFile}}'
            fi
            pf_enabled_by_us=0
            rollback_apply() {
              rb_ok=1
              if [ "$pf_enabled_by_us" -eq 1 ]; then
                pfctl -d >/dev/null 2>&1 || rb_ok=0
              fi
              if [ -s '{{origAnchorFile}}' ]; then
                pfctl -a '{{anchor}}' -f '{{origAnchorFile}}' >/dev/null 2>&1 || rb_ok=0
              else
                pfctl -a '{{anchor}}' -F all >/dev/null 2>&1 || rb_ok=0
              fi
              pfctl -Rf '{{origMainFile}}' >/dev/null 2>&1 || rb_ok=0
              if [ "$rb_ok" -eq 1 ]; then
                echo 'PF_APPLY_ROLLED_BACK' >&2
              else
                echo 'PF_APPLY_ROLLBACK_FAILED' >&2
              fi
            }
            {
              grep -E '^(set|table|scrub|altq|queue|dummynet|nat|rdr|binat)\b' '{{origMainFile}}' || true
              printf 'anchor "{{anchor}}"\n'
              grep -v -E '^(set|table|scrub|altq|queue|dummynet|nat|rdr|binat)\b' '{{origMainFile}}' | grep -v -E '^anchor[[:space:]]+"?{{anchor}}"?([[:space:]]|$)' || true
            } | sed '/^$/d' > '{{mainTempFile}}'
            if ! pfctl -a '{{anchor}}' -f '{{tempFile}}'; then
              rollback_apply
              exit 22
            fi
            if ! pfctl -Rf '{{mainTempFile}}'; then
              rollback_apply
              exit 23
            fi
            info_before="$(pfctl -s info 2>/dev/null || true)"
            if ! printf '%s\n' "$info_before" | grep -qiE '^[[:space:]]*Status:[[:space:]]*Enabled'; then
              if ! pfctl -e; then
                rollback_apply
                exit 24
              fi
              pf_enabled_by_us=1
            fi
            info_out="$(pfctl -s info)" || { rollback_apply; exit 27; }
            main_out="$(pfctl -sr)" || { rollback_apply; exit 27; }
            anchor_out="$(pfctl -a '{{anchor}}' -sr)" || { rollback_apply; exit 27; }
            printf '===PF_PRE_STATE===\npf_enabled_by_us=%s\nhad_orig_anchor=%s\n' "$pf_enabled_by_us" "$had_orig_anchor"
            printf '===PF_INFO===\n%s\n===PF_MAIN===\n%s\n===PF_ANCHOR===\n%s\n' "$info_out" "$main_out" "$anchor_out"
            """;
        await File.WriteAllTextAsync(helperSh, shellBody.Replace("\r\n", "\n"), new UTF8Encoding(false), token);
        try
        {
            var readback = await commandRunner.Run("osascript", ["-e", $"do shell script \"/bin/sh '{helperSh}'\" with administrator privileges without altering line endings"], token, 60);
            try
            {
                VerifyCombinedReadback(readback, anchor, guestInterface, "127.0.0.1", gatewayPort, endpointIps, endpointPort);
            }
            catch (Exception verifyEx)
            {
                var (pfEnabledByUs, hadOrigAnchor) = ParsePreState(readback);
                try
                {
                    await RestorePreApplySnapshotAsync(origMainFile, origAnchorFile, anchor, hadOrigAnchor, pfEnabledByUs, token, isMacOverride);
                }
                catch (Exception restoreEx)
                {
                    throw new SetupException($"macOS PF 防火墙回读校验失败（{verifyEx.Message}），且按应用前快照恢复失败（{restoreEx.Message}）。");
                }
                throw new SetupException($"macOS PF 防火墙回读校验失败（已按应用前快照恢复原规则与启用状态）：{verifyEx.Message}");
            }
        }
        catch (SetupException) { throw; }
        catch (Exception ex)
        {
            throw new SetupException($"macOS PF 防火墙应用失败（未能完成激活或回读）：{ex.Message}");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(origMainFile)) File.Delete(origMainFile);
            if (File.Exists(origAnchorFile)) File.Delete(origAnchorFile);
            if (File.Exists(mainTempFile)) File.Delete(mainTempFile);
            if (File.Exists(helperSh)) File.Delete(helperSh);
        }
    }

    internal static (bool pfEnabledByUs, bool hadOrigAnchor) ParsePreState(string readback)
    {
        var normalized = NormalizeLines(readback);
        var pfEnabled = Regex.IsMatch(normalized, @"(?m)^pf_enabled_by_us=1$");
        var hadAnchor = Regex.IsMatch(normalized, @"(?m)^had_orig_anchor=1$");
        return (pfEnabled, hadAnchor);
    }

    internal async Task RestorePreApplySnapshotAsync(
        string origMainFile,
        string origAnchorFile,
        string anchor,
        bool hadOrigAnchor,
        bool pfEnabledByUs,
        CancellationToken token,
        bool? isMacOverride)
    {
        if (!(isMacOverride ?? OperatingSystem.IsMacOS())) return;
        if (!File.Exists(origMainFile))
            throw new SetupException("缺少应用前 PF 主规则快照，无法验证并恢复原状态。");
        var restoreSh = Path.Combine(Path.GetTempPath(), $"claudome-pf-restore-{Guid.NewGuid():N}.sh");
        var hadAnchorInt = hadOrigAnchor ? 1 : 0;
        var enabledByUsInt = pfEnabledByUs ? 1 : 0;
        var script = $$"""
            #!/bin/sh
            set -eu
            if [ "{{hadAnchorInt}}" -eq 1 ] || [ -s '{{origAnchorFile}}' ]; then
              pfctl -a '{{anchor}}' -f '{{origAnchorFile}}'
            else
              pfctl -a '{{anchor}}' -F all
            fi
            pfctl -Rf '{{origMainFile}}'
            if [ "{{enabledByUsInt}}" -eq 1 ]; then
              if ! pfctl -sr 2>/dev/null | grep -qE '^anchor[[:space:]]+"?claudome-' ; then
                info_now="$(pfctl -s info 2>/dev/null || true)"
                if printf '%s\n' "$info_now" | grep -qiE '^[[:space:]]*Status:[[:space:]]*Enabled'; then
                  pfctl -d
                fi
              else
                echo 'PF_RESTORE_KEPT_ENABLED_OTHER_ANCHORS' >&2
              fi
            fi
            echo '===PF_RESTORE_INFO==='
            pfctl -s info
            echo '===PF_RESTORE_MAIN==='
            pfctl -sr
            """;
        await File.WriteAllTextAsync(restoreSh, script.Replace("\r\n", "\n"), new UTF8Encoding(false), token);
        try
        {
            await commandRunner.Run("osascript", ["-e", $"do shell script \"/bin/sh '{restoreSh}'\" with administrator privileges without altering line endings"], token, 30);
        }
        finally
        {
            if (File.Exists(restoreSh)) File.Delete(restoreSh);
        }
    }

    public Task VerifyAsync(string workspaceId, string[] endpointIps, int endpointPort, int gatewayPort, CancellationToken token) =>
        VerifyReadbackAsync(workspaceId, lastAppliedInterface, endpointIps, endpointPort, gatewayPort, token);

    public Task VerifyReadbackAsync(string workspaceId, string guestInterface, string[] endpointIps, int endpointPort, int gatewayPort, CancellationToken token) =>
        VerifyReadbackCoreAsync(workspaceId, guestInterface, endpointIps, endpointPort, gatewayPort, token, isMacOverride: null);

    internal async Task VerifyReadbackCoreAsync(string workspaceId, string guestInterface, string[] endpointIps, int endpointPort, int gatewayPort, CancellationToken token, bool? isMacOverride)
    {
        if (!(isMacOverride ?? OperatingSystem.IsMacOS())) return;
        var anchor = AnchorName(workspaceId);
        var script = $"echo '===PF_INFO===' && pfctl -s info && echo '===PF_MAIN===' && pfctl -sr && echo '===PF_ANCHOR===' && pfctl -a '{anchor}' -sr";
        var output = await commandRunner.Run("osascript", ["-e", $"do shell script \"{script}\" with administrator privileges without altering line endings"], token, 30);
        VerifyCombinedReadback(output, anchor, guestInterface, "127.0.0.1", gatewayPort, endpointIps, endpointPort);
    }

    public Task ClearAsync(string workspaceId, CancellationToken token) =>
        ClearCoreAsync(workspaceId, token, isMacOverride: null);

    internal async Task ClearCoreAsync(string workspaceId, CancellationToken token, bool? isMacOverride)
    {
        if (!(isMacOverride ?? OperatingSystem.IsMacOS())) return;
        var anchor = AnchorName(workspaceId);
        var origMainFile = Path.Combine(Path.GetTempPath(), $"claudome-pf-clear-orig-{Guid.NewGuid():N}.conf");
        var mainTempFile = Path.Combine(Path.GetTempPath(), $"claudome-pf-clear-{Guid.NewGuid():N}.conf");
        var helperSh = Path.Combine(Path.GetTempPath(), $"claudome-pf-clear-{Guid.NewGuid():N}.sh");
        var shellBody = $$"""
            #!/bin/sh
            set -eu
            if ! pfctl -sr > '{{origMainFile}}' 2>/dev/null; then
              echo 'PF_READ_MAIN_FAILED' >&2
              exit 21
            fi
            grep -v -E '^anchor[[:space:]]+"?{{anchor}}"?([[:space:]]|$)' '{{origMainFile}}' | sed '/^$/d' > '{{mainTempFile}}' || true
            if ! pfctl -Rf '{{mainTempFile}}'; then
              echo 'PF_CLEAR_MAIN_FAILED_KEPT_ANCHOR' >&2
              exit 25
            fi
            if ! pfctl -a '{{anchor}}' -F all; then
              if pfctl -Rf '{{origMainFile}}' >/dev/null 2>&1; then
                echo 'PF_CLEAR_ANCHOR_FAILED_RESTORED_MAIN' >&2
              else
                echo 'PF_CLEAR_ROLLBACK_FAILED' >&2
              fi
              exit 26
            fi
            """;
        await File.WriteAllTextAsync(helperSh, shellBody.Replace("\r\n", "\n"), new UTF8Encoding(false), token);
        try
        {
            await commandRunner.Run("osascript", ["-e", $"do shell script \"/bin/sh '{helperSh}'\" with administrator privileges without altering line endings"], token, 30);
        }
        catch (SetupException) { throw; }
        catch (Exception ex)
        {
            throw new SetupException($"macOS PF 防火墙撤回失败（原有防护或恢复状态已报错）：{ex.Message}");
        }
        finally
        {
            if (File.Exists(origMainFile)) File.Delete(origMainFile);
            if (File.Exists(mainTempFile)) File.Delete(mainTempFile);
            if (File.Exists(helperSh)) File.Delete(helperSh);
        }
    }
}
