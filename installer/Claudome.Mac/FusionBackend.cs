using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

/// <summary>
/// VMware Fusion backend for macOS (Apple Silicon Windows 11 ARM64 and Intel Windows 11 x64).
/// Mirrors Workstation.cs isolation guarantees using .vmx settings and vmrun -T fusion.
/// </summary>
public sealed class FusionBackend(ICommandRunner? commandRunner = null, string? testVmrunPath = null) : IVmBackend
{
    private readonly ICommandRunner runner = commandRunner ?? new CommandRunner();

    public static string? VmrunPath(string? overridePath = null)
    {
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;
        return MacHostEnvironment.DefaultFusionVmrunPaths.FirstOrDefault(File.Exists);
    }

    private string RequireVmrun() => VmrunPath(testVmrunPath)
        ?? throw new SetupException("尚未检测到 VMware Fusion（未找到 vmrun）。请从 Broadcom 官方安装 VMware Fusion 后继续。");

    private static string VmxPath(SessionState state) =>
        Paths.Under(state.Options.VmDirectory, state.Options.VmName + ".vmx");

    /// <summary>Builds a .vmx descriptor for VMware Fusion with shared folders/DnD disabled and configurable bidirectional clipboard.</summary>
    public static string BuildVmx(SessionState state, bool appleSilicon, string vmdkPath, string windowsIsoPath, string? seedIsoPath)
    {
        var guestOs = appleSilicon ? "arm-windows11-64" : "windows11-64";
        var clipboardDisabled = state.Options.SharedClipboardEnabled ? "FALSE" : "TRUE";
        var lines = new List<string>
        {
            ".encoding = \"UTF-8\"",
            "config.version = \"8\"",
            "virtualHW.version = \"21\"",
            $"displayName = \"{state.Options.VmName}\"",
            $"guestOS = \"{guestOs}\"",
            "firmware = \"efi\"",
            "uefi.secureBoot.enabled = \"TRUE\"",
            $"memsize = \"{state.Options.MemoryMb}\"",
            $"numvcpus = \"{state.Options.CpuCount}\"",
            "nvme0.present = \"TRUE\"",
            "nvme0:0.present = \"TRUE\"",
            $"nvme0:0.fileName = \"{Path.GetFileName(vmdkPath)}\"",
            "sata0.present = \"TRUE\"",
            "sata0:0.present = \"TRUE\"",
            "sata0:0.deviceType = \"cdrom-image\"",
            $"sata0:0.fileName = \"{windowsIsoPath}\"",
            "sata0:0.startConnected = \"TRUE\"",
            "ethernet0.present = \"TRUE\"",
            "ethernet0.virtualDev = \"e1000e\"",
            "ethernet0.connectionType = \"nat\"",
            "isolation.tools.hgfs.disable = \"TRUE\"",
            $"isolation.tools.copy.disable = \"{clipboardDisabled}\"",
            $"isolation.tools.paste.disable = \"{clipboardDisabled}\"",
            "isolation.tools.dnd.disable = \"TRUE\"",
            "sharedFolder0.enabled = \"FALSE\"",
            "hgfs.mapRootShare = \"FALSE\"",
            "usb.present = \"TRUE\"",
            "ehci.present = \"TRUE\"",
            "usb_xhci.present = \"TRUE\""
        };
        if (!string.IsNullOrEmpty(seedIsoPath))
        {
            lines.Add("sata0:1.present = \"TRUE\"");
            lines.Add("sata0:1.deviceType = \"cdrom-image\"");
            lines.Add($"sata0:1.fileName = \"{seedIsoPath}\"");
            lines.Add("sata0:1.startConnected = \"TRUE\"");
        }
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Toggles bidirectional shared clipboard in the .vmx config and persists the setting.</summary>
    public static async Task SetSharedClipboardAsync(SessionState state, bool enabled, CancellationToken token)
    {
        state.Options.SharedClipboardEnabled = enabled;
        var vmx = VmxPath(state);
        if (File.Exists(vmx))
        {
            var disabled = enabled ? "FALSE" : "TRUE";
            var content = await File.ReadAllTextAsync(vmx, token);
            content = UpsertVmxKey(content, "isolation.tools.copy.disable", disabled);
            content = UpsertVmxKey(content, "isolation.tools.paste.disable", disabled);
            await File.WriteAllTextAsync(vmx, content, new UTF8Encoding(false), token);
        }
        if (!string.IsNullOrEmpty(state.VmId)) state.Save();
    }

    private static string UpsertVmxKey(string vmx, string key, string value)
    {
        var line = $"{key} = \"{value}\"";
        var pattern = $"^{Regex.Escape(key)}\\s*=.*$";
        return Regex.IsMatch(vmx, pattern, RegexOptions.Multiline)
            ? Regex.Replace(vmx, pattern, line, RegexOptions.Multiline)
            : vmx.TrimEnd('\r', '\n') + "\n" + line + "\n";
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task Prepare(SetupOptions options, CancellationToken token)
    {
        _ = RequireVmrun();
        return Task.CompletedTask;
    }

    public Task<string> EnsureMachine(SessionState state, CancellationToken token)
    {
        var marker = Paths.Under(state.Options.VmDirectory, ".claude-workspace.json");
        if (!string.IsNullOrEmpty(state.VmId))
        {
            if (!File.Exists(marker)) throw new SetupException("VMware Fusion 任务标识缺失，未接管现有虚拟机。");
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            if (json.RootElement.GetProperty("vmId").GetString() != state.VmId ||
                json.RootElement.GetProperty("workspaceId").GetString() != state.Options.WorkspaceId)
                throw new SetupException("VMware Fusion 任务身份不匹配。");
        }
        else
        {
            if (Directory.Exists(state.Options.VmDirectory))
                throw new SetupException("同名 VMware Fusion 目录已存在，未覆盖。请改名或载入原任务。");
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
        var vmx = VmxPath(state);
        if (File.Exists(vmx)) return;

        var disk = Paths.Under(state.Options.VmDirectory, state.Options.VmName + ".vmdk");
        var vdiskManager = Path.Combine(Path.GetDirectoryName(RequireVmrun())!, "vmware-vdiskmanager");
        if (OperatingSystem.IsMacOS() && File.Exists(vdiskManager))
        {
            await runner.Run(vdiskManager, ["-c", "-s", $"{state.Options.DiskGb}GB", "-a", "lsilogic", "-t", "0", disk], token, 120);
        }
        var appleSilicon = RuntimeInformationIsArm64();
        var content = BuildVmx(state, appleSilicon, disk, state.Options.IsoPath, null);
        await File.WriteAllTextAsync(vmx, content, new UTF8Encoding(false), token);
    }

    private static bool RuntimeInformationIsArm64() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

    public async Task InstallWindows(SessionState state, Secrets secrets, CancellationToken token)
    {
        var appleSilicon = RuntimeInformationIsArm64();
        var seed = await MacProvisioningMedia.EnsureSeedIsoAsync(state, secrets, appleSilicon, runner, token);
        var vmx = VmxPath(state);
        var disk = Paths.Under(state.Options.VmDirectory, state.Options.VmName + ".vmdk");
        await File.WriteAllTextAsync(vmx, BuildVmx(state, appleSilicon, disk, state.Options.IsoPath, seed), new UTF8Encoding(false), token);
        if (OperatingSystem.IsMacOS())
            await runner.Run(RequireVmrun(), ["-T", "fusion", "start", vmx], token, 60);
    }

    public async Task<bool> IsRunning(SessionState state, CancellationToken token)
    {
        var output = await runner.Run(RequireVmrun(), ["-T", "fusion", "list"], token, 20);
        var target = Path.GetFullPath(VmxPath(state));
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Trim('"'))
            .Where(Path.IsPathFullyQualified)
            .Any(line => Path.GetFullPath(line).Equals(target, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> GuestReady(SessionState state, Secrets secrets, CancellationToken token)
    {
        try
        {
            var probe = await GuestRun(state, secrets, @"C:\Windows\System32\whoami.exe", [], token, 20);
            return !string.IsNullOrWhiteSpace(probe);
        }
        catch { return false; }
    }

    public Task CleanupWindowsMedia(SessionState state, CancellationToken token) => Task.CompletedTask;

    public async Task<string> GuestRun(SessionState state, Secrets secrets, string executable, IEnumerable<string> args, CancellationToken token, int seconds = 300)
    {
        var arguments = new List<string> { "-T", "fusion", "-gu", state.Options.UserName, "-gp", secrets.VmPassword, "runProgramInGuest", VmxPath(state), executable };
        arguments.AddRange(args);
        return await runner.Run(RequireVmrun(), arguments, token, seconds);
    }

    public Task<bool> GuestFileExists(SessionState state, Secrets secrets, string file, CancellationToken token) =>
        runner.Run(RequireVmrun(), ["-T", "fusion", "-gu", state.Options.UserName, "-gp", secrets.VmPassword, "fileExistsInGuest", VmxPath(state), file], token, 20)
            .ContinueWith(t => t.IsCompletedSuccessfully && !t.Result.Contains("not exist", StringComparison.OrdinalIgnoreCase), token);

    public Task CopyTo(SessionState state, Secrets secrets, string local, string destination, CancellationToken token) =>
        runner.Run(RequireVmrun(), ["-T", "fusion", "-gu", state.Options.UserName, "-gp", secrets.VmPassword, "CopyFileFromHostToGuest", VmxPath(state), local, destination], token, 120);

    public Task CopyFrom(SessionState state, Secrets secrets, string guest, string destination, CancellationToken token) =>
        runner.Run(RequireVmrun(), ["-T", "fusion", "-gu", state.Options.UserName, "-gp", secrets.VmPassword, "CopyFileFromGuestToHost", VmxPath(state), guest, destination], token, 120);

    public async Task Open(SessionState state, CancellationToken token)
    {
        await EnsureMachine(state, token);
        if (!await IsRunning(state, token) && OperatingSystem.IsMacOS())
            await runner.Run(RequireVmrun(), ["-T", "fusion", "start", VmxPath(state)], token, 60);
        if (OperatingSystem.IsMacOS())
            await runner.Run("open", ["-a", "VMware Fusion"], token, 15);
    }

    public async Task ShutdownOriginal(SessionState state, CancellationToken token)
    {
        await EnsureMachine(state, token);
        if (!await IsRunning(state, token)) return;
        await runner.Run(RequireVmrun(), ["-T", "fusion", "stop", VmxPath(state), "soft"], token, 75);
    }
}
