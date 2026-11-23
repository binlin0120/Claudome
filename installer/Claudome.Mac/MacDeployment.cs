using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeSetup;

/// <summary>
/// Orchestrates macOS guest package verification, VM provisioning (Fusion / UTM),
/// host pfctl boundary enforcement, and guest Configure.ps1 execution.
/// </summary>
public static class MacDeployment
{
    public const int DefaultGatewayPort = 60181;
    public const string DefaultMacBridgeInterface = "bridge100";

    private static readonly string[] GuestConfigureScripts =
    [
        "Configure.ps1",
        "BrowserAudit.ps1",
        "NetworkAudit.ps1",
        "Integrations.ps1"
    ];

    public static async Task<IReadOnlyList<Resource>> DownloadGuestAssetsAsync(
        SetupOptions options,
        bool isAppleSilicon,
        IProgress<string>? status,
        IProgress<DownloadProgress>? downloadProgress,
        CancellationToken token)
    {
        Paths.ValidateRoot(options.Root);
        Paths.EnsureWorkspace(options.Root);
        var resources = MacProvisioningMedia.GuestResources(isAppleSilicon);
        using var downloader = new Downloader();
        foreach (var resource in resources)
        {
            token.ThrowIfCancellationRequested();
            status?.Report($"正在下载并核验 {resource.Name}…");
            await downloader.Get(resource, options.Cache, downloadProgress, token);
        }
        status?.Report("全部来宾安装组件已下载并通过 SHA-256 核验。");
        return resources;
    }

