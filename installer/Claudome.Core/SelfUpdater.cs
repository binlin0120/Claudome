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
    string PublishedAtUtc);

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
    string ExeSha256);

/// <summary>
/// Checks GitHub Releases manifests, downloads and SHA-256 verifies Windows update archives,
/// enforces anti-downgrade and Zip-Slip checks, and performs atomic in-place EXE swap + restart.
/// </summary>
public static class SelfUpdater
{
    public const string DefaultManifestUrl = "https://github.com/binlin0120/Claudome/releases/latest/download/latest.json";
    public const string DefaultReleaseBaseUrl = "https://github.com/binlin0120/Claudome/releases/download";

    private static readonly string[] PayloadSideFiles =
    [
        "快速开始.txt",
        "PROJECT-LICENSE.txt",
        "Gateway-THIRD-PARTY-NOTICES.txt"
    ];

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

    public static ReleaseManifest ParseAndValidateManifest(string json)
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

        if (!TryParseVersion(version, out _))
            throw new SetupException("更新清单版本号格式无效。");
        if (!Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+\.[0-9]+$"))
            throw new SetupException("更新清单 tag 格式无效。");
        if (!Regex.IsMatch(zipName, @"^[A-Za-z0-9._-]+\.zip$"))
            throw new SetupException("更新清单压缩包名称不合法。");
        if (!IsValidSha256(zipSha) || !IsValidSha256(exeSha))
            throw new SetupException("更新清单缺少有效的 SHA-256 校验值。");

        return new ReleaseManifest(version, tag, zipName, zipSha, exeSha, notes, published);
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
        var url = string.IsNullOrWhiteSpace(manifestUrl) ? DefaultManifestUrl : manifestUrl;
        using var client = CreateHttpClient(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(url, token);
        if ((int)response.StatusCode == 404)
        {
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
        CancellationToken token)
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
        if (!string.Equals(versionInfo.ProductName?.Trim(), "Claudome", StringComparison.Ordinal))
            throw new SetupException("更新文件不是合法的 Claudome 程序，已拒绝替换。");

        var rawVersion = !string.IsNullOrWhiteSpace(versionInfo.ProductVersion)
            ? versionInfo.ProductVersion
            : versionInfo.FileVersion;
        if (!TryParseVersion(rawVersion, out var candidate))
            throw new SetupException("无法读取新版本 Claudome.exe 的版本号。");

        if (candidate < currentVersion || (!allowSameVersion && candidate == currentVersion))
            throw new SetupException($"目标版本（v{candidate.ToString(3)}）不高于当前版本（v{currentVersion.ToString(3)}），已阻止降级覆盖。");

        return new StagedUpdate(candidate.ToString(3), stagingRoot, Path.GetDirectoryName(exePath)!, exePath, actualExeHash);
    }

    public static void StartSwapAndRestart(StagedUpdate staged, string currentExePath, int currentPid, bool relaunch = true)
    {
        if (!File.Exists(staged.ExePath))
            throw new SetupException("待更新的 Claudome.exe 已失效，请重试。");

        var appDir = Path.GetDirectoryName(Path.GetFullPath(currentExePath))
            ?? throw new SetupException("无法定位当前程序目录。");
        var exeName = Path.GetFileName(currentExePath);
        var backupName = $"{exeName}.{currentPid}.bak";
        var scriptPath = Path.Combine(Path.GetTempPath(), $"claudome-update-{Guid.NewGuid():N}.cmd");
        var script = BuildSwapScript(appDir, staged.PayloadDirectory, staged.StagingDirectory, exeName, backupName, currentPid, relaunch);
        File.WriteAllText(scriptPath, script, Encoding.Default);

        var start = new ProcessStartInfo("cmd.exe", $"/d /c \"\"{scriptPath}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath()
        };
        if (Process.Start(start) is null)
            throw new SetupException("无法启动本地替换更新脚本。");
    }

    internal static string BuildSwapScript(
        string appDir,
        string payloadDir,
        string stagingDir,
        string exeName,
        string backupName,
        int currentPid,
        bool relaunch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set \"APP_DIR={appDir}\"");
        sb.AppendLine($"set \"PAYLOAD_DIR={payloadDir}\"");
        sb.AppendLine($"set \"STAGE_DIR={stagingDir}\"");
        sb.AppendLine($"set \"EXE_NAME={exeName}\"");
        sb.AppendLine($"set \"BAK_NAME={backupName}\"");
        sb.AppendLine("set /a COUNT=0");
        sb.AppendLine(":WAIT_EXIT");
        sb.AppendLine($"tasklist /FI \"PID eq {currentPid}\" 2>nul | find \"{currentPid}\" >nul");
        sb.AppendLine("if errorlevel 1 goto DO_SWAP");
        sb.AppendLine("set /a COUNT+=1");
        sb.AppendLine("if %COUNT% GEQ 30 goto DO_SWAP");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        sb.AppendLine("goto WAIT_EXIT");
        sb.AppendLine(":DO_SWAP");
        sb.AppendLine("if exist \"%APP_DIR%\\%BAK_NAME%\" del /f /q \"%APP_DIR%\\%BAK_NAME%\" >nul 2>nul");
        sb.AppendLine("if exist \"%APP_DIR%\\%EXE_NAME%\" ren \"%APP_DIR%\\%EXE_NAME%\" \"%BAK_NAME%\"");
        sb.AppendLine("if errorlevel 1 goto ROLLBACK");
        sb.AppendLine("copy /y \"%PAYLOAD_DIR%\\%EXE_NAME%\" \"%APP_DIR%\\%EXE_NAME%\" >nul");
        sb.AppendLine("if errorlevel 1 goto ROLLBACK");
        foreach (var file in PayloadSideFiles)
        {
            sb.AppendLine($"if exist \"%PAYLOAD_DIR%\\{file}\" copy /y \"%PAYLOAD_DIR%\\{file}\" \"%APP_DIR%\\{file}\" >nul 2>nul");
        }
        sb.AppendLine("rmdir /s /q \"%STAGE_DIR%\" >nul 2>nul");
        if (relaunch)
        {
            sb.AppendLine("start \"\" \"%APP_DIR%\\%EXE_NAME%\"");
        }
        sb.AppendLine("del /f /q \"%~f0\" >nul 2>nul");
        sb.AppendLine("exit /b 0");
        sb.AppendLine(":ROLLBACK");
        sb.AppendLine("if not exist \"%APP_DIR%\\%EXE_NAME%\" if exist \"%APP_DIR%\\%BAK_NAME%\" ren \"%APP_DIR%\\%BAK_NAME%\" \"%EXE_NAME%\"");
        sb.AppendLine("del /f /q \"%~f0\" >nul 2>nul");
        sb.AppendLine("exit /b 1");
        return sb.ToString();
    }

    public static void CleanupStaleUpdateFiles(string appDir, string tempRoot)
    {
        if (Directory.Exists(appDir))
        {
            foreach (var file in Directory.GetFiles(appDir, "Claudome.exe.*.bak"))
            {
                TryDeleteFile(file);
            }
        }
        if (Directory.Exists(tempRoot))
        {
            foreach (var script in Directory.GetFiles(tempRoot, "claudome-update-*.cmd"))
            {
                TryDeleteFile(script);
            }
            foreach (var stage in Directory.GetDirectories(tempRoot, "claudome-stage-*"))
            {
                TryDeleteDirectory(stage);
            }
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
