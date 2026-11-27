using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed record MigrationHostCheck(
    bool IsEligible,
    string EditionSummary,
    string? BlockReason,
    string Guidance);

public sealed record LegacyInstallInfo(
    string InstallDirectory,
    string ExePath,
    string ProductName,
    string Version,
    string ExeSha256,
    bool HasEverRun,
    IReadOnlyList<string> TopLevelFiles);

public sealed record DiscoveredLegacyTask(
    string TaskDirectory,
    string StatePath,
    string VmName,
    string VmId,
    string WorkspaceId,
    string PlatformId,
    string VmDirectory,
    bool SetupFinished,
    bool WorkspaceMarkerValid,
    string Summary)
{
    public bool IsHyperV => string.Equals(PlatformId, "hyperv", StringComparison.OrdinalIgnoreCase);
    public bool IsLegacyThirdPartyVm =>
        string.Equals(PlatformId, "virtualbox", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(PlatformId, "workstation", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(PlatformId, "vmware", StringComparison.OrdinalIgnoreCase);
}

public sealed record VerifiedHyperVPayload(
    string SourceLabel,
    string PayloadDirectory,
    string? TempStagingRoot,
    string Version,
    string ExePath,
    string ExeSha256,
    string? ZipSha256,
    IReadOnlyDictionary<string, string> FileSha256Map) : IDisposable
{
    public void Dispose()
    {
        if (!string.IsNullOrWhiteSpace(TempStagingRoot) && Directory.Exists(TempStagingRoot))
        {
            try { Directory.Delete(TempStagingRoot, recursive: true); } catch { }
        }
    }
}

public sealed record LegacyMigrationPlan(
    MigrationHostCheck Host,
    LegacyInstallInfo LegacyInstall,
    string TargetVersion,
    string TargetSourceLabel,
    string TargetExeSha256,
    string BackupDirectory,
    string LegacyLauncherExePath,
    string LegacyLauncherCmdPath,
    IReadOnlyList<DiscoveredLegacyTask> HyperVTasks,
    IReadOnlyList<DiscoveredLegacyTask> LegacyVmTasks)
{
    public string FormatSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"宿主机预检：{(Host.IsEligible ? "通过" : "不满足条件")}（{Host.EditionSummary}）");
        if (!Host.IsEligible && !string.IsNullOrWhiteSpace(Host.BlockReason))
        {
            sb.AppendLine($"阻止原因：{Host.BlockReason}");
            sb.AppendLine($"建议：{Host.Guidance}");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine($"旧版程序目录：{LegacyInstall.InstallDirectory}");
        sb.AppendLine($"当前旧版：{LegacyInstall.ProductName} v{LegacyInstall.Version}（{(LegacyInstall.HasEverRun ? "本机曾运行" : "未运行过的解压副本")}）");
        sb.AppendLine($"目标专线版：Claudome Hyper-V v{TargetVersion}（来源：{TargetSourceLabel}）");
        sb.AppendLine($"完整旧版备份：{BackupDirectory}");
        sb.AppendLine($"旧版独立入口：{LegacyLauncherExePath}");
        sb.AppendLine();

        if (HyperVTasks.Count > 0)
        {
            sb.AppendLine($"检测到 {HyperVTasks.Count} 个已有 Hyper-V 任务（升级后校验目录、VM UUID、WorkspaceId 与当前绑定并无缝续用）：");
            foreach (var t in HyperVTasks)
                sb.AppendLine($"  • {t.VmName} | {t.TaskDirectory} | VM={t.VmId} | WorkspaceId={t.WorkspaceId}");
        }
        else
        {
            sb.AppendLine("已有 Hyper-V 任务：未发现（升级后可在专线版内新建或载入 Hyper-V 任务）。");
        }

        sb.AppendLine();
        if (LegacyVmTasks.Count > 0)
        {
            sb.AppendLine($"检测到 {LegacyVmTasks.Count} 个 VirtualBox / VMware 旧任务（磁盘、快照和任务记录 100% 保留）：");
            foreach (var t in LegacyVmTasks)
                sb.AppendLine($"  • [{t.PlatformId}] {t.VmName} | {t.TaskDirectory}");
            sb.AppendLine($"  → 如需继续打开上述旧任务，请直接运行备份目录中的独立多线路版：{LegacyLauncherExePath}");
            sb.AppendLine("  → 如需改用 Hyper-V 专线版，请在升级后显式新建独立任务并选择新的任务目录。");
        }
        else
        {
            sb.AppendLine("已有 VirtualBox / VMware 任务：未发现（仍会保留完整可独立运行的旧版备份以供回退）。");
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed record HyperVTaskContinuityCheck(
    string TaskDirectory,
    string VmName,
    string VmId,
    string WorkspaceId,
    bool DirectoryMatched,
    bool WorkspaceMarkerMatched,
    bool VmIdValid,
    bool? LiveVmVerified,
    string Detail)
{
    public bool Passed => DirectoryMatched && WorkspaceMarkerMatched && VmIdValid && LiveVmVerified != false;
}

public sealed record LegacyMigrationResult(
    bool Succeeded,
    bool RolledBack,
    string TargetDirectory,
    string BackupDirectory,
    string LegacyLauncherExePath,
    string LegacyLauncherCmdPath,
    string InstalledVersion,
    string InstalledExeSha256,
    IReadOnlyList<HyperVTaskContinuityCheck> VerifiedHyperVTasks,
    IReadOnlyList<DiscoveredLegacyTask> PreservedLegacyVmTasks,
    string ReceiptPath,
    string Summary);

/// <summary>
/// Dedicated one-click migration workflow from the legacy multi-route Claudome build
/// to the standalone Claudome Hyper-V edition, enforcing host eligibility preflight,
/// strict package &amp; product identity verification, full runnable legacy backup,
/// atomic replacement, automatic rollback on failure, and post-upgrade Hyper-V task verification.
/// </summary>
public static class LegacyMigration
{
    public const string LegacyProductName = "Claudome";
    public const string HyperVProductName = "Claudome Hyper-V";
    public const string LegacyAppDataFolderName = "ClaudeEnvironmentSetup";
    public const string HyperVAppDataFolderName = "ClaudomeHyperVSetup";
    public const string MigrationLockFileName = "claudome-migration.lock";
    public const string MigrationTransactionFileName = "claudome-migration-tx.json";
    public const string MigrationReceiptFileName = "claudome-migration-receipt.json";
    public const string MigrationRollbackReceiptFileName = "claudome-migration-rollback.json";
    public const string BackupManifestFileName = "migration-backup-manifest.json";
    public const string LegacyLauncherScriptName = "打开旧多线路版.cmd";

    private static readonly string[] HyperVPayloadFiles =
    [
        "Claudome.exe",
        "PROJECT-LICENSE.txt",
        "Gateway-THIRD-PARTY-NOTICES.txt",
        "快速开始-HyperV.txt"
    ];

    private static readonly HashSet<string> TransientFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        MigrationLockFileName,
        MigrationTransactionFileName,
        MigrationReceiptFileName,
        MigrationRollbackReceiptFileName,
        SelfUpdater.UpdateSwapLockName,
        SelfUpdater.UpdatePendingTxName,
        SelfUpdater.UpdateRollbackManifestName,
        SelfUpdater.UpdateCompletedReceiptName,
        SelfUpdater.UpdateVerifiedMarkerName,
        SelfUpdater.UpdateDeferredManifestName,
        SelfUpdater.UpdateErrorMarkerName,
        SelfUpdater.UpdateUnrecoveredListName
    };

    private sealed record MigrationTransactionRecord(
        string TransactionId,
        string State,
        string TargetDirectory,
        string BackupDirectory,
        string LegacyVersion,
        string TargetVersion,
        Dictionary<string, string> OriginalFileSha256,
        Dictionary<string, string> TargetFileSha256,
        DateTime StartedUtc);

    public static bool IsHomeEdition(string? edition) =>
        edition is "Core" or "CoreN" or "CoreCountrySpecific" or "CoreSingleLanguage";

    public static bool IsHyperVSupportedEdition(string? edition) =>
        !string.IsNullOrWhiteSpace(edition) &&
        (edition.StartsWith("Professional", StringComparison.OrdinalIgnoreCase) ||
         edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) ||
         edition.StartsWith("Education", StringComparison.OrdinalIgnoreCase));

    public static MigrationHostCheck EvaluateHostEligibility(
        bool is64Bit,
        int osBuild,
        string edition,
        string editionDisplayName,
        bool virtualizationKnown,
        bool virtualizationReady)
    {
        var summary = $"Windows {editionDisplayName}（Build {osBuild} · {(is64Bit ? "x64" : "非 x64")}）";
        if (!is64Bit || osBuild < 22000)
        {
            return new MigrationHostCheck(
                IsEligible: false,
                EditionSummary: summary,
                BlockReason: $"当前宿主机为 {summary}，Hyper-V 专线版要求 Windows 11 x64（专业版 / 企业版 / 教育版）。迁移已在任何文件替换前停止，旧多线路版与原任务未作任何改动。",
                Guidance: "请继续使用现有的旧多线路版 Claudome 与原任务；日后更换或升级到符合条件的系统后，可重新检查并升级。");
        }
        if (IsHomeEdition(edition) || !IsHyperVSupportedEdition(edition))
        {
            return new MigrationHostCheck(
                IsEligible: false,
                EditionSummary: summary,
                BlockReason: $"当前宿主机为 Windows {editionDisplayName}，不支持 Hyper-V 专线版。迁移已在任何文件替换前停止，旧多线路版与原任务未作任何改动。",
                Guidance: "请继续使用已有的多线路版 Claudome 和原任务。后续在「设置 → 系统 → 激活」升级到 Windows 11 专业版 / 企业版 / 教育版后，可随时返回重新检查。");
        }
        if (virtualizationKnown && !virtualizationReady)
        {
            return new MigrationHostCheck(
                IsEligible: false,
                EditionSummary: summary,
                BlockReason: "未检测到可用的硬件虚拟化（VT-x / AMD-V / SLAT）。迁移已在任何文件替换前停止，旧多线路版与原任务未作任何改动。",
                Guidance: "请先在主板 BIOS/UEFI 中开启虚拟化支持后重启电脑，再返回点击「重新检查」。");
        }
        return new MigrationHostCheck(
            IsEligible: true,
            EditionSummary: summary,
            BlockReason: null,
            Guidance: "宿主机满足 Hyper-V 专线版运行条件，可先完整备份旧版再原地升级。");
    }

    public static string DefaultLegacyAppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyAppDataFolderName);

    public static string DefaultHyperVAppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), HyperVAppDataFolderName);

    public static LegacyInstallInfo InspectLegacyDirectory(
        string legacyDirectory,
        string? legacyAppDataDir = null,
        Func<string, (string ProductName, string Version)>? binaryIdentityReader = null)
    {
        if (string.IsNullOrWhiteSpace(legacyDirectory))
            throw new SetupException("请选择旧多线路版 Claudome 所在的程序目录。");
        var fullDir = Path.GetFullPath(legacyDirectory.Trim());
        if (!Directory.Exists(fullDir))
            throw new SetupException($"所选目录不存在：{fullDir}");
        if (StorageCleanupService.IsReparsePointPath(fullDir))
            throw new SetupException("所选程序目录包含符号链接或联接点，已拒绝迁移。");

        var exePath = Path.Combine(fullDir, "Claudome.exe");
        if (!File.Exists(exePath))
            throw new SetupException($"在所选目录中未找到 Claudome.exe：{fullDir}");
        if (StorageCleanupService.IsReparsePointPath(exePath))
            throw new SetupException("目标 Claudome.exe 为符号链接，已拒绝迁移。");

        var (productName, rawVersion) = ReadBinaryIdentity(exePath, binaryIdentityReader);
        if (string.Equals(productName, HyperVProductName, StringComparison.Ordinal))
            throw new SetupException("所选目录已经是 Claudome Hyper-V 专线版，无需再次执行旧版迁移；请直接使用「检查在线更新」或「从本地 ZIP 更新」。");
        if (!string.Equals(productName, LegacyProductName, StringComparison.Ordinal))
            throw new SetupException($"所选目录中的 Claudome.exe 产品标识为「{(string.IsNullOrEmpty(productName) ? "未知" : productName)}」，不是已发布的旧多线路版 Claudome，已拒绝替换。");

        if (!SelfUpdater.TryParseVersion(rawVersion, out var parsedVersion))
            throw new SetupException("无法读取旧版 Claudome.exe 的版本号。");

        var exeSha = ComputeSha256Strict(exePath, "Claudome.exe");
        var files = Directory.GetFiles(fullDir, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name) &&
                           !TransientFileNames.Contains(name!) &&
                           !name!.EndsWith(".migrating.tmp", StringComparison.OrdinalIgnoreCase) &&
                           !name!.EndsWith(".rollback.tmp", StringComparison.OrdinalIgnoreCase))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var appData = string.IsNullOrWhiteSpace(legacyAppDataDir) ? DefaultLegacyAppDataDirectory : legacyAppDataDir;
        var hasEverRun = File.Exists(Path.Combine(appData, "preferences.json")) ||
                         File.Exists(Path.Combine(appData, "backup-registry.json"));

        return new LegacyInstallInfo(
            InstallDirectory: fullDir,
            ExePath: exePath,
            ProductName: productName,
            Version: parsedVersion.ToString(3),
            ExeSha256: exeSha,
            HasEverRun: hasEverRun,
            TopLevelFiles: files);
    }

    public static IReadOnlyList<DiscoveredLegacyTask> DiscoverLegacyTasks(
        string? legacyAppDataDir = null,
        IEnumerable<string>? additionalCandidateRoots = null)
    {
        var appData = string.IsNullOrWhiteSpace(legacyAppDataDir) ? DefaultLegacyAppDataDirectory : legacyAppDataDir;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidateRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path.Trim());
                if (File.Exists(full) && Path.GetFileName(full).Equals("setup-state.json", StringComparison.OrdinalIgnoreCase))
                    full = Path.GetDirectoryName(full) ?? full;
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Directory.Exists(full) && File.Exists(Path.Combine(full, "setup-state.json")))
                    candidates.Add(full);
            }
            catch { }
        }

        // 1. Legacy AppData preferences.json (last opened task)
        var prefPath = Path.Combine(appData, "preferences.json");
        if (File.Exists(prefPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(prefPath));
                if (doc.RootElement.TryGetProperty("StatePath", out var sp) && sp.ValueKind == JsonValueKind.String)
                    AddCandidateRoot(sp.GetString());
            }
            catch { }
        }

        // 2. Legacy backup-registry.json (recorded task roots)
        var regPath = Path.Combine(appData, "backup-registry.json");
        if (File.Exists(regPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(regPath));
                if (doc.RootElement.TryGetProperty("Records", out var recs) && recs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rec in recs.EnumerateArray())
                    {
                        if (rec.TryGetProperty("Path", out var p) && p.ValueKind == JsonValueKind.String)
                        {
                            var raw = p.GetString();
                            if (!string.IsNullOrWhiteSpace(raw))
                            {
                                var dir = Path.GetDirectoryName(raw);
                                AddCandidateRoot(dir);
                                if (!string.IsNullOrWhiteSpace(dir))
                                    AddCandidateRoot(Path.GetDirectoryName(dir));
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // 3. Explicit candidate roots (or standard default folders when none supplied)
        if (additionalCandidateRoots is not null)
        {
            foreach (var root in additionalCandidateRoots)
                AddCandidateRoot(root);
        }
        else
        {
            AddCandidateRoot(@"D:\ClaudeWorkspace");
            AddCandidateRoot(@"D:\Claudome");
            AddCandidateRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeWorkspace"));
        }

        var results = new List<DiscoveredLegacyTask>();
        foreach (var taskDir in candidates.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var statePath = Path.Combine(taskDir, "setup-state.json");
            try
            {
                var state = TaskCheckpointStore.Read(statePath);
                var markerPath = Path.Combine(taskDir, ".claude-setup-workspace.json");
                var markerValid = VerifyWorkspaceMarker(markerPath, state.Options.WorkspaceId);
                var platformId = string.IsNullOrWhiteSpace(state.Options.PlatformId) ? "virtualbox" : state.Options.PlatformId.Trim().ToLowerInvariant();
                var summary = $"{state.Options.VmName}（{platformId} · {(state.SetupFinished ? "已完成设置" : state.Status)}）";
                results.Add(new DiscoveredLegacyTask(
                    TaskDirectory: taskDir,
                    StatePath: statePath,
                    VmName: state.Options.VmName,
                    VmId: state.VmId ?? "",
                    WorkspaceId: state.Options.WorkspaceId ?? "",
                    PlatformId: platformId,
                    VmDirectory: state.Options.VmDirectory,
                    SetupFinished: state.SetupFinished,
                    WorkspaceMarkerValid: markerValid,
                    Summary: summary));
            }
            catch { }
        }
        return results;
    }

    public static async Task<VerifiedHyperVPayload> StageHyperVPackageFromZipAsync(
        string zipPath,
        string stagingRoot,
        string? expectedZipSha256 = null,
        string? expectedExeSha256 = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            throw new SetupException("未找到指定的 Hyper-V 专线版 ZIP 安装包。");
        var actualZipSha = await SelfUpdater.ComputeSha256Async(zipPath, token);
        Directory.CreateDirectory(stagingRoot);
        var tempRoot = Path.Combine(stagingRoot, "claudome-migrate-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var staged = await SelfUpdater.ExtractAndVerifyPackageAsync(
                zipPath,
                Path.Combine(tempRoot, "extracted"),
                new Version(0, 0, 0),
                expectedZipSha256,
                expectedExeSha256,
                allowSameVersion: true,
                token,
                expectedProductName: HyperVProductName);

            var fileMap = await BuildPayloadHashMapAsync(staged.PayloadDirectory, token);
            return new VerifiedHyperVPayload(
                SourceLabel: Path.GetFileName(zipPath),
                PayloadDirectory: staged.PayloadDirectory,
                TempStagingRoot: tempRoot,
                Version: staged.Version,
                ExePath: staged.ExePath,
                ExeSha256: staged.ExeSha256,
                ZipSha256: actualZipSha,
                FileSha256Map: fileMap);
        }
        catch
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
            throw;
        }
    }

    public static async Task<VerifiedHyperVPayload> StageHyperVPayloadFromDirectoryAsync(
        string hyperVAppDir,
        CancellationToken token = default,
        Func<string, (string ProductName, string Version)>? binaryIdentityReader = null)
    {
        if (string.IsNullOrWhiteSpace(hyperVAppDir) || !Directory.Exists(hyperVAppDir))
            throw new SetupException("未找到可用的 Hyper-V 专线版源程序目录。");
        var fullDir = Path.GetFullPath(hyperVAppDir);
        if (StorageCleanupService.IsReparsePointPath(fullDir))
            throw new SetupException("Hyper-V 专线版源程序目录包含符号链接，已拒绝迁移。");

        var exePath = Path.Combine(fullDir, "Claudome.exe");
        if (!File.Exists(exePath) || StorageCleanupService.IsReparsePointPath(exePath))
            throw new SetupException("Hyper-V 专线版源程序目录缺少合法的 Claudome.exe。");

        var (productName, rawVersion) = ReadBinaryIdentity(exePath, binaryIdentityReader);
        if (!string.Equals(productName, HyperVProductName, StringComparison.Ordinal))
            throw new SetupException("源程序不是合法的 Claudome Hyper-V 专线版，已拒绝迁移。");

        if (!SelfUpdater.TryParseVersion(rawVersion, out var parsedVersion))
            throw new SetupException("无法读取 Hyper-V 专线版版本号。");

        var fileMap = await BuildPayloadHashMapAsync(fullDir, token);
        return new VerifiedHyperVPayload(
            SourceLabel: $"当前已校验程序（v{parsedVersion.ToString(3)}）",
            PayloadDirectory: fullDir,
            TempStagingRoot: null,
            Version: parsedVersion.ToString(3),
            ExePath: exePath,
            ExeSha256: fileMap["Claudome.exe"],
            ZipSha256: null,
            FileSha256Map: fileMap);
    }

    private static async Task<Dictionary<string, string>> BuildPayloadHashMapAsync(string payloadDir, CancellationToken token)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exePath = Path.Combine(payloadDir, "Claudome.exe");
        if (!File.Exists(exePath) || StorageCleanupService.IsReparsePointPath(exePath))
            throw new SetupException("专线版包中缺少 Claudome.exe。");
        map["Claudome.exe"] = await SelfUpdater.ComputeSha256Async(exePath, token);

        foreach (var req in SelfUpdater.RequiredSideFileNames)
        {
            var reqPath = Path.Combine(payloadDir, req);
            if (!File.Exists(reqPath) || StorageCleanupService.IsReparsePointPath(reqPath))
                throw new SetupException($"专线版包中缺少必需随包文件：{req}。");
            map[req] = await SelfUpdater.ComputeSha256Async(reqPath, token);
        }

        var quickstart = Path.Combine(payloadDir, "快速开始-HyperV.txt");
        if (File.Exists(quickstart) && !StorageCleanupService.IsReparsePointPath(quickstart))
            map["快速开始-HyperV.txt"] = await SelfUpdater.ComputeSha256Async(quickstart, token);

        return map;
    }

    public static LegacyMigrationPlan BuildPlan(
        MigrationHostCheck host,
        LegacyInstallInfo legacyInstall,
        VerifiedHyperVPayload targetPayload,
        IReadOnlyList<DiscoveredLegacyTask> discoveredTasks,
        string? backupDirectoryOverride = null)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var backupDir = !string.IsNullOrWhiteSpace(backupDirectoryOverride)
            ? Path.GetFullPath(backupDirectoryOverride)
            : Path.Combine(legacyInstall.InstallDirectory, $"legacy-multi-route-backup-v{legacyInstall.Version}-{stamp}");
        var hyperVTasks = discoveredTasks.Where(t => t.IsHyperV).ToList();
        var legacyVmTasks = discoveredTasks.Where(t => !t.IsHyperV).ToList();
        return new LegacyMigrationPlan(
            Host: host,
            LegacyInstall: legacyInstall,
            TargetVersion: targetPayload.Version,
            TargetSourceLabel: targetPayload.SourceLabel,
            TargetExeSha256: targetPayload.ExeSha256,
            BackupDirectory: backupDir,
            LegacyLauncherExePath: Path.Combine(backupDir, "Claudome.exe"),
            LegacyLauncherCmdPath: Path.Combine(backupDir, LegacyLauncherScriptName),
            HyperVTasks: hyperVTasks,
            LegacyVmTasks: legacyVmTasks);
    }

    public static HyperVTaskContinuityCheck VerifyHyperVTaskContinuity(
        DiscoveredLegacyTask task,
        Func<DiscoveredLegacyTask, (bool Verified, string Detail)>? liveVmVerifier = null)
    {
        if (!File.Exists(task.StatePath))
        {
            return new HyperVTaskContinuityCheck(
                task.TaskDirectory, task.VmName, task.VmId, task.WorkspaceId,
                DirectoryMatched: false, WorkspaceMarkerMatched: false, VmIdValid: false, LiveVmVerified: false,
                Detail: "任务状态文件 setup-state.json 不存在。");
        }
        var state = TaskCheckpointStore.Read(task.StatePath);
        var normDir = Path.GetFullPath(task.TaskDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normRoot = Path.GetFullPath(state.Options.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dirMatched = normDir.Equals(normRoot, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(state.Options.PlatformId, "hyperv", StringComparison.OrdinalIgnoreCase);
        var workspaceId = state.Options.WorkspaceId ?? "";
        var wsValid = Regex.IsMatch(workspaceId, "^[0-9a-f]{32}$", RegexOptions.IgnoreCase) &&
                      VerifyWorkspaceMarker(Path.Combine(task.TaskDirectory, ".claude-setup-workspace.json"), workspaceId);
        var vmIdValid = Guid.TryParse(state.VmId, out var parsedVmId) && parsedVmId != Guid.Empty;

        bool? liveOk = null;
        var liveDetail = "仅完成本地任务元数据核对";
        if (vmIdValid && liveVmVerifier is not null)
        {
            var (ok, detail) = liveVmVerifier(task with { VmId = state.VmId, WorkspaceId = workspaceId });
            liveOk = ok;
            liveDetail = detail;
        }
        var overall = dirMatched && wsValid && vmIdValid && liveOk != false;
        var summary = overall
            ? $"已核对原 Hyper-V 任务目录、VM UUID（{state.VmId}）与 WorkspaceId（{workspaceId}）；{liveDetail}。"
            : $"Hyper-V 任务核对存在异常：目录={dirMatched}，工作区标识={wsValid}，VM UUID={vmIdValid}，{liveDetail}。";
        return new HyperVTaskContinuityCheck(
            task.TaskDirectory, state.Options.VmName, state.VmId, workspaceId,
            dirMatched, wsValid, vmIdValid, liveOk, summary);
    }

    public static async Task<LegacyMigrationResult> ExecuteMigrationAsync(
        LegacyMigrationPlan plan,
        VerifiedHyperVPayload payload,
        string? hyperVAppDataDir = null,
        Func<DiscoveredLegacyTask, (bool Verified, string Detail)>? liveVmVerifier = null,
        Func<string, Task>? faultInjectionBeforeFileCommit = null,
        Func<Task>? faultInjectionBeforeFinalize = null,
        Func<string, (string ProductName, string Version)>? binaryIdentityReader = null,
        CancellationToken token = default)
    {
        // Hard Gate 1: Ineligible host stops before ANY filesystem mutation.
        if (!plan.Host.IsEligible)
            throw new SetupException(plan.Host.BlockReason ?? "当前宿主机不符合 Hyper-V 专线版要求，迁移已停止。");

        var targetDir = Path.GetFullPath(plan.LegacyInstall.InstallDirectory);
        var payloadDir = Path.GetFullPath(payload.PayloadDirectory);
        if (targetDir.TrimEnd('\\').Equals(payloadDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new SetupException("目标旧版目录与 Hyper-V 专线版源目录相同，已拒绝覆盖正在运行的程序目录。");

        if (SelfUpdater.TryGetPendingUpdateBlocker(targetDir, out var pendingBlocker))
            throw new SetupException($"目标旧版目录存在未收尾的更新状态，请先处理后再迁移：{pendingBlocker}");

        // Hard Gate 2: Acquire exclusive migration lock & confirm target EXE is not running/locked.
        var lockPath = Path.Combine(targetDir, MigrationLockFileName);
        FileStream? lockStream = null;
        try
        {
            lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SetupException($"目标目录正被另一项迁移或进程占用（{ex.Message}），请稍后重试。");
        }

        var originalHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var backupCreated = false;
        var mutationStarted = false;
        var txId = Guid.NewGuid().ToString("N");
        var txPath = Path.Combine(targetDir, MigrationTransactionFileName);

        try
        {
            // Verify target Claudome.exe is not currently running/locked and matches expected pre-plan hash.
            var targetExePath = Path.Combine(targetDir, "Claudome.exe");
            try
            {
                using var probe = new FileStream(targetExePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SetupException($"旧版 Claudome.exe 正在运行或被占用，请先关闭该目录中的旧版 Claudome 再执行升级（{ex.Message}）。");
            }

            // Re-enumerate all top-level files to back up completely and compute strict original SHA-256.
            var filesToBackup = Directory.GetFiles(targetDir, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) &&
                               !TransientFileNames.Contains(name!) &&
                               !name!.EndsWith(".migrating.tmp", StringComparison.OrdinalIgnoreCase) &&
                               !name!.EndsWith(".rollback.tmp", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var fileName in filesToBackup)
            {
                var srcPath = Path.Combine(targetDir, fileName);
                if (StorageCleanupService.IsReparsePointPath(srcPath))
                    throw new SetupException($"目标目录中的 {fileName} 为符号链接，已拒绝迁移。");
                originalHashes[fileName] = ComputeSha256Strict(srcPath, fileName);
            }
            if (!originalHashes.TryGetValue("Claudome.exe", out var currentTargetExeSha) ||
                !currentTargetExeSha.Equals(plan.LegacyInstall.ExeSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupException("目标目录中的旧版 Claudome.exe 在确认后发生变化，已中止升级，请重新扫描后再试。");
            }

            // Phase 1: Complete runnable backup of the existing legacy installation.
            if (Directory.Exists(plan.BackupDirectory))
                throw new SetupException($"备份目录已存在，为防覆盖旧备份已中止：{plan.BackupDirectory}");
            Directory.CreateDirectory(plan.BackupDirectory);
            backupCreated = true;

            foreach (var (fileName, origSha) in originalHashes)
            {
                token.ThrowIfCancellationRequested();
                var src = Path.Combine(targetDir, fileName);
                var dst = Path.Combine(plan.BackupDirectory, fileName);
                File.Copy(src, dst, overwrite: false);
                var bakSha = ComputeSha256Strict(dst, $"备份/{fileName}");
                if (!bakSha.Equals(origSha, StringComparison.OrdinalIgnoreCase))
                    throw new SetupException($"备份文件 {fileName} 校验不一致，已中止替换。");
            }

            // Write standalone launcher script & backup manifest so legacy VirtualBox/VMware tasks stay directly openable.
            var cmdContent = "@echo off\r\nstart \"\" \"%~dp0Claudome.exe\"\r\n";
            File.WriteAllText(plan.LegacyLauncherCmdPath, cmdContent, new UTF8Encoding(false));
            JsonFile.Write(Path.Combine(plan.BackupDirectory, BackupManifestFileName), new
            {
                transactionId = txId,
                createdUtc = DateTime.UtcNow,
                legacyDirectory = targetDir,
                legacyProductName = plan.LegacyInstall.ProductName,
                legacyVersion = plan.LegacyInstall.Version,
                legacyExeSha256 = plan.LegacyInstall.ExeSha256,
                originalFiles = originalHashes,
                preservedLegacyVmTasks = plan.LegacyVmTasks.Select(t => new
                {
                    t.PlatformId,
                    t.VmName,
                    t.TaskDirectory,
                    t.VmDirectory,
                    t.WorkspaceId,
                    t.VmId
                }).ToList()
            });

            // Record InProgress transaction before touching any target file.
            JsonFile.Write(txPath, new MigrationTransactionRecord(
                TransactionId: txId,
                State: "InProgress",
                TargetDirectory: targetDir,
                BackupDirectory: plan.BackupDirectory,
                LegacyVersion: plan.LegacyInstall.Version,
                TargetVersion: payload.Version,
                OriginalFileSha256: originalHashes,
                TargetFileSha256: new Dictionary<string, string>(payload.FileSha256Map, StringComparer.OrdinalIgnoreCase),
                StartedUtc: DateTime.UtcNow));

            // Phase 2: Atomic staged replacement of Hyper-V standalone files.
            mutationStarted = true;
            foreach (var fileName in HyperVPayloadFiles)
            {
                if (!payload.FileSha256Map.TryGetValue(fileName, out var expectedSha))
                    continue;
                token.ThrowIfCancellationRequested();
                if (faultInjectionBeforeFileCommit is not null)
                    await faultInjectionBeforeFileCommit(fileName);

                var src = Path.Combine(payloadDir, fileName);
                var dst = Path.Combine(targetDir, fileName);
                var tmp = Path.Combine(targetDir, $"{fileName}.{txId}.migrating.tmp");
                try
                {
                    File.Copy(src, tmp, overwrite: true);
                    var tmpSha = await SelfUpdater.ComputeSha256Async(tmp, token);
                    if (!tmpSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                        throw new SetupException($"写入校验失败：{fileName} SHA-256 不匹配。");
                    File.Move(tmp, dst, overwrite: true);
                }
                finally
                {
                    TryDeleteFile(tmp);
                }
            }

            // Remove stale legacy 快速开始.txt once 快速开始-HyperV.txt is safely installed (original stays in backup).
            if (payload.FileSha256Map.ContainsKey("快速开始-HyperV.txt") &&
                originalHashes.ContainsKey("快速开始.txt"))
            {
                TryDeleteFile(Path.Combine(targetDir, "快速开始.txt"));
            }

            // Phase 3: Strict post-replacement verification before commit.
            if (faultInjectionBeforeFinalize is not null)
                await faultInjectionBeforeFinalize();

            foreach (var (fileName, expectedSha) in payload.FileSha256Map)
            {
                var installedPath = Path.Combine(targetDir, fileName);
                if (!File.Exists(installedPath))
                    throw new SetupException($"替换后缺少必需文件：{fileName}。");
                var actualSha = await SelfUpdater.ComputeSha256Async(installedPath, token);
                if (!actualSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                    throw new SetupException($"替换后文件校验失败：{fileName}。");
            }
            var (installedProductName, _) = ReadBinaryIdentity(targetExePath, binaryIdentityReader);
            if (!string.Equals(installedProductName, HyperVProductName, StringComparison.Ordinal))
                throw new SetupException("替换后 Claudome.exe 产品身份校验失败。");

            // Phase 4: Verify existing Hyper-V task continuity (directory, VM UUID, WorkspaceId, bindings).
            var verifiedHyperV = new List<HyperVTaskContinuityCheck>();
            foreach (var hvTask in plan.HyperVTasks)
                verifiedHyperV.Add(VerifyHyperVTaskContinuity(hvTask, liveVmVerifier));

            // Bind verified Hyper-V task into Hyper-V AppData preferences (never touching legacy AppData preferences!).
            var firstVerifiedHv = verifiedHyperV.FirstOrDefault(v => v.Passed);
            if (firstVerifiedHv is not null)
            {
                var hvAppData = string.IsNullOrWhiteSpace(hyperVAppDataDir) ? DefaultHyperVAppDataDirectory : hyperVAppDataDir;
                var hvPrefPath = Path.Combine(hvAppData, "preferences.json");
                var shouldWritePref = !File.Exists(hvPrefPath);
                if (!shouldWritePref)
                {
                    try
                    {
                        using var existingPref = JsonDocument.Parse(File.ReadAllText(hvPrefPath));
                        var existingStatePath = existingPref.RootElement.TryGetProperty("StatePath", out var sp) ? sp.GetString() : null;
                        shouldWritePref = string.IsNullOrWhiteSpace(existingStatePath) || !File.Exists(existingStatePath);
                    }
                    catch { shouldWritePref = true; }
                }
                if (shouldWritePref)
                {
                    Directory.CreateDirectory(hvAppData);
                    JsonFile.Write(hvPrefPath, new { StatePath = Path.Combine(firstVerifiedHv.TaskDirectory, "setup-state.json") });
                }
            }

            var receiptPath = Path.Combine(targetDir, MigrationReceiptFileName);
            var summary = $"已将 {targetDir} 从旧多线路版 v{plan.LegacyInstall.Version} 原地升级到 Claudome Hyper-V v{payload.Version}。" +
                          $"原多线路版完整备份在：{plan.BackupDirectory}（可直接运行 {plan.LegacyLauncherExePath} 打开旧任务）。";
            JsonFile.Write(receiptPath, new
            {
                transactionId = txId,
                completedUtc = DateTime.UtcNow,
                targetDirectory = targetDir,
                previousVersion = plan.LegacyInstall.Version,
                previousExeSha256 = plan.LegacyInstall.ExeSha256,
                installedVersion = payload.Version,
                installedExeSha256 = payload.ExeSha256,
                backupDirectory = plan.BackupDirectory,
                legacyLauncherExePath = plan.LegacyLauncherExePath,
                legacyLauncherCmdPath = plan.LegacyLauncherCmdPath,
                verifiedHyperVTasks = verifiedHyperV,
                preservedLegacyVmTasks = plan.LegacyVmTasks,
                summary
            });
            TryDeleteFile(txPath);

            return new LegacyMigrationResult(
                Succeeded: true,
                RolledBack: false,
                TargetDirectory: targetDir,
                BackupDirectory: plan.BackupDirectory,
                LegacyLauncherExePath: plan.LegacyLauncherExePath,
                LegacyLauncherCmdPath: plan.LegacyLauncherCmdPath,
                InstalledVersion: payload.Version,
                InstalledExeSha256: payload.ExeSha256,
                VerifiedHyperVTasks: verifiedHyperV,
                PreservedLegacyVmTasks: plan.LegacyVmTasks,
                ReceiptPath: receiptPath,
                Summary: summary);
        }
        catch (Exception ex)
        {
            if (mutationStarted && backupCreated)
            {
                var (restoredOk, rollbackDetail) = RollbackFromBackup(targetDir, plan.BackupDirectory, originalHashes, txId);
                if (restoredOk)
                {
                    TryDeleteFile(txPath);
                    JsonFile.Write(Path.Combine(targetDir, MigrationRollbackReceiptFileName), new
                    {
                        transactionId = txId,
                        rolledBackUtc = DateTime.UtcNow,
                        reason = ex.Message,
                        backupDirectory = plan.BackupDirectory,
                        detail = rollbackDetail
                    });
                    throw new SetupException($"升级过程中发生错误，已自动完整回退到原多线路版 v{plan.LegacyInstall.Version}（所有原文件校验一致）：{ex.Message}");
                }
                throw new SetupException($"升级失败且自动回退未能恢复全部文件（{rollbackDetail}）；请关闭占用程序后从备份目录 {plan.BackupDirectory} 手动恢复。原因：{ex.Message}");
            }
            if (backupCreated && !mutationStarted)
            {
                try { Directory.Delete(plan.BackupDirectory, recursive: true); } catch { }
                TryDeleteFile(txPath);
            }
            throw;
        }
        finally
        {
            lockStream?.Dispose();
            TryDeleteFile(lockPath);
        }
    }

    private static (bool Succeeded, string Detail) RollbackFromBackup(
        string targetDir,
        string backupDir,
        Dictionary<string, string> originalHashes,
        string txId)
    {
        var failures = new List<string>();
        // Clean up any leftover temp files first.
        try
        {
            foreach (var tmp in Directory.GetFiles(targetDir, "*.migrating.tmp", SearchOption.TopDirectoryOnly))
                TryDeleteFile(tmp);
        }
        catch { }

        // Remove Hyper-V-only files that did not exist in the original legacy install.
        foreach (var hvFile in HyperVPayloadFiles)
        {
            if (!originalHashes.ContainsKey(hvFile))
            {
                var extraPath = Path.Combine(targetDir, hvFile);
                if (File.Exists(extraPath))
                {
                    try { File.Delete(extraPath); }
                    catch (Exception ex) { failures.Add($"删除新增文件 {hvFile} 失败：{ex.Message}"); }
                }
            }
        }

        // Restore every original file from the verified backup and re-verify SHA-256.
        foreach (var (fileName, expectedSha) in originalHashes)
        {
            var bakPath = Path.Combine(backupDir, fileName);
            var dstPath = Path.Combine(targetDir, fileName);
            var tmpPath = Path.Combine(targetDir, $"{fileName}.{txId}.rollback.tmp");
            try
            {
                if (!File.Exists(bakPath))
                {
                    failures.Add($"备份中缺少 {fileName}");
                    continue;
                }
                File.Copy(bakPath, tmpPath, overwrite: true);
                File.Move(tmpPath, dstPath, overwrite: true);
                var restoredSha = ComputeSha256Strict(dstPath, fileName);
                if (!restoredSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"恢复后校验不一致：{fileName}");
            }
            catch (Exception ex)
            {
                failures.Add($"恢复 {fileName} 失败：{ex.Message}");
            }
            finally
            {
                TryDeleteFile(tmpPath);
            }
        }

        if (failures.Count > 0)
            return (false, string.Join("；", failures));
        return (true, $"已恢复并校验全部 {originalHashes.Count} 个原版文件。");
    }

    private static bool VerifyWorkspaceMarker(string markerPath, string? expectedWorkspaceId)
    {
        if (!File.Exists(markerPath) || string.IsNullOrWhiteSpace(expectedWorkspaceId))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(markerPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("WorkspaceId", out var ws) && ws.ValueKind == JsonValueKind.String)
                return string.Equals(ws.GetString(), expectedWorkspaceId, StringComparison.OrdinalIgnoreCase);
            if (root.TryGetProperty("workspaceId", out var wsLower) && wsLower.ValueKind == JsonValueKind.String)
                return string.Equals(wsLower.GetString(), expectedWorkspaceId, StringComparison.OrdinalIgnoreCase);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static (string ProductName, string Version) ReadBinaryIdentity(
        string exePath,
        Func<string, (string ProductName, string Version)>? reader)
    {
        if (reader is not null)
        {
            var (p, v) = reader(exePath);
            return (p?.Trim() ?? "", v?.Trim() ?? "");
        }
        var vi = FileVersionInfo.GetVersionInfo(exePath);
        var version = !string.IsNullOrWhiteSpace(vi.ProductVersion) ? vi.ProductVersion : (vi.FileVersion ?? "");
        return (vi.ProductName?.Trim() ?? "", version.Trim());
    }

    private static string ComputeSha256Strict(string path, string displayName)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SetupException($"无法读取 {displayName} 的 SHA-256 校验值（{ex.Message}）。");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