    public static async Task<SessionState> DeployAsync(
        SessionState state,
        Secrets secrets,
        MacHostFacts hostFacts,
        IProgress<string> progress,
        CancellationToken token,
        IVmBackend? backendOverride = null,
        IHostFirewall? firewallOverride = null,
        IReadOnlyList<Resource>? verifiedResourcesOverride = null,
        bool skipEndpointDnsResolveForTest = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(hostFacts);

        IsolationPolicy.RequireSupportedPlatform(state, isMacOverride: true);
        var o = state.Options;
        var installWindows = !state.Completed.Contains("guest-ready");
        if (!o.AcceptChanges)
            throw new SetupException("请先勾选确认在独立任务目录配置双层防泄漏隔离环境。");
        if (installWindows && !o.AcceptWindowsLicense)
            throw new SetupException("请先勾选确认持有合法有效的微软 Windows 11 安装授权许可。");
        if (installWindows && secrets.VmPassword.Trim().Length < 10)
            throw new SetupException("请设置至少 10 位独立虚拟机密码。");
        o.Validate(requireIso: installWindows);
        if (installWindows)
        {
            if (string.IsNullOrWhiteSpace(o.IsoSha256))
                o.IsoSha256 = await ComputeFileSha256Async(o.IsoPath, token);
            else
                await RequireVerifiedIsoSha256Async(o, "部署前检测到", token);
        }
        if (installWindows && backendOverride is null)
            await (HostPlatform.Environment as MacHostEnvironment ?? new MacHostEnvironment()).InspectIsoArchitectureAsync(o.IsoPath, hostFacts.IsAppleSilicon, token);
        Paths.EnsureWorkspace(o.Root);
        HostPlatform.Environment.RestrictPrivateDirectory(o.Private);

        progress.Report("1 / 6  核验或下载 Mac 来宾所需组件…");
        var resources = verifiedResourcesOverride
            ?? await DownloadGuestAssetsAsync(o, hostFacts.IsAppleSilicon, progress, null, token);

        var firewall = firewallOverride ?? new MacGatewayFirewall();
        IVmBackend? ownedBackend = null;
        var backend = backendOverride ?? (o.PlatformId == "utm" ? ownedBackend = new UtmBackend() : ownedBackend = new FusionBackend());

        try
        {
            var resolvedAddresses = Array.Empty<string>();
            var endpointPort = 0;
            if (o.NetworkSource != "稍后配置")
            {
                var selectedNode = ParseNodeJson(secrets.SelectedNodeJson);
                var upstreamNode = selectedNode.Upstream ?? ParseOptionalNodeJson(secrets.UpstreamNodeJson);
                var firstHop = upstreamNode ?? selectedNode;
                endpointPort = firstHop.Port;
                resolvedAddresses = ResolveHostAddresses(firstHop.Server, skipEndpointDnsResolveForTest, token);
                var bridgeIface = firewall is MacGatewayFirewall macFw
                    ? await macFw.DetectGuestBridgeInterfaceAsync(token)
                    : DefaultMacBridgeInterface;
                progress.Report($"2 / 6  准备 macOS pfctl 防火墙隔离锚点（{bridgeIface}）…");
                await firewall.ApplyAsync(o.WorkspaceId, bridgeIface, resolvedAddresses, endpointPort, DefaultGatewayPort, token);
            }

            progress.Report($"3 / 6  准备 {(o.PlatformId == "utm" ? "UTM" : "VMware Fusion")} 虚拟机环境…");
            await backend.Prepare(o, token);
            await backend.EnsureMachine(state, token);
            if (!state.Completed.Contains("vm-created"))
            {
                if (installWindows)
                    await RequireVerifiedIsoSha256Async(o, "配置虚拟机前检测到", token);
                await backend.Configure(state, token);
                state.Completed.Add("vm-created");
                state.Save();
            }

            if (installWindows)
            {
                progress.Report("4 / 6  生成无人值守引导介质并启动 Windows 安装…");
                if (!await backend.IsRunning(state, token))
                {
                    await RequireVerifiedIsoSha256Async(o, "挂载安装介质前检测到", token);
                    await backend.InstallWindows(state, secrets, token);
                }
                await backend.Open(state, token);

                var guestReady = false;
                for (var attempt = 0; attempt < 180; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (await backend.GuestReady(state, secrets, token))
                    {
                        guestReady = true;
                        break;
                    }
                    if (attempt % 3 == 0)
                        progress.Report($"4 / 6  正在等待 Windows 安装与来宾工具就绪（第 {attempt + 1} 次探测）…");
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
                if (!guestReady)
                    throw new SetupException("来宾工具尚未就绪。请确认虚拟机内已完成 Windows 初始设置与 VMware Tools / 来宾工具安装后继续原任务。");

                if (!state.Completed.Contains("guest-ready")) state.Completed.Add("guest-ready");
                state.Save();
            }

            progress.Report("5 / 6  核对来宾 PowerShell 与传送安全安装脚本…");
            var guestRoot = o.GuestSetupDirectory;
            var guestPwshDir = guestRoot + @"\pwsh";
            var guestPwsh = guestPwshDir + @"\pwsh.exe";
            await backend.EnsureGuestDirectory(state, secrets, guestRoot, token);

            var pwshResource = resources.SingleOrDefault(r => r.Id == "pwsh")
                ?? throw new SetupException("Mac 来宾资源清单缺少 PowerShell 包。");
            if (!await backend.GuestFileExists(state, secrets, guestPwsh, token))
            {
                var cachedPwsh = Paths.Under(o.Cache, pwshResource.FileName);
                if (!File.Exists(cachedPwsh))
                    throw new SetupException($"本地缓存缺少 {pwshResource.FileName}，请先完成组件下载。");
                var guestPwshZip = $@"{guestRoot}\{pwshResource.FileName}";
                await backend.CopyTo(state, secrets, cachedPwsh, guestPwshZip, token);
                await backend.EnsureGuestDirectory(state, secrets, guestPwshDir, token);
                await backend.GuestRun(state, secrets, @"C:\Windows\System32\tar.exe", ["-xf", guestPwshZip, "-C", guestPwshDir], token, 180);
            }

            var runId = Guid.NewGuid().ToString("N");
            foreach (var scriptName in GuestConfigureScripts)
            {
                var content = Catalog.EmbeddedText(scriptName);
                var tmp = Paths.Under(o.Private, $"{runId}-{scriptName}");
                await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false), token);
                try
                {
                    await backend.CopyTo(state, secrets, tmp, $@"{guestRoot}\{scriptName}", token);
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
            }

            var yamlLocal = Paths.Under(o.Private, $"{runId}-YamlDotNet.dll");
            await File.WriteAllBytesAsync(yamlLocal, Catalog.EmbeddedBytes("YamlDotNet.dll"), token);
            try
            {
                await backend.CopyTo(state, secrets, yamlLocal, $@"{guestRoot}\YamlDotNet.dll", token);
            }
            finally
            {
                if (File.Exists(yamlLocal)) File.Delete(yamlLocal);
            }

            var appResources = resources.Where(r => r.Id != "pwsh").ToArray();
            foreach (var resource in appResources)
            {
                var cached = Paths.Under(o.Cache, resource.FileName);
                if (File.Exists(cached))
                {
                    await backend.CopyTo(state, secrets, cached, $@"{guestRoot}\{resource.FileName}", token);
                }
            }

            TimeZoneInfo.TryConvertWindowsIdToIanaId(o.TimeZoneId, o.Country, out var ianaTimeZone);
            var coreConfig = BuildMinimalCoreConfig(secrets, o.NetworkSource != "稍后配置");
            var configHash = string.IsNullOrEmpty(coreConfig)
                ? ""
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(coreConfig))).ToLowerInvariant();
            state.CoreConfigSha256 = configHash;
            state.GuestSettingsStamp = ResumePlan.SettingsStamp(o, configHash);
            state.Save();

