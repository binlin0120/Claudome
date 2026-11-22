using System.Text.Json;

namespace ClaudeSetup;

public enum CheckStatus { NotTested, Passed, Failed, Unverified }
public sealed record ProtectionCheck(CheckStatus Status = CheckStatus.NotTested, DateTime? CheckedUtc = null, string Evidence = "");
public sealed class ProtectionEvidence
{
    public string ConfigHash { get; set; } = "";
    public string BootUtc { get; set; } = "";
    public ProtectionCheck Boundary { get; set; } = new();
    public ProtectionCheck Exit { get; set; } = new();
    public ProtectionCheck Browser { get; set; } = new();
    public ProtectionCheck Dns { get; set; } = new();
    public string BlockAuditNote { get; set; } = "";
    public ProtectionCheck Outage { get; set; } = new();

    internal string[] PendingChecks(SessionState state)
    {
        var pending = new List<string>();
        if (!state.Completed.Contains("software")) pending.Add("软件安装");
        var current = ConfigHash.Length > 0 && ConfigHash == state.CoreConfigSha256;
        if (!current || Boundary.Status != CheckStatus.Passed) pending.Add("外层网关与来宾防火墙");
        if (!current || !state.NetworkPassed || Exit.Status != CheckStatus.Passed) pending.Add("来宾出口");
        if (!current || Browser.Status != CheckStatus.Passed) pending.Add("AdsPower 窗口出口");
        if (!current || Dns.Status != CheckStatus.Passed) pending.Add("DNS 与直连旁路");
        return [.. pending];
    }

    internal static bool IsExitMismatch(string? code) => code is "MISMATCH" or "IP_MISMATCH" or "COUNTRY_MISMATCH";

    internal static ProtectionCheck GuestExit(JsonElement report, DateTime checkedAt)
    {
        if (report.GetProperty("networkPassed").GetBoolean())
            return new(CheckStatus.Passed, checkedAt, "来宾 HTTPS 与预期出口匹配");
        var code = report.TryGetProperty("exitCode", out var value) ? value.GetString() : null;
        return code switch
        {
            "IP_MISMATCH" => new(CheckStatus.Failed, checkedAt, "实测公网 IP 与预期固定 IPv4 不一致"),
            "COUNTRY_MISMATCH" => new(CheckStatus.Failed, checkedAt, "实测出口国家或地区与任务设置不一致"),
            "MISMATCH" => new(CheckStatus.Failed, checkedAt, "实测出口与任务设置不一致"),
            _ => new(CheckStatus.Unverified, checkedAt, "未取得有效的来宾出口结果")
        };
    }
}
