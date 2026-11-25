using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed partial class Deployment
{
    // Called after the backend has verified the task VM and the guest login.
    internal async Task<IReadOnlyList<Resource>> PrepareGuestPackages(IVmBackend box, SessionState state, Secrets secrets,
        string guestRoot, bool browserOnly, CancellationToken token, Func<Resource, CancellationToken, Task<string>>? fetchPackage = null)
    {
        var options = state.Options;
        var packages = Catalog.Resources.Where(Catalog.IsGuestResource).ToArray();
        var pwsh = packages.Single(r => r.Id == "pwsh");
        var expectedVersion = Regex.Match(pwsh.FileName, @"^PowerShell-([\d.]+)-win-x64\.zip$").Groups[1].Value;
        if (expectedVersion.Length == 0) throw new SetupException("来宾 PowerShell 版本清单无效。");
        var guestPwsh = guestRoot + @"\pwsh\pwsh.exe";

        using var downloader = new Downloader();
        async Task Upload(Resource resource)
        {
            messages.Report("来宾缺少已核验的 " + resource.Name + "，正在补传。");
            var localFile = fetchPackage is null ? await downloader.Get(resource, options.Cache, downloads, token)
                : await fetchPackage(resource, token);
            var destination = guestRoot + "\\" + resource.FileName;
            try
            {
                if (box is HyperV hyperV)
                    await hyperV.CopyVerifiedResource(state, secrets, localFile, destination, resource, token);
                else
                    await box.CopyTo(state, secrets, localFile, destination, token);
            }
            catch (HandoffException) { throw; }
            catch (SetupException error) { throw new SetupException($"补传 {resource.Name} 失败。{Redaction.SafeError(error)}"); }
        }

        messages.Report("正在核对原来宾的 PowerShell 和安装资源。");
        await box.EnsureGuestDirectory(state, secrets, guestRoot, token);
        var runtimePresent = await box.GuestFileExists(state, secrets, guestPwsh, token);
        async Task<bool> VerifyExeVersion(string exePath)
        {
            if (options.PlatformId != "workstation")
            {
                var output = await box.GuestRun(state, secrets, exePath, ["-Version"], token, 30);
                if (HasExpectedPowerShell(output, expectedVersion)) return true;
                if (Regex.IsMatch(output, @"(?m)^\s*PowerShell\s+\d+\.")) return false;
                throw new SetupException("未能核对来宾 PowerShell 版本，已停止补传。");
            }
            return await TestWorkstationGuest(exePath,
                "if ($PSVersionTable.PSVersion.ToString() -ne '" + expectedVersion + "') { exit 41 }");
        }
        Task<bool> VerifyVersion() => VerifyExeVersion(guestPwsh);
        async Task<bool> TestWorkstationGuest(string exe, string command)
        {
            var code = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            try
            {
                await box.GuestRun(state, secrets, exe, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", code], token, 90);
                return true;
            }
            catch (SetupException error) when (error.Message == CommandFailure.GuestExit(41)) { return false; }
        }

        if (!runtimePresent || !await VerifyVersion())
        {
            var reusedBootstrapPwsh = false;
            const string bootstrapPwshDir = @"C:\ProgramData\ClaudeBootstrap\pwsh";
            const string bootstrapPwshExe = bootstrapPwshDir + @"\pwsh.exe";
            if (options.PlatformId == "hyperv" && await box.GuestFileExists(state, secrets, bootstrapPwshExe, token))
            {
                try
                {
                    if (await VerifyExeVersion(bootstrapPwshExe))
                    {
                        messages.Report("已在来宾内定位初始化阶段写入的 PowerShell 7，直接本地复用（免重复传送）。");
                        var targetDirEscaped = (guestRoot + @"\pwsh").Replace("'", "''");
                        await box.EnsureGuestDirectory(state, secrets, guestRoot + @"\pwsh", token);
                        var copyScript = $"Copy-Item -LiteralPath '{bootstrapPwshDir}' -Destination '{targetDirEscaped}' -Recurse -Force";
                        var encodedCopy = Convert.ToBase64String(Encoding.Unicode.GetBytes(copyScript));
                        await box.GuestRun(state, secrets, bootstrapPwshExe, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedCopy], token, 120);
                        reusedBootstrapPwsh = await VerifyVersion();
                    }
                }
                catch { reusedBootstrapPwsh = false; }
            }
            if (!reusedBootstrapPwsh)
            {
                var archive = guestRoot + "\\" + pwsh.FileName;
                var archiveMatches = false;
                if (await box.GuestFileExists(state, secrets, archive, token))
                {
                    if (options.PlatformId != "workstation")
                    {
                        var checksum = await box.GuestRun(state, secrets, @"C:\Windows\System32\certutil.exe", ["-hashfile", archive, "SHA256"], token, 150);
                        archiveMatches = Regex.IsMatch(checksum, @"(?im)^\s*" + Regex.Escape(pwsh.Sha256) + @"\s*$");
                    }
                    else if (runtimePresent)
                    {
                        var path = archive.Replace("'", "''");
                        archiveMatches = await TestWorkstationGuest(guestPwsh,
                            $"if ((Get-FileHash -LiteralPath '{path}' -Algorithm SHA256).Hash -ine '{pwsh.Sha256}') {{ exit 41 }}");
                    }
                }
                if (!archiveMatches) await Upload(pwsh);
                else messages.Report("已核验来宾 PowerShell 压缩包，直接复用。");
                await box.EnsureGuestDirectory(state, secrets, guestRoot + @"\pwsh", token);
                messages.Report("正在来宾中解压 PowerShell。");
                await box.GuestRun(state, secrets, @"C:\Windows\System32\tar.exe", ["-xf", archive, "-C", guestRoot + @"\pwsh"], token, 180);
                if (!await VerifyVersion())
                    throw new SetupException("来宾 PowerShell 解压后未通过版本回读。");
            }
        }
        else messages.Report("来宾 PowerShell 版本已核对，直接复用。");

        if (browserOnly) return [];
        var appPackages = packages.Where(r => r.Id != "pwsh").ToArray();
        var inventory = await ReadGuestInstallerInventory(box, state, secrets, guestRoot, guestPwsh, appPackages, token);
        var needed = appPackages.Where(r => !inventory.Installed.Contains(r.Id)).ToArray();
        var uploadIndex = 0;
        var toUploadCount = appPackages.Count(r => !inventory.Installed.Contains(r.Id) && !inventory.Cached.Contains(r.Id));
        foreach (var package in appPackages)
        {
            if (inventory.Installed.Contains(package.Id)) messages.Report(package.Name + " 已安装，沿用现有应用。");
            else if (inventory.Cached.Contains(package.Id)) messages.Report(package.Name + " 安装包已核验，直接复用。");
            else
            {
                uploadIndex++;
                messages.Report($"正在下载/核验并传送 {package.Name}（{uploadIndex}/{toUploadCount} · {package.Size / 1048576d:F1} MB）…");
                await Upload(package);
            }
        }
        return needed;
    }

    internal static bool HasExpectedPowerShell(string response, string version) =>
        response.Split('\n', StringSplitOptions.TrimEntries).Any(line => line == "PowerShell " + version);

    private sealed record PackageInventory(HashSet<string> Installed, HashSet<string> Cached);

    private static async Task<PackageInventory> ReadGuestInstallerInventory(IVmBackend box, SessionState state, Secrets secrets,
        string guestRoot, string guestPwsh, Resource[] packages, CancellationToken token)
    {
        var runId = Guid.NewGuid().ToString("N");
        var name = $"resource-inventory-{runId}.json";
        var guestReply = state.Options.PlatformId == "workstation" ? guestRoot + "\\" + name : "";
        var input = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            runId, vmId = state.VmId, workspaceId = state.Options.WorkspaceId,
            platform = state.Options.PlatformId, guestRoot, guestReply,
            resources = packages.Select(r => new { id = r.Id, fileName = r.FileName, sha256 = r.Sha256, size = r.Size })
        }));
        var script = "$inventoryInput='" + input + "'\n" + Catalog.EmbeddedText("GuestResourceInventory.ps1");
        var command = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result = await box.GuestRun(state, secrets, guestPwsh,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", command], token, 150);
        if (guestReply.Length != 0)
            result = await TakeGuestInventoryFile(box, state, secrets, guestReply, name, token);
        const string prefix = "CLAUDOME_INSTALLER_INVENTORY:";
        var lines = result.Split('\n', StringSplitOptions.TrimEntries).Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (lines.Length != 1 || lines[0].Length > 4096)
            throw new SetupException("未取得本轮来宾资源清单。请检查来宾工具后继续部署。");
        using var report = JsonDocument.Parse(lines[0][prefix.Length..]);
        GuestEvidence.RequireCurrent(report.RootElement, state, runId);
        if (report.RootElement.GetProperty("status").GetString() != "ready")
            throw new SetupException("来宾安装目录或资源无法可靠核对，已停止补传。");
        HashSet<string> Read(string key) => report.RootElement.GetProperty(key).EnumerateArray()
            .Select(entry => entry.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
        var installed = Read("installed");
        if (installed.Any(id => id is not ("clash" or "adspower")))
            throw new SetupException("来宾的已安装应用清单无效。");
        return new(installed, Read("cached"));
    }

    private static async Task<string> TakeGuestInventoryFile(IVmBackend box, SessionState state, Secrets secrets,
        string guestFile, string fileName, CancellationToken token)
    {
        var local = Paths.Under(state.Options.Private, fileName);
        try
        {
            await box.CopyFrom(state, secrets, guestFile, local, token);
            if (new FileInfo(local).Length > 8192) throw new SetupException("来宾资源回执超出限制。");
            return File.ReadAllText(local);
        }
        finally
        {
            if (File.Exists(local)) File.Delete(local);
            var escaped = guestFile.Replace("'", "''");
            var cleanup = Convert.ToBase64String(Encoding.Unicode.GetBytes($"Remove-Item -LiteralPath '{escaped}' -Force -ErrorAction SilentlyContinue"));
            try { await box.GuestRun(state, secrets, state.Options.GuestSetupDirectory + @"\pwsh\pwsh.exe",
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", cleanup], token, 15); }
            catch (SetupException) { }
        }
    }
}