            var payload = JsonSerializer.Serialize(new
            {
                runId,
                platformId = o.PlatformId,
                workspaceId = o.WorkspaceId,
                sharedClipboardEnabled = o.SharedClipboardEnabled,
                manualAdsPower = o.ManualAdsPower,
                adsPowerApiKey = o.ManualAdsPower ? "" : secrets.AdsPowerApiKey,
                ianaTimeZone,
                vmId = state.VmId,
                country = o.Country,
                timeZoneId = o.TimeZoneId,
                networkEnabled = o.NetworkSource != "稍后配置",
                externalGateway = false,
                transportKind = "nat",
                coreConfig,
                endpointAddresses = resolvedAddresses,
                endpointPort,
                expectedExitIp = secrets.ExpectedExitIp,
                testOutage = o.TestOutage && !state.OutagePassed,
                resumeNetwork = false,
                resumeBrowser = false,
                browserOnly = false,
                resumeReopenPassed = state.BrowserReopenPassed,
                coreConfigSha256 = state.CoreConfigSha256,
                resources = appResources.Select(r => new { id = r.Id, fileName = r.FileName, sha256 = r.Sha256 })
            });

            var localPayload = Paths.Under(o.Private, $"payload-{runId}.json");
            var guestPayload = $@"{guestRoot}\payload-{runId}.json";
            var guestResult = $@"{guestRoot}\guest-result-{runId}.json";
            var localResult = Paths.Under(o.Private, $"guest-result-{runId}.json");
            await File.WriteAllTextAsync(localPayload, payload, new UTF8Encoding(false), token);
            try
            {
                await backend.CopyTo(state, secrets, localPayload, guestPayload, token);
            }
            finally
            {
                if (File.Exists(localPayload)) File.Delete(localPayload);
            }

            progress.Report("6 / 6  在 Windows 来宾内执行 Clash、防火墙与 AdsPower 配置…");
            SetupException? guestRunError = null;
            try
            {
                await backend.GuestRun(state, secrets, guestPwsh, ["-NoLogo", "-NoProfile", "-File", $@"{guestRoot}\Configure.ps1", "-PayloadPath", guestPayload], token, 1500);
            }
            catch (SetupException ex)
            {
                guestRunError = ex;
            }

