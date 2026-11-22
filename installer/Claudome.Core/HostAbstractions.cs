using System.Runtime.InteropServices;

namespace ClaudeSetup;

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
