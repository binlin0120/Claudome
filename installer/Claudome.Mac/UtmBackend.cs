using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeSetup;

/// <summary>
/// Open-source UTM (QEMU) backend for macOS Apple Silicon (ARM64) and Intel hosts.
/// Generates .utm/config.plist + qcow2 disk + unattended OEMDRV seed media and manages lifecycle via utmctl.
/// </summary>
public sealed class UtmBackend(ICommandRunner? commandRunner = null, string? testUtmCtlPath = null) : IVmBackend
{
    private readonly ICommandRunner runner = commandRunner ?? new CommandRunner();

    public static string? UtmCtlPath(string? overridePath = null)
    {
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;
        return MacHostEnvironment.DefaultUtmCtlPaths.FirstOrDefault(File.Exists);
    }

    private string RequireUtmCtl() => UtmCtlPath(testUtmCtlPath)
        ?? throw new SetupException("尚未检测到 UTM（未找到 utmctl）。请安装官方开源 UTM 后继续。");

    private static string UtmBundleDirectory(SessionState state) =>
        Paths.Under(state.Options.VmDirectory, state.Options.VmName + ".utm");

    internal static string GenerateLocallyAdministeredMac()
    {
        var bytes = RandomNumberGenerator.GetBytes(6);
        bytes[0] = (byte)((bytes[0] & 0xFE) | 0x02);
        return string.Join(":", bytes.Select(b => b.ToString("x2")));
    }