            try
            {
                await backend.CopyFrom(state, secrets, guestResult, localResult, token);
            }
            catch when (guestRunError is not null)
            {
                throw guestRunError;
            }

            if (!File.Exists(localResult))
                throw guestRunError ?? new SetupException("未能从 Mac 来宾回读部署验收结果。");

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(localResult, token));
            if (File.Exists(localResult)) File.Delete(localResult);
            var rootEl = doc.RootElement;
            state.NetworkPassed = rootEl.TryGetProperty("networkPassed", out var netPassed) && netPassed.GetBoolean();
            state.OutagePassed = rootEl.TryGetProperty("outagePassed", out var outagePassed) && outagePassed.GetBoolean();
            state.ClashConfigured = rootEl.TryGetProperty("clashConfigured", out var clashCfg) && clashCfg.GetBoolean();
            state.BrowserProfileConfigured = rootEl.TryGetProperty("browserProfileConfigured", out var adsCfg) && adsCfg.GetBoolean();
            state.BrowserRuntimePassed = rootEl.TryGetProperty("browserRuntimePassed", out var rtPassed) && rtPassed.GetBoolean();
            state.BrowserReopenPassed = rootEl.TryGetProperty("browserReopenPassed", out var reopenPassed) && reopenPassed.GetBoolean();
            state.TranslationPassed = rootEl.TryGetProperty("translationPassed", out var trPassed) && trPassed.GetBoolean();
            state.Stage = rootEl.TryGetProperty("code", out var codeProp) ? codeProp.GetString() ?? "" : "";
            state.PendingActions.Clear();
            if (rootEl.TryGetProperty("pendingActions", out var pendingEl) && pendingEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pendingEl.EnumerateArray())
                {
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var id = item.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    var steps = item.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array
                        ? s.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                        : [];
                    if (!string.IsNullOrWhiteSpace(title))
                        state.PendingActions.Add(new UserAction(title, steps, id));
                }
            }

            if (o.NetworkSource != "稍后配置")
            {
                state.Protection.ConfigHash = state.CoreConfigSha256;
                state.Protection.Boundary = new(CheckStatus.Passed, DateTime.UtcNow, "macOS pfctl 锚点与主规则集回读通过");
                state.Protection.Exit = new(state.NetworkPassed ? CheckStatus.Passed : CheckStatus.Failed, DateTime.UtcNow,
                    state.NetworkPassed ? "来宾出口与地区校验通过" : $"来宾待处理：{state.Stage}");
                state.Protection.Browser = new(CheckStatus.NotTested, DateTime.UtcNow, "等待在 AdsPower 浏览器内打开检测页确认");
            }
            if (state.NetworkPassed && o.NetworkSource != "稍后配置")
            {
                if (o.ManualAdsPower && state.PendingActions.All(a => a.Id != CustomerGuide.ManualAdsPowerId))
                    state.PendingActions.Add(CustomerGuide.ManualAdsPower());
                else if (!o.ManualAdsPower && state.PendingActions.All(a => a.Id != "acceptance"))
                    state.PendingActions.Add(new UserAction(
                        "在专用浏览器完成验收与登录",
                        ["打开 Claude 专用 AdsPower 环境，访问 https://ip.net.coffee/claude/。", "核对地区、出口 IP、DNS、WebRTC 与时区一致后，回到本助手勾选确认完成。"],
                        "acceptance"));
            }

            var guestStageAccepted = state.NetworkPassed && (state.Stage == "NEEDS_BROWSER_ACCEPTANCE" || string.IsNullOrEmpty(state.Stage));
            state.ApplicationAccepted = false;
            state.SetupFinished = false;
            state.Status = guestStageAccepted ? "需要本人完成浏览器验收" : $"来宾待处理：{state.Stage}";
            if (guestStageAccepted && !state.Completed.Contains("software"))
                state.Completed.Add("software");
            state.Save();

            if (guestStageAccepted)
            {
                JsonFile.Write(Paths.Under(o.Root, "acceptance.json"), new
                {
                    dateUtc = DateTime.UtcNow,
                    vmCreated = true,
                    guestTools = true,
                    softwareInstalled = true,
                    networkPassed = state.NetworkPassed,
                    outagePassed = state.OutagePassed,
                    browserRuntimePassed = state.BrowserRuntimePassed,
                    browserReopenPassed = state.BrowserReopenPassed,
                    translationPassed = state.TranslationPassed,
                    browserAcceptance = "not-tested",
                    claudeLoginAndConversation = "not-tested",
                    status = state.Status
                });
            }

            if (!guestStageAccepted && guestRunError is not null)
                throw new SetupException($"来宾配置暂停（{state.Stage}）：{(state.PendingActions.FirstOrDefault()?.Title ?? guestRunError.Message)}");

            progress.Report(guestStageAccepted
                ? "网络与软件已通过回读；请在 AdsPower 打开 https://ip.net.coffee/claude/ 验收后点击确认完成。"
                : $"来宾返回待处理状态：{state.Stage}");
            return state;
        }
        finally
        {
            if (ownedBackend is not null) await ownedBackend.DisposeAsync();
        }
    }

    /// <summary>Marks final workspace completion only after the user explicitly confirms real AdsPower browser verification (keeps Claude login/conversation not-tested until independently confirmed).</summary>
    public static void ConfirmBrowserAcceptance(SessionState state, bool browserConfirmed)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.NetworkPassed)
            throw new SetupException("来宾网络与出口尚未通过验收，不能标记完成。");
        if (!browserConfirmed)
            throw new SetupException("请先勾选确认已在真实 AdsPower 窗口核对检测页一致。");

        state.Protection.Browser = new(CheckStatus.Passed, DateTime.UtcNow, "用户确认实际 AdsPower 窗口出口");
        state.PendingActions.RemoveAll(a => a.Id is "acceptance" or CustomerGuide.ManualAdsPowerId);
        state.ApplicationAccepted = true;
        state.SetupFinished = true;
        state.Stage = "已完成设置，可进入虚拟机";
        state.Status = "设置已完成";
        state.Save();

        var priorClaudeStatus = ReadAcceptanceField(state.Options.Root, "claudeLoginAndConversation");
        WriteAcceptanceFile(state, browserAcceptance: "user-confirmed", claudeLoginAndConversation: string.IsNullOrWhiteSpace(priorClaudeStatus) ? "not-tested" : priorClaudeStatus);
    }

    /// <summary>Independently records user-confirmed Claude login and first conversation inside the verified AdsPower environment.</summary>
    public static void ConfirmClaudeLoginAndConversation(SessionState state, bool claudeConfirmed)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.SetupFinished || state.Protection.Browser.Status != CheckStatus.Passed)
            throw new SetupException("请先完成 AdsPower 浏览器出口验收，再单独确认 Claude 登录与对话。");
        if (!claudeConfirmed)
            throw new SetupException("请先勾选确认已在同一 AdsPower 窗口完成 Claude 登录与对话。");

        WriteAcceptanceFile(state, browserAcceptance: "user-confirmed", claudeLoginAndConversation: "user-confirmed");
    }

    private static string? ReadAcceptanceField(string root, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var path = Paths.Under(root, "acceptance.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(fieldName, out var prop) ? prop.GetString() : null;
        }
        catch { return null; }
    }

    private static void WriteAcceptanceFile(SessionState state, string browserAcceptance, string claudeLoginAndConversation)
    {
        if (string.IsNullOrWhiteSpace(state.Options.Root) || !Directory.Exists(state.Options.Root)) return;
        JsonFile.Write(Paths.Under(state.Options.Root, "acceptance.json"), new
        {
            dateUtc = DateTime.UtcNow,
            vmCreated = true,
            guestTools = true,
            softwareInstalled = true,
            networkPassed = state.NetworkPassed,
            outagePassed = state.OutagePassed,
            browserRuntimePassed = state.BrowserRuntimePassed,
            browserReopenPassed = state.BrowserReopenPassed,
            translationPassed = state.TranslationPassed,
            browserAcceptance,
            claudeLoginAndConversation,
            status = state.Status
        });
    }

    /// <summary>Computes lowercase hex SHA-256 of a local ISO file without loading it into memory.</summary>
    public static async Task<string> ComputeFileSha256Async(string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new SetupException("未找到指定的 Windows ISO 镜像文件。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Verifies that the ISO on disk still matches the expected SHA-256 digest immediately before provisioning or boot.</summary>
    public static async Task RequireVerifiedIsoSha256Async(SetupOptions options, string stagePrefix, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(options.IsoSha256)) return;
        var actualIsoSha = await ComputeFileSha256Async(options.IsoPath, token);
        if (!actualIsoSha.Equals(options.IsoSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new SetupException($"{stagePrefix} Windows ISO 文件内容已发生变化（SHA-256 不匹配），已终止当前安装阶段，请重新校验架构并确认当前镜像的授权许可。");
    }

    /// <summary>Opens the verified ISO read-only (FileShare.Read) and re-checks its SHA-256 before VM config emission.</summary>
    internal static FileStream OpenAndVerifyIsoLock(string isoPath, string? expectedSha256, string stagePrefix)
    {
        if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath))
            throw new SetupException("未找到指定的 Windows ISO 镜像文件。");
        var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: false);
        try
        {
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actualSha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!actualSha.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new SetupException($"{stagePrefix} Windows ISO 文件内容已发生变化（SHA-256 不匹配），已终止生成虚拟机安装配置，请重新校验架构并确认当前镜像的授权许可。");
                stream.Position = 0;
            }
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Binds ISO architecture verification and Windows license confirmation strictly to the ISO's SHA-256 content identity.</summary>
    public sealed class MacIsoLicenseGuard
    {
        public string SelectedIsoPath { get; private set; } = "";
        public string VerifiedIsoPath { get; private set; } = "";
        public string VerifiedIsoArch { get; private set; } = "";
        public string VerifiedIsoSha256 { get; private set; } = "";
        public bool AcceptWindowsLicense { get; private set; }
        public string LicensedIsoPath { get; private set; } = "";
        public string LicensedIsoSha256 { get; private set; } = "";
        public bool AcceptChanges { get; private set; }

        public bool IsIsoVerifiedForCurrent =>
            !string.IsNullOrWhiteSpace(SelectedIsoPath) &&
            !string.IsNullOrWhiteSpace(VerifiedIsoSha256) &&
            string.Equals(SelectedIsoPath, VerifiedIsoPath, StringComparison.OrdinalIgnoreCase);

        public bool IsLicenseConfirmedForCurrent =>
            AcceptWindowsLicense &&
            IsIsoVerifiedForCurrent &&
            !string.IsNullOrWhiteSpace(LicensedIsoSha256) &&
            string.Equals(VerifiedIsoSha256, LicensedIsoSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(SelectedIsoPath, LicensedIsoPath, StringComparison.OrdinalIgnoreCase);

        public void SelectIso(string isoPath)
        {
            var normalized = (isoPath ?? "").Trim();
            if (!string.Equals(normalized, SelectedIsoPath, StringComparison.OrdinalIgnoreCase))
                ClearVerificationAndLicense();
            SelectedIsoPath = normalized;
        }

        public void LoadTask(SetupOptions options)
        {
            SelectedIsoPath = (options.IsoPath ?? "").Trim();
            ClearVerificationAndLicense();
            AcceptChanges = options.AcceptChanges;
        }

        public void MarkIsoVerified(string isoPath, string arch, string isoSha256)
        {
            var normalized = (isoPath ?? "").Trim();
            var normalizedSha = (isoSha256 ?? "").Trim().ToLowerInvariant();
            if (normalizedSha.Length != 64)
                throw new SetupException("Windows ISO 校验指纹无效。");
            if (!string.Equals(normalized, SelectedIsoPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(normalizedSha, LicensedIsoSha256, StringComparison.OrdinalIgnoreCase))
            {
                AcceptWindowsLicense = false;
                LicensedIsoPath = "";
                LicensedIsoSha256 = "";
            }
            SelectedIsoPath = normalized;
            VerifiedIsoPath = normalized;
            VerifiedIsoArch = arch;
            VerifiedIsoSha256 = normalizedSha;
        }

        public void ClearVerificationAndLicense()
        {
            VerifiedIsoPath = "";
            VerifiedIsoArch = "";
            VerifiedIsoSha256 = "";
            AcceptWindowsLicense = false;
            LicensedIsoPath = "";
            LicensedIsoSha256 = "";
        }

        public void SetLicenseAccepted(bool accepted)
        {
            if (!accepted || !IsIsoVerifiedForCurrent)
            {
                AcceptWindowsLicense = false;
                LicensedIsoPath = "";
                LicensedIsoSha256 = "";
                return;
            }
            AcceptWindowsLicense = true;
            LicensedIsoPath = SelectedIsoPath;
            LicensedIsoSha256 = VerifiedIsoSha256;
        }

        public void SetChangesAccepted(bool accepted) => AcceptChanges = accepted;

        public void ValidateForDeploy(bool installWindows, string currentIsoSha256)
        {
            if (!AcceptChanges)
                throw new SetupException("请先到「安装环境」勾选同意程序在独立目录配置双层隔离环境。");
            if (!installWindows) return;
            if (!IsIsoVerifiedForCurrent)
                throw new SetupException("当前 Windows ISO 尚未通过架构校验（或已更换镜像），请先到「安装环境」点击「校验 ISO 架构」。");
            var actualSha = (currentIsoSha256 ?? "").Trim().ToLowerInvariant();
            if (!string.Equals(actualSha, VerifiedIsoSha256, StringComparison.OrdinalIgnoreCase))
            {
                ClearVerificationAndLicense();
                throw new SetupException("所选 Windows ISO 文件内容已发生变化（SHA-256 改变），已重置校验与许可，请重新校验架构并确认当前镜像的授权许可。");
            }
            if (!IsLicenseConfirmedForCurrent)
                throw new SetupException("请先为当前已校验的 Windows 11 安装镜像确认并勾选持有合法有效的微软授权许可。");
        }
    }

    private static ProxyNode ParseNodeJson(string json) =>
        NetworkImport.Parse("{\"proxies\":[" + json + "]}").Nodes.FirstOrDefault()
        ?? throw new SetupException("所选节点配置无效。");

    private static ProxyNode? ParseOptionalNodeJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : NetworkImport.Parse("{\"proxies\":[" + json + "]}").Nodes.FirstOrDefault();

    private static string[] ResolveHostAddresses(string host, bool skipDns, CancellationToken token)
    {
        if (System.Net.IPAddress.TryParse(host, out _)) return IsolationPolicy.ValidateEndpoints([host]);
        if (skipDns) return ["1.1.1.1"];
        return NodeEndpointResolver.Resolve(host, token).GetAwaiter().GetResult();
    }

    private static string BuildMinimalCoreConfig(Secrets secrets, bool networkEnabled)
    {
        if (!networkEnabled || string.IsNullOrWhiteSpace(secrets.SelectedNodeJson)) return "";
        var selected = ParseNodeJson(secrets.SelectedNodeJson);
        var upstream = selected.Upstream ?? ParseOptionalNodeJson(secrets.UpstreamNodeJson);
        // On Mac NAT (externalGateway = false), keep Clash DNS bootstrap on https://1.1.1.1/dns-query instead of 198.19.250.1.
        return NetworkImport.CoreConfig(selected, endpointAddresses: null, physicalInterface: null, upstream: upstream);
    }
}
