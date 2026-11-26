using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed record ReleaseManifest(
    string Version,
    string Tag,
    string ZipName,
    string ZipSha256,
    string ExeSha256,
    string Notes,
    string PublishedAtUtc,
    string Channel = "standard");

public sealed record UpdateCheckResult(
    bool HasUpdate,
    ReleaseManifest? Manifest,
    string DownloadUrl,
    string StatusMessage);

public sealed record StagedUpdate(
    string Version,
    string StagingDirectory,
    string PayloadDirectory,
    string ExePath,
    string ExeSha256,
    IReadOnlyDictionary<string, string>? RequiredSideFileSha256 = null);

/// <summary>
/// Checks GitHub Releases manifests, downloads and SHA-256 verifies Windows update archives,
/// enforces anti-downgrade and Zip-Slip checks, and performs atomic in-place EXE swap + restart.
/// </summary>
public static class SelfUpdater
{
    public const string DefaultManifestUrl = "https://github.com/binlin0120/Claudome/releases/latest/download/latest.json";
    public const string DefaultReleaseBaseUrl = "https://github.com/binlin0120/Claudome/releases/download";

    private static readonly string[] RequiredSideFiles =
    [
        "PROJECT-LICENSE.txt",
        "Gateway-THIRD-PARTY-NOTICES.txt"
    ];

    private static readonly string[] DeferrableSideFiles =
    [
        "快速开始.txt"
    ];

    private static readonly string[] PayloadSideFiles =
    [
        .. RequiredSideFiles,
        .. DeferrableSideFiles
    ];

    public const string UpdateErrorMarkerName = "claudome-update-error.txt";
    public const string UpdateDeferredManifestName = "claudome-update-deferred.json";
    public const string UpdatePendingTxName = "claudome-update-pending.json";
    public const string UpdateCompletedReceiptName = "claudome-update-completed.json";
    public const string UpdateRollbackManifestName = "claudome-update-rollback.json";
    public const string UpdateUnrecoveredListName = "claudome-update-unrecovered.txt";
    public const string UpdateSwapLockName = "claudome-update.lock";
    public const string UpdateVerifiedMarkerName = "claudome-update-verified.json";

    internal static readonly string[] RequiredSideFileNames = RequiredSideFiles;

