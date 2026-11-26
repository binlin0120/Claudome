using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeSetup;

public sealed record ResumePlan(bool CreateMachine,bool InstallWindows,string ResumeTitle)
{
    public static ResumePlan For(SessionState state)
    {
        if(state.Completed.Any(x=>new[]{"machine","windows-started","guest-ready","guest-files","software"}.Contains(x)) && !Guid.TryParse(state.VmId,out _))throw new SetupException("任务记录缺少原虚拟机身份，未重新创建虚拟机。请载入完整的原任务。");
        var create=string.IsNullOrEmpty(state.VmId);
        var install=!HasWindowsInstallStarted(state);
        var title=state.Completed.Contains("guest-ready")?"恢复任务：继续来宾配置与验收":state.Completed.Contains("windows-started")?"恢复任务：等待原 Windows 安装与来宾工具":"恢复任务：继续准备 Windows 安装";
        return new(create,install,title);
    }
    internal static bool HasWindowsInstallStarted(SessionState state) =>
        state.Completed.Contains("windows-started") || state.Completed.Contains("guest-ready");
    internal static bool HasVerifiedSystemDiskProof(SessionState state)
    {
        if (string.IsNullOrEmpty(state.VmId)) return false;
        if (state.Completed.Contains("guest-ready")) return true;
        if (!state.Completed.Contains("windows-started")) return false;
        if (state.Options.PlatformId != "hyperv") return true;
        try
        {
            var vmDir = state.Options.VmDirectory;
            if (string.IsNullOrWhiteSpace(vmDir) || !Directory.Exists(vmDir)) return false;
            var vmFull = Path.GetFullPath(vmDir).TrimEnd('\\') + '\\';
            var logPath = Path.Combine(vmDir, ".windows-install.json");
            if (File.Exists(logPath))
            {
                if (new FileInfo(logPath).Length > 65536) return false;
                using var doc = JsonDocument.Parse(File.ReadAllText(logPath));
                var root = doc.RootElement;
                if (!root.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1) return false;
                if (!root.TryGetProperty("vmId", out var vmIdEl) || !string.Equals(vmIdEl.GetString(), state.VmId, StringComparison.OrdinalIgnoreCase)) return false;
                if (!root.TryGetProperty("workspaceId", out var wsEl) || !string.Equals(wsEl.GetString(), state.Options.WorkspaceId, StringComparison.OrdinalIgnoreCase)) return false;
                if (!root.TryGetProperty("phase", out var phaseEl) || phaseEl.GetString() != "ready") return false;
                if (!root.TryGetProperty("diskPath", out var diskEl) || string.IsNullOrWhiteSpace(diskEl.GetString())) return false;
                var diskFull = Path.GetFullPath(diskEl.GetString()!);
                return diskFull.StartsWith(vmFull, StringComparison.OrdinalIgnoreCase) && File.Exists(diskFull);
            }
            var legacyApplied = Path.Combine(vmDir, ".windows-applied");
            var legacyDisk = Path.Combine(vmDir, "Windows11.vhdx");
            return File.Exists(legacyApplied) && File.ReadAllText(legacyApplied).Trim() == "applied" && File.Exists(legacyDisk);
        }
        catch
        {
            return false;
        }
    }
    internal static bool CanResumeBrowser(SessionState state, bool checkSavedNetwork = false) =>
        Guid.TryParse(state.VmId, out _) && state.Options.NetworkSource != "稍后配置" &&
        // This checkpoint selects the next phase. The guest must still verify the live network.
        state.ClashConfigured && !string.IsNullOrEmpty(state.GuestSettingsStamp) &&
        new[] { "machine", "guest-ready", "media-cleanup", "recovery-cleanup", "guest-files", "software" }.All(state.Completed.Contains) &&
        state.PendingActions.Any(action => (IsBrowserHandoff(action) && action.Id is not ("outage-validation" or "browser-reopen")) ||
            IsGuestNetworkProbe(action) || (checkSavedNetwork && action.Id == "resume-network")) &&
        state.PendingActions.All(action => IsBrowserHandoff(action) || IsGuestNetworkProbe(action) || (checkSavedNetwork && action.Id == "resume-network"));

    internal static bool IsBrowserHandoff(UserAction action) =>
        action.Id is "adspower" or "browser-active" or "browser-runtime" or "browser-reopen" or
            "acceptance" or "outage-validation" or CustomerGuide.ManualAdsPowerId;

    internal static bool IsGuestNetworkProbe(UserAction action) =>
        action.Id == "guest-network-probe" || (action.Id == "guest-step" && action.Title == "复查来宾的代理联网");
    private static readonly Lazy<string> ResourceSignature=new(()=>ComputeResourceStamp());
    public static string ResourceStamp()=>ResourceSignature.Value;
    public static string ComputeResourceStamp(IEnumerable<Resource>? catalog=null)=>Hash(JsonSerializer.Serialize(new {
        resources=(catalog??Catalog.Resources).Where(Catalog.IsGuestResource).Select(r=>new{r.Id,r.Sha256}),
        binaries=new[]{"YamlDotNet.dll","ClaudomeGuestNetwork.exe"}.Select(s=>new{name=s,hash=Convert.ToHexString(SHA256.HashData(Catalog.EmbeddedBytes(s)))}),
        scripts=new[]{"Configure.ps1","Integrations.ps1","BrowserAudit.ps1","NetworkAudit.ps1"}.Select(s=>new{name=s,hash=Hash(Catalog.EmbeddedText(s))})
    }));
    public static string SettingsStamp(SetupOptions options,string coreHash)=>Hash(JsonSerializer.Serialize(new{coreHash,options.Country,options.TimeZoneId,options.WorkspaceId,options.UserName}));
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class HostCompatibility
{
    public const int MinimumBuild=19041;
    public static bool IsSupported(string system,string architecture,int build)=>system=="Windows" && architecture.Equals("x64",StringComparison.OrdinalIgnoreCase) && build>=MinimumBuild;
    public static void Require(string system,string architecture,int build)
    {
        if(!IsSupported(system,architecture,build))throw new SetupException("需要 Windows 10 2004 及以上 / Windows 11 的 x64 主机。请核对客户电脑的系统版本与架构；来宾可继续使用 Windows 11 镜像。");
    }
}
