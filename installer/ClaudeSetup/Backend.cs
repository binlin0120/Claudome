namespace ClaudeSetup;

public interface IVmBackend : IAsyncDisposable
{
    Task Prepare(SetupOptions options, CancellationToken token);
    Task<string> EnsureMachine(SessionState state, CancellationToken token);
    Task Configure(SessionState state, CancellationToken token);
    Task InstallWindows(SessionState state, Secrets secrets, CancellationToken token);
    Task<bool> IsRunning(SessionState state, CancellationToken token);
    Task<bool> GuestReady(SessionState state, Secrets secrets, CancellationToken token);
    Task CleanupWindowsMedia(SessionState state, CancellationToken token);
    Task<string> GuestRun(SessionState state, Secrets secrets, string executable, IEnumerable<string> args, CancellationToken token, int seconds = 300);
    async Task EnsureGuestDirectory(SessionState state, Secrets secrets, string directory, CancellationToken token) =>
        _ = await GuestRun(state, secrets, @"C:\Windows\System32\cmd.exe",
            ["/d", "/c", "if not exist \"" + directory + "\" mkdir \"" + directory + "\""], token);
    Task<bool> GuestFileExists(SessionState state, Secrets secrets, string file, CancellationToken token);
    Task CopyTo(SessionState state, Secrets secrets, string local, string destination, CancellationToken token);
    Task CopyFrom(SessionState state, Secrets secrets, string guest, string destination, CancellationToken token);
    Task Open(SessionState state, CancellationToken token);
}
public static class Backends
{
    public static IVmBackend Create(string platform, IProgress<InstallationProgress>? progress = null) => platform switch
    {
        "virtualbox" => new VirtualBox(new CommandRunner()),
        "hyperv" => new HyperV(progress),
        "workstation" => new Workstation(),
        _ => throw new SetupException("此平台的自动化适配尚未完成，已停止；不会替换所选虚拟机。")
    };
    internal static Task ShutdownOriginal(IVmBackend backend, SessionState state, CancellationToken token)
    {
        if (!Guid.TryParseExact(state.VmId, "D", out _))
            throw new SetupException("请先载入本任务的虚拟机。");
        return backend switch
        {
            VirtualBox box => box.ShutdownOriginal(state, token),
            HyperV hyperV => hyperV.ShutdownOriginal(state, token),
            Workstation workstation => workstation.ShutdownOriginal(state, token),
            _ => throw new SetupException("当前平台尚未提供来宾关机操作。")
        };
    }

    public static async Task Open(SessionState state, CancellationToken token)
    {
        IsolationPolicy.RequireSupportedPlatform(state);
        if (!Guid.TryParseExact(state.VmId, "D", out _)) throw new SetupException("请先载入本任务的虚拟机。");
        if (state.Options.NetworkSource != "稍后配置") {
            await BoundaryRuntime.VerifyCurrent(state, token);
            var active = BoundaryRuntime.BackendFor(state) ?? throw new SetupException("管理连接已结束，请继续原任务重新连接。");
            await active.EnsureMachine(state, token); await active.Open(state, token); return;
        }
        await using var backend = Create(state.Options.PlatformId);
        await backend.EnsureMachine(state, token);
        if (state.Options.NetworkSource == "稍后配置") {
            await BoundaryRuntime.Replace(null, "");
            if (backend is VirtualBox box) await VirtualBoxBoundary.Disconnect(box, state, token);
            else await BusBoundary.VerifyOffline(backend,state,token);
        } else await BoundaryRuntime.VerifyCurrent(state, token);
        await backend.Open(state, token);
    }
}
