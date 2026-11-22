using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed record Resource(string Id, string Name, string FileName, string Url, string Sha256, long Size, string License);
public sealed record DownloadProgress(string Name, long Received, long? Total, double BytesPerSecond, bool FromCache = false)
{
    public string Display => FromCache ? $"{Name}  ·  已核验缓存 {Received / 1048576d:F1} MB" : $"{Name}  ·  {Received / 1048576d:F1} MB" + (Total is > 0 ? $" / {Total / 1048576d:F1} MB" : "") + $"  ·  {BytesPerSecond / 1048576d:F1} MB/s";
}
public sealed class SetupOptions
{
    public string Root { get; set; } = Directory.Exists("D:\\") ? "D:\\ClaudeWorkspace" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeWorkspace");
    public string VmName { get; set; } = "Claude-Workspace";
    public int MemoryMb { get; set; } = 4096;
    public int CpuCount { get; set; } = 2;
    public bool ManualCpuConfirmed { get; set; }
    public static int RecommendedCpuCount(string platformId, bool? hostNemOverride = null) =>
        platformId == "virtualbox" && (hostNemOverride ?? HostPlatform.Environment.IsHostHypervisorActive) ? 1 : 2;
    /// <summary>Returns a user-facing warning when a VirtualBox multi-vCPU configuration runs under Windows NEM.</summary>
    public static string? MultiCoreNemWarning(string platformId, int cpuCount, bool? hostNemOverride = null, string? hostEditionOverride = null)
    {
        if (platformId != "virtualbox" || cpuCount < 2 || !(hostNemOverride ?? HostPlatform.Environment.IsHostHypervisorActive)) return null;
        var edition = hostEditionOverride ?? HostPlatform.Environment.HostEdition;
        var homeHint = HostPlatform.Environment.IsHomeEdition(edition) ? "，且当前 Windows 家庭版不支持原生 Hyper-V 路线" : "";
        return $"注意：当前电脑启用了 Windows 虚拟化安全（Hyper-V 兼容模式 / NEM）。VirtualBox 在 {cpuCount} 核下大概率在启动阶段白屏（0x0）死锁{homeHint}。建议保持 1 核；若确需多核，需先关闭宿主机“内核隔离 / 内存完整性 / VBS”或改用 VMware Workstation。";
    }
    public int DiskGb { get; set; } = 80;
    public string IsoPath { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string IsoUrl { get; set; } = "";
    public string IsoSha256 { get; set; } = "";
    public int WindowsImageIndex { get; set; } = 1;
    public string UserName { get; set; } = "ClaudeUser";
    public string GuestLocalAppData { get; set; } = "";
    public bool UsesMicrosoftGuestAccount { get; set; }
    internal bool HasUsableGuestPassword(string? value) =>
        value is not null && value.Length >= (UsesMicrosoftGuestAccount ? 1 : 10) &&
        value.IndexOfAny(['\0', '\r', '\n']) < 0;
    internal static bool IsMicrosoftGuestAddress(string? value) =>
        value is not null && value.Length <= 254 &&
        Regex.IsMatch(value, @"\A[a-zA-Z0-9][a-zA-Z0-9._+!-]*@[a-zA-Z0-9-]+(?:\.[a-zA-Z0-9-]+)+\z");
    internal string GuestSetupDirectory =>
        (PlatformId == "virtualbox" && IsGuestLocalAppData(GuestLocalAppData)
            ? GuestLocalAppData
            : @"C:\Users\" + UserName + @"\AppData\Local") + @"\ClaudeEnvironmentSetup";
    internal static bool IsGuestLocalAppData(string? value)
    {
        if (value is null || !Regex.IsMatch(value,
            @"\AC:\\Users\\[\p{L}\p{N}_][\p{L}\p{M}\p{N}_. -]{0,62}\\AppData\\Local\z",
            RegexOptions.IgnoreCase)) return false;
        var folder = value.Split('\\')[2];
        return !folder.EndsWith(' ') && !folder.EndsWith('.') &&
            !folder.Equals("TEMP", StringComparison.OrdinalIgnoreCase) &&
            !folder.StartsWith("TEMP.", StringComparison.OrdinalIgnoreCase);
    }
    public string NetworkSource { get; set; } = "稍后配置";
    public string NodeName { get; set; } = "";
    public bool ChainEnabled { get; set; }
    public bool ChainFromImport { get; set; }
    public string UpstreamSource { get; set; } = "节点链接 / 本地配置";
    public string UpstreamName { get; set; } = "";
    public string Country { get; set; } = "US";
    public string TimeZoneId { get; set; } = "Pacific Standard Time";
    public bool Remember { get; set; }
    public bool AcceptChanges { get; set; }
    public bool AcceptWindowsLicense { get; set; }
    public bool TestOutage { get; set; }
    public bool ManualAdsPower { get; set; }
    public string WorkspaceId { get; set; } = Guid.NewGuid().ToString("N");
    public string PlatformId { get; set; } = "virtualbox";
    public bool VmwareSecurityConfirmed { get; set; }
    public bool HyperVBasicConsoleConfirmed { get; set; }

    public string StatePath => Path.Combine(Root, "setup-state.json");
    public string Cache => Path.Combine(Root, "downloads");
    public string Private => Path.Combine(Root, "private");
    public string VmDirectory => Path.Combine(Root, "machines", VmName);
    public void Validate(bool requireIso)
    {
        Paths.ValidateRoot(Root);
        if (!Regex.IsMatch(VmName, "^[a-zA-Z0-9_-]{1,50}$")) throw new SetupException("虚拟机名称请使用 1–50 位英文字母、数字、短横线或下划线。");
        var userPattern = requireIso ? "^[a-zA-Z][a-zA-Z0-9_-]{0,19}$" : @"^[\p{L}\p{N}](?:[\p{L}\p{N}_.-]| [\p{L}\p{N}_.-]){0,19}$";
        if (!Regex.IsMatch(UserName, userPattern) || UserName.Contains("..", StringComparison.Ordinal) || new[] { "administrator", "guest", "defaultaccount", "system", "wdagutilityaccount" }.Contains(UserName.ToLowerInvariant())) throw new SetupException("请填写有效的 Windows 用户名（长度不超过 20 位，不可使用系统保留账号）。");
        if (UsesMicrosoftGuestAccount && (PlatformId != "virtualbox" || requireIso || !IsGuestLocalAppData(GuestLocalAppData)))
            throw new SetupException("微软账号同步仅适用于已安装的 VirtualBox 来宾，请在原虚拟机桌面重新同步。");
        if (!string.IsNullOrEmpty(GuestLocalAppData) && (PlatformId != "virtualbox" || !IsGuestLocalAppData(GuestLocalAppData)))
            throw new SetupException("已保存的来宾配置目录无效，请重新同步账号。");
        var minCpu = PlatformId == "virtualbox" ? 1 : 2;
        if (MemoryMb < 4096 || MemoryMb > 32768 || CpuCount < minCpu || CpuCount > Environment.ProcessorCount || DiskGb < 80 || DiskGb > 1024) throw new SetupException($"请核对内存、CPU 和磁盘分配。要求至少 4 GB 来宾内存、{minCpu} 个 CPU、80 GB 磁盘。");
        if (WindowsImageIndex < 1 || WindowsImageIndex > 20) throw new SetupException("Windows 镜像索引应为 1–20。");
        if (requireIso) HostPlatform.Environment.RequireWindowsIsoFile(IsoPath);
        if (!string.IsNullOrWhiteSpace(IsoSha256) && !Regex.IsMatch(IsoSha256, "^[a-fA-F0-9]{64}$")) throw new SetupException("SHA-256 应为 64 位十六进制字符。");
        if (!Regex.IsMatch(Country, "^[A-Z]{2}$")) throw new SetupException("请选择节点国家或地区。");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }
}
public sealed record SavedRoute(string NodeJson, string UpstreamJson, string Country, string TimeZone);
public sealed class Secrets
{
    public string VmPassword { get; set; } = "";
    public string VmMicrosoftAddress { get; set; } = "";
    public string SubscriptionUrl { get; set; } = "";
    public string NetworkText { get; set; } = "";
    public string SelectedNodeJson { get; set; } = "";
    public string UpstreamNodeJson { get; set; } = "";
    public string UpstreamSubscriptionUrl { get; set; } = "";
    public string UpstreamNetworkText { get; set; } = "";
    public string ExpectedExitIp { get; set; } = "";
    public string AdsPowerApiKey { get; set; } = "";
    public List<SavedRoute> BackupRoutes { get; set; } = [];
}
public sealed class SessionState
{
    internal bool AwaitingGuestTools { get; set; }
    public int Schema { get; set; } = 1;
    public SetupOptions Options { get; set; } = new();
    public string VmId { get; set; } = "";
    public string Stage { get; set; } = "尚未开始";
    public string Status { get; set; } = "待配置";
    public List<string> Completed { get; set; } = [];
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public bool NetworkPassed { get; set; }
    public bool OutagePassed { get; set; }
    public bool ApplicationAccepted { get; set; }
    public bool SetupFinished { get; set; }
    public string CredentialsRevision { get; set; } = "";
    public ProtectionEvidence Protection { get; set; } = new();
    internal bool DeferCheckpoint { get; set; }
    public bool ClashConfigured { get; set; }
    public bool BrowserProfileConfigured { get; set; }
    public string CoreConfigSha256 { get; set; } = "";
    public string GuestFilesStamp { get; set; } = "";
    public string GuestSettingsStamp { get; set; } = "";
    public string RestartBootBefore { get; set; } = "";
    public bool BrowserRuntimePassed { get; set; }
    public bool BrowserReopenPassed { get; set; }
    public bool TranslationPassed { get; set; }
    public bool RestartPassed { get; set; }
    public List<UserAction> PendingActions { get; set; } = [];
    public void RequireResumeIdentity(SetupOptions requested)
    {
        if (!Path.GetFullPath(Options.Root).TrimEnd('\\').Equals(Path.GetFullPath(requested.Root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            Options.WorkspaceId != requested.WorkspaceId)
            throw new SetupException("所选目录属于另一个安装任务，请先载入该目录中的 setup-state.json；不会混用任务身份或资料。");
    }
    public void Save()
    {
        if (DeferCheckpoint) return;
        UpdatedUtc = DateTime.UtcNow;
        TaskCheckpointStore.Write(this);
    }
}
public sealed record UserAction(string Title, string[] Steps, string Id);
public class SetupException(string message) : Exception(message);
public sealed class HandoffException(UserAction action) : SetupException(action.Title + "。请按右侧操作指引完成后继续。")
{
    public UserAction Action { get; } = action;
}

public static class JsonFile
{
    public static readonly JsonSerializerOptions Format = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Paths.RejectLinks(path);
        var temporary = path + ".new";
        Paths.RejectLinks(temporary);
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Format), new System.Text.UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
    public static T Read<T>(string path) => typeof(T)==typeof(SessionState)?(T)(object)TaskCheckpointStore.Read(path):JsonSerializer.Deserialize<T>(File.ReadAllText(path), Format) ?? throw new SetupException("状态文件内容不完整。");
}
public static class Paths
{
    public static void EnsureWorkspace(string root)
    {
        ValidateRoot(root);
        var marker = Under(root, ".claude-setup-workspace.json");
        if (File.Exists(marker))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            if (!json.RootElement.TryGetProperty("application", out var app) || app.GetString() != "claude-environment-setup") throw new SetupException("该目录不是此安装器的任务目录。");
            return;
        }
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new SetupException("所选目录已有其他资料，请选择一个空的独立子目录；原任务应通过状态文件载入。");
        Directory.CreateDirectory(root);
        JsonFile.Write(marker, new { application = "claude-environment-setup", schema = 1 });
    }
    public static string Under(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new SetupException("文件路径超出所选任务目录。");
        RejectLinks(path);
        return path;
    }
    public static void RejectLinks(string path)
    {
        for (var part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new SetupException("任务路径包含目录链接，请选择实际本地目录。");
    }
    public static void ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.StartsWith("\\\\")) throw new SetupException("请选择一个完整的本地安装目录。");
        var full = Path.GetFullPath(root).TrimEnd('\\');
        if (full.Length < 5 || Path.GetPathRoot(full)!.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase)) throw new SetupException("请使用独立子文件夹，不能直接使用磁盘根目录。");
        foreach (var system in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (!string.IsNullOrEmpty(system) && (full.Equals(system, StringComparison.OrdinalIgnoreCase) || full.StartsWith(system + "\\", StringComparison.OrdinalIgnoreCase))) throw new SetupException("数据目录不能放在 Windows 或 Program Files 中。");
        RejectLinks(full);
    }
}
