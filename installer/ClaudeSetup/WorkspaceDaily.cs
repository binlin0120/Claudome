using System.IO;
using System.Text.Json;
using System.Windows;

namespace ClaudeSetup;

internal sealed class GuestCheckPendingException(string message) : SetupException(message);

public sealed partial class SetupWindow
{
    private async Task EnterOriginalWorkspace(bool networkOnly = false)
    {
        if (Machine.HyperVStandaloneIneligibilityReason() is { } ineligibility)
        {
            Notice(ineligibility);
            return;
        }
        if (enteringWorkspace || busy || recoveringDisplay) return;
        var hasNetwork = Deployment.HasConfiguredWorkspaceNetwork(state);
        if (networkOnly && !hasNetwork)
        {
            Notice(state.Completed.Contains("software") ? "此任务尚无已应用的网络配置。请到「网络节点」选择线路并应用。"
                : "来宾配置尚未完成。请先选「继续原任务并连接」。");
            return;
        }
        if (string.IsNullOrEmpty(state.VmId)) return;
        if (!CanEnterDesktopWorkspace())
        {
            Notice("原虚拟机尚未写入或未核验到可启动的 Windows 系统盘，请先前往「安装向导 → 6 开始安装」点击「开始 / 继续部署」。");
            Go(5);
            return;
        }
        if (state.Options.PlatformId == "hyperv" && !state.Completed.Contains("guest-ready"))
        {
            var probeOk = false;
            try
            {
                await Perform(async (_, token) =>
                {
                    await using var hv = new HyperV();
                    probeOk = await Deployment.VerifyHyperVDiskBeforeDesktopEntryAsync(
                        state,
                        () => hv.ProbeWindowsInstallState(state, token));
                }, useSavedOptions: true);
            }
            catch (OperationCanceledException) { return; }
            if (!probeOk)
            {
                Notice("检测到原虚拟机系统盘尚未写入或尚待恢复核验，已改为引导至「6 开始安装」继续。");
                Go(5);
                return;
            }
        }
        enteringWorkspace = true;
        try
        {
            Navigate(7);
            desktopConnectionPolicy.ResumeExplicitly();
            autoConnectDesktop = true;
            if (!hasNetwork)
            {
                await ConnectDesktop(requested: true);
                var engine = Engine();
                await Perform(async (_, token) =>
                {
                    if (BoundaryRuntime.BackendFor(state) is not null) return;
                    await engine.OpenOriginalOffline(state, token);
                }, useSavedOptions: true);
            }
            else
            {
                await Perform(async (_, token) =>
                {
                    using var taskLock = WorkspaceLock.Acquire(state);
                    RequireCurrentCheckpoint(state, JsonFile.Read<SessionState>(state.Options.StatePath));
                    var login = AppliedLogin();
                    try
                    {
                        if (BoundaryRuntime.BackendFor(state) is not null)
                        {
                            await BoundaryRuntime.VerifyCurrent(state, token);
                            var active = BoundaryRuntime.BackendFor(state)!;
                            await Engine().StartOriginalDesktop(state, active, token);
                            var opened = await Engine().ReadDailyGuest(active, state, login, token, networkOnly);
                            RecordTaskActivity(networkOnly ? "受控网络已连接，来宾出口已核对。" : DailyReadyMessage(opened));
                            return;
                        }
                        var browserOpened = await Engine().ReconnectConfiguredWorkspace(state, login, token, networkOnly);
                        RecordTaskActivity(networkOnly ? "受控网络已连接，来宾出口已核对。" : DailyReadyMessage(browserOpened));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (GuestCheckPendingException error) when (BoundaryRuntime.BackendFor(state) is not null)
                    {
                        state.NetworkPassed = false;
                        RecordTaskActivity("来宾出口未能验证，保留当前外层限制。 " + Redaction.SafeError(error));
                    }
                    catch (Exception error)
                    {
                        state.NetworkPassed = false;
                        await BoundaryRuntime.StopFor(state);
                        RecordTaskActivity("网络待恢复 · " + Redaction.SafeError(error));
                        if (!networkOnly) await Engine().OpenOriginalOffline(state, token);
                    }
                    finally { Deployment.WriteProtectionReport(state); }
                }, useSavedOptions: true);
            }
            if (section == 7 && !closeAfterPause)
            {
                desktopConnectionPolicy.ResumeExplicitly();
                autoConnectDesktop = true;
                await ConnectDesktop(requested: true);
            }
        }
        finally { enteringWorkspace = false; }
    }

    private string DailyReadyMessage(bool browserOpened) =>
        "受控网络就绪，来宾出口已核对。 " +
        (state.Options.ManualAdsPower ? "请在来宾打开原 AdsPower 环境。" :
            browserOpened ? "原 AdsPower 环境已打开。" : "请在来宾打开原 AdsPower 环境，或检查 API 权限。");
}

public sealed partial class Deployment
{
    internal static bool HasConfiguredWorkspaceNetwork(SessionState state) =>
        Guid.TryParseExact(state.VmId, "D", out _) && state.Completed.Contains("software") &&
        state.Options.NetworkSource != "稍后配置" && state.CoreConfigSha256.Length == 64;