    /// <summary>Builds a valid Apple XML PropertyList (.utm/config.plist) with TPM 2.0, UEFI, isolated VirtIO NIC, and configurable clipboard sharing.</summary>
    public static string BuildUtmConfigPlist(SessionState state, bool appleSilicon, string macAddress, string diskFileName, string windowsIsoPath, string? seedIsoPath)
    {
        var arch = appleSilicon ? "aarch64" : "x86_64";
        var target = appleSilicon ? "virt" : "q35";
        var name = SecurityElement.Escape(state.Options.VmName) ?? "Claude-Workspace";
        var escapedDisk = SecurityElement.Escape(diskFileName) ?? "disk0.qcow2";
        var escapedIso = SecurityElement.Escape(windowsIsoPath) ?? "";
        var escapedSeed = SecurityElement.Escape(seedIsoPath ?? "") ?? "";
        var escapedMac = SecurityElement.Escape(macAddress) ?? "02:00:00:00:00:01";
        var clipboardTag = state.Options.SharedClipboardEnabled ? "<true/>" : "<false/>";

        var seedDriveBlock = string.IsNullOrEmpty(seedIsoPath)
            ? ""
            : $"""
                  <dict>
                    <key>ImagePath</key><string>{escapedSeed}</string>
                    <key>ImageType</key><string>CD</string>
                    <key>Interface</key><string>USB</string>
                    <key>ReadOnly</key><true/>
                  </dict>
            """;

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Backend</key><string>QEMU</string>
              <key>ConfigurationVersion</key><integer>4</integer>
              <key>Information</key>
              <dict>
                <key>Name</key><string>{name}</string>
                <key>UUID</key><string>{SecurityElement.Escape(state.VmId)}</string>
              </dict>
              <key>System</key>
              <dict>
                <key>Architecture</key><string>{arch}</string>
                <key>Target</key><string>{target}</string>
                <key>CPUCount</key><integer>{state.Options.CpuCount}</integer>
                <key>MemorySize</key><integer>{state.Options.MemoryMb}</integer>
              </dict>
              <key>QEMU</key>
              <dict>
                <key>UEFIBoot</key><true/>
                <key>TPMDevice</key><true/>
                <key>RNGDevice</key><true/>
              </dict>
              <key>Sharing</key>
              <dict>
                <key>ClipboardSharing</key>{clipboardTag}
                <key>DirectoryShareMode</key><string>None</string>
              </dict>
              <key>Network</key>
              <array>
                <dict>
                  <key>Hardware</key><string>virtio-net-pci</string>
                  <key>MacAddress</key><string>{escapedMac}</string>
                  <key>Mode</key><string>Shared</string>
                </dict>
              </array>
              <key>Drive</key>
              <array>
                <dict>
                  <key>ImageName</key><string>{escapedDisk}</string>
                  <key>ImageType</key><string>Disk</string>
                  <key>Interface</key><string>NVMe</string>
                </dict>
                <dict>
                  <key>ImagePath</key><string>{escapedIso}</string>
                  <key>ImageType</key><string>CD</string>
                  <key>Interface</key><string>USB</string>
                  <key>ReadOnly</key><true/>
                </dict>
            {seedDriveBlock}
              </array>
            </dict>
            </plist>
            """;
    }

    /// <summary>Toggles ClipboardSharing inside .utm/config.plist and persists the preference.</summary>
    public static async Task SetSharedClipboardAsync(SessionState state, bool enabled, CancellationToken token)
    {
        state.Options.SharedClipboardEnabled = enabled;
        var plistPath = Path.Combine(UtmBundleDirectory(state), "config.plist");
        if (File.Exists(plistPath))
        {
            var content = await File.ReadAllTextAsync(plistPath, token);
            var tag = enabled ? "<true/>" : "<false/>";
            content = System.Text.RegularExpressions.Regex.Replace(
                content,
                @"<key>ClipboardSharing</key>\s*<(?:true|false)/>",
                $"<key>ClipboardSharing</key>{tag}");
            await File.WriteAllTextAsync(plistPath, content, new UTF8Encoding(false), token);
        }
        if (!string.IsNullOrEmpty(state.VmId)) state.Save();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task Prepare(SetupOptions options, CancellationToken token)
    {
        _ = RequireUtmCtl();
        return Task.CompletedTask;
    }

    public Task<string> EnsureMachine(SessionState state, CancellationToken token)
    {
        var marker = Paths.Under(state.Options.VmDirectory, ".claude-workspace.json");
        if (!string.IsNullOrEmpty(state.VmId))
        {
            if (!File.Exists(marker)) throw new SetupException("UTM 任务标识缺失，未接管现有虚拟机。");
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            if (json.RootElement.GetProperty("vmId").GetString() != state.VmId ||
                json.RootElement.GetProperty("workspaceId").GetString() != state.Options.WorkspaceId)
                throw new SetupException("UTM 任务身份不匹配。");
        }
        else
        {
            if (Directory.Exists(state.Options.VmDirectory))
                throw new SetupException("同名 UTM 目录已存在，未覆盖。请改名或载入原任务。");
            Directory.CreateDirectory(state.Options.VmDirectory);
            state.VmId = Guid.NewGuid().ToString();
            JsonFile.Write(marker, new { vmId = state.VmId, workspaceId = state.Options.WorkspaceId });
            state.Save();
        }
        return Task.FromResult(state.VmId);
    }

    public async Task Configure(SessionState state, CancellationToken token)
    {
        await EnsureMachine(state, token);
        var bundle = UtmBundleDirectory(state);
        var dataDir = Path.Combine(bundle, "Data");
        Directory.CreateDirectory(dataDir);
        var diskPath = Path.Combine(dataDir, "disk0.qcow2");
        if (!File.Exists(diskPath) && OperatingSystem.IsMacOS())
        {
            var qemuImg = "/Applications/UTM.app/Contents/MacOS/qemu-img";
            if (File.Exists(qemuImg))
                await runner.Run(qemuImg, ["create", "-f", "qcow2", diskPath, $"{state.Options.DiskGb}G"], token, 60);
        }
        var appleSilicon = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        var plist = BuildUtmConfigPlist(state, appleSilicon, GenerateLocallyAdministeredMac(), "disk0.qcow2", state.Options.IsoPath, null);
        await File.WriteAllTextAsync(Path.Combine(bundle, "config.plist"), plist, new UTF8Encoding(false), token);
    }

    public async Task InstallWindows(SessionState state, Secrets secrets, CancellationToken token)
    {
        var appleSilicon = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        var seed = await MacProvisioningMedia.EnsureSeedIsoAsync(state, secrets, appleSilicon, runner, token);
        var bundle = UtmBundleDirectory(state);
        Directory.CreateDirectory(Path.Combine(bundle, "Data"));
        var plist = BuildUtmConfigPlist(state, appleSilicon, GenerateLocallyAdministeredMac(), "disk0.qcow2", state.Options.IsoPath, seed);
        await File.WriteAllTextAsync(Path.Combine(bundle, "config.plist"), plist, new UTF8Encoding(false), token);
        if (OperatingSystem.IsMacOS())
        {
            await runner.Run("open", ["-a", "UTM", bundle], token, 30);
            await runner.Run(RequireUtmCtl(), ["start", state.VmId], token, 60);
        }
    }

    public async Task<bool> IsRunning(SessionState state, CancellationToken token)
    {
        var output = await runner.Run(RequireUtmCtl(), ["status", state.VmId], token, 20);
        return output.Contains("started", StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> GuestReady(SessionState state, Secrets secrets, CancellationToken token) => Task.FromResult(false);
    public Task CleanupWindowsMedia(SessionState state, CancellationToken token) => Task.CompletedTask;

    public Task<string> GuestRun(SessionState state, Secrets secrets, string executable, IEnumerable<string> args, CancellationToken token, int seconds = 300) =>
        throw new SetupException("UTM 来宾执行需在来宾代理就绪后经隔离通道执行，当前尚待 Mac 真机联调来宾代理。");

    public Task<bool> GuestFileExists(SessionState state, Secrets secrets, string file, CancellationToken token) => Task.FromResult(false);
    public Task CopyTo(SessionState state, Secrets secrets, string local, string destination, CancellationToken token) =>
        throw new SetupException("UTM 文件传送尚待 Mac 真机联调来宾代理。");
    public Task CopyFrom(SessionState state, Secrets secrets, string guest, string destination, CancellationToken token) =>
        throw new SetupException("UTM 文件回读尚待 Mac 真机联调来宾代理。");

    public async Task Open(SessionState state, CancellationToken token)
    {
        await EnsureMachine(state, token);
        if (!await IsRunning(state, token) && OperatingSystem.IsMacOS())
            await runner.Run(RequireUtmCtl(), ["start", state.VmId], token, 60);
        if (OperatingSystem.IsMacOS())
            await runner.Run("open", ["-a", "UTM", UtmBundleDirectory(state)], token, 15);
    }

    public async Task ShutdownOriginal(SessionState state, CancellationToken token)
    {
        await EnsureMachine(state, token);
        if (!await IsRunning(state, token)) return;
        await runner.Run(RequireUtmCtl(), ["stop", state.VmId], token, 60);
    }
}
