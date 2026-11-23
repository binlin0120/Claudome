using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Claudome.Transport;

namespace ClaudeSetup;

public static class BusBoundary
{
    public static BridgeSpec Configuration(SessionState state, GatewayBinding binding)
    {
        if (binding.VmId != state.VmId || state.Options.PlatformId is not ("hyperv" or "workstation")) throw new SetupException("虚拟机总线通道身份不匹配。");
        SecretStore.RestrictDirectory(state.Options.Private); var file = Paths.Under(state.Options.Private, "bridge-identity.dpapi");
        if (File.Exists(file)) {
            var plain = SecretStore.Protect(File.ReadAllBytes(file), true);
            try {
                var saved = JsonSerializer.Deserialize<BridgeSpec>(plain)!; saved.Validate();
                if (saved.VmId != state.VmId || saved.WorkspaceId != state.Options.WorkspaceId || saved.Platform != state.Options.PlatformId || saved.GuestMac != binding.GuestMac) throw new SetupException("已保存的总线通道属于其他环境。"); return saved;
            } finally { CryptographicOperations.ZeroMemory(plain); }
        }
        var spec = new BridgeSpec(state.Options.PlatformId, state.VmId, state.Options.WorkspaceId, Guid.NewGuid().ToString(), (uint)RandomNumberGenerator.GetInt32(20000, int.MaxValue - 1), binding.GuestMac, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        spec.Validate(); var data = JsonSerializer.SerializeToUtf8Bytes(spec); try { File.WriteAllBytes(file, SecretStore.Protect(data)); } finally { CryptographicOperations.ZeroMemory(data); } return spec;
    }
    public static async Task Prepare(IVmBackend backend, SessionState state, BridgeSpec spec, CancellationToken token)
    {
        if (backend is HyperV hyperv) await hyperv.PrepareBoundary(state, spec, token);
        else if (backend is Workstation workstation) await workstation.PrepareBoundary(state, spec, token);
        else throw new SetupException("当前平台不使用总线通道。");
    }
    public static async Task Verify(IVmBackend backend, SessionState state, BridgeSpec spec, CancellationToken token)
    {
        if (backend is HyperV hyperv) await hyperv.VerifyBoundary(state, spec, token);
        else if (backend is Workstation workstation) await workstation.VerifyBoundary(state, spec, token);
        else throw new SetupException("无法核验所选总线通道。");
    }
    public static async Task VerifyOffline(IVmBackend backend, SessionState state, CancellationToken token)
    {
        if (backend is HyperV hyperv) await hyperv.VerifyOffline(state, token);
        else if (backend is Workstation workstation) await workstation.VerifyOffline(state, token);
        else throw new SetupException("无法核验此平台的断网状态。");
    }
    private const string GuestExecutableName = "ClaudomeGuestNetwork.exe";

    internal static async Task<bool> TryReuseConnectedGuest(IVmBackend backend, SessionState state,
        Secrets secrets, Func<bool> isConnected, CancellationToken token)
    {
        if (state.Options.PlatformId != "hyperv") return false;

        const string check = """
            $ErrorActionPreference = 'Stop'
            $path = 'C:\ProgramData\ClaudomeNetwork\ClaudomeGuestNetwork.exe'
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return }
            if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { return }
            if (Get-Process -Name ClaudomeGuestNetwork -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $path }) {
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            }
            """;
        var expectedHash = Convert.ToHexString(SHA256.HashData(Catalog.EmbeddedBytes(GuestExecutableName)));
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(check));
        try
        {
            var observed = await backend.GuestRun(state, secrets, state.Options.GuestSetupDirectory + @"\pwsh\pwsh.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], token, 30);
            if (!observed.Trim().Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
            // A running agent may still be finishing the previous 10-second connection attempt.
            for (var i = 0; i < 12 && !isConnected(); i++) await Task.Delay(1000, token);
            return isConnected();
        }
        catch (SetupException)
        {
            return false;
        }
    }

    public static async Task InstallGuest(IVmBackend backend, SessionState state, Secrets secrets, BridgeSpec spec, CancellationToken token)
    {
        var guestRoot = state.Options.GuestSetupDirectory;
        var data = Catalog.EmbeddedBytes(GuestExecutableName);
        var expectedHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var executable = Paths.Under(state.Options.Private, $"ClaudomeGuestNetwork.{expectedHash[..16]}.exe");
        if (File.Exists(executable) &&
            !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new SetupException("来宾网络组件与内置版本不一致。");
        if (!File.Exists(executable)) File.WriteAllBytes(executable, data);

        using var configuration = new PrivateFile(state.Options.Private, JsonSerializer.Serialize(new
        {
            spec,
            executableSha256 = expectedHash
        }), ".json");
        using var install = new PrivateFile(state.Options.Private, Catalog.EmbeddedText("BridgeInstall.ps1"), ".ps1");
        await CopyComponent(executable, GuestExecutableName);
        await CopyComponent(configuration.FileName, "bridge-input.json");
        await CopyComponent(install.FileName, "BridgeInstall.ps1");
        try
        {
            await backend.GuestRun(state, secrets, guestRoot + @"\pwsh\pwsh.exe",
                ["-NoLogo", "-NoProfile", "-File", guestRoot + @"\BridgeInstall.ps1",
                 "-InputPath", guestRoot + @"\bridge-input.json"], token, 300);
        }
        catch (HandoffException) { throw; }
        catch (SetupException error) { throw new SetupException($"运行 BridgeInstall.ps1 失败。{Redaction.SafeError(error)}"); }

        async Task CopyComponent(string source, string name)
        {
            try { await backend.CopyTo(state, secrets, source, guestRoot + "\\" + name, token); }
            catch (HandoffException) { throw; }
            catch (SetupException error) { throw new SetupException($"传送 {name} 失败。{Redaction.SafeError(error)}"); }
        }
    }
}
