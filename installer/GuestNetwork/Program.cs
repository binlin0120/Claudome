using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Claudome.Transport;

namespace Claudome.GuestNetwork;

internal static class Program
{
    private const string Root = @"C:\ProgramData\ClaudomeNetwork";
    private static async Task<int> Main(string[] args)
    {
        try {
            if (args.Length != 0) return 1;
            using var user = WindowsIdentity.GetCurrent(); if (!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator)) return 1;
            if (!File.Exists(Path.Combine(Root, "bridge.json")) || new FileInfo(Path.Combine(Root, "bridge.json")).Length > 8192) return 1;
            RejectLinks(Root); var spec = JsonSerializer.Deserialize<BridgeSpec>(File.ReadAllText(Path.Combine(Root, "bridge.json")))!; spec.Validate(); AssertGuest(spec);
            using var single = new Mutex(true, "Global\\ClaudomeNetwork-" + spec.VmId, out var created); if (!created) return 1;
            Wintun.LoadOfficialLibrary(Root); using var adapter = new Wintun(Guid.Parse(spec.ServiceId));
            await Netsh(["interface", "ipv4", "set", "address", "name=ClaudomeBridge", "source=static", "address=198.19.250.10", "mask=255.255.255.0", "gateway=198.19.250.1", "gwmetric=500"]);
            await Netsh(["interface", "ipv4", "set", "dnsservers", "name=ClaudomeBridge", "source=static", "address=198.19.250.1", "validate=no"]);
            await Netsh(["interface", "ipv4", "set", "subinterface", "ClaudomeBridge", "mtu=1500", "store=persistent"]);
            Status(spec, false, "adapter-ready");
            while (true) {
                using var lifetime = new CancellationTokenSource();
                try {
                    using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var socket = await HypervisorSockets.Connect(spec, connect.Token);
                    using var stream = new NetworkStream(socket, false); await BridgeProtocol.AuthenticateClient(stream, spec, connect.Token); Status(spec, true, "connected");
                    var receive = Task.Run(async () => { while (!lifetime.IsCancellationRequested) { var packet = adapter.Receive(lifetime.Token); if (BridgeProtocol.ValidIPv4(packet)) await BridgeProtocol.WritePacket(stream, packet, lifetime.Token); } }, lifetime.Token);
                    var send = Task.Run(async () => { while (!lifetime.IsCancellationRequested) { var packet = await BridgeProtocol.ReadPacket(stream, lifetime.Token); adapter.Send(packet); } }, lifetime.Token);
                    await Task.WhenAny(receive, send); lifetime.Cancel(); socket.Dispose();
                    try { await Task.WhenAll(receive, send); } catch { }
                } catch { lifetime.Cancel(); }
                Status(spec, false, "disconnected"); await Task.Delay(2000);
            }
        } catch { return 1; }
    }
    private static void AssertGuest(BridgeSpec spec)
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var manufacturer = bios?.GetValue("SystemManufacturer")?.ToString() ?? ""; var model = bios?.GetValue("SystemProductName")?.ToString() ?? "";
        if (spec.Platform == "hyperv" ? manufacturer != "Microsoft Corporation" || model != "Virtual Machine" : !manufacturer.Contains("VMware", StringComparison.OrdinalIgnoreCase) || !model.Contains("VMware", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("not_target_guest");
        using var identity = JsonDocument.Parse(File.ReadAllText(@"C:\ProgramData\ClaudeBootstrap\identity.json"));
        if (identity.RootElement.GetProperty("vmId").GetString() != spec.VmId || identity.RootElement.GetProperty("workspaceId").GetString() != spec.WorkspaceId) throw new InvalidDataException("guest_scope_mismatch");
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current)) if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("guest_path_link");
        foreach (var file in Directory.EnumerateFiles(path)) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("guest_path_link");
    }
    private static async Task Netsh(string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg); using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
        await output; await error; if (process.ExitCode != 0) throw new IOException("guest_interface_configuration_failed");
    }
    private static void Status(BridgeSpec spec, bool connected, string stage)
    {
        var path = Path.Combine(Root, "status.json"); var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { vmId = spec.VmId, workspaceId = spec.WorkspaceId, adapter = "ClaudomeBridge", connected, stage, timeUtc = DateTime.UtcNow })); File.Move(temporary, path, true);
    }
}
