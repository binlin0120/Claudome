using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public enum BackupCategory
{
    ProgramUpdate,
    TaskCheckpoint,
    RouteConfig,
    UserArchives,
    ProtectedAssets
}

public enum CleanupItemStatus
{
    Cleanable,
    Retained,
    InUse,
    Pending
}

public enum BackupOpState
{
    InProgress,
    FailedOrCancelled,
    VerifiedSuccess
}

public sealed record BackupRegistryRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public BackupCategory Category { get; init; }
    public string OwnerPlatform { get; init; } = "windows";
    public string SourceFeature { get; init; } = "";
    public string ScopeId { get; init; } = "";
    public string ScopeLabel { get; init; } = "";
    public string Path { get; init; } = "";
    public string StageRoot { get; init; } = "";
    public string TransactionId { get; init; } = "";
    public string Role { get; init; } = "backup";
    public BackupOpState OpState { get; set; } = BackupOpState.VerifiedSuccess;
    public string ReferencedBy { get; set; } = "";
    public bool PinProtected { get; set; }
    public string ProtectedReason { get; set; } = "";
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public long SizeBytes { get; set; }
    public string ContentSha256 { get; init; } = "";
}

public sealed record CleanupExecutionResult(
    DateTime ExecutedUtc,
    bool IsAutomatic,
    string ScopeSummary,
    int DeletedCount,
    long FreedBytes,
    int SkippedCount,
    int RetainedCount,
    List<string> DeletedNames,
    List<string> SkippedReasons,
    List<string> RetainedReasons);

public sealed class CleanupSettings
{
    public bool AutoCleanProgramUpdate { get; set; } = true;
    public bool AutoCleanTaskCheckpoint { get; set; } = true;
    public bool AutoCleanRouteConfig { get; set; } = true;
    public HashSet<string> PinnedArchivePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public CleanupExecutionResult? LastAutoCleanup { get; set; }

    public static string DefaultPath =>
        Path.Combine(ProductProfile.AppDataDirectory, "storage-cleanup-settings.json");

    public bool IsAutoCleanEnabled(BackupCategory category) => category switch
    {
        BackupCategory.ProgramUpdate => AutoCleanProgramUpdate,
        BackupCategory.TaskCheckpoint => AutoCleanTaskCheckpoint,
        BackupCategory.RouteConfig => AutoCleanRouteConfig,
        _ => false
    };

    public void SetAutoCleanEnabled(BackupCategory category, bool enabled)
    {
        switch (category)
        {
            case BackupCategory.ProgramUpdate: AutoCleanProgramUpdate = enabled; break;
            case BackupCategory.TaskCheckpoint: AutoCleanTaskCheckpoint = enabled; break;
            case BackupCategory.RouteConfig: AutoCleanRouteConfig = enabled; break;
        }
    }

    public static CleanupSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new CleanupSettings();
            Paths.RejectLinks(path);
            var settings = JsonSerializer.Deserialize<CleanupSettings>(File.ReadAllText(path), JsonFile.Format);
            if (settings is null) return new CleanupSettings();
            settings.PinnedArchivePaths = new HashSet<string>(settings.PinnedArchivePaths ?? [], StringComparer.OrdinalIgnoreCase);
            return settings;
        }
        catch { return new CleanupSettings(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        JsonFile.Write(path, this);
    }
}

public sealed record CleanupCandidate(
    string Id,
    BackupCategory Category,
    string CategoryLabel,
    string SourceFeature,
    string ScopeId,
    string ScopeLabel,
    string Path,
    string DisplayName,
    bool IsDirectory,
    long SizeBytes,
    DateTime ModifiedUtc,
    CleanupItemStatus Status,
    string StatusLabel,
    string Reason,
    bool CanSelect,
    long? SnapshotLength,
    DateTime? SnapshotWriteUtc,
    string AuthorizedRoot,
    string? SnapshotDirFingerprint = null,
    string? SnapshotContentSha256 = null);

public sealed record CleanupCategorySummary(
    BackupCategory Category,
    string CategoryLabel,
    int FileCount,
    long TotalBytes,
    int CleanableCount,
    long CleanableBytes,
    string PlatformNote);

public sealed record CleanupInspectionReport(
    DateTime ScannedUtc,
    List<CleanupCategorySummary> Categories,
    List<CleanupCandidate> Items);

public sealed record StorageCleanupContext(
    string PlatformId,
    string AppDirectory,
    string TempDirectory,
    IReadOnlyList<string> WorkspaceRoots,
    string? SettingsPath = null,
    string? RegistryPath = null,
    string? ArchiveDirectory = null,
    string? ActiveArchivePath = null,
    string? ActiveWorkspaceId = null,
    string? ActiveRouteRevision = null,
    string? PreviousRouteRevision = null,
    string? ExpectedPlatformId = null,
    string? ExpectedPackagePrefix = null)
{
    public static string DefaultRegistryPath =>
        Path.Combine(ProductProfile.AppDataDirectory, "backup-registry.json");

    public string EffectiveSettingsPath => SettingsPath ?? CleanupSettings.DefaultPath;
    public string EffectiveRegistryPath => RegistryPath ?? DefaultRegistryPath;
}

public static class StorageCleanupService
{
    private static readonly object RegistrySync = new();

    public static string CategoryTitle(BackupCategory category) => category switch
    {
        BackupCategory.ProgramUpdate => "程序更新备份与暂存",
        BackupCategory.TaskCheckpoint => "任务检查点备份",
        BackupCategory.RouteConfig => "线路与配置变更备份",
        BackupCategory.UserArchives => "用户选择的交付 ZIP / 导出包",
        BackupCategory.ProtectedAssets => "受保护资产（不纳入自动轮换）",
        _ => category.ToString()
    };

