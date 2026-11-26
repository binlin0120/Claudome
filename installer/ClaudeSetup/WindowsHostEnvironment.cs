using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ClaudeSetup;

internal sealed class WindowsHostEnvironment : IHostEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Catalog.RegisterAssembly(typeof(WindowsHostEnvironment).Assembly);
        HostPlatform.Environment = new WindowsHostEnvironment();
    }

    public bool IsHostHypervisorActive => Machine.IsHostHypervisorActive;
    public string HostEdition => Machine.Read().Edition;
    public bool IsHomeEdition(string? edition) => Machine.IsHomeEdition(edition);
    public void RestrictPrivateDirectory(string path) => SecretStore.RestrictDirectory(path);
    public void RequireWindowsIsoFile(string isoPath) => WindowsMedia.RequireIsoFile(isoPath);

    internal static async Task<int> ElevatedInstaller(
        string path,
        IEnumerable<string> args,
        CancellationToken token = default,
        Action? onWaitingWhileRunning = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new SetupException("未启动安装器。");
        using var registration = token.CanBeCanceled && onWaitingWhileRunning is not null
            ? token.Register(onWaitingWhileRunning)
            : default;
        // Do not kill Windows Installer, DISM mount, or driver installation on a UI cancellation.
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