    internal async Task StartOriginalDesktop(SessionState state, IVmBackend backend, CancellationToken token)
    {
        await backend.EnsureMachine(state, token);
        if (backend is VirtualBox box) await box.EnsureRunning(state, token);
        else if (!await backend.IsRunning(state, token)) await backend.Open(state, token);
    }

    internal async Task OpenOriginalOffline(SessionState state, CancellationToken token)
    {
        IsolationPolicy.RequireSupportedPlatform(state);
        if (!Guid.TryParseExact(state.VmId, "D", out _)) throw new SetupException("未找到原任务虚拟机。");
        await using var backend = Backends.Create(state.Options.PlatformId);
        await backend.EnsureMachine(state, token);
        if (backend is VirtualBox box)
        {
            if (state.Options.NetworkSource == "稍后配置") await VirtualBoxBoundary.Disconnect(box, state, token);
            else
            {
                var binding = ExternalGateway.Binding(state);
                await VirtualBoxBoundary.Verify(box, state, binding, token);
            }
        }
        else await BusBoundary.VerifyOffline(backend, state, token);
        await StartOriginalDesktop(state, backend, token);
    }

    internal async Task<bool> ReadDailyGuest(IVmBackend backend, SessionState state, Secrets secrets, CancellationToken token, bool networkOnly)
    {
        if (!state.Options.HasUsableGuestPassword(secrets.VmPassword))
            throw new SetupException("请填写原虚拟机的登录资料。");
        var openBrowser = !networkOnly && state.SetupFinished && state.BrowserProfileConfigured && !state.Options.ManualAdsPower;
        using var report = state.Options.PlatformId == "virtualbox" && !openBrowser
            ? await ReadQuickNetwork(backend, state, secrets, token)
            : await ReadValidation(backend, state, secrets, false, token, guestOnly: true, quick: true, openBrowser: openBrowser, elevate: networkOnly);
        var result = report.RootElement;
        var boot = result.GetProperty("bootUtc").GetString() ?? "";
        if (state.Protection.ConfigHash != state.CoreConfigSha256 || (boot.Length > 0 && state.Protection.BootUtc != boot))
        {
            state.Protection = new() { ConfigHash = state.CoreConfigSha256, BootUtc = boot };
            state.OutagePassed = state.RestartPassed = false;
        }
        if (!result.GetProperty("networkPassed").GetBoolean() || !result.GetProperty("clashRuntimePassed").GetBoolean())
        {
            state.Protection.Exit = ProtectionEvidence.GuestExit(result, DateTime.UtcNow);
            if (ProtectionEvidence.IsExitMismatch(result.GetProperty("exitCode").GetString()) || result.GetProperty("runtimeCode").GetString() == "MISMATCH")
                throw new SetupException("确认当前来宾保护或实际出口与已应用配置不符。");
            throw new GuestCheckPendingException("未得到有效的来宾保护或出口结果，可在检测与修复里重试。");
        }
        await BoundaryRuntime.VerifyCurrent(state, token);
        state.Protection.Exit = ProtectionEvidence.GuestExit(result, DateTime.UtcNow);
        state.NetworkPassed = true;
        return result.GetProperty("browserOpened").GetBoolean();
    }

