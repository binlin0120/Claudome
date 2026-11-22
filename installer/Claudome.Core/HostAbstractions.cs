using System.Runtime.InteropServices;

namespace ClaudeSetup;

public sealed record PlatformFacts(string System, string Architecture, string Edition, bool VirtualizationReady, bool VirtualizationKnown, long MemoryGb, bool VBoxInstalled, bool WorkstationInstalled, bool ParallelsInstalled = false, int Build = 22000);
public sealed record PlatformChoice(string Id, string Name, int Rank, bool Compatible, bool Automatic, string Reason, string[] Instructions)
{
    public string Label => $"{Rank}. {Name} · {(Automatic && Compatible ? "自动部署" : Compatible ? "操作指引" : "条件未满足")}";
}

/// <summary>Common virtual-machine backend lifecycle and guest execution contract.</summary>
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

/// <summary>Platform-specific secret persistence contract (DPAPI on Windows, Keychain + AES-GCM on macOS).</summary>
public interface ISecretStore
{
    void Save(string directory, Secrets secrets, string revision = "");
    Secrets? Load(string directory, string revision = "");
    void SaveMerged(string directory, Secrets incoming, bool preserveHiddenNetwork, bool preserveUnloadedSecrets = false, string revision = "");
    void Forget(string directory, string revision = "");
    void PruneRevisions(SessionState committed, SessionState previous);
}

/// <summary>Host-side firewall boundary contract (Windows Defender on Windows, pfctl anchor on macOS).</summary>
public interface IHostFirewall
{
    Task ApplyAsync(string workspaceId, string guestInterface, string[] endpointIps, int endpointPort, int gatewayPort, CancellationToken token);
    Task VerifyAsync(string workspaceId, string[] endpointIps, int endpointPort, int gatewayPort, CancellationToken token);
    Task ClearAsync(string workspaceId, CancellationToken token);
}

/// <summary>Platform-specific host queries and filesystem isolation hooks used by Claudome.Core.</summary>
public interface IHostEnvironment
{
    bool IsHostHypervisorActive { get; }
    string HostEdition { get; }
    bool IsHomeEdition(string? edition);
    void RestrictPrivateDirectory(string path);
    void RequireWindowsIsoFile(string isoPath);
}

/// <summary>Fallback cross-platform host environment for POSIX platforms (macOS/Linux) and standalone Core tests.</summary>
internal sealed class DefaultHostEnvironment : IHostEnvironment
{
    public bool IsHostHypervisorActive => false;
    public string HostEdition => RuntimeInformation.OSDescription;
    public bool IsHomeEdition(string? edition) => edition is "Core" or "CoreCountrySpecific" or "CoreSingleLanguage";

    public void RestrictPrivateDirectory(string path)
    {
        Paths.RejectLinks(path);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void RequireWindowsIsoFile(string isoPath)
    {
        if (!File.Exists(isoPath))
            throw new SetupException("请选择已获得授权的 Windows 11 ISO，或先完成镜像下载。");
        if (new FileInfo(isoPath).Length < 1024L * 1024 * 1024 || !isoPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new SetupException("所选文件不是预期大小的 ISO 镜像。");
    }
}

/// <summary>Registers the active platform host environment implementation.</summary>
public static class HostPlatform
{
    public static IHostEnvironment Environment { get; set; } = new DefaultHostEnvironment();
}