    public static string StatusTitle(CleanupItemStatus status) => status switch
    {
        CleanupItemStatus.Cleanable => "可清理",
        CleanupItemStatus.Retained => "保留",
        CleanupItemStatus.InUse => "使用中",
        CleanupItemStatus.Pending => "待处理",
        _ => status.ToString()
    };

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576d:F1} MB",
        _ => $"{bytes / 1073741824d:F2} GB"
    };

    public static void RegisterRecord(BackupRegistryRecord record, string? registryPath = null)
    {
        registryPath ??= StorageCleanupContext.DefaultRegistryPath;
        lock (RegistrySync)
        {
            var list = LoadRegistryUnlocked(registryPath);
            list.RemoveAll(r => r.Path.Equals(record.Path, StringComparison.OrdinalIgnoreCase) &&
                                r.ScopeId.Equals(record.ScopeId, StringComparison.OrdinalIgnoreCase));
            list.Add(record);
            if (list.Count > 500)
                list = list.OrderByDescending(r => r.CreatedUtc).Take(500).ToList();
            SaveRegistryUnlocked(registryPath, list);
        }
    }

    public static void MarkTransactionVerified(BackupCategory category, string scopeId, string transactionId, string? registryPath = null)
    {
        if (string.IsNullOrWhiteSpace(transactionId)) return;
        registryPath ??= StorageCleanupContext.DefaultRegistryPath;
        lock (RegistrySync)
        {
            var list = LoadRegistryUnlocked(registryPath);
            var changed = false;
            foreach (var r in list.Where(x => x.Category == category &&
                                              x.ScopeId.Equals(scopeId, StringComparison.OrdinalIgnoreCase) &&
                                              x.TransactionId.Equals(transactionId, StringComparison.OrdinalIgnoreCase)))
            {
                if (r.OpState != BackupOpState.VerifiedSuccess)
                {
                    r.OpState = BackupOpState.VerifiedSuccess;
                    changed = true;
                }
            }
            if (changed) SaveRegistryUnlocked(registryPath, list);
        }
    }

    public static void UpdateTransactionState(BackupCategory category, string scopeId, string transactionId, BackupOpState state, string? referenceNote = null, string? registryPath = null)
    {
        if (string.IsNullOrWhiteSpace(transactionId)) return;
        registryPath ??= StorageCleanupContext.DefaultRegistryPath;
        lock (RegistrySync)
        {
            var list = LoadRegistryUnlocked(registryPath);
            var changed = false;
            foreach (var r in list.Where(x => x.Category == category &&
                                              x.ScopeId.Equals(scopeId, StringComparison.OrdinalIgnoreCase) &&
                                              x.TransactionId.Equals(transactionId, StringComparison.OrdinalIgnoreCase)))
            {
                r.OpState = state;
                if (referenceNote is not null) r.ReferencedBy = referenceNote;
                changed = true;
            }
            if (changed) SaveRegistryUnlocked(registryPath, list);
        }
    }

    public static void MarkScopeVerified(BackupCategory category, string scopeId, string? keepLatestPath = null, string? registryPath = null)
    {
        registryPath ??= StorageCleanupContext.DefaultRegistryPath;
        lock (RegistrySync)
        {
            var list = LoadRegistryUnlocked(registryPath);
            var changed = false;
            foreach (var r in list.Where(x => x.Category == category && x.ScopeId.Equals(scopeId, StringComparison.OrdinalIgnoreCase)))
            {
                if (r.OpState == BackupOpState.InProgress)
                {
                    r.OpState = BackupOpState.VerifiedSuccess;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(keepLatestPath) &&
                    r.Path.Equals(Path.GetFullPath(keepLatestPath), StringComparison.OrdinalIgnoreCase))
                {
                    r.ReferencedBy = "latest-verified";
                    changed = true;
                }
            }
            if (changed) SaveRegistryUnlocked(registryPath, list);
        }
    }

    public static void UpdateScopeOperationState(BackupCategory category, string scopeId, BackupOpState state, string? referenceNote = null, string? registryPath = null)
    {
        registryPath ??= StorageCleanupContext.DefaultRegistryPath;
        lock (RegistrySync)
        {
            var list = LoadRegistryUnlocked(registryPath);
            var changed = false;
            foreach (var r in list.Where(x => x.Category == category && x.ScopeId.Equals(scopeId, StringComparison.OrdinalIgnoreCase)))
            {
                r.OpState = state;
                if (referenceNote is not null) r.ReferencedBy = referenceNote;
                changed = true;
            }
            if (changed) SaveRegistryUnlocked(registryPath, list);
        }
    }

    public static List<BackupRegistryRecord> LoadRegistry(string? registryPath = null)
    {
        registryPath ??= StorageCleanupContext.DefaultRegistryPath;
        lock (RegistrySync) return LoadRegistryUnlocked(registryPath);
    }

    private static List<BackupRegistryRecord> LoadRegistryUnlocked(string registryPath)
    {
        try
        {
            if (!File.Exists(registryPath)) return [];
            Paths.RejectLinks(registryPath);
            return JsonSerializer.Deserialize<List<BackupRegistryRecord>>(File.ReadAllText(registryPath), JsonFile.Format) ?? [];
        }
        catch { return []; }
    }

    private static void SaveRegistryUnlocked(string registryPath, List<BackupRegistryRecord> list)
    {
        JsonFile.Write(registryPath, list);
    }

    public static CleanupInspectionReport Inspect(StorageCleanupContext context)
    {
        var settings = CleanupSettings.Load(context.EffectiveSettingsPath);
        var registry = LoadRegistry(context.EffectiveRegistryPath);
        var items = new List<CleanupCandidate>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddItem(CleanupCandidate item)
        {
            var normalized = SafeNormalizePath(item.Path);
            if (!seenPaths.Add(normalized + "::" + item.Category)) return;
            items.Add(item);
        }

        InspectProgramUpdateItems(context, registry, AddItem);
        InspectWorkspaceItems(context, registry, AddItem);
        if (!string.IsNullOrWhiteSpace(context.ArchiveDirectory))
            InspectUserArchiveDirectory(context, settings, AddItem);

        var categories = BuildCategorySummaries(context.PlatformId, items);
        return new CleanupInspectionReport(DateTime.UtcNow, categories, items);
    }

    public static CleanupExecutionResult RotateAfterVerifiedSuccess(
        BackupCategory category,
        string scopeId,
        StorageCleanupContext context)
    {
        if (category is BackupCategory.UserArchives or BackupCategory.ProtectedAssets)
            return new(DateTime.UtcNow, true, CategoryTitle(category), 0, 0, 0, 0, [], [], ["该类别不支持自动清理"]);

        var settings = CleanupSettings.Load(context.EffectiveSettingsPath);
        if (!settings.IsAutoCleanEnabled(category))
            return new(DateTime.UtcNow, true, CategoryTitle(category), 0, 0, 0, 0, [], [], ["已关闭该类别自动清理"]);

        var report = Inspect(context);
        var candidates = report.Items
            .Where(i => i.Category == category &&
                        i.Status == CleanupItemStatus.Cleanable &&
                        i.CanSelect &&
                        (string.IsNullOrEmpty(scopeId) || i.ScopeId.Equals(scopeId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return ExecuteCleanupInternal(candidates, report.Items, context, settings, isAutomatic: true,
            scopeSummary: $"{CategoryTitle(category)}（{(string.IsNullOrEmpty(scopeId) ? "全局" : scopeId)}）");
    }

    public static CleanupExecutionResult ExecuteManualCleanup(
        IEnumerable<CleanupCandidate> selected,
        StorageCleanupContext context)
    {
        var selectedList = selected.ToList();
        var fresh = Inspect(context);
        var freshById = fresh.Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        var approved = new List<CleanupCandidate>();
        var preSkippedReasons = new List<string>();

        foreach (var sel in selectedList)
        {
            if (!freshById.TryGetValue(sel.Id, out var current))
            {
                preSkippedReasons.Add($"{sel.DisplayName}：预览后已不存在或超出授权范围，已跳过，请刷新清单");
                continue;
            }
            if (current.Status != CleanupItemStatus.Cleanable || !current.CanSelect)
            {
                preSkippedReasons.Add($"{sel.DisplayName}：状态已变为「{current.StatusLabel}」（{current.Reason}），已跳过，请刷新后重新确认");
                continue;
            }
            // Keep the exact snapshot (size, mtime, directory fingerprint) confirmed by the user, NOT fresh's mutated metadata!
            approved.Add(sel);
        }

        var settings = CleanupSettings.Load(context.EffectiveSettingsPath);
        return ExecuteCleanupInternal(approved.ToArray(), fresh.Items, context, settings, isAutomatic: false,
            scopeSummary: "手动勾选清理", preSkippedReasons);
    }

    private static CleanupExecutionResult ExecuteCleanupInternal(
        CleanupCandidate[] candidates,
        IReadOnlyList<CleanupCandidate> allItems,
        StorageCleanupContext context,
        CleanupSettings settings,
        bool isAutomatic,
        string scopeSummary,
        IEnumerable<string>? initialSkippedReasons = null)
    {
        var deletedNames = new List<string>();
        var skippedReasons = new List<string>(initialSkippedReasons ?? []);
        var retainedReasons = allItems
            .Where(i => i.Status != CleanupItemStatus.Cleanable)
            .Select(i => $"{i.DisplayName}（{StatusTitle(i.Status)}）：{i.Reason}")
            .Distinct()
            .ToList();
        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long freedBytes = 0;

        foreach (var item in candidates)
        {
            try
            {
                if (!IsWithinAuthorizedDirectory(item.Path, item.AuthorizedRoot) || IsReparsePointPath(item.Path))
                {
                    skippedReasons.Add($"{item.DisplayName}：路径越界或为符号链接，已跳过");
                    continue;
                }

                if (item.IsDirectory)
                {
                    if (File.Exists(item.Path) || !Directory.Exists(item.Path)) continue;
                    if (!isAutomatic && item.SnapshotDirFingerprint is not null)
                    {
                        var currentFingerprint = ComputeDirectoryFingerprint(item.Path);
                        if (!string.Equals(currentFingerprint, item.SnapshotDirFingerprint, StringComparison.Ordinal))
                        {
                            skippedReasons.Add($"{item.DisplayName}：目录内容已在预览确认后变化，已跳过，请刷新后重新确认");
                            continue;
                        }
                    }
                    var actualSize = MeasureDirectoryBytes(item.Path);
                    Directory.Delete(item.Path, recursive: true);
                    freedBytes += actualSize;
                    deletedNames.Add(item.DisplayName);
                    deletedPaths.Add(SafeNormalizePath(item.Path));
                }
                else
                {
                    if (Directory.Exists(item.Path) || !File.Exists(item.Path)) continue;
                    var info = new FileInfo(item.Path);
                    if (item.SnapshotLength.HasValue && info.Length != item.SnapshotLength.Value)
                    {
                        skippedReasons.Add($"{item.DisplayName}：文件大小已在预览确认后变化，已跳过，请刷新后重新确认");
                        continue;
                    }
                    if (item.SnapshotWriteUtc.HasValue && info.LastWriteTimeUtc != item.SnapshotWriteUtc.Value)
                    {
                        skippedReasons.Add($"{item.DisplayName}：文件修改时间已在预览确认后变化，已跳过，请刷新后重新确认");
                        continue;
                    }
                    if (!isAutomatic)
                    {
                        if (string.IsNullOrEmpty(item.SnapshotContentSha256))
                        {
                            skippedReasons.Add($"{item.DisplayName}：缺少确认前内容指纹，已跳过，请刷新后重新确认");
                            continue;
                        }
                        var liveSha = ComputeFileSha256Safe(item.Path);
                        if (!string.Equals(liveSha, item.SnapshotContentSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            skippedReasons.Add($"{item.DisplayName}：文件内容已在预览确认后变化（即使大小或时间相同），已跳过，请刷新后重新确认");
                            continue;
                        }
                    }
                    var fileLen = info.Length;
                    File.Delete(item.Path);
                    freedBytes += fileLen;
                    deletedNames.Add(item.DisplayName);
                    deletedPaths.Add(SafeNormalizePath(item.Path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SetupException)
            {
                skippedReasons.Add($"{item.DisplayName}：文件被占用或权限不足，已保留待下次重试");
            }
        }

        if (deletedPaths.Count > 0)
        {
            lock (RegistrySync)
            {
                var reg = LoadRegistryUnlocked(context.EffectiveRegistryPath);
                reg.RemoveAll(r => deletedPaths.Contains(SafeNormalizePath(r.Path)));
                SaveRegistryUnlocked(context.EffectiveRegistryPath, reg);
            }
        }

        var result = new CleanupExecutionResult(
            DateTime.UtcNow,
            isAutomatic,
            scopeSummary,
            deletedNames.Count,
            freedBytes,
            skippedReasons.Count,
            retainedReasons.Count,
            deletedNames,
            skippedReasons,
            retainedReasons);

        if (isAutomatic)
        {
            settings.LastAutoCleanup = result;
            try { settings.Save(context.EffectiveSettingsPath); } catch { }
        }
        return result;
    }

    private static void InspectProgramUpdateItems(
        StorageCleanupContext context,
        IReadOnlyList<BackupRegistryRecord> registry,
        Action<CleanupCandidate> add)
    {
        var appDir = string.IsNullOrWhiteSpace(context.AppDirectory) ? "" : Path.GetFullPath(context.AppDirectory);
        var tempDir = string.IsNullOrWhiteSpace(context.TempDirectory) ? Path.GetTempPath() : Path.GetFullPath(context.TempDirectory);

        var pending = !string.IsNullOrWhiteSpace(appDir) && Directory.Exists(appDir)
            ? SelfUpdater.ReadPendingUpdateNotice(appDir, context.EffectiveRegistryPath)
            : new SelfUpdater.PendingUpdateNotice(null, null, null);
        var hasPendingRecovery = !string.IsNullOrWhiteSpace(pending.ErrorReason) ||
                                 !string.IsNullOrWhiteSpace(pending.DeferredFile) ||
                                 pending.IsUpdateInProgress ||
                                 pending.IsRollbackIncomplete ||
                                 (!string.IsNullOrWhiteSpace(appDir) &&
                                  (File.Exists(Path.Combine(appDir, SelfUpdater.UpdateUnrecoveredListName)) ||
                                   SelfUpdater.HasUnresolvedRollbackManifest(appDir, context.EffectiveRegistryPath)));
        var deferredStageRoot = string.IsNullOrWhiteSpace(pending.DeferredStageDir) ? null : SafeNormalizePath(pending.DeferredStageDir);

        if (!string.IsNullOrWhiteSpace(appDir) && Directory.Exists(appDir))
        {
            // Blocker 5: Select the latest COMPLETE, VERIFIABLE, SAME-TRANSACTION rollback group (EXE + all RequiredSideFiles/absent proof).
            string GetGroupKey(BackupRegistryRecord r) =>
                !string.IsNullOrWhiteSpace(r.TransactionId)
                    ? $"tx:{r.TransactionId.Trim().ToLowerInvariant()}"
                    : $"pid:{ExtractBackupPidToken(r.Path) ?? r.Id}";

            bool IsBackupMemberVerifiable(BackupRegistryRecord rec)
            {
                if (rec.Role == "backup-absent")
                    return true;
                if (!File.Exists(rec.Path) || IsReparsePointPath(rec.Path))
                    return false;
                var liveSha = ComputeFileSha256Safe(rec.Path);
                if (string.IsNullOrWhiteSpace(liveSha))
                    return false;
                if (!string.IsNullOrWhiteSpace(rec.ContentSha256) &&
                    !liveSha.Equals(rec.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            }

            bool IsSidecarBackupPath(string path)
            {
                var name = Path.GetFileName(path);
                return SelfUpdater.RequiredSideFileNames.Any(s =>
                    name.StartsWith(s + ".", StringComparison.OrdinalIgnoreCase));
            }

            bool IsExeBackupRecord(BackupRegistryRecord rec) =>
                !IsSidecarBackupPath(rec.Path) &&
                Path.GetFileName(rec.Path).Contains(".exe.", StringComparison.OrdinalIgnoreCase) &&
                (rec.Role == "backup" || rec.Role == "backup-absent");

            bool IsCompleteVerifiableGroup(List<BackupRegistryRecord> group)
            {
                if (group.Count == 0) return false;
                var hasExe = group.Any(r =>
                    IsExeBackupRecord(r) &&
                    IsBackupMemberVerifiable(r));
                if (!hasExe) return false;

                foreach (var reqSide in SelfUpdater.RequiredSideFileNames)
                {
                    var sideRec = group.FirstOrDefault(r =>
                        (r.Role == "backup-sidecar" || r.Role == "backup-absent") &&
                        Path.GetFileName(r.Path).StartsWith(reqSide + ".", StringComparison.OrdinalIgnoreCase) &&
                        IsBackupMemberVerifiable(r));
                    if (sideRec is null) return false;
                }
                return true;
            }

            var verifiedGroups = registry
                .Where(r => r.Category == BackupCategory.ProgramUpdate &&
                            r.ScopeId.Equals(appDir, StringComparison.OrdinalIgnoreCase) &&
                            (r.Role == "backup" || r.Role == "backup-sidecar" || r.Role == "backup-absent") &&
                            r.OpState == BackupOpState.VerifiedSuccess)
                .GroupBy(GetGroupKey)
                .Select(g => (Key: g.Key, Records: g.ToList(), MaxCreatedUtc: g.Max(x => x.CreatedUtc)))
                .OrderByDescending(g => g.MaxCreatedUtc)
                .ToList();

            var completeGroupKeys = verifiedGroups
                .Where(g => IsCompleteVerifiableGroup(g.Records))
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var anchorGroup = verifiedGroups.FirstOrDefault(g => completeGroupKeys.Contains(g.Key));
            var anchorGroupKey = anchorGroup.Key;
            var anchorCreatedUtc = anchorGroup.MaxCreatedUtc;

            foreach (var file in SafeEnumerateFiles(appDir, "*.bak"))
            {
                var fileName = Path.GetFileName(file);
                if (!IsKnownUpdateBackupName(fileName)) continue;
                var fullFile = Path.GetFullPath(file);
                var regMatch = registry.FirstOrDefault(r =>
                    r.Category == BackupCategory.ProgramUpdate &&
                    r.ScopeId.Equals(appDir, StringComparison.OrdinalIgnoreCase) &&
                    (r.Role == "backup" || r.Role == "backup-sidecar") &&
                    r.Path.Equals(fullFile, StringComparison.OrdinalIgnoreCase));
                var (len, mtime, isLink) = InspectFileMeta(file);
                if (isLink)
                {
                    add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序原地更新", appDir, appDir, file, len, mtime,
                        CleanupItemStatus.Retained, "路径为符号链接，跳过", canSelect: false, appDir));
                    continue;
                }
                if (hasPendingRecovery)
                {
                    add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序原地更新", appDir, appDir, file, len, mtime,
                        CleanupItemStatus.Pending, "当前存在未完成更新、异常退出或回滚待核实，保留回退备份", canSelect: false, appDir));
                    continue;
                }
                if (regMatch is null)
                {
                    add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序原地更新", "unowned", "未登记备份", file, len, mtime,
                        CleanupItemStatus.Retained, "未确认更新事务归属的备份文件，自动清理保留，请人工核实", canSelect: false, appDir));
                    continue;
                }
                if (regMatch.OpState != BackupOpState.VerifiedSuccess)
                {
                    add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序原地更新", appDir, appDir, file, len, mtime,
                        CleanupItemStatus.Pending, "当前存在未完成更新或回滚待核实，保留回退备份", canSelect: false, appDir));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(anchorGroupKey))
                {
                    add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序原地更新", appDir, appDir, file, len, mtime,
                        CleanupItemStatus.Retained, "未找到包含主程序与必需随包文件的完整可验证回退组，保守保留当前备份", canSelect: false, appDir));
                    continue;
                }
                var groupKey = GetGroupKey(regMatch);
                if (groupKey.Equals(anchorGroupKey, StringComparison.OrdinalIgnoreCase))
                {
                    add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序原地更新", appDir, appDir, file, len, mtime,
                        CleanupItemStatus.Retained, "保留最近一份完整可用的已验证上一版回退组", canSelect: false, appDir));
                    continue;
                }
                var groupInfo = verifiedGroups.FirstOrDefault(g => g.Key.Equals(groupKey, StringComparison.OrdinalIgnoreCase));
                if (!completeGroupKeys.Contains(groupKey) && groupInfo.MaxCreatedUtc >= anchorCreatedUtc)
                {
                    add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序原地更新", appDir, appDir, file, len, mtime,
                        CleanupItemStatus.Retained, "较新备份组不完整或成员无法核实，已保留较早完整组并保留当前残留待核实", canSelect: false, appDir));
                    continue;
                }
                add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序原地更新", appDir, appDir, file, len, mtime,
                    CleanupItemStatus.Cleanable, "已被更近一套完整可用的已验证更新回退组替代的旧更新备份", canSelect: true, appDir));
            }
        }

        if (Directory.Exists(tempDir))
        {
            foreach (var stage in SafeEnumerateDirectories(tempDir, "claudome-stage-*"))
            {
                var full = SafeNormalizePath(stage);
                var isLink = IsReparsePointPath(full);
                var size = isLink ? 0 : MeasureDirectoryBytes(full);
                var mtime = Directory.GetLastWriteTimeUtc(full);
                var regMatch = registry.FirstOrDefault(r =>
                    r.Category == BackupCategory.ProgramUpdate &&
                    r.Role == "staging" &&
                    SafeNormalizePath(r.StageRoot.Length > 0 ? r.StageRoot : r.Path).Equals(full, StringComparison.OrdinalIgnoreCase));

                if (isLink)
                {
                    add(MakeDirCandidate(BackupCategory.ProgramUpdate, "程序原地更新", appDir, appDir, full, size, mtime,
                        CleanupItemStatus.Retained, "暂存目录为符号链接，已跳过", canSelect: false, tempDir));
                    continue;
                }
                if (deferredStageRoot is not null && full.Equals(deferredStageRoot, StringComparison.OrdinalIgnoreCase))
                {
                    add(MakeDirCandidate(BackupCategory.ProgramUpdate, "程序原地更新", appDir, appDir, full, size, mtime,
                        CleanupItemStatus.Pending, $"随包文件 {pending.DeferredFile} 待补全，保留对应暂存", canSelect: false, tempDir));
                    continue;
                }
                if (regMatch is null)
                {
                    add(MakeDirCandidate(BackupCategory.ProgramUpdate, "程序原地更新", "unowned", "未登记暂存", full, size, mtime,
                        CleanupItemStatus.Retained, "无当前安装归属登记的暂存目录（可能属于其他安装或历史残留），不自动清理", canSelect: false, tempDir));
                    continue;
                }
                if (!string.IsNullOrEmpty(appDir) && !regMatch.ScopeId.Equals(appDir, StringComparison.OrdinalIgnoreCase))
                {
                    add(MakeDirCandidate(BackupCategory.ProgramUpdate, "程序原地更新", regMatch.ScopeId, regMatch.ScopeLabel, full, size, mtime,
                        CleanupItemStatus.Retained, $"属于另一个安装目录（{regMatch.ScopeLabel}），跨安装隔离保留", canSelect: false, tempDir));
                    continue;
                }
                if (regMatch.OpState != BackupOpState.VerifiedSuccess || hasPendingRecovery)
                {
                    add(MakeDirCandidate(BackupCategory.ProgramUpdate, "程序原地更新", appDir, appDir, full, size, mtime,
                        CleanupItemStatus.Pending, "对应更新尚未验证完成或需要恢复，保留暂存", canSelect: false, tempDir));
                    continue;
                }
                add(MakeDirCandidate(BackupCategory.ProgramUpdate, "程序原地更新", appDir, appDir, full, size, mtime,
                    CleanupItemStatus.Cleanable, "更新已验证完成且无待补文件引用，可清理暂存目录", canSelect: true, tempDir));
            }

            foreach (var script in SafeEnumerateFiles(tempDir, "claudome-update-*.cmd"))
            {
                var full = SafeNormalizePath(script);
                var (len, mtime, isLink) = InspectFileMeta(full);
                var regMatch = registry.FirstOrDefault(r =>
                    r.Category == BackupCategory.ProgramUpdate &&
                    r.Role == "swap-script" &&
                    SafeNormalizePath(r.Path).Equals(full, StringComparison.OrdinalIgnoreCase));
                if (isLink)
                {
                    add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序更新脚本", appDir, appDir, full, len, mtime,
                        CleanupItemStatus.Retained, "路径为符号链接，跳过", canSelect: false, tempDir));
                    continue;
                }
                if (regMatch is null)
                {
                    add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序更新脚本", "unowned", "未登记脚本", full, len, mtime,
                        CleanupItemStatus.Retained, "未登记归属的更新脚本，保留待人工核实", canSelect: false, tempDir));
                    continue;
                }
                if (!string.IsNullOrEmpty(appDir) && !regMatch.ScopeId.Equals(appDir, StringComparison.OrdinalIgnoreCase))
                {
                    add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序更新脚本", regMatch.ScopeId, regMatch.ScopeLabel, full, len, mtime,
                        CleanupItemStatus.Retained, $"属于另一个安装目录（{regMatch.ScopeLabel}），保留", canSelect: false, tempDir));
                    continue;
                }
                if (regMatch.OpState != BackupOpState.VerifiedSuccess || hasPendingRecovery)
                {
                    add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序更新脚本", appDir, appDir, full, len, mtime,
                        CleanupItemStatus.Pending, "对应更新尚未验证完成或需要恢复，保留脚本", canSelect: false, tempDir));
                    continue;
                }
                add(MakeFileCandidate(BackupCategory.ProgramUpdate, "程序更新脚本", appDir, appDir, full, len, mtime,
                    CleanupItemStatus.Cleanable, "更新已验证完成的可清理临时脚本", canSelect: true, tempDir));
            }
        }
    }

    public static bool IsWorkspaceOwnedByEdition(string root, string? expectedPlatformId)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var full = SafeNormalizePath(root);
            if (!Directory.Exists(full) || IsReparsePointPath(full)) return false;
            var marker = Path.Combine(full, ".claude-setup-workspace.json");
            if (!File.Exists(marker) || IsReparsePointPath(marker)) return false;
            using var markerDoc = JsonDocument.Parse(File.ReadAllText(marker));
            if (!markerDoc.RootElement.TryGetProperty("application", out var appProp) ||
                appProp.GetString() != "claude-environment-setup")
                return false;
            if (string.IsNullOrWhiteSpace(expectedPlatformId)) return true;

            foreach (var stateFile in new[] { Path.Combine(full, "setup-state.json"), TaskCheckpointStore.BackupPath(Path.Combine(full, "setup-state.json")) })
            {
                if (!File.Exists(stateFile) || IsReparsePointPath(stateFile)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(stateFile));
                    if (doc.RootElement.TryGetProperty("Options", out var opts) &&
                        opts.TryGetProperty("PlatformId", out var plat) &&
                        plat.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(plat.GetString()))
                    {
                        return string.Equals(plat.GetString(), expectedPlatformId, StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch { }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void InspectWorkspaceItems(
        StorageCleanupContext context,
        IReadOnlyList<BackupRegistryRecord> registry,
        Action<CleanupCandidate> add)
    {
        foreach (var rawRoot in context.WorkspaceRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var root = SafeNormalizePath(rawRoot);
            if (!Directory.Exists(root)) continue;
            if (!string.IsNullOrWhiteSpace(context.ExpectedPlatformId) &&
                !IsWorkspaceOwnedByEdition(root, context.ExpectedPlatformId))
                continue;
            var scopeId = TryReadWorkspaceId(root) ?? root;
            var scopeLabel = TryReadVmName(root) ?? Path.GetFileName(root);

            // 1. Checkpoints
            var primary = Path.Combine(root, "setup-state.json");
            var backup = TaskCheckpointStore.BackupPath(primary);
            var primaryValid = IsValidCheckpointFile(primary, root);

            if (File.Exists(primary))
            {
                var (len, mtime, _) = InspectFileMeta(primary);
                add(MakeFileCandidate(BackupCategory.ProtectedAssets, "任务主状态", scopeId, scopeLabel, primary, len, mtime,
                    CleanupItemStatus.InUse, "当前任务主存档（受保护资产）", canSelect: false, root));
            }
            if (File.Exists(backup))
            {
                var (len, mtime, isLink) = InspectFileMeta(backup);
                var status = !primaryValid ? CleanupItemStatus.Pending : CleanupItemStatus.Retained;
                var reason = !primaryValid
                    ? "主存档损坏或缺少字段，当前正依赖上一份有效自动存档恢复"
                    : "保留上一份有效自动存档（保留优先于数量上限）";
                add(MakeFileCandidate(BackupCategory.TaskCheckpoint, "任务检查点", scopeId, scopeLabel, backup, len, mtime,
                    isLink ? CleanupItemStatus.Retained : status, isLink ? "路径为符号链接，跳过" : reason, canSelect: false, root));
            }

            var historyDir = Path.Combine(root, "checkpoints-history");
            if (Directory.Exists(historyDir))
            {
                foreach (var hist in SafeEnumerateFiles(historyDir, "setup-state.prev-*.json").OrderByDescending(File.GetLastWriteTimeUtc))
                {
                    var (len, mtime, isLink) = InspectFileMeta(hist);
                    if (isLink)
                    {
                        add(MakeFileCandidate(BackupCategory.TaskCheckpoint, "任务检查点", scopeId, scopeLabel, hist, len, mtime,
                            CleanupItemStatus.Retained, "路径为符号链接，跳过", canSelect: false, root));
                        continue;
                    }
                    if (!primaryValid)
                    {
                        add(MakeFileCandidate(BackupCategory.TaskCheckpoint, "任务检查点", scopeId, scopeLabel, hist, len, mtime,
                            CleanupItemStatus.Pending, "当前主存档异常，暂停清理历史检查点", canSelect: false, root));
                        continue;
                    }
                    add(MakeFileCandidate(BackupCategory.TaskCheckpoint, "任务检查点", scopeId, scopeLabel, hist, len, mtime,
                        CleanupItemStatus.Cleanable, "已被更近一份有效自动存档取代的历史检查点", canSelect: true, root));
                }
            }

            // 2. Route & Credential revisions in Private (B04: require confirmed readable task state before allowing older revision cleanup!)
            var privateDir = Path.Combine(root, "private");
            if (Directory.Exists(privateDir))
            {
                var routeStatus = ResolveRouteRevisions(root, registry, scopeId);
                foreach (var credFile in SafeEnumerateFiles(privateDir, "credentials*"))
                {
                    var name = Path.GetFileNameWithoutExtension(credFile);
                    var ext = Path.GetExtension(credFile);
                    if (!ext.Equals(".dpapi", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".mac", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var isDefault = name.Equals("credentials", StringComparison.OrdinalIgnoreCase);
                    var rev = isDefault ? "" : (name.StartsWith("credentials-", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(name[12..], "N", out _) ? name[12..] : null);
                    if (rev is null)
                    {
                        var (uLen, uTime, _) = InspectFileMeta(credFile);
                        add(MakeFileCandidate(BackupCategory.RouteConfig, "线路与凭据", scopeId, scopeLabel, credFile, uLen, uTime,
                            CleanupItemStatus.Retained, "名称不符合受管版本格式，跳过", canSelect: false, privateDir));
                        continue;
                    }
                    var (len, mtime, isLink) = InspectFileMeta(credFile);
                    if (isLink)
                    {
                        add(MakeFileCandidate(BackupCategory.RouteConfig, "线路与凭据", scopeId, scopeLabel, credFile, len, mtime,
                            CleanupItemStatus.Retained, "路径为符号链接，跳过", canSelect: false, privateDir));
                        continue;
                    }
                    if (routeStatus.ActiveRev is not null && rev.Equals(routeStatus.ActiveRev, StringComparison.OrdinalIgnoreCase))
                    {
                        add(MakeFileCandidate(BackupCategory.ProtectedAssets, "线路与凭据", scopeId, scopeLabel, credFile, len, mtime,
                            CleanupItemStatus.InUse, "当前正在使用的已验证线路凭据", canSelect: false, privateDir));
                    }
                    else if (!routeStatus.CanCleanSuperseded)
                    {
                        var refMatch = routeStatus.RecoveryOrPreviousRev is not null && rev.Equals(routeStatus.RecoveryOrPreviousRev, StringComparison.OrdinalIgnoreCase);
                        var pendingReason = refMatch
                            ? "恢复流程或未完成操作正引用该线路凭据，保留待核实"
                            : routeStatus.PendingReason;
                        add(MakeFileCandidate(BackupCategory.RouteConfig, "线路与凭据", scopeId, scopeLabel, credFile, len, mtime,
                            CleanupItemStatus.Pending, pendingReason, canSelect: false, privateDir));
                    }
                    else if (routeStatus.RecoveryOrPreviousRev is not null && rev.Equals(routeStatus.RecoveryOrPreviousRev, StringComparison.OrdinalIgnoreCase))
                    {
                        add(MakeFileCandidate(BackupCategory.RouteConfig, "线路与凭据", scopeId, scopeLabel, credFile, len, mtime,
                            CleanupItemStatus.Retained, "保留上一份确认可用的线路凭据备份", canSelect: false, privateDir));
                    }
                    else
                    {
                        add(MakeFileCandidate(BackupCategory.RouteConfig, "线路与凭据", scopeId, scopeLabel, credFile, len, mtime,
                            CleanupItemStatus.Cleanable, "已被新线路与有效存档取代的更早凭据版本", canSelect: true, privateDir));
                    }
                }
            }

            // 3. Protected VMs / disks / ISOs visible in workspace
            foreach (var asset in EnumerateTopProtectedAssets(root))
            {
                var (len, mtime, _) = InspectFileMeta(asset);
                add(MakeFileCandidate(BackupCategory.ProtectedAssets, "受保护资产", scopeId, scopeLabel, asset, len, mtime,
                    CleanupItemStatus.Retained, "虚拟机、磁盘或系统镜像不纳入普通清理", canSelect: false, root));
            }
        }
    }

    private static bool MatchesExpectedArchivePackage(string zipPath, string? expectedPackagePrefix)
    {
        if (string.IsNullOrWhiteSpace(expectedPackagePrefix)) return true;
        var name = Path.GetFileName(zipPath);
        if (!name.StartsWith(expectedPackagePrefix + "_", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries.Any(e => Path.GetFileName(e.FullName).Equals("Claudome.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static void InspectUserArchiveDirectory(
        StorageCleanupContext context,
        CleanupSettings settings,
        Action<CleanupCandidate> add)
    {
        var dir = Path.GetFullPath(context.ArchiveDirectory!);
        if (!Directory.Exists(dir) || IsReparsePointPath(dir)) return;

        var activeArchive = string.IsNullOrWhiteSpace(context.ActiveArchivePath) ? "" : SafeNormalizePath(context.ActiveArchivePath);
        var zips = SafeEnumerateFiles(dir, "*.zip").OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        var latestOwnedPath = zips.FirstOrDefault(z => MatchesExpectedArchivePackage(z, context.ExpectedPackagePrefix));

        foreach (var zip in zips)
        {
            var full = SafeNormalizePath(zip);
            var (len, mtime, isLink) = InspectFileMeta(full);
            if (isLink)
            {
                add(MakeFileCandidate(BackupCategory.UserArchives, "用户选择的交付目录", dir, Path.GetFileName(dir), full, len, mtime,
                    CleanupItemStatus.Retained, "路径为符号链接，跳过", canSelect: false, dir));
                continue;
            }
            if (!MatchesExpectedArchivePackage(full, context.ExpectedPackagePrefix))
            {
                add(MakeFileCandidate(BackupCategory.UserArchives, "用户选择的交付目录", dir, Path.GetFileName(dir), full, len, mtime,
                    CleanupItemStatus.Retained, "非当前专版确认归属的交付包（普通版或其他压缩包），已隔离保留", canSelect: false, dir));
                continue;
            }
            if (full.Equals(activeArchive, StringComparison.OrdinalIgnoreCase))
            {
                add(MakeFileCandidate(BackupCategory.UserArchives, "用户选择的交付目录", dir, Path.GetFileName(dir), full, len, mtime,
                    CleanupItemStatus.InUse, "当前正在使用的安装包，保留", canSelect: false, dir));
                continue;
            }
            if (settings.PinnedArchivePaths.Contains(full))
            {
                add(MakeFileCandidate(BackupCategory.UserArchives, "用户选择的交付目录", dir, Path.GetFileName(dir), full, len, mtime,
                    CleanupItemStatus.Retained, "已标记保留的历史交付包", canSelect: false, dir));
                continue;
            }
            if (latestOwnedPath is not null && full.Equals(SafeNormalizePath(latestOwnedPath), StringComparison.OrdinalIgnoreCase))
            {
                add(MakeFileCandidate(BackupCategory.UserArchives, "用户选择的交付目录", dir, Path.GetFileName(dir), full, len, mtime,
                    CleanupItemStatus.Retained, "默认保留该目录最近一份可用交付包；仅手动或取消保留后处理更早版本", canSelect: false, dir));
                continue;
            }
            add(MakeFileCandidate(BackupCategory.UserArchives, "用户选择的交付目录", dir, Path.GetFileName(dir), full, len, mtime,
                CleanupItemStatus.Cleanable, "用户显式选定目录中的历史压缩包，仅手动确认后删除", canSelect: true, dir));
        }
    }

    private static List<CleanupCategorySummary> BuildCategorySummaries(string platformId, List<CleanupCandidate> items)
    {
        var order = new[]
        {
            BackupCategory.ProgramUpdate,
            BackupCategory.TaskCheckpoint,
            BackupCategory.RouteConfig,
            BackupCategory.UserArchives,
            BackupCategory.ProtectedAssets
        };
        return order.Select(cat =>
        {
            var group = items.Where(i => i.Category == cat).ToList();
            var cleanable = group.Where(i => i.Status == CleanupItemStatus.Cleanable).ToList();
            var note = cat switch
            {
                BackupCategory.ProgramUpdate when platformId == "mac" && group.Count == 0 => "Mac 预览版暂未接入 Windows 原地 EXE 替换，当前无本机更新备份",
                BackupCategory.UserArchives when group.Count == 0 => "需由用户先点击「选择历史包目录预览」显式选取目录，从不自动扫描与删除",
                BackupCategory.ProtectedAssets => "虚拟机、虚拟磁盘、ISO 与活动配置始终受保护",
                _ => ""
            };
            return new CleanupCategorySummary(cat, CategoryTitle(cat), group.Count, group.Sum(x => x.SizeBytes),
                cleanable.Count, cleanable.Sum(x => x.SizeBytes), note);
        }).ToList();
    }

    private sealed record RouteRevisionStatus(bool CanCleanSuperseded, string? ActiveRev, string? RecoveryOrPreviousRev, string PendingReason);

    private static RouteRevisionStatus ResolveRouteRevisions(
        string root,
        IReadOnlyList<BackupRegistryRecord> registry,
        string scopeId)
    {
        var normalizedRoot = SafeNormalizePath(root);
        var primaryPath = Path.Combine(root, "setup-state.json");
        var backupPath = TaskCheckpointStore.BackupPath(primaryPath);

        SessionState? primaryState = null;
        if (File.Exists(primaryPath) && !IsReparsePointPath(primaryPath))
        {
            try { primaryState = TaskCheckpointStore.DeserializeValid(primaryPath, normalizedRoot); } catch { }
        }

        var backupExists = File.Exists(backupPath);
        SessionState? backupState = null;
        if (backupExists && !IsReparsePointPath(backupPath))
        {
            try { backupState = TaskCheckpointStore.DeserializeValid(backupPath, normalizedRoot); } catch { }
        }

        if (primaryState is null && backupState is null)
        {
            return new(false, null, null, "主存档与上一份备份均不可读或缺失，无法核实线路凭据引用关系，保留待恢复核实");
        }

        if (primaryState is null && backupState is not null)
        {
            var recoveryRev = backupState.Options.Remember ? backupState.CredentialsRevision : null;
            return new(false, null, recoveryRev, "主存档异常且正在依赖上一份备份恢复，保留相关线路凭据");
        }

        // primaryState != null from here on
        var activeRev = primaryState!.Options.Remember ? primaryState.CredentialsRevision : null;
        if (backupExists && backupState is null)
        {
            return new(false, activeRev, null, "上一份任务备份损坏或不可读，暂停清理历史线路凭据");
        }

        if (registry.Any(r => r.Category == BackupCategory.RouteConfig &&
                              r.ScopeId.Equals(scopeId, StringComparison.OrdinalIgnoreCase) &&
                              r.OpState != BackupOpState.VerifiedSuccess))
        {
            var prevCandidate = backupState is not null && backupState.Options.Remember &&
                                !string.Equals(backupState.CredentialsRevision, activeRev, StringComparison.OrdinalIgnoreCase)
                ? backupState.CredentialsRevision
                : null;
            return new(false, activeRev, prevCandidate, "存在进行中或失败的线路切换操作，保留当前与回退线路凭据");
        }

        string? prevRev = null;
        if (backupState is not null && backupState.Options.Remember &&
            !string.Equals(backupState.CredentialsRevision, activeRev, StringComparison.OrdinalIgnoreCase))
        {
            prevRev = backupState.CredentialsRevision;
        }
        else
        {
            var regPrev = registry
                .Where(r => r.Category == BackupCategory.RouteConfig &&
                            r.ScopeId.Equals(scopeId, StringComparison.OrdinalIgnoreCase) &&
                            r.OpState == BackupOpState.VerifiedSuccess &&
                            !string.IsNullOrEmpty(r.ReferencedBy) &&
                            !r.ReferencedBy.Equals(activeRev ?? "", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.CreatedUtc)
                .FirstOrDefault();
            if (regPrev is not null)
                prevRev = regPrev.ReferencedBy;
        }

        return new(true, activeRev, prevRev, "");
    }

    private static string? ExtractBackupPidToken(string path)
    {
        var m = Regex.Match(Path.GetFileName(path), @"\.(\d+)\.bak$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool IsKnownUpdateBackupName(string fileName)
    {
        if (Regex.IsMatch(fileName, @"^Claudome\.exe\.\d+\.bak$", RegexOptions.IgnoreCase)) return true;
        foreach (var req in new[] { "PROJECT-LICENSE.txt", "Gateway-THIRD-PARTY-NOTICES.txt" })
        {
            if (Regex.IsMatch(fileName, $@"^{Regex.Escape(req)}\.\d+\.bak$", RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsValidCheckpointFile(string path, string root)
    {
        if (!File.Exists(path) || IsReparsePointPath(path)) return false;
        try
        {
            _ = TaskCheckpointStore.DeserializeValid(path, SafeNormalizePath(root));
            return true;
        }
        catch { return false; }
    }

    private static string? TryReadWorkspaceId(string root)
    {
        foreach (var candidate in new[] { Path.Combine(root, "setup-state.json"), TaskCheckpointStore.BackupPath(Path.Combine(root, "setup-state.json")) })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(candidate), JsonFile.Format);
                if (!string.IsNullOrWhiteSpace(s?.Options?.WorkspaceId)) return s.Options.WorkspaceId;
            }
            catch { }
        }
        return null;
    }

    private static string? TryReadVmName(string root)
    {
        foreach (var candidate in new[] { Path.Combine(root, "setup-state.json"), TaskCheckpointStore.BackupPath(Path.Combine(root, "setup-state.json")) })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(candidate), JsonFile.Format);
                if (!string.IsNullOrWhiteSpace(s?.Options?.VmName)) return s.Options.VmName;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateTopProtectedAssets(string root)
    {
        var machinesDir = Path.Combine(root, "machines");
        if (!Directory.Exists(machinesDir)) yield break;
        foreach (var vmDir in SafeEnumerateDirectories(machinesDir, "*"))
        {
            foreach (var file in SafeEnumerateFiles(vmDir, "*"))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".vmx" or ".vmdk" or ".vhdx" or ".vdi" or ".qcow2" or ".iso")
                    yield return file;
            }
        }
    }

    private static CleanupCandidate MakeFileCandidate(
        BackupCategory cat, string sourceFeature, string scopeId, string scopeLabel,
        string path, long size, DateTime mtime, CleanupItemStatus status, string reason, bool canSelect, string authorizedRoot) =>
        new(
            Id: $"{cat}::{SafeNormalizePath(path)}",
            Category: cat,
            CategoryLabel: CategoryTitle(cat),
            SourceFeature: sourceFeature,
            ScopeId: scopeId,
            ScopeLabel: scopeLabel,
            Path: path,
            DisplayName: Path.GetFileName(path),
            IsDirectory: false,
            SizeBytes: size,
            ModifiedUtc: mtime,
            Status: status,
            StatusLabel: StatusTitle(status),
            Reason: reason,
            CanSelect: canSelect,
            SnapshotLength: size,
            SnapshotWriteUtc: mtime,
            AuthorizedRoot: SafeNormalizePath(authorizedRoot),
            SnapshotDirFingerprint: null,
            SnapshotContentSha256: status == CleanupItemStatus.Cleanable ? ComputeFileSha256Safe(path) : null);

    private static CleanupCandidate MakeDirCandidate(
        BackupCategory cat, string sourceFeature, string scopeId, string scopeLabel,
        string path, long size, DateTime mtime, CleanupItemStatus status, string reason, bool canSelect, string authorizedRoot) =>
        new(
            Id: $"{cat}::{SafeNormalizePath(path)}",
            Category: cat,
            CategoryLabel: CategoryTitle(cat),
            SourceFeature: sourceFeature,
            ScopeId: scopeId,
            ScopeLabel: scopeLabel,
            Path: path,
            DisplayName: Path.GetFileName(path),
            IsDirectory: true,
            SizeBytes: size,
            ModifiedUtc: mtime,
            Status: status,
            StatusLabel: StatusTitle(status),
            Reason: reason,
            CanSelect: canSelect,
            SnapshotLength: null,
            SnapshotWriteUtc: null,
            AuthorizedRoot: SafeNormalizePath(authorizedRoot),
            SnapshotDirFingerprint: status == CleanupItemStatus.Cleanable ? ComputeDirectoryFingerprint(path) : null,
            SnapshotContentSha256: null);

    internal static string ComputeFileSha256Safe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: false);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch { return ""; }
    }

    internal static string ComputeDirectoryFingerprint(string dir)
    {
        if (!Directory.Exists(dir) || IsReparsePointPath(dir)) return "";
        try
        {
            var root = SafeNormalizePath(dir);
            var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(p =>
                {
                    var rel = Path.GetRelativePath(root, p);
                    if (IsReparsePointPath(p)) return $"L|{rel}";
                    if (Directory.Exists(p)) return $"D|{rel}";
                    var info = new FileInfo(p);
                    var memberHash = ComputeFileSha256Safe(p);
                    return $"F|{rel}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{memberHash}";
                })
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", entries)))).ToLowerInvariant();
        }
        catch { return Guid.NewGuid().ToString("N"); }
    }

    internal static bool IsWithinAuthorizedDirectory(string candidatePath, string authorizedRoot)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(authorizedRoot)) return false;
        var root = SafeNormalizePath(authorizedRoot);
        var full = SafeNormalizePath(candidatePath);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsReparsePointPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch { return true; }
    }

    private static (long len, DateTime mtime, bool isLink) InspectFileMeta(string path)
    {
        try
        {
            var isLink = IsReparsePointPath(path);
            var info = new FileInfo(path);
            return (isLink ? 0 : info.Length, info.LastWriteTimeUtc, isLink);
        }
        catch { return (0, DateTime.MinValue, true); }
    }

    private static long MeasureDirectoryBytes(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { if (!IsReparsePointPath(file)) total += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly); } catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir, string pattern)
    {
        try { return Directory.GetDirectories(dir, pattern, SearchOption.TopDirectoryOnly); } catch { return []; }
    }

    private static string SafeNormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