    internal async Task<bool> ReconnectConfiguredWorkspace(SessionState state, Secrets secrets, CancellationToken token, bool networkOnly)
    {
        IsolationPolicy.RequireSupportedPlatform(state);
        if (!HasConfiguredWorkspaceNetwork(state) || string.IsNullOrEmpty(secrets.SelectedNodeJson) ||
            !state.Options.HasUsableGuestPassword(secrets.VmPassword))
            throw new SetupException("请载入已保存的线路和来宾登录资料。");
        state.NetworkPassed = false;
        await BoundaryRuntime.StopFor(state);
        var node = NetworkImport.Parse("{\"proxies\":[" + secrets.SelectedNodeJson + "]}").Nodes.Single();
        var upstream = string.IsNullOrEmpty(secrets.UpstreamNodeJson) ? null :
            NetworkImport.Parse("{\"proxies\":[" + secrets.UpstreamNodeJson + "]}").Nodes.Single();
        if (state.Options.ChainEnabled != (upstream is not null))
            throw new SetupException("保存的链式线路资料不完整。");
        var first = upstream ?? node;
        var resolutionStrategy = ProductProfile.IsHyperVStandalone ? EndpointResolutionStrategy.HyperVStandalone : EndpointResolutionStrategy.Default;
        var (addresses, finalAddresses, _) = await ResolveOrBindEndpointsAsync(
            first, node, upstream is not null, resolutionStrategy, pinnedEndpoints: null, token,
            options: state.Options, upstreamNode: upstream,
            preferredCoreConfigSha256: state.CoreConfigSha256,
            physicalInterfaceForConfigHash: state.Options.PlatformId == "virtualbox" ? null : "ClaudomeBridge");
        var config = NetworkImport.CoreConfig(node, addresses,
            state.Options.PlatformId == "virtualbox" ? null : "ClaudomeBridge", upstream, finalAddresses);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(config)));
        if (!hash.Equals(state.CoreConfigSha256, StringComparison.OrdinalIgnoreCase))
            throw new SetupException("线路端点或来宾配置已变化，请在网络节点页重新应用已保存的线路。");
        var backend = Backends.Create(state.Options.PlatformId);
        var retained = false;
        BusPacketRelay? relay = null;
        ExternalGateway? gateway = null;
        try
        {
            await backend.EnsureMachine(state, token);
            var binding = ExternalGateway.Binding(state);
            if (backend is VirtualBox vbox)
                gateway = await VirtualBoxBoundary.Connect(vbox, state, binding, addresses, first.Port, first.Server, token);
            else
            {
                var spec = BusBoundary.Configuration(state, binding);
                await BusBoundary.Verify(backend, state, spec, token);
                relay = new BusPacketRelay(spec, binding);
                var channel = relay;
                gateway = await ExternalGateway.Start(state, binding, addresses, first.Port, first.Server, async ct =>
                {
                    if (!channel.IsAlive) throw new SetupException("来宾总线通道已停止。");
                    await BusBoundary.Verify(backend, state, spec, ct);
                }, token);
            }
            await BoundaryRuntime.Replace(gateway, state.VmId, relay, finalAddresses);
            gateway = null;
            relay = null;
            await StartOriginalDesktop(state, backend, token);
            var ready = false;
            for (var attempt = 0; attempt < 12 && !ready; attempt++)
            {
                token.ThrowIfCancellationRequested();
                ready = await backend.GuestReady(state, secrets, token);
                if (!ready) await Task.Delay(1000, token);
            }
            if (!ready) throw new SetupException("来宾未登录或工具尚未就绪。进入桌面后点「一键连接网络」。");
            var opened = await ReadDailyGuest(backend, state, secrets, token, networkOnly);
            BoundaryRuntime.RetainBackend(backend, state);
            retained = true;
            return opened;
        }
        catch
        {
            await BoundaryRuntime.StopFor(state);
            throw;
        }
        finally
        {
            if (gateway is not null) await gateway.DisposeAsync();
            if (relay is not null) await relay.DisposeAsync();
            if (!retained) await backend.DisposeAsync();
        }
    }
}
