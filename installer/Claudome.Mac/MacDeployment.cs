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
        o.Validate(requireIso: installWindows);
        HostPlatform.Environment.RestrictPrivateDirectory(o.Private);

        progress.Report("1 / 6  核验或下载 Mac 来宾所需组件…");
        var resources = verifiedResourcesOverride
            ?? await DownloadGuestAssetsAsync(o, hostFacts.IsAppleSilicon, progress, null, token);

        var firewall = firewallOverride ?? new MacGatewayFirewall();
        IVmBackend? ownedBackend = null;
        var backend = backendOverride ?? (o.PlatformId == "utm" ? ownedBackend = new UtmBackend() : ownedBackend = new FusionBackend());

        try
        {
            if (o.NetworkSource != "稍后配置")
            {
                var selectedNode = ParseNodeJson(secrets.SelectedNodeJson);
                var upstreamNode = ParseOptionalNodeJson(secrets.UpstreamNodeJson);
                var targetHost = upstreamNode?.Server ?? selectedNode.Server;
                var targetPort = upstreamNode?.Port ?? selectedNode.Port;
                string[] addresses;
                if (skipEndpointDnsResolveForTest && System.Net.IPAddress.TryParse(targetHost, out _))
                {
                    addresses = IsolationPolicy.ValidateEndpoints([targetHost]);
                }
                else
                {
                    addresses = await NodeEndpointResolver.Resolve(targetHost, token);
                }
                progress.Report("2 / 6  准备 macOS pfctl 防火墙隔离锚点…");
                await firewall.ApplyAsync(o.WorkspaceId, DefaultMacBridgeInterface, addresses, targetPort, DefaultGatewayPort, token);
            }

            progress.Report($"3 / 6  准备 {(o.PlatformId == "utm" ? "UTM" : "VMware Fusion")} 虚拟机环境…");
            await backend.Prepare(o, token);
            await backend.EnsureMachine(state, token);
            if (!state.Completed.Contains("vm-created"))
            {
                await backend.Configure(state, token);
                state.Completed.Add("vm-created");
                state.Save();
            }

            if (installWindows)
            {
                progress.Report("4 / 6  生成无人值守引导介质并启动 Windows 安装…");
                await backend.InstallWindows(state, secrets, token);
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

            progress.Report("5 / 6  向来宾传送安全安装脚本与组件包…");
            var guestRoot = o.GuestSetupDirectory;
            var guestPwsh = guestRoot + @"\pwsh\pwsh.exe";
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

            foreach (var resource in resources)
            {
                var cached = Paths.Under(o.Cache, resource.FileName);
                if (File.Exists(cached))
                {
                    await backend.CopyTo(state, secrets, cached, $@"{guestRoot}\{resource.FileName}", token);
                }
            }

            TimeZoneInfo.TryConvertWindowsIdToIanaId(o.TimeZoneId, o.Country, out var ianaTimeZone);
            var resolvedAddresses = o.NetworkSource != "稍后配置" ? ResolveCachedAddresses(secrets, skipEndpointDnsResolveForTest) : [];
            var coreConfig = BuildMinimalCoreConfig(secrets, o.NetworkSource != "稍后配置", resolvedAddresses);
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
                endpointPort = 0,
                expectedExitIp = secrets.ExpectedExitIp,
                testOutage = o.TestOutage && !state.OutagePassed,
                resumeNetwork = false,
                resumeBrowser = false,
                browserOnly = false,
                resumeReopenPassed = state.BrowserReopenPassed,
                coreConfigSha256 = state.CoreConfigSha256,
                resources = resources.Select(r => new { id = r.Id, fileName = r.FileName, sha256 = r.Sha256 })
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
            await backend.GuestRun(state, secrets, guestPwsh, ["-NoLogo", "-NoProfile", "-File", $@"{guestRoot}\Configure.ps1", "-PayloadPath", guestPayload], token, 1500);
            await backend.CopyFrom(state, secrets, guestResult, localResult, token);

            if (!File.Exists(localResult))
                throw new SetupException("未能从 Mac 来宾回读部署验收结果。");

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(localResult, token));
            var rootEl = doc.RootElement;
            state.NetworkPassed = rootEl.TryGetProperty("networkPassed", out var netPassed) && netPassed.GetBoolean();
            state.OutagePassed = rootEl.TryGetProperty("outagePassed", out var outagePassed) && outagePassed.GetBoolean();
            state.SetupFinished = state.NetworkPassed;
            state.Status = state.NetworkPassed ? "Mac 来宾环境已完成部署与验收" : "来宾尚有待处理项";
            state.Stage = rootEl.TryGetProperty("code", out var codeProp) ? codeProp.GetString() ?? "" : "";
            state.Save();
            progress.Report(state.NetworkPassed ? "全部 Mac 部署与来宾配置已通过回读。" : $"来宾返回待处理状态：{state.Stage}");
            return state;
        }
        finally
        {
            if (ownedBackend is not null) await ownedBackend.DisposeAsync();
        }
    }

    private static ProxyNode ParseNodeJson(string json) =>
        NetworkImport.Parse("{\"proxies\":[" + json + "]}").Nodes.FirstOrDefault()
        ?? throw new SetupException("所选节点配置无效。");

    private static ProxyNode? ParseOptionalNodeJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : NetworkImport.Parse("{\"proxies\":[" + json + "]}").Nodes.FirstOrDefault();

    private static string[] ResolveCachedAddresses(Secrets secrets, bool skipDns)
    {
        if (string.IsNullOrWhiteSpace(secrets.SelectedNodeJson)) return [];
        var selected = ParseNodeJson(secrets.SelectedNodeJson);
        var upstream = ParseOptionalNodeJson(secrets.UpstreamNodeJson);
        var host = upstream?.Server ?? selected.Server;
        if (System.Net.IPAddress.TryParse(host, out _)) return IsolationPolicy.ValidateEndpoints([host]);
        if (skipDns) return [];
        return NodeEndpointResolver.Resolve(host, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string BuildMinimalCoreConfig(Secrets secrets, bool networkEnabled, string[] endpointAddresses)
    {
        if (!networkEnabled || string.IsNullOrWhiteSpace(secrets.SelectedNodeJson)) return "";
        return NetworkImport.CoreConfig(
            ParseNodeJson(secrets.SelectedNodeJson),
            endpointAddresses,
            physicalInterface: null,
            upstream: ParseOptionalNodeJson(secrets.UpstreamNodeJson));
    }
}