    private static readonly string LockWrapperEncodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes("""
        $ErrorActionPreference = "Stop"
        $fs = $null
        for ($i = 0; $i -lt 40; $i++) {
            try {
                $fs = [System.IO.File]::Open($env:CLAUDOME_LOCK_PATH, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
                break
            } catch [System.IO.IOException] {
                if ($env:CLAUDOME_HANDOFF -ne "1" -or $i -eq 39) { throw }
                Start-Sleep -Milliseconds 50
            }
        }
        try {
            $psi = [System.Diagnostics.ProcessStartInfo]::new($env:ComSpec, ('/d /c "chcp 65001 >nul && "' + $env:CLAUDOME_SELF_PATH + '""'))
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $p = [System.Diagnostics.Process]::Start($psi)
            $p.WaitForExit()
            exit $p.ExitCode
        } finally {
            if ($null -ne $fs) { $fs.Dispose() }
        }
        """));

    internal sealed record PendingUpdateTransaction(
        string TransactionId,
        string TargetExeSha256,
        string TargetExePath,
        string StageDir,
        string ScriptPath,
        DateTime CreatedUtc,
        int SwapProcessId = 0,
        long SwapProcessStartTicks = 0,
        Dictionary<string, string>? ExpectedRequiredSideFileSha256 = null);

    internal sealed record VerifiedUpdateReceipt(
        string TransactionId,
        string TargetExeSha256,
        Dictionary<string, string> ExpectedRequiredSideFileSha256,
        DateTime VerifiedUtc);

    public sealed record RollbackFileEntry(
        string FileName,
        bool ExistedBefore,
        string OriginalSha256,
        string BackupFileName);

    public sealed record RollbackManifest(
        string TransactionId,
        string AppDirectory,
        int BackupPid,
        DateTime CapturedUtc,
        List<RollbackFileEntry> Items);

    public sealed record RollbackResumeResult(
        bool Succeeded,
        string[] RestoredFiles,
        string[] RemainingFiles,
        string Message);

    public static Version CurrentVersion(Assembly? assembly = null)
    {
        var target = assembly ?? Assembly.GetEntryAssembly() ?? typeof(SelfUpdater).Assembly;
        var info = target.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info) && TryParseVersion(info, out var parsed)) return parsed;
        var file = target.GetName().Version;
        return file is not null
            ? new Version(file.Major, file.Minor, Math.Max(0, file.Build))
            : new Version(0, 4, 7);
    }

    public static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[1..];
        var plus = cleaned.IndexOf('+');
        if (plus >= 0) cleaned = cleaned[..plus];
        var dash = cleaned.IndexOf('-');
        if (dash >= 0) cleaned = cleaned[..dash];
        if (!Version.TryParse(cleaned, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    public static bool IsValidSha256(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && Regex.IsMatch(hex, "^[0-9a-f]{64}$", RegexOptions.IgnoreCase);

    public static ReleaseManifest ParseAndValidateManifest(string json, string? expectedChannel = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new SetupException("更新清单为空，无法校验版本。");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string ReadRequired(string name)
        {
            if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
                throw new SetupException($"更新清单缺少字段：{name}。");
            return prop.GetString()?.Trim() ?? "";
        }

        var version = ReadRequired("version");
        var tag = ReadRequired("tag");
        var zipName = ReadRequired("zipName");
        var zipSha = ReadRequired("zipSha256").ToLowerInvariant();
        var exeSha = ReadRequired("exeSha256").ToLowerInvariant();
        var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        var published = root.TryGetProperty("publishedAtUtc", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        var channel = root.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ch.GetString())
            ? ch.GetString()!.Trim().ToLowerInvariant()
            : "standard";
        var targetChannel = string.IsNullOrWhiteSpace(expectedChannel) ? ProductProfile.ChannelId : expectedChannel.Trim().ToLowerInvariant();
        if (!string.Equals(channel, targetChannel, StringComparison.OrdinalIgnoreCase))
            throw new SetupException($"更新清单通道不匹配（当前需要「{targetChannel}」，清单为「{channel}」）。");

        if (!TryParseVersion(version, out _))
            throw new SetupException("更新清单版本号格式无效。");
        var tagPattern = targetChannel == "hyperv"
            ? @"^(?:v|hyperv-v?|hyperv-preview-)[0-9]+\.[0-9]+\.[0-9]+$"
            : @"^v[0-9]+\.[0-9]+\.[0-9]+$";
        if (!Regex.IsMatch(tag, tagPattern))
            throw new SetupException("更新清单 tag 格式无效。");
        if (!Regex.IsMatch(zipName, @"^[A-Za-z0-9._-]+\.zip$"))
            throw new SetupException("更新清单压缩包名称不合法。");
        if (!IsValidSha256(zipSha) || !IsValidSha256(exeSha))
            throw new SetupException("更新清单缺少有效的 SHA-256 校验值。");

        return new ReleaseManifest(version, tag, zipName, zipSha, exeSha, notes, published, channel);
    }

    public static string BuildDownloadUrl(ReleaseManifest manifest, string? releaseBaseUrl = null)
    {
        var baseUrl = string.IsNullOrWhiteSpace(releaseBaseUrl) ? DefaultReleaseBaseUrl : releaseBaseUrl.TrimEnd('/');
        return $"{baseUrl}/{manifest.Tag}/{manifest.ZipName}";
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(
        Version currentVersion,
        string? manifestUrl = null,
        string? releaseBaseUrl = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl) && !ProductProfile.OnlineUpdatesEnabled)
        {
            return new UpdateCheckResult(false, null, ProductProfile.PreReleasePageUrl,
                $"在线更新暂未开放（当前为 {ProductProfile.WindowTitle} v{currentVersion.ToString(3)}），请前往预发布页手动下载新版或使用本地 ZIP 更新。");
        }
        var url = string.IsNullOrWhiteSpace(manifestUrl) ? DefaultManifestUrl : manifestUrl;
        using var client = CreateHttpClient(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(url, token);
        if ((int)response.StatusCode == 404)
        {
            if (ProductProfile.IsHyperVStandalone)
                throw new SetupException("当前更新通道尚未开放自动下载，请前往发布页手动下载最新安装包，或使用「从本地 ZIP 更新」。");
            return new UpdateCheckResult(false, null, "", $"当前已是最新版本（v{currentVersion.ToString(3)}）。");
        }
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(token);
        var manifest = ParseAndValidateManifest(body);
        if (!TryParseVersion(manifest.Version, out var candidate))
            throw new SetupException("无法解析远端版本号。");
        if (candidate <= currentVersion)
        {
            return new UpdateCheckResult(false, manifest, "", $"当前已是最新版本（v{currentVersion.ToString(3)}）。");
        }
        var downloadUrl = BuildDownloadUrl(manifest, releaseBaseUrl);
        return new UpdateCheckResult(true, manifest, downloadUrl, $"发现新版本 v{candidate.ToString(3)}，可直接在本地更新。");
    }

    public static async Task<StagedUpdate> DownloadAndStageAsync(
        ReleaseManifest manifest,
        string downloadUrl,
        Version currentVersion,
        string stagingRoot,
        IProgress<double>? progress,
        CancellationToken token)
    {
        Directory.CreateDirectory(stagingRoot);
        var stagingDir = Path.Combine(stagingRoot, "claudome-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        var zipPath = Path.Combine(stagingDir, manifest.ZipName);
        try
        {
            using var client = CreateHttpClient(TimeSpan.FromMinutes(4));
            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1L;
            await using (var remote = await response.Content.ReadAsStreamAsync(token))
            await using (var local = File.Create(zipPath))
            {
                var buffer = new byte[81920];
                long read = 0;
                int chunk;
                while ((chunk = await remote.ReadAsync(buffer, token)) > 0)
                {
                    await local.WriteAsync(buffer.AsMemory(0, chunk), token);
                    read += chunk;
                    if (total > 0) progress?.Report(Math.Clamp(read * 100d / total, 0, 100));
                }
            }
            var extractDir = Path.Combine(stagingDir, "extracted");
            return await ExtractAndVerifyPackageAsync(zipPath, extractDir, currentVersion, manifest.ZipSha256, manifest.ExeSha256, allowSameVersion: false, token);
        }
        catch
        {
            TryDeleteDirectory(stagingDir);
            throw;
        }
    }

    public static async Task<StagedUpdate> ExtractAndVerifyPackageAsync(
        string zipPath,
        string stagingDir,
        Version currentVersion,
        string? expectedZipSha256,
        string? expectedExeSha256,
        bool allowSameVersion,
        CancellationToken token,
        string? expectedProductName = null)
    {
        if (!File.Exists(zipPath))
            throw new SetupException("未找到更新压缩包。");

        var actualZipHash = await ComputeSha256Async(zipPath, token);
        if (!string.IsNullOrWhiteSpace(expectedZipSha256) &&
            !actualZipHash.Equals(expectedZipSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new SetupException("更新包 ZIP 校验失败（SHA-256 不匹配），已放弃安装。");

        Directory.CreateDirectory(stagingDir);
        var stagingRoot = Path.GetFullPath(stagingDir);
        var stagingPrefix = stagingRoot.EndsWith(Path.DirectorySeparatorChar)
            ? stagingRoot
            : stagingRoot + Path.DirectorySeparatorChar;

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(stagingRoot, entry.FullName));
                if (!destination.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new SetupException("更新包包含越界路径，已阻止解压。");
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        var exes = Directory.GetFiles(stagingRoot, "Claudome.exe", SearchOption.AllDirectories);
        if (exes.Length != 1)
            throw new SetupException(exes.Length == 0 ? "更新包中未找到 Claudome.exe。" : "更新包结构异常，存在多个 Claudome.exe。");

        var exePath = exes[0];
        var actualExeHash = await ComputeSha256Async(exePath, token);
        if (!string.IsNullOrWhiteSpace(expectedExeSha256) &&
            !actualExeHash.Equals(expectedExeSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new SetupException("Claudome.exe 校验失败（SHA-256 不匹配），已放弃安装。");

        var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
        var requiredProduct = string.IsNullOrWhiteSpace(expectedProductName) ? ProductProfile.ProductName : expectedProductName.Trim();
        var actualProduct = versionInfo.ProductName?.Trim() ?? "";
        if (!string.Equals(actualProduct, requiredProduct, StringComparison.Ordinal))
        {
            var crossEdition = string.Equals(actualProduct, "Claudome", StringComparison.Ordinal) ||
                               string.Equals(actualProduct, "Claudome Hyper-V", StringComparison.Ordinal);
            throw new SetupException(crossEdition
                ? $"更新包产品类型不匹配（当前为「{requiredProduct}」，所选包为「{actualProduct}」），已拒绝跨版替换。"
                : $"更新文件不是合法的 {requiredProduct} 程序，已拒绝替换。");
        }

        var rawVersion = !string.IsNullOrWhiteSpace(versionInfo.ProductVersion)
            ? versionInfo.ProductVersion
            : versionInfo.FileVersion;
        if (!TryParseVersion(rawVersion, out var candidate))
            throw new SetupException("无法读取新版本 Claudome.exe 的版本号。");

        var exeDir = Path.GetDirectoryName(exePath)
            ?? throw new SetupException("更新包目录结构异常。");
        var requiredHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requiredFile in RequiredSideFiles)
        {
            var reqPath = Path.Combine(exeDir, requiredFile);
            if (!File.Exists(reqPath) || StorageCleanupService.IsReparsePointPath(reqPath))
                throw new SetupException($"更新包缺少必需随包文件（{requiredFile}），已拒绝安装。");
            requiredHashes[requiredFile] = await ComputeSha256Async(reqPath, token);
        }

        if (candidate < currentVersion || (!allowSameVersion && candidate == currentVersion))
            throw new SetupException($"目标版本（v{candidate.ToString(3)}）不高于当前版本（v{currentVersion.ToString(3)}），已阻止降级覆盖。");

        return new StagedUpdate(candidate.ToString(3), stagingRoot, exeDir, exePath, actualExeHash, requiredHashes);
    }

    public static void StartSwapAndRestart(StagedUpdate staged, string currentExePath, int currentPid, bool relaunch = true, string? registryPath = null)
    {
        if (!File.Exists(staged.ExePath))
            throw new SetupException("待更新的 Claudome.exe 已失效，请重试。");

        var appDir = Path.GetDirectoryName(Path.GetFullPath(currentExePath))
            ?? throw new SetupException("无法定位当前程序目录。");
        var exeName = Path.GetFileName(currentExePath);
        var backupName = $"{exeName}.{currentPid}.bak";
        var fullAppDir = Path.GetFullPath(appDir);
        var fullStageDir = Path.GetFullPath(staged.StagingDirectory);
        var txId = Guid.NewGuid().ToString("N");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"claudome-update-{txId}.cmd");
        var targetExePath = Path.Combine(fullAppDir, exeName);
        var lockPath = Path.Combine(fullAppDir, UpdateSwapLockName);

        if (TryGetPendingUpdateBlocker(fullAppDir, out var preBlocker, registryPath))
            throw new SetupException(preBlocker);

        FileStream authorityLock;
        try
        {
            authorityLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SetupException($"当前已有另一笔原地更新正在占用安装目录（{ex.Message}），请等待完成后再试。");
        }

        var scriptStarted = false;
        try
        {
            // Re-verify inside the exclusive directory lock that no prior unfinished/unverified/recovery tx exists.
            if (TryGetPendingUpdateBlocker(fullAppDir, out var lockedBlocker, registryPath, ignoreActiveLock: true))
                throw new SetupException(lockedBlocker);

            var expectedSideHashes = ResolveStagedRequiredSideHashes(staged);
            // Compute strict hashes & manifest strictly in memory first; aborts cleanly with zero state mutation if locked.
            var manifest = BuildRollbackManifest(fullAppDir, exeName, currentPid, txId);
            JsonFile.Write(Path.Combine(fullAppDir, UpdateRollbackManifestName), manifest);

            var pendingTxPath = Path.Combine(fullAppDir, UpdatePendingTxName);
            JsonFile.Write(pendingTxPath, new PendingUpdateTransaction(
                txId,
                staged.ExeSha256,
                targetExePath,
                fullStageDir,
                Path.GetFullPath(scriptPath),
                DateTime.UtcNow,
                ExpectedRequiredSideFileSha256: expectedSideHashes));
            TryDeleteFile(Path.Combine(fullAppDir, UpdateCompletedReceiptName));
            TryDeleteFile(Path.Combine(fullAppDir, UpdateVerifiedMarkerName));
            TryDeleteFile(Path.Combine(fullAppDir, UpdateUnrecoveredListName));
            TryDeleteFile(Path.Combine(fullAppDir, UpdateErrorMarkerName));

            foreach (var item in manifest.Items)
            {
                var isExe = item.FileName.Equals(exeName, StringComparison.OrdinalIgnoreCase);
                var role = item.ExistedBefore
                    ? (isExe ? "backup" : "backup-sidecar")
                    : "backup-absent";
                var bakTargetPath = item.ExistedBefore
                    ? Path.Combine(fullAppDir, item.BackupFileName)
                    : Path.Combine(fullAppDir, $"{item.FileName}.{currentPid}.absent");
                var existingPath = Path.Combine(fullAppDir, item.FileName);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord
                {
                    Category = BackupCategory.ProgramUpdate,
                    OwnerPlatform = "windows",
                    SourceFeature = "程序原地更新",
                    ScopeId = fullAppDir,
                    ScopeLabel = fullAppDir,
                    Path = bakTargetPath,
                    StageRoot = fullStageDir,
                    TransactionId = txId,
                    Role = role,
                    OpState = BackupOpState.InProgress,
                    SizeBytes = item.ExistedBefore && File.Exists(existingPath) ? new FileInfo(existingPath).Length : 0,
                    ContentSha256 = item.OriginalSha256
                }, registryPath);
            }
            StorageCleanupService.RegisterRecord(new BackupRegistryRecord
            {
                Category = BackupCategory.ProgramUpdate,
                OwnerPlatform = "windows",
                SourceFeature = "程序原地更新",
                ScopeId = fullAppDir,
                ScopeLabel = fullAppDir,
                Path = fullStageDir,
                StageRoot = fullStageDir,
                TransactionId = txId,
                Role = "staging",
                OpState = BackupOpState.InProgress
            }, registryPath);
            StorageCleanupService.RegisterRecord(new BackupRegistryRecord
            {
                Category = BackupCategory.ProgramUpdate,
                OwnerPlatform = "windows",
                SourceFeature = "程序更新脚本",
                ScopeId = fullAppDir,
                ScopeLabel = fullAppDir,
                Path = Path.GetFullPath(scriptPath),
                StageRoot = fullStageDir,
                TransactionId = txId,
                Role = "swap-script",
                OpState = BackupOpState.InProgress
            }, registryPath);

            var targetHashes = new Dictionary<string, string>(expectedSideHashes, StringComparer.OrdinalIgnoreCase)
            {
                [exeName] = staged.ExeSha256
            };
            var script = BuildSwapScript(appDir, staged.PayloadDirectory, staged.StagingDirectory, exeName, backupName, currentPid, relaunch, txId, targetHashes);
            // Set UTF-8 codepage on cmd.exe BEFORE opening the UTF-8 script (calling chcp inside a .cmd corrupts cmd's file seek pointer).
            File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

            var start = new ProcessStartInfo("cmd.exe", BuildCmdArguments(scriptPath))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            };
            start.Environment["CLAUDOME_HANDOFF"] = "1";
            var proc = Process.Start(start)
                ?? throw new SetupException("无法启动本地替换更新脚本。");

            var startTicks = 0L;
            try { startTicks = proc.StartTime.ToUniversalTime().Ticks; } catch { }
            JsonFile.Write(pendingTxPath, new PendingUpdateTransaction(
                txId,
                staged.ExeSha256,
                targetExePath,
                fullStageDir,
                Path.GetFullPath(scriptPath),
                DateTime.UtcNow,
                proc.Id,
                startTicks,
                expectedSideHashes));
            scriptStarted = true;
        }
        finally
        {
            authorityLock.Dispose();
            if (!scriptStarted)
                TryDeleteFile(lockPath);
        }
    }

    public static bool TryGetPendingUpdateBlocker(
        string appDir,
        out string message,
        string? registryPath = null,
        bool ignoreActiveLock = false)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(appDir) || !Directory.Exists(appDir))
            return false;
        var fullAppDir = Path.GetFullPath(appDir);
        if (!ignoreActiveLock && IsSwapLockActive(fullAppDir))
        {
            message = "当前已有原地更新正在执行中，请等待本次更新完成后再试。";
            return true;
        }

        var notice = ReadPendingUpdateNoticeCore(fullAppDir, checkLock: !ignoreActiveLock, registryPath);
        if (notice.IsRollbackIncomplete || File.Exists(Path.Combine(fullAppDir, UpdateUnrecoveredListName)))
        {
            var filesText = notice.UnrecoveredFiles is { Length: > 0 }
                ? string.Join("、", notice.UnrecoveredFiles)
                : "部分核心文件";
            message = $"上次原地更新回滚尚未完成（待恢复：{filesText}），请先完成恢复原版后再启动新更新。";
            return true;
        }
        if (!string.IsNullOrWhiteSpace(notice.ErrorReason))
        {
            message = $"上次原地更新存在未核实或未清理的异常状态（{notice.ErrorReason}），请先完成处理后再启动新更新。";
            return true;
        }
        if (!string.IsNullOrWhiteSpace(notice.DeferredFile))
        {
            message = $"上次更新仍有随包文件（{notice.DeferredFile}）待补全，请先补全后再启动新更新。";
            return true;
        }

        var verifiedMarker = ReadVerifiedUpdateMarker(fullAppDir);
        var pendingTx = ReadPendingTransaction(fullAppDir);
        var pendingVerified = pendingTx is not null &&
                              (IsTransactionVerified(fullAppDir, pendingTx.TransactionId, registryPath) ||
                               (verifiedMarker is not null && string.Equals(verifiedMarker.TransactionId, pendingTx.TransactionId, StringComparison.OrdinalIgnoreCase)));
        if (pendingTx is not null && !pendingVerified)
        {
            message = "上一笔原地更新尚待首启核验或收尾处理，请先完成核验后再启动新更新。";
            return true;
        }

        var manifest = ReadRollbackManifest(fullAppDir);
        var manifestVerified = manifest is not null &&
                               (IsTransactionVerified(fullAppDir, manifest.TransactionId, registryPath) ||
                                (verifiedMarker is not null && string.Equals(verifiedMarker.TransactionId, manifest.TransactionId, StringComparison.OrdinalIgnoreCase)));
        if (manifest is not null && !manifestVerified)
        {
            message = "存在尚未收尾的更新回退清单，请先完成恢复或首启核验后再启动新更新。";
            return true;
        }

        var completedPath = Path.Combine(fullAppDir, UpdateCompletedReceiptName);
        if (File.Exists(completedPath))
        {
            var completedTxId = TryReadCompletedReceiptTxId(fullAppDir);
            var completedAlreadyVerified =
                verifiedMarker is not null &&
                ((pendingTx is null && manifest is null && string.IsNullOrWhiteSpace(completedTxId)) ||
                 (!string.IsNullOrWhiteSpace(completedTxId) &&
                  (string.Equals(completedTxId, verifiedMarker.TransactionId, StringComparison.OrdinalIgnoreCase) ||
                   IsTransactionVerified(fullAppDir, completedTxId, registryPath)))) ||
                (!string.IsNullOrWhiteSpace(completedTxId) && IsTransactionVerified(fullAppDir, completedTxId, registryPath));
            if (completedAlreadyVerified)
            {
                TryDeleteFile(completedPath);
                if (!File.Exists(completedPath))
                    return false;
            }
            message = "上一笔更新完成回执尚未完成首启核验与收尾，请先重启完成核验后再启动新更新。";
            return true;
        }
        return false;
    }

    private static Dictionary<string, string> ResolveStagedRequiredSideHashes(StagedUpdate staged)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var side in RequiredSideFiles)
        {
            if (staged.RequiredSideFileSha256 is not null &&
                staged.RequiredSideFileSha256.TryGetValue(side, out var precomputed) &&
                !string.IsNullOrWhiteSpace(precomputed))
            {
                result[side] = precomputed;
                continue;
            }
            var sidePath = Path.Combine(staged.PayloadDirectory, side);
            if (!File.Exists(sidePath) || StorageCleanupService.IsReparsePointPath(sidePath))
                throw new SetupException($"更新载荷缺少必需随包文件（{side}），已拒绝更新。");
            result[side] = ComputeSha256Strict(sidePath, side);
        }
        return result;
    }

    internal static string BuildCmdArguments(string scriptPath) =>
        $"/d /c \"chcp 65001 >nul && \"{scriptPath}\"\"";

    public static RollbackManifest CaptureRollbackManifest(string appDir, string exeName, int currentPid, string transactionId)
    {
        var manifest = BuildRollbackManifest(appDir, exeName, currentPid, transactionId);
        JsonFile.Write(Path.Combine(manifest.AppDirectory, UpdateRollbackManifestName), manifest);
        return manifest;
    }

    private static RollbackManifest BuildRollbackManifest(string appDir, string exeName, int currentPid, string transactionId)
    {
        var fullAppDir = Path.GetFullPath(appDir);
        var items = new List<RollbackFileEntry>();
        var candidates = new List<string> { exeName };
        candidates.AddRange(RequiredSideFiles);
        foreach (var fileName in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var targetPath = Path.Combine(fullAppDir, fileName);
            if (StorageCleanupService.IsReparsePointPath(targetPath))
                throw new SetupException($"目标文件 {fileName} 为符号链接，已拒绝原地更新。");
            var existed = File.Exists(targetPath);
            var sha = existed ? ComputeSha256Strict(targetPath, fileName) : "";
            items.Add(new RollbackFileEntry(fileName, existed, sha, $"{fileName}.{currentPid}.bak"));
        }
        return new RollbackManifest(transactionId, fullAppDir, currentPid, DateTime.UtcNow, items);
    }

    private static string ComputeSha256Strict(string path, string displayName)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(sha))
                throw new SetupException($"更新前无法读取原文件 {displayName} 的校验值，已停止原地更新。");
            return sha;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SetupException($"更新前原文件 {displayName} 正在被占用或无法读取（{ex.Message}），已停止原地更新。");
        }
    }

    public static bool IsSwapLockActive(string appDir)
    {
        if (string.IsNullOrWhiteSpace(appDir) || !Directory.Exists(appDir)) return false;
        var lockPath = Path.Combine(appDir, UpdateSwapLockName);
        if (File.Exists(lockPath) && !StorageCleanupService.IsReparsePointPath(lockPath))
        {
            try
            {
                using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        // Cover seamless parent-to-child handoff after StartSwapAndRestart launches cmd.exe.
        var pendingTx = ReadPendingTransaction(appDir);
        if (pendingTx is { SwapProcessId: > 0 } &&
            !HasValidCompletedReceipt(appDir, pendingTx, null) &&
            IsRecordedProcessAlive(pendingTx.SwapProcessId, pendingTx.SwapProcessStartTicks))
        {
            return true;
        }
        return false;
    }

    private static bool IsRecordedProcessAlive(int pid, long startTimeTicks)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (startTimeTicks > 0 && p.StartTime.ToUniversalTime().Ticks != startTimeTicks)
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string BuildSwapScript(
        string appDir,
        string payloadDir,
        string stagingDir,
        string exeName,
        string backupName,
        int currentPid,
        bool relaunch,
        string? transactionId = null,
        IReadOnlyDictionary<string, string>? expectedTargetHashes = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal EnableExtensions");
        sb.AppendLine($"set \"APP_DIR={appDir}\"");
        sb.AppendLine($"set \"PAYLOAD_DIR={payloadDir}\"");
        sb.AppendLine($"set \"STAGE_DIR={stagingDir}\"");
        sb.AppendLine($"set \"EXE_NAME={exeName}\"");
        sb.AppendLine($"set \"BAK_NAME={backupName}\"");
        sb.AppendLine($"set \"LOCK_FILE=%APP_DIR%\\{UpdateSwapLockName}\"");
        sb.AppendLine($"set \"ERR_FILE=%APP_DIR%\\{UpdateErrorMarkerName}\"");
        sb.AppendLine($"set \"DEF_FILE=%APP_DIR%\\{UpdateDeferredManifestName}\"");
        sb.AppendLine($"set \"DONE_FILE=%APP_DIR%\\{UpdateCompletedReceiptName}\"");
        sb.AppendLine($"set \"ROLLBACK_FILE=%APP_DIR%\\{UpdateRollbackManifestName}\"");
        sb.AppendLine($"set \"UNREC_FILE=%APP_DIR%\\{UpdateUnrecoveredListName}\"");
        sb.AppendLine("if \"%CLAUDOME_SWAP_LOCKED%\"==\"1\" goto SWAP_BODY");
        sb.AppendLine("set \"CLAUDOME_LOCK_PATH=%LOCK_FILE%\"");
        sb.AppendLine("set \"CLAUDOME_SELF_PATH=%~f0\"");
        sb.AppendLine("set \"CLAUDOME_SWAP_LOCKED=1\"");
        sb.AppendLine($"\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -InputFormat None -ExecutionPolicy Bypass -EncodedCommand {LockWrapperEncodedCommand}");
        sb.AppendLine("set \"SWAP_RC=%errorlevel%\"");
        sb.AppendLine("set \"CLAUDOME_SWAP_LOCKED=\"");
        sb.AppendLine("if exist \"%LOCK_FILE%\" del /f /q \"%LOCK_FILE%\" >nul 2>nul");
        sb.AppendLine("if not \"%SWAP_RC%\"==\"0\" exit /b %SWAP_RC%");
        if (relaunch)
        {
            sb.AppendLine("start \"\" \"%APP_DIR%\\%EXE_NAME%\"");
        }
        sb.AppendLine("exit /b 0");
        sb.AppendLine(":SWAP_BODY");
        sb.AppendLine("set \"FAILED_REASON=\"");
        sb.AppendLine("set \"DEFERRED_FILES=\"");
        sb.AppendLine("set \"UNRECOVERED_ANY=\"");
        sb.AppendLine("if exist \"%ERR_FILE%\" del /f /q \"%ERR_FILE%\" >nul 2>nul");
        sb.AppendLine("if exist \"%DEF_FILE%\" del /f /q \"%DEF_FILE%\" >nul 2>nul");
        sb.AppendLine("if exist \"%DONE_FILE%\" del /f /q \"%DONE_FILE%\" >nul 2>nul");
        sb.AppendLine("if exist \"%UNREC_FILE%\" del /f /q \"%UNREC_FILE%\" >nul 2>nul");
        sb.AppendLine("set /a COUNT=0");
        sb.AppendLine(":WAIT_EXIT");
        sb.AppendLine($"tasklist /FI \"PID eq {currentPid}\" 2>nul | find \"{currentPid}\" >nul");
        sb.AppendLine("if errorlevel 1 goto DO_SWAP");
        sb.AppendLine("set /a COUNT+=1");
        sb.AppendLine("if %COUNT% GEQ 30 goto DO_SWAP");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        sb.AppendLine("goto WAIT_EXIT");
        sb.AppendLine(":DO_SWAP");
        sb.AppendLine("if not exist \"%PAYLOAD_DIR%\\%EXE_NAME%\" (");
        sb.AppendLine("  set \"FAILED_REASON=Staged payload missing required file: %EXE_NAME%\"");
        sb.AppendLine("  goto ABORT_PRECHECK");
        sb.AppendLine(")");
        foreach (var file in RequiredSideFiles)
        {
            sb.AppendLine($"if not exist \"%PAYLOAD_DIR%\\{file}\" (");
            sb.AppendLine($"  set \"FAILED_REASON=Staged payload missing required file: {file}\"");
            sb.AppendLine("  goto ABORT_PRECHECK");
            sb.AppendLine(")");
        }
        if (expectedTargetHashes is not null)
        {
            foreach (var reqName in (new[] { exeName }).Concat(RequiredSideFiles).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (expectedTargetHashes.TryGetValue(reqName, out var expectedSha) && !string.IsNullOrWhiteSpace(expectedSha))
                {
                    sb.AppendLine($"certutil -hashfile \"%PAYLOAD_DIR%\\{reqName}\" SHA256 | findstr /r /i \"^{expectedSha.Trim()}$\" >nul");
                    sb.AppendLine("if errorlevel 1 (");
                    sb.AppendLine($"  set \"FAILED_REASON=Staged payload SHA-256 mismatch: {reqName}\"");
                    sb.AppendLine("  goto ABORT_PRECHECK");
                    sb.AppendLine(")");
                }
            }
        }
        sb.AppendLine("if exist \"%APP_DIR%\\%BAK_NAME%\" del /f /q \"%APP_DIR%\\%BAK_NAME%\" >nul 2>nul");
        sb.AppendLine($"if exist \"%APP_DIR%\\%EXE_NAME%.{currentPid}.absent\" del /f /q \"%APP_DIR%\\%EXE_NAME%.{currentPid}.absent\" >nul 2>nul");
        sb.AppendLine($"if not exist \"%APP_DIR%\\%EXE_NAME%\" (> \"%APP_DIR%\\%EXE_NAME%.{currentPid}.absent\" echo absent)");
        sb.AppendLine("if exist \"%APP_DIR%\\%EXE_NAME%\" (");
        sb.AppendLine("  ren \"%APP_DIR%\\%EXE_NAME%\" \"%BAK_NAME%\"");
        sb.AppendLine("  if errorlevel 1 (");
        sb.AppendLine("    set \"FAILED_REASON=Failed to back up executable\"");
        sb.AppendLine("    goto ROLLBACK");
        sb.AppendLine("  )");
        sb.AppendLine(")");
        sb.AppendLine("copy /y \"%PAYLOAD_DIR%\\%EXE_NAME%\" \"%APP_DIR%\\%EXE_NAME%\" >nul");
        sb.AppendLine("if errorlevel 1 (");
        sb.AppendLine("  set \"FAILED_REASON=Failed to replace executable\"");
        sb.AppendLine("  goto ROLLBACK");
        sb.AppendLine(")");
        foreach (var file in RequiredSideFiles)
        {
            sb.AppendLine($"if not exist \"%PAYLOAD_DIR%\\{file}\" (");
            sb.AppendLine($"  set \"FAILED_REASON=Staged payload missing required file: {file}\"");
            sb.AppendLine("  goto ROLLBACK");
            sb.AppendLine(")");
            sb.AppendLine($"if exist \"%APP_DIR%\\{file}.{currentPid}.bak\" del /f /q \"%APP_DIR%\\{file}.{currentPid}.bak\" >nul 2>nul");
            sb.AppendLine($"if exist \"%APP_DIR%\\{file}.{currentPid}.absent\" del /f /q \"%APP_DIR%\\{file}.{currentPid}.absent\" >nul 2>nul");
            sb.AppendLine($"if exist \"%APP_DIR%\\{file}\" (");
            sb.AppendLine($"  copy /y \"%APP_DIR%\\{file}\" \"%APP_DIR%\\{file}.{currentPid}.bak\" >nul 2>nul");
            sb.AppendLine("  if errorlevel 1 (");
            sb.AppendLine($"    set \"FAILED_REASON=Required file backup failed: {file}\"");
            sb.AppendLine("    goto ROLLBACK");
            sb.AppendLine("  )");
            sb.AppendLine(") else (");
            sb.AppendLine($"  > \"%APP_DIR%\\{file}.{currentPid}.absent\" echo absent");
            sb.AppendLine(")");
            sb.AppendLine($"copy /y \"%PAYLOAD_DIR%\\{file}\" \"%APP_DIR%\\{file}\" >nul 2>nul");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine($"  set \"FAILED_REASON=Required file locked or unwritable: {file}\"");
            sb.AppendLine("  goto ROLLBACK");
            sb.AppendLine(")");
        }
        // Keep RequiredSideFiles .{currentPid}.bak alongside EXE.{currentPid}.bak as complete rollback group; rotated later on verified subsequent updates (B03).
        foreach (var file in DeferrableSideFiles)
        {
            sb.AppendLine($"if exist \"%PAYLOAD_DIR%\\{file}\" (");
            sb.AppendLine($"  copy /y \"%PAYLOAD_DIR%\\{file}\" \"%APP_DIR%\\{file}\" >nul 2>nul");
            sb.AppendLine("  if errorlevel 1 (");
            sb.AppendLine($"    set \"DEFERRED_FILES={file}\"");
            sb.AppendLine("  )");
            sb.AppendLine(")");
        }
        if (!string.IsNullOrWhiteSpace(transactionId))
        {
            sb.AppendLine("for %%F in (\"%APP_DIR%\\%EXE_NAME%\" \"%APP_DIR%\\PROJECT-LICENSE.txt\" \"%APP_DIR%\\Gateway-THIRD-PARTY-NOTICES.txt\") do (");
            sb.AppendLine("  if not exist %%F (");
            sb.AppendLine("    set \"FAILED_REASON=Required target file missing before commit: %%~nxF\"");
            sb.AppendLine("    goto RECEIPT_FAIL");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            if (expectedTargetHashes is not null)
            {
                foreach (var reqName in (new[] { exeName }).Concat(RequiredSideFiles).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (expectedTargetHashes.TryGetValue(reqName, out var expectedSha) && !string.IsNullOrWhiteSpace(expectedSha))
                    {
                        sb.AppendLine($"certutil -hashfile \"%APP_DIR%\\{reqName}\" SHA256 | findstr /r /i \"^{expectedSha.Trim()}$\" >nul");
                        sb.AppendLine("if errorlevel 1 (");
                        sb.AppendLine($"  set \"FAILED_REASON=Target SHA-256 mismatch before commit: {reqName}\"");
                        sb.AppendLine("  goto RECEIPT_FAIL");
                        sb.AppendLine(")");
                    }
                }
            }
            sb.AppendLine("set \"RECEIPT_OK=\"");
            sb.AppendLine("if exist \"%DONE_FILE%.tmp\" del /f /q \"%DONE_FILE%.tmp\" >nul 2>nul");
            sb.AppendLine($"> \"%DONE_FILE%.tmp\" echo {{\"transactionId\":\"{transactionId}\",\"swapSucceeded\":true}}");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  for %%I in (\"%DONE_FILE%.tmp\") do if %%~zI GTR 10 (");
            sb.AppendLine("    move /y \"%DONE_FILE%.tmp\" \"%DONE_FILE%\" >nul 2>nul");
            sb.AppendLine("    if not errorlevel 1 (");
            sb.AppendLine("      for %%J in (\"%DONE_FILE%\") do if %%~zJ GTR 10 set \"RECEIPT_OK=1\"");
            sb.AppendLine("    )");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine("if exist \"%DONE_FILE%.tmp\" del /f /q \"%DONE_FILE%.tmp\" >nul 2>nul");
            sb.AppendLine("if not defined RECEIPT_OK (");
            sb.AppendLine("  set \"FAILED_REASON=Failed to write completion receipt: %DONE_FILE%\"");
            sb.AppendLine("  goto RECEIPT_FAIL");
            sb.AppendLine(")");
        }
        sb.AppendLine($"if exist \"%APP_DIR%\\%EXE_NAME%.{currentPid}.absent\" del /f /q \"%APP_DIR%\\%EXE_NAME%.{currentPid}.absent\" >nul 2>nul");
        foreach (var file in RequiredSideFiles)
            sb.AppendLine($"if exist \"%APP_DIR%\\{file}.{currentPid}.absent\" del /f /q \"%APP_DIR%\\{file}.{currentPid}.absent\" >nul 2>nul");
        // Preserve ROLLBACK_FILE until startup verification succeeds and writes claudome-update-verified.json (Blocker 1 / Blocker 2).
        sb.AppendLine("if exist \"%UNREC_FILE%\" del /f /q \"%UNREC_FILE%\" >nul 2>nul");
        sb.AppendLine("if not defined DEFERRED_FILES goto CLEAN_STAGE");
        sb.AppendLine("set \"ESC_STAGE=%STAGE_DIR:\\=\\\\%\"");
        sb.AppendLine("set \"ESC_DIR=%PAYLOAD_DIR:\\=\\\\%\"");
        sb.AppendLine("> \"%DEF_FILE%\" echo {\"file\":\"%DEFERRED_FILES%\",\"stageDir\":\"%ESC_STAGE%\",\"payloadDir\":\"%ESC_DIR%\",\"sourceDir\":\"%ESC_DIR%\"}");
        sb.AppendLine("goto AFTER_STAGE");
        sb.AppendLine(":CLEAN_STAGE");
        sb.AppendLine("rmdir /s /q \"%STAGE_DIR%\" >nul 2>nul");
        sb.AppendLine(":AFTER_STAGE");
        sb.AppendLine("exit /b 0");
        sb.AppendLine(":RECEIPT_FAIL");
        sb.AppendLine("if not defined FAILED_REASON set \"FAILED_REASON=Failed to write completion receipt\"");
        sb.AppendLine("> \"%UNREC_FILE%\" echo %EXE_NAME%");
        foreach (var file in RequiredSideFiles)
            sb.AppendLine($">> \"%UNREC_FILE%\" echo {file}");
        sb.AppendLine("> \"%ERR_FILE%\" echo %FAILED_REASON%");
        sb.AppendLine("exit /b 1");
        sb.AppendLine(":ABORT_PRECHECK");
        sb.AppendLine("if not defined FAILED_REASON set \"FAILED_REASON=Update precheck failed\"");
        sb.AppendLine("> \"%ERR_FILE%\" echo %FAILED_REASON%");
        sb.AppendLine("exit /b 1");
        sb.AppendLine(":ROLLBACK");
        sb.AppendLine("set \"UNRECOVERED_ANY=\"");
        sb.AppendLine("if exist \"%UNREC_FILE%\" del /f /q \"%UNREC_FILE%\" >nul 2>nul");
        sb.AppendLine("if exist \"%APP_DIR%\\%BAK_NAME%\" (");
        sb.AppendLine("  if exist \"%APP_DIR%\\%EXE_NAME%\" del /f /q \"%APP_DIR%\\%EXE_NAME%\" >nul 2>nul");
        sb.AppendLine("  if exist \"%APP_DIR%\\%EXE_NAME%\" (");
        sb.AppendLine("    set \"UNRECOVERED_ANY=1\"");
        sb.AppendLine("    >> \"%UNREC_FILE%\" echo %EXE_NAME%");
        sb.AppendLine("  ) else (");
        sb.AppendLine("    ren \"%APP_DIR%\\%BAK_NAME%\" \"%EXE_NAME%\" >nul 2>nul");
        sb.AppendLine("    if errorlevel 1 (");
        sb.AppendLine("      set \"UNRECOVERED_ANY=1\"");
        sb.AppendLine("      >> \"%UNREC_FILE%\" echo %EXE_NAME%");
        sb.AppendLine("    ) else if not exist \"%APP_DIR%\\%EXE_NAME%\" (");
        sb.AppendLine("      set \"UNRECOVERED_ANY=1\"");
        sb.AppendLine("      >> \"%UNREC_FILE%\" echo %EXE_NAME%");
        sb.AppendLine("    )");
        sb.AppendLine("  )");
        sb.AppendLine($") else if exist \"%APP_DIR%\\%EXE_NAME%.{currentPid}.absent\" (");
        sb.AppendLine("  if exist \"%APP_DIR%\\%EXE_NAME%\" del /f /q \"%APP_DIR%\\%EXE_NAME%\" >nul 2>nul");
        sb.AppendLine("  if exist \"%APP_DIR%\\%EXE_NAME%\" (");
        sb.AppendLine("    set \"UNRECOVERED_ANY=1\"");
        sb.AppendLine("    >> \"%UNREC_FILE%\" echo %EXE_NAME%");
        sb.AppendLine("  )");
        sb.AppendLine(")");
        foreach (var file in RequiredSideFiles)
        {
            sb.AppendLine($"if exist \"%APP_DIR%\\{file}.{currentPid}.bak\" (");
            sb.AppendLine($"  copy /y \"%APP_DIR%\\{file}.{currentPid}.bak\" \"%APP_DIR%\\{file}\" >nul 2>nul");
            sb.AppendLine("  if errorlevel 1 (");
            sb.AppendLine("    set \"UNRECOVERED_ANY=1\"");
            sb.AppendLine($"    >> \"%UNREC_FILE%\" echo {file}");
            sb.AppendLine("  )");
            sb.AppendLine($") else if exist \"%APP_DIR%\\{file}.{currentPid}.absent\" (");
            sb.AppendLine($"  if exist \"%APP_DIR%\\{file}\" del /f /q \"%APP_DIR%\\{file}\" >nul 2>nul");
            sb.AppendLine($"  if exist \"%APP_DIR%\\{file}\" (");
            sb.AppendLine("    set \"UNRECOVERED_ANY=1\"");
            sb.AppendLine($"    >> \"%UNREC_FILE%\" echo {file}");
            sb.AppendLine("  )");
            sb.AppendLine(")");
        }
        sb.AppendLine("if not defined UNRECOVERED_ANY (");
        sb.AppendLine($"  if exist \"%APP_DIR%\\%EXE_NAME%.{currentPid}.absent\" del /f /q \"%APP_DIR%\\%EXE_NAME%.{currentPid}.absent\" >nul 2>nul");
        foreach (var file in RequiredSideFiles)
        {
            sb.AppendLine($"  if exist \"%APP_DIR%\\{file}.{currentPid}.bak\" del /f /q \"%APP_DIR%\\{file}.{currentPid}.bak\" >nul 2>nul");
            sb.AppendLine($"  if exist \"%APP_DIR%\\{file}.{currentPid}.absent\" del /f /q \"%APP_DIR%\\{file}.{currentPid}.absent\" >nul 2>nul");
        }
        sb.AppendLine("  if exist \"%UNREC_FILE%\" del /f /q \"%UNREC_FILE%\" >nul 2>nul");
        sb.AppendLine(") else (");
        sb.AppendLine("  if defined FAILED_REASON set \"FAILED_REASON=%FAILED_REASON% (rollback incomplete)\"");
        sb.AppendLine(")");
        sb.AppendLine("if not defined FAILED_REASON set \"FAILED_REASON=Update rollback triggered\"");
        sb.AppendLine("> \"%ERR_FILE%\" echo %FAILED_REASON%");
        sb.AppendLine("exit /b 1");
        return sb.ToString();
    }

    public static void CleanupStaleUpdateFiles(string appDir, string tempRoot, string? settingsPath = null, string? registryPath = null, string? runningExePath = null)
    {
        var fullAppDir = string.IsNullOrWhiteSpace(appDir) ? "" : Path.GetFullPath(appDir);
        var pending = !string.IsNullOrWhiteSpace(fullAppDir) && Directory.Exists(fullAppDir)
            ? ReadPendingUpdateNotice(fullAppDir, registryPath)
            : new PendingUpdateNotice(null, null, null);
        if (pending.IsUpdateInProgress)
            return;

        PendingUpdateTransaction? pendingTx = !string.IsNullOrWhiteSpace(fullAppDir)
            ? ReadPendingTransaction(fullAppDir)
            : null;
        RollbackManifest? manifest = !string.IsNullOrWhiteSpace(fullAppDir) && Directory.Exists(fullAppDir)
            ? ReadRollbackManifest(fullAppDir)
            : null;

        var completedPath = string.IsNullOrWhiteSpace(fullAppDir) ? "" : Path.Combine(fullAppDir, UpdateCompletedReceiptName);
        var completedValid = !string.IsNullOrWhiteSpace(fullAppDir) && HasValidCompletedReceipt(fullAppDir, pendingTx, manifest);
        var completedTxId = !string.IsNullOrWhiteSpace(fullAppDir) ? TryReadCompletedReceiptTxId(fullAppDir) : null;
        var verifiedMarker = !string.IsNullOrWhiteSpace(fullAppDir) ? ReadVerifiedUpdateMarker(fullAppDir) : null;
        var candidateTxId = pendingTx?.TransactionId ?? manifest?.TransactionId ?? completedTxId;
        string? verifiedTxId = null;
        if (verifiedMarker is not null &&
            (string.IsNullOrWhiteSpace(candidateTxId) ||
             string.Equals(verifiedMarker.TransactionId, candidateTxId, StringComparison.OrdinalIgnoreCase)))
        {
            verifiedTxId = verifiedMarker.TransactionId;
        }
        else if (!string.IsNullOrWhiteSpace(fullAppDir) &&
                 !string.IsNullOrWhiteSpace(candidateTxId) &&
                 IsTransactionVerified(fullAppDir, candidateTxId, registryPath))
        {
            verifiedTxId = candidateTxId;
        }
        var alreadyVerified = !string.IsNullOrWhiteSpace(verifiedTxId);

        // Blocker 2: If an already-verified transaction left residual rollback/pending/receipt files or unpromoted registry state behind
        // (e.g. locked file on prior boot, or crash right after writing verified.json), retry promotion & metadata cleanup idempotently!
        if (alreadyVerified && string.IsNullOrWhiteSpace(pending.ErrorReason) && !pending.IsRollbackIncomplete)
        {
            StorageCleanupService.MarkTransactionVerified(BackupCategory.ProgramUpdate, fullAppDir, verifiedTxId!, registryPath);
            if (!IsSwapLockActive(fullAppDir))
                TryDeleteFile(Path.Combine(fullAppDir, UpdateSwapLockName));
            if (manifest is not null && manifest.TransactionId.Equals(verifiedTxId, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(Path.Combine(fullAppDir, UpdateRollbackManifestName));
                TryDeleteFile(Path.Combine(fullAppDir, UpdateUnrecoveredListName));
                foreach (var item in manifest.Items)
                    TryDeleteFile(Path.Combine(fullAppDir, $"{item.FileName}.{manifest.BackupPid}.absent"));
            }
            if (pendingTx is not null && pendingTx.TransactionId.Equals(verifiedTxId, StringComparison.OrdinalIgnoreCase))
                TryDeleteFile(Path.Combine(fullAppDir, UpdatePendingTxName));

            if (!string.IsNullOrWhiteSpace(completedPath) && File.Exists(completedPath) &&
                ((pendingTx is null && manifest is null) ||
                 string.Equals(completedTxId, verifiedTxId, StringComparison.OrdinalIgnoreCase)))
            {
                TryDeleteFile(completedPath);
            }
            if (string.IsNullOrWhiteSpace(pending.DeferredFile))
            {
                var ctxAfter = new StorageCleanupContext("windows", fullAppDir, tempRoot, [], SettingsPath: settingsPath, RegistryPath: registryPath);
                StorageCleanupService.RotateAfterVerifiedSuccess(BackupCategory.ProgramUpdate, fullAppDir, ctxAfter);
            }
            return;
        }

        var actualRunningExe = !string.IsNullOrWhiteSpace(runningExePath) ? runningExePath : (Environment.ProcessPath ?? "");
        var runningMatchesTargetPath = pendingTx is not null &&
                                       !string.IsNullOrWhiteSpace(actualRunningExe) &&
                                       Path.GetFullPath(actualRunningExe).Equals(Path.GetFullPath(pendingTx.TargetExePath), StringComparison.OrdinalIgnoreCase);

        var hasError = !string.IsNullOrWhiteSpace(pending.ErrorReason);
        var hasDeferred = !string.IsNullOrWhiteSpace(pending.DeferredFile);
        var hasUnrecoveredMarker = !string.IsNullOrWhiteSpace(fullAppDir) &&
                                   File.Exists(Path.Combine(fullAppDir, UpdateUnrecoveredListName));
        var hasBlockingRollbackManifest = !string.IsNullOrWhiteSpace(fullAppDir) &&
                                          File.Exists(Path.Combine(fullAppDir, UpdateRollbackManifestName)) &&
                                          !completedValid &&
                                          !alreadyVerified;

        if (hasError || pending.IsRollbackIncomplete || hasUnrecoveredMarker || hasBlockingRollbackManifest || (hasDeferred && !completedValid))
        {
            if (pendingTx is not null && !string.IsNullOrWhiteSpace(fullAppDir) && !alreadyVerified)
            {
                var detail = (pending.IsRollbackIncomplete || hasUnrecoveredMarker) && pending.UnrecoveredFiles is { Length: > 0 }
                    ? $"Unrecovered rollback files: {string.Join(", ", pending.UnrecoveredFiles)}"
                    : (hasError ? pending.ErrorReason : pending.DeferredFile);
                StorageCleanupService.UpdateTransactionState(BackupCategory.ProgramUpdate, fullAppDir, pendingTx.TransactionId,
                    BackupOpState.FailedOrCancelled, detail, registryPath);
            }
            // Blocker 1: Once the error marker has been acknowledged/cleared (!hasError), settle pendingTx/rollback.json
            // if the original group is verified intact; while hasError remains active, keep rollback.json so UI can show
            // verified-original proof until ClearUpdateErrorMarker clears everything together.
            if (!hasError &&
                !pending.IsRollbackIncomplete &&
                !hasUnrecoveredMarker &&
                !completedValid &&
                pending.IsOriginalGroupVerifiedRestored &&
                !string.IsNullOrWhiteSpace(fullAppDir))
            {
                if (!IsSwapLockActive(fullAppDir))
                    TryDeleteFile(Path.Combine(fullAppDir, UpdateSwapLockName));
                TryDeleteFile(Path.Combine(fullAppDir, UpdatePendingTxName));
                TryDeleteFile(Path.Combine(fullAppDir, UpdateRollbackManifestName));
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(fullAppDir) && !IsSwapLockActive(fullAppDir))
            TryDeleteFile(Path.Combine(fullAppDir, UpdateSwapLockName));

        // B02 + Blocker 3 + First-boot target identity & integrity gate:
        // - Launching from a DIFFERENT path keeps waiting for the target executable in pendingTx.TargetExePath.
        // - Launching from TargetExePath itself when EXE is missing/unreadable/hash-mismatched OR required sidecars fail
        //   shares the same fail-closed recovery settlement so local rollback backups stay intact and recoverable.
        if (pendingTx is not null && !string.IsNullOrWhiteSpace(fullAppDir))
        {
            if (completedValid && runningMatchesTargetPath)
            {
                if (manifest is not null && !manifest.TransactionId.Equals(pendingTx.TransactionId, StringComparison.OrdinalIgnoreCase))
                    return;
                var targetExeName = Path.GetFileName(pendingTx.TargetExePath);
                var liveExeSha = File.Exists(actualRunningExe)
                    ? StorageCleanupService.ComputeFileSha256Safe(actualRunningExe)
                    : "";
                if (string.IsNullOrWhiteSpace(liveExeSha))
                {
                    SettleFailedFirstBootVerification(
                        fullAppDir,
                        pendingTx,
                        $"更新首启核验失败：无法读取主程序 {targetExeName}（可能被安全软件或其它进程临时占用），已保留上一版回退备份，请关闭占用后重试恢复原版。",
                        registryPath);
                    return;
                }
                if (!liveExeSha.Equals(pendingTx.TargetExeSha256, StringComparison.OrdinalIgnoreCase))
                {
                    SettleFailedFirstBootVerification(
                        fullAppDir,
                        pendingTx,
                        $"更新首启核验失败：主程序 {targetExeName} 内容与已签名事务不符，已保留上一版回退备份，请点击恢复原版后重试。",
                        registryPath);
                    return;
                }
                if (!VerifyRequiredSideFilesMatch(fullAppDir, pendingTx, out var sideError))
                {
                    SettleFailedFirstBootVerification(fullAppDir, pendingTx, sideError, registryPath);
                    return;
                }

                // Persist authoritative VerifiedSuccess receipt BEFORE deleting any temporary tx/rollback files (Blocker 2).
                var verifiedHashes = pendingTx.ExpectedRequiredSideFileSha256 is not null
                    ? new Dictionary<string, string>(pendingTx.ExpectedRequiredSideFileSha256, StringComparer.OrdinalIgnoreCase)
                    : CaptureCurrentRequiredSideHashes(fullAppDir);
                JsonFile.Write(Path.Combine(fullAppDir, UpdateVerifiedMarkerName), new VerifiedUpdateReceipt(
                    pendingTx.TransactionId,
                    pendingTx.TargetExeSha256,
                    verifiedHashes,
                    DateTime.UtcNow));

                StorageCleanupService.MarkTransactionVerified(BackupCategory.ProgramUpdate, fullAppDir, pendingTx.TransactionId, registryPath);
                if (manifest is not null)
                {
                    foreach (var item in manifest.Items)
                        TryDeleteFile(Path.Combine(fullAppDir, $"{item.FileName}.{manifest.BackupPid}.absent"));
                }
                TryDeleteFile(Path.Combine(fullAppDir, UpdateRollbackManifestName));
                TryDeleteFile(Path.Combine(fullAppDir, UpdateUnrecoveredListName));
                TryDeleteFile(completedPath);
                TryDeleteFile(Path.Combine(fullAppDir, UpdatePendingTxName));
                if (hasDeferred) return;
            }
            else if (hasDeferred)
            {
                return;
            }
        }

        // Startup cleanup uses the exact same unified StorageCleanupService rules; remaining InProgress/unowned records are never deleted.
        var ctx = new StorageCleanupContext("windows", fullAppDir, tempRoot, [], SettingsPath: settingsPath, RegistryPath: registryPath);
        StorageCleanupService.RotateAfterVerifiedSuccess(BackupCategory.ProgramUpdate, fullAppDir, ctx);
    }

    public sealed record PendingUpdateNotice(
        string? ErrorReason,
        string? DeferredFile,
        string? DeferredSourceDir,
        string? DeferredStageDir = null,
        bool IsRollbackIncomplete = false,
        string[]? UnrecoveredFiles = null,
        bool IsUpdateInProgress = false,
        bool IsOriginalGroupVerifiedRestored = false);

    public static PendingUpdateNotice ReadPendingUpdateNotice(string appDir, string? registryPath = null) =>
        ReadPendingUpdateNoticeCore(appDir, checkLock: true, registryPath);

    private static PendingUpdateNotice ReadPendingUpdateNoticeCore(string appDir, bool checkLock, string? registryPath)
    {
        if (checkLock && IsSwapLockActive(appDir))
            return new(null, null, null, null, IsRollbackIncomplete: false, UnrecoveredFiles: [], IsUpdateInProgress: true);

        string? errReason = null;
        var errFile = Path.Combine(appDir, UpdateErrorMarkerName);
        if (File.Exists(errFile))
        {
            try { errReason = File.ReadAllText(errFile).Trim(); } catch { errReason = "上次更新文件替换失败并已回滚。"; }
        }
        string? defFile = null;
        string? defPayload = null;
        string? defStage = null;
        var defPath = Path.Combine(appDir, UpdateDeferredManifestName);
        if (File.Exists(defPath))
        {
            try
            {
                (defFile, defPayload, _, defStage) = ReadDeferredPaths(defPath);
            }
            catch { }
        }

        var unrecovered = new List<string>();
        var unrecSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddUnrecovered(string name)
        {
            var trimmed = name.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && unrecSet.Add(trimmed))
                unrecovered.Add(trimmed);
        }

        var unrecPath = Path.Combine(appDir, UpdateUnrecoveredListName);
        if (File.Exists(unrecPath) && !StorageCleanupService.IsReparsePointPath(unrecPath))
        {
            try
            {
                foreach (var line in File.ReadAllLines(unrecPath))
                    AddUnrecovered(line);
            }
            catch { }
        }

        var fullAppDir = Path.GetFullPath(appDir);
        var pendingTx = ReadPendingTransaction(fullAppDir);
        var manifest = ReadRollbackManifest(fullAppDir);
        var hasCompleted = HasValidCompletedReceipt(fullAppDir, pendingTx, manifest);
        var txVerified = manifest is not null && IsTransactionVerified(fullAppDir, manifest.TransactionId, registryPath);
        var registry = StorageCleanupService.LoadRegistry(registryPath);
        if (manifest is not null && !txVerified && (!hasCompleted || File.Exists(unrecPath) || File.Exists(errFile)))
        {
            foreach (var entry in manifest.Items)
            {
                if (IsRollbackEntryMismatched(fullAppDir, manifest.TransactionId, entry, registry))
                    AddUnrecovered(entry.FileName);
            }
        }

        var isIncomplete = unrecovered.Count > 0;
        var isOriginalRestored = !isIncomplete &&
                                 !hasCompleted &&
                                 !File.Exists(unrecPath) &&
                                 !txVerified &&
                                 AllManifestItemsOriginalAndNoPidBackups(fullAppDir, manifest, registry);
        if (isIncomplete && string.IsNullOrWhiteSpace(errReason))
            errReason = "上次更新过程意外中断且尚未完成，部分文件尚未恢复原版。";

        return new(errReason, defFile, defPayload, defStage, isIncomplete, unrecovered.ToArray(), IsUpdateInProgress: false, IsOriginalGroupVerifiedRestored: isOriginalRestored);
    }

    public static RollbackResumeResult ResumePendingRollback(string appDir, string? registryPath = null)
    {
        if (string.IsNullOrWhiteSpace(appDir) || !Directory.Exists(appDir))
            throw new SetupException("无法定位程序目录，无法继续恢复原版。");

        var fullAppDir = Path.GetFullPath(appDir);
        if (IsSwapLockActive(fullAppDir))
            return new RollbackResumeResult(false, [], [], "原地更新进程仍在执行替换，请等待更新完成后再试。");

        var pendingTx = ReadPendingTransaction(fullAppDir);
        var manifest = ReadRollbackManifest(fullAppDir);
        var unrecPath = Path.Combine(fullAppDir, UpdateUnrecoveredListName);
        var errPath = Path.Combine(fullAppDir, UpdateErrorMarkerName);

        var checkTxId = manifest?.TransactionId ?? pendingTx?.TransactionId;
        if (IsTransactionVerified(fullAppDir, checkTxId, registryPath))
            return new RollbackResumeResult(false, [], [], "该更新事务已完成首启校验并生效，禁止回退到旧版本。");

        if (HasValidCompletedReceipt(fullAppDir, pendingTx, manifest) && !File.Exists(unrecPath) && !File.Exists(errPath))
            return new RollbackResumeResult(false, [], [], "当前原地更新已写入完成回执，不能回退到旧版本。");

        var registry = StorageCleanupService.LoadRegistry(registryPath);
        var entries = BuildRollbackRecoveryEntries(fullAppDir, manifest, registry);
        if (entries.Count == 0)
            return new RollbackResumeResult(true, [], [], "未发现需要继续恢复的回滚项。");

        var restored = new List<string>();
        var remaining = new List<string>();
        var residualFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (TryRestoreSingleRollbackEntry(fullAppDir, manifest?.TransactionId, manifest?.BackupPid, entry, registry, residualFiles))
                restored.Add(entry.FileName);
            else
                remaining.Add(entry.FileName);
        }

        var distinctRestored = restored.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var distinctRemaining = remaining.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (distinctRemaining.Length > 0)
        {
            File.WriteAllLines(unrecPath, distinctRemaining, new UTF8Encoding(false));
            return new RollbackResumeResult(
                false,
                distinctRestored,
                distinctRemaining,
                $"仍有 {distinctRemaining.Length} 个文件未恢复原版（{string.Join("、", distinctRemaining)}），请关闭占用这些文件的程序或核实备份归属后重试。");
        }

        foreach (var residual in residualFiles)
            TryDeleteFile(residual);
        TryDeleteFile(Path.Combine(fullAppDir, UpdateRollbackManifestName));
        TryDeleteFile(unrecPath);
        TryDeleteFile(errPath);
        TryDeleteFile(Path.Combine(fullAppDir, UpdateCompletedReceiptName));
        TryDeleteFile(Path.Combine(fullAppDir, UpdateSwapLockName));

        var txId = manifest?.TransactionId ?? pendingTx?.TransactionId;
        TryDeleteFile(Path.Combine(fullAppDir, UpdatePendingTxName));

        if (!string.IsNullOrWhiteSpace(txId))
        {
            StorageCleanupService.UpdateTransactionState(BackupCategory.ProgramUpdate, fullAppDir, txId,
                BackupOpState.FailedOrCancelled, "Rollback resumed and verified against original checksums", registryPath);
        }

        return new RollbackResumeResult(true, distinctRestored, [], "已从本地备份完整恢复原版文件并通过校验。");
    }

    internal static bool HasUnresolvedRollbackManifest(string appDir, string? registryPath = null)
    {
        if (string.IsNullOrWhiteSpace(appDir) || !Directory.Exists(appDir)) return false;
        var manifest = ReadRollbackManifest(appDir);
        if (manifest is null) return false;
        if (IsTransactionVerified(appDir, manifest.TransactionId, registryPath)) return false;
        return !HasValidCompletedReceipt(appDir, ReadPendingTransaction(appDir), manifest);
    }

    internal static bool IsTransactionVerified(string appDir, string? transactionId, string? registryPath = null)
    {
        if (string.IsNullOrWhiteSpace(appDir) || string.IsNullOrWhiteSpace(transactionId)) return false;
        var fullAppDir = Path.GetFullPath(appDir);
        var verifiedPath = Path.Combine(fullAppDir, UpdateVerifiedMarkerName);
        if (File.Exists(verifiedPath) && !StorageCleanupService.IsReparsePointPath(verifiedPath))
        {
            try
            {
                var v = JsonSerializer.Deserialize<VerifiedUpdateReceipt>(File.ReadAllText(verifiedPath), JsonFile.Format);
                if (v is not null && string.Equals(v.TransactionId, transactionId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }

        var reg = StorageCleanupService.LoadRegistry(registryPath);
        var matches = reg.Where(r =>
            r.Category == BackupCategory.ProgramUpdate &&
            r.ScopeId.Equals(fullAppDir, StringComparison.OrdinalIgnoreCase) &&
            r.TransactionId.Equals(transactionId, StringComparison.OrdinalIgnoreCase) &&
            (r.Role == "backup" || r.Role == "backup-sidecar" || r.Role == "backup-absent")).ToList();
        return matches.Count > 0 && matches.All(r => r.OpState == BackupOpState.VerifiedSuccess);
    }

    private static void SettleFailedFirstBootVerification(
        string fullAppDir,
        PendingUpdateTransaction pendingTx,
        string errorReason,
        string? registryPath)
    {
        StorageCleanupService.UpdateTransactionState(
            BackupCategory.ProgramUpdate,
            fullAppDir,
            pendingTx.TransactionId,
            BackupOpState.FailedOrCancelled,
            errorReason,
            registryPath);
        TryDeleteFile(Path.Combine(fullAppDir, UpdateCompletedReceiptName));
        var unrecItems = (new[] { Path.GetFileName(pendingTx.TargetExePath) })
            .Concat(RequiredSideFiles)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        try { File.WriteAllLines(Path.Combine(fullAppDir, UpdateUnrecoveredListName), unrecItems, new UTF8Encoding(false)); } catch { }
        try { File.WriteAllText(Path.Combine(fullAppDir, UpdateErrorMarkerName), errorReason, new UTF8Encoding(false)); } catch { }
    }

    private static bool VerifyRequiredSideFilesMatch(string fullAppDir, PendingUpdateTransaction pendingTx, out string errorReason)
    {
        errorReason = "";
        foreach (var req in RequiredSideFiles)
        {
            var reqPath = Path.Combine(fullAppDir, req);
            if (!File.Exists(reqPath) || StorageCleanupService.IsReparsePointPath(reqPath))
            {
                errorReason = $"更新首启核验失败：缺少必需随包文件 {req}，已保留上一版回退备份，请核查后重试。";
                return false;
            }
            var liveSha = StorageCleanupService.ComputeFileSha256Safe(reqPath);
            if (string.IsNullOrWhiteSpace(liveSha))
            {
                errorReason = $"更新首启核验失败：无法读取必需随包文件 {req}，已保留上一版回退备份。";
                return false;
            }
            if (pendingTx.ExpectedRequiredSideFileSha256 is not null &&
                pendingTx.ExpectedRequiredSideFileSha256.TryGetValue(req, out var expectedSha) &&
                !string.IsNullOrWhiteSpace(expectedSha) &&
                !liveSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                errorReason = $"更新首启核验失败：必需随包文件 {req} 内容与已签名事务不符，已保留上一版回退备份。";
                return false;
            }
        }
        return true;
    }

    private static Dictionary<string, string> CaptureCurrentRequiredSideHashes(string fullAppDir)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in RequiredSideFiles)
        {
            var reqPath = Path.Combine(fullAppDir, req);
            if (File.Exists(reqPath) && !StorageCleanupService.IsReparsePointPath(reqPath))
                hashes[req] = StorageCleanupService.ComputeFileSha256Safe(reqPath);
        }
        return hashes;
    }

    private static VerifiedUpdateReceipt? ReadVerifiedUpdateMarker(string appDir)
    {
        var verifiedPath = Path.Combine(appDir, UpdateVerifiedMarkerName);
        if (!File.Exists(verifiedPath) || StorageCleanupService.IsReparsePointPath(verifiedPath)) return null;
        try { return JsonSerializer.Deserialize<VerifiedUpdateReceipt>(File.ReadAllText(verifiedPath), JsonFile.Format); }
        catch { return null; }
    }

    private static string? TryReadCompletedReceiptTxId(string appDir)
    {
        var completedPath = Path.Combine(appDir, UpdateCompletedReceiptName);
        if (!File.Exists(completedPath) || StorageCleanupService.IsReparsePointPath(completedPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(completedPath));
            var swapOk = doc.RootElement.TryGetProperty("swapSucceeded", out var okProp) && okProp.GetBoolean();
            if (!swapOk) return null;
            return doc.RootElement.TryGetProperty("transactionId", out var txProp) ? txProp.GetString() : null;
        }
        catch { return null; }
    }

    private static bool AllManifestItemsOriginalAndNoPidBackups(
        string fullAppDir,
        RollbackManifest? manifest,
        IReadOnlyList<BackupRegistryRecord> registry)
    {
        if (manifest is null || manifest.Items.Count == 0)
            return false;
        foreach (var item in manifest.Items)
        {
            if (IsRollbackEntryMismatched(fullAppDir, manifest.TransactionId, item, registry))
                return false;
            if (manifest.BackupPid > 0)
            {
                var bakPath = Path.Combine(fullAppDir, $"{item.FileName}.{manifest.BackupPid}.bak");
                var absPath = Path.Combine(fullAppDir, $"{item.FileName}.{manifest.BackupPid}.absent");
                if (File.Exists(bakPath) || File.Exists(absPath))
                    return false;
            }
        }
        return true;
    }

    private static PendingUpdateTransaction? ReadPendingTransaction(string appDir)
    {
        var pendingTxFile = Path.Combine(appDir, UpdatePendingTxName);
        if (!File.Exists(pendingTxFile) || StorageCleanupService.IsReparsePointPath(pendingTxFile)) return null;
        try { return JsonSerializer.Deserialize<PendingUpdateTransaction>(File.ReadAllText(pendingTxFile), JsonFile.Format); }
        catch { return null; }
    }

    internal static bool HasValidCompletedReceipt(string appDir) =>
        HasValidCompletedReceipt(appDir, ReadPendingTransaction(appDir), ReadRollbackManifest(appDir));

    private static bool HasValidCompletedReceipt(string appDir, PendingUpdateTransaction? pendingTx, RollbackManifest? manifest)
    {
        var completedPath = Path.Combine(appDir, UpdateCompletedReceiptName);
        if (!File.Exists(completedPath) || StorageCleanupService.IsReparsePointPath(completedPath)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(completedPath));
            var swapOk = doc.RootElement.TryGetProperty("swapSucceeded", out var okProp) && okProp.GetBoolean();
            if (!swapOk) return false;
            var expectedTx = pendingTx?.TransactionId ?? manifest?.TransactionId;
            if (string.IsNullOrWhiteSpace(expectedTx)) return true;
            return doc.RootElement.TryGetProperty("transactionId", out var txProp) &&
                   string.Equals(txProp.GetString(), expectedTx, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static RollbackManifest? ReadRollbackManifest(string appDir)
    {
        var path = Path.Combine(appDir, UpdateRollbackManifestName);
        if (!File.Exists(path) || StorageCleanupService.IsReparsePointPath(path)) return null;
        try { return JsonSerializer.Deserialize<RollbackManifest>(File.ReadAllText(path), JsonFile.Format); }
        catch { return null; }
    }

    private static string? TryGetOwnedBackupPath(
        string fullAppDir,
        string? transactionId,
        RollbackFileEntry entry,
        IReadOnlyList<BackupRegistryRecord> registry)
    {
        if (!string.IsNullOrWhiteSpace(entry.BackupFileName))
        {
            var exact = Path.Combine(fullAppDir, entry.BackupFileName);
            var foreignOwner = registry.FirstOrDefault(r =>
                r.Category == BackupCategory.ProgramUpdate &&
                r.ScopeId.Equals(fullAppDir, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(r.TransactionId) &&
                !string.IsNullOrWhiteSpace(transactionId) &&
                !r.TransactionId.Equals(transactionId, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFullPath(r.Path).Equals(Path.GetFullPath(exact), StringComparison.OrdinalIgnoreCase));
            if (foreignOwner is null)
                return exact;
        }
        if (!string.IsNullOrWhiteSpace(transactionId))
        {
            var ownedRec = registry.FirstOrDefault(r =>
                r.Category == BackupCategory.ProgramUpdate &&
                r.ScopeId.Equals(fullAppDir, StringComparison.OrdinalIgnoreCase) &&
                r.TransactionId.Equals(transactionId, StringComparison.OrdinalIgnoreCase) &&
                (r.Role == "backup" || r.Role == "backup-sidecar") &&
                Path.GetFileName(r.Path).StartsWith(entry.FileName + ".", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(r.Path).EndsWith(".bak", StringComparison.OrdinalIgnoreCase));
            if (ownedRec is not null)
                return ownedRec.Path;
        }
        return null;
    }

    private static (string? SourcePath, bool IsOwnedByTx) TryResolveSourceBackup(
        string fullAppDir,
        string? transactionId,
        RollbackFileEntry entry,
        IReadOnlyList<BackupRegistryRecord> registry)
    {
        var ownedPath = TryGetOwnedBackupPath(fullAppDir, transactionId, entry, registry);
        if (!string.IsNullOrWhiteSpace(ownedPath) &&
            File.Exists(ownedPath) &&
            !StorageCleanupService.IsReparsePointPath(ownedPath))
        {
            var ownedSha = StorageCleanupService.ComputeFileSha256Safe(ownedPath);
            if (!string.IsNullOrWhiteSpace(ownedSha) &&
                (string.IsNullOrWhiteSpace(entry.OriginalSha256) ||
                 ownedSha.Equals(entry.OriginalSha256, StringComparison.OrdinalIgnoreCase)))
            {
                return (ownedPath, true);
            }
            return (null, false);
        }

        // Strict legacy fallback ONLY when exact owned backup filename/pid was unavailable:
        // exclude any backup owned by another transaction, require content proof (OriginalSha256) or exact txId registry match, and require unique match!
        if (!string.IsNullOrWhiteSpace(entry.BackupFileName))
            return (null, false);

        var foreignPaths = registry
            .Where(r => r.Category == BackupCategory.ProgramUpdate &&
                        r.ScopeId.Equals(fullAppDir, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(r.TransactionId) &&
                        (string.IsNullOrWhiteSpace(transactionId) || !r.TransactionId.Equals(transactionId, StringComparison.OrdinalIgnoreCase)))
            .Select(r => Path.GetFullPath(r.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = Directory.GetFiles(fullAppDir, $"{entry.FileName}.*.bak")
            .Where(p => !StorageCleanupService.IsReparsePointPath(p) &&
                        !foreignPaths.Contains(Path.GetFullPath(p)))
            .Where(p =>
            {
                var sha = StorageCleanupService.ComputeFileSha256Safe(p);
                if (string.IsNullOrWhiteSpace(sha)) return false;
                if (!string.IsNullOrWhiteSpace(entry.OriginalSha256))
                    return sha.Equals(entry.OriginalSha256, StringComparison.OrdinalIgnoreCase);
                return !string.IsNullOrWhiteSpace(transactionId) && registry.Any(r =>
                    r.Category == BackupCategory.ProgramUpdate &&
                    r.TransactionId.Equals(transactionId, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFullPath(r.Path).Equals(Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        if (candidates.Count != 1)
            return (null, false);

        var chosen = candidates[0];
        var isOwned = !string.IsNullOrWhiteSpace(transactionId) && registry.Any(r =>
            r.Category == BackupCategory.ProgramUpdate &&
            r.TransactionId.Equals(transactionId, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFullPath(r.Path).Equals(Path.GetFullPath(chosen), StringComparison.OrdinalIgnoreCase));
        return (chosen, isOwned);
    }

    private static bool IsRollbackEntryMismatched(
        string appDir,
        string? transactionId,
        RollbackFileEntry entry,
        IReadOnlyList<BackupRegistryRecord> registry)
    {
        var fullAppDir = Path.GetFullPath(appDir);
        var targetPath = Path.Combine(fullAppDir, entry.FileName);
        if (!entry.ExistedBefore)
            return File.Exists(targetPath);
        if (!File.Exists(targetPath) || StorageCleanupService.IsReparsePointPath(targetPath))
            return true;
        var liveSha = StorageCleanupService.ComputeFileSha256Safe(targetPath);
        if (string.IsNullOrWhiteSpace(liveSha))
            return true;
        if (!string.IsNullOrWhiteSpace(entry.OriginalSha256))
            return !liveSha.Equals(entry.OriginalSha256, StringComparison.OrdinalIgnoreCase);

        // Legacy/empty-SHA manifest fallback: never treat unverified live file as recovered unless backed by an unambiguous matching backup.
        var (bakPath, _) = TryResolveSourceBackup(fullAppDir, transactionId, entry, registry);
        if (string.IsNullOrWhiteSpace(bakPath))
            return true;
        var bakSha = StorageCleanupService.ComputeFileSha256Safe(bakPath);
        if (string.IsNullOrWhiteSpace(bakSha))
            return true;
        return !liveSha.Equals(bakSha, StringComparison.OrdinalIgnoreCase);
    }

    private static List<RollbackFileEntry> BuildRollbackRecoveryEntries(
        string fullAppDir,
        RollbackManifest? manifest,
        IReadOnlyList<BackupRegistryRecord> registry)
    {
        var entries = new List<RollbackFileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest is not null)
        {
            foreach (var item in manifest.Items)
            {
                if (seen.Add(item.FileName))
                    entries.Add(item);
            }
        }

        var unrecPath = Path.Combine(fullAppDir, UpdateUnrecoveredListName);
        if (File.Exists(unrecPath) && !StorageCleanupService.IsReparsePointPath(unrecPath))
        {
            foreach (var raw in File.ReadAllLines(unrecPath))
            {
                var name = raw.Trim();
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;

                var absentRec = manifest is not null
                    ? registry.FirstOrDefault(r =>
                        r.Category == BackupCategory.ProgramUpdate &&
                        r.ScopeId.Equals(fullAppDir, StringComparison.OrdinalIgnoreCase) &&
                        r.TransactionId.Equals(manifest.TransactionId, StringComparison.OrdinalIgnoreCase) &&
                        r.Role == "backup-absent" &&
                        Path.GetFileName(r.Path).StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
                    : null;
                var pidAbsent = manifest is { BackupPid: > 0 }
                    ? Path.Combine(fullAppDir, $"{name}.{manifest.BackupPid}.absent")
                    : null;
                if (absentRec is not null || (pidAbsent is not null && File.Exists(pidAbsent)))
                {
                    entries.Add(new RollbackFileEntry(name, false, "", ""));
                    continue;
                }

                var exactBak = manifest is { BackupPid: > 0 } ? $"{name}.{manifest.BackupPid}.bak" : "";
                var ownedRec = !string.IsNullOrWhiteSpace(manifest?.TransactionId)
                    ? registry.FirstOrDefault(r =>
                        r.Category == BackupCategory.ProgramUpdate &&
                        r.ScopeId.Equals(fullAppDir, StringComparison.OrdinalIgnoreCase) &&
                        r.TransactionId.Equals(manifest.TransactionId, StringComparison.OrdinalIgnoreCase) &&
                        (r.Role == "backup" || r.Role == "backup-sidecar") &&
                        Path.GetFileName(r.Path).StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
                    : null;
                entries.Add(new RollbackFileEntry(
                    name,
                    true,
                    ownedRec?.ContentSha256 ?? "",
                    ownedRec is not null ? Path.GetFileName(ownedRec.Path) : exactBak));
            }
        }
        return entries;
    }

    private static bool TryRestoreSingleRollbackEntry(
        string fullAppDir,
        string? transactionId,
        int? backupPid,
        RollbackFileEntry entry,
        IReadOnlyList<BackupRegistryRecord> registry,
        HashSet<string> residualFiles)
    {
        var targetPath = Path.Combine(fullAppDir, entry.FileName);
        if (StorageCleanupService.IsReparsePointPath(targetPath))
            return false;

        if (!entry.ExistedBefore)
        {
            if (File.Exists(targetPath))
            {
                try { File.Delete(targetPath); }
                catch { return false; }
            }
            if (File.Exists(targetPath)) return false;

            if (backupPid is > 0)
                residualFiles.Add(Path.Combine(fullAppDir, $"{entry.FileName}.{backupPid.Value}.absent"));
            if (!string.IsNullOrWhiteSpace(transactionId))
            {
                foreach (var rec in registry.Where(r =>
                    r.Category == BackupCategory.ProgramUpdate &&
                    r.ScopeId.Equals(fullAppDir, StringComparison.OrdinalIgnoreCase) &&
                    r.TransactionId.Equals(transactionId, StringComparison.OrdinalIgnoreCase) &&
                    r.Role == "backup-absent" &&
                    Path.GetFileName(r.Path).StartsWith(entry.FileName + ".", StringComparison.OrdinalIgnoreCase)))
                {
                    residualFiles.Add(rec.Path);
                }
            }
            return true;
        }

        if (!IsRollbackEntryMismatched(fullAppDir, transactionId, entry, registry))
        {
            // Only mark THIS transaction's exact owned backup file as residual for deletion (never touch prior txs!).
            var ownedBak = TryGetOwnedBackupPath(fullAppDir, transactionId, entry, registry);
            if (!string.IsNullOrWhiteSpace(ownedBak) && File.Exists(ownedBak))
                residualFiles.Add(ownedBak);
            return true;
        }

        var (bakPath, isOwned) = TryResolveSourceBackup(fullAppDir, transactionId, entry, registry);
        if (string.IsNullOrWhiteSpace(bakPath) || !File.Exists(bakPath) || StorageCleanupService.IsReparsePointPath(bakPath))
            return false;

        try
        {
            File.Copy(bakPath, targetPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only rename-aside if targetPath IS the running executable itself; external locks must remain unrecovered.
            var runningPath = Environment.ProcessPath;
            var isSelfExe = !string.IsNullOrWhiteSpace(runningPath) &&
                            Path.GetFullPath(runningPath).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase);
            if (!isSelfExe) return false;

            try
            {
                var replacedTmp = $"{targetPath}.{Environment.ProcessId}.replaced-tmp";
                if (File.Exists(replacedTmp)) File.Delete(replacedTmp);
                File.Move(targetPath, replacedTmp);
                File.Copy(bakPath, targetPath, overwrite: true);
            }
            catch
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        if (IsRollbackEntryMismatched(fullAppDir, transactionId, entry, registry))
            return false;

        if (isOwned)
            residualFiles.Add(bakPath);
        return true;
    }

    public static void RetryDeferredSideFiles(string appDir)
    {
        var defPath = Path.Combine(appDir, UpdateDeferredManifestName);
        if (!File.Exists(defPath)) return;
        var (fileName, payloadDir, _, stageRoot) = ReadDeferredPaths(defPath);
        if (!DeferrableSideFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            throw new SetupException("待补全文件名称不在允许范围内。");
        var src = Path.Combine(payloadDir, fileName);
        if (!File.Exists(src))
            throw new SetupException($"未找到暂存的 {fileName}，请重新执行更新包。");
        File.Copy(src, Path.Combine(appDir, fileName), overwrite: true);
        File.Delete(defPath);
        if (!string.IsNullOrWhiteSpace(stageRoot) && IsStageRootDirectory(stageRoot))
            TryDeleteDirectory(stageRoot);
    }

    internal static (string file, string payloadDir, string rawStageDir, string? resolvedStageRoot) ReadDeferredPaths(string defPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(defPath));
        var root = doc.RootElement;
        var file = root.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
        var payload = root.TryGetProperty("payloadDir", out var p) && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()!
            : root.TryGetProperty("sourceDir", out var s) ? s.GetString() ?? "" : "";
        var stage = root.TryGetProperty("stageDir", out var st) ? st.GetString() ?? "" : "";
        var resolvedStage = ResolveStageRoot(stage, payload);
        return (file, payload, stage, resolvedStage);
    }

    private static bool IsStageRootDirectory(string path) =>
        Path.GetFileName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .StartsWith("claudome-stage-", StringComparison.OrdinalIgnoreCase);

    internal static string? ResolveStageRoot(string? explicitStageDir, string? payloadDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitStageDir))
        {
            var fullStage = Path.GetFullPath(explicitStageDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsStageRootDirectory(fullStage)) return fullStage;
        }
        if (string.IsNullOrWhiteSpace(payloadDir)) return null;
        var cur = Path.GetFullPath(payloadDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(cur))
        {
            if (IsStageRootDirectory(cur)) return cur;
            cur = Path.GetDirectoryName(cur);
        }
        return null;
    }

    public static void ClearUpdateErrorMarker(string appDir, string? registryPath = null)
    {
        if (string.IsNullOrWhiteSpace(appDir) || !Directory.Exists(appDir))
            return;
        var fullAppDir = Path.GetFullPath(appDir);
        var notice = ReadPendingUpdateNoticeCore(fullAppDir, checkLock: false, registryPath);
        TryDeleteFile(Path.Combine(fullAppDir, UpdateErrorMarkerName));

        if (!IsSwapLockActive(fullAppDir) &&
            !notice.IsRollbackIncomplete &&
            !File.Exists(Path.Combine(fullAppDir, UpdateUnrecoveredListName)) &&
            notice.IsOriginalGroupVerifiedRestored &&
            !HasValidCompletedReceipt(fullAppDir))
        {
            var pendingTx = ReadPendingTransaction(fullAppDir);
            if (pendingTx is not null && !IsTransactionVerified(fullAppDir, pendingTx.TransactionId, registryPath))
            {
                StorageCleanupService.UpdateTransactionState(BackupCategory.ProgramUpdate, fullAppDir, pendingTx.TransactionId,
                    BackupOpState.FailedOrCancelled, notice.ErrorReason ?? "Update error acknowledged; original group verified intact", registryPath);
            }
            TryDeleteFile(Path.Combine(fullAppDir, UpdateSwapLockName));
            TryDeleteFile(Path.Combine(fullAppDir, UpdatePendingTxName));
            TryDeleteFile(Path.Combine(fullAppDir, UpdateRollbackManifestName));
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Claudome-Updater/1.0");
        return client;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
