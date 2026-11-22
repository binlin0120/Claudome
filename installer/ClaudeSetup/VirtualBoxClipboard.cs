using System.IO;
using System.Text.Json;

namespace ClaudeSetup;

internal static class VirtualBoxClipboard
{
    private sealed record Permission(string VmId, string WorkspaceId, bool Enabled, bool RetryAutomatically = false);
    private static string RecordPath(SessionState state) => Paths.Under(state.Options.Private, "virtualbox-clipboard.json");

    internal static bool IsAllowed(SessionState state) => ReadPermission(state, defaultEnabled: false);
    internal static bool WantsSharedClipboard(SessionState state) => ReadPermission(state, defaultEnabled: true);

    private static bool ReadPermission(SessionState state, bool defaultEnabled)
    {
        if (state.Options.PlatformId != "virtualbox" || !Guid.TryParseExact(state.VmId, "D", out _)) return false;
        try
        {
            var path = RecordPath(state);
            if (!File.Exists(path)) return defaultEnabled;
            var value = JsonFile.Read<Permission>(path);
            return value.VmId == state.VmId && value.WorkspaceId == state.Options.WorkspaceId &&
                (value.Enabled || (defaultEnabled && value.RetryAutomatically));
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or SetupException)
        {
            return false;
        }
    }

    internal static async Task Change(VirtualBox box, SessionState state, bool enabled, CancellationToken token) =>
        _ = await Change(box, state, enabled, false, token);

    internal static Task<bool> RestoreDefault(VirtualBox box, SessionState state, CancellationToken token) =>
        Change(box, state, true, true, token);

    private static async Task<bool> Change(VirtualBox box, SessionState state, bool enabled, bool automatic, CancellationToken token)
    {
        if (state.Options.PlatformId != "virtualbox" || !Guid.TryParseExact(state.VmId, "D", out _))
            throw new SetupException("请先载入本任务的 VirtualBox 虚拟机。");
        using var workspace = WorkspaceLock.Acquire(state);
        if (automatic && !WantsSharedClipboard(state)) return false;
        await box.EnsureMachine(state, token);
        var info = await ReadInfo(box, state, token);
        EnsureRunning(info);
        var path = RecordPath(state);
        if (!enabled)
        {
            await Apply(box, state, info, false, token);
            JsonFile.Write(path, new Permission(state.VmId, state.Options.WorkspaceId, false));
            return false;
        }

        if (automatic && VirtualBoxDisplayFit.ReadAdditionsRunLevel(info) < 2) return false;
        RequireAdditions(info);
        VirtualBoxBoundary.CheckSharing(info, allowClipboard: true);
        if (!IsAllowed(state)) JsonFile.Write(path, new Permission(state.VmId, state.Options.WorkspaceId, true));
        try { await Apply(box, state, info, true, token); }
        catch (Exception error) when (error is SetupException or IOException or OperationCanceledException)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await Apply(box, state, await ReadInfo(box, state, cleanup.Token), false, cleanup.Token);
                JsonFile.Write(path, new Permission(state.VmId, state.Options.WorkspaceId, false, automatic));
            }
            catch (Exception rollback) when (rollback is SetupException or IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                throw new SetupException("未能确认双向剪贴板的当前状态。虚拟机运行时可在左侧点“关闭双向剪贴板”并重新确认。");
            }
            throw;
        }
        return true;
    }

    private static Task<string> ReadInfo(VirtualBox box, SessionState state, CancellationToken token) =>
        box.Run(token, "showvminfo", state.VmId, "--machinereadable");

    private static void EnsureRunning(string info)
    {
        if (VmInventory.VBoxValue(info, "VMState") != "running")
            throw new SetupException("请先连接正在运行的虚拟机，再调整双向剪贴板。");
    }

    private static void RequireAdditions(string info)
    {
        if (VirtualBoxDisplayFit.ReadAdditionsRunLevel(info) < 2)
            throw new SetupException("请先在来宾 Windows 里完成 VirtualBox Guest Additions 安装。");
    }

    private static async Task Apply(VirtualBox box, SessionState state, string info, bool enabled, CancellationToken token)
    {
        var value = enabled ? "bidirectional" : "disabled";
        if (VmInventory.VBoxValue(info, "clipboard") != value)
        {
            await box.Run(token, "controlvm", state.VmId, "clipboard", "mode", value);
            info = await ReadInfo(box, state, token);
        }
        if (VmInventory.VBoxValue(info, "clipboard") != value)
            throw new SetupException("VirtualBox 双向剪贴板状态未通过回读。");
        if (enabled) VirtualBoxBoundary.CheckSharing(info, allowClipboard: true);
    }
}
