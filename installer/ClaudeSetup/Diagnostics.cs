using System.Reflection;
using System.Runtime.InteropServices;
using System.IO;
using System.Security.Cryptography;

namespace ClaudeSetup;

public sealed record DiagnosticReport(int Schema, DateTime CreatedUtc, string Application, string Version, string SystemEdition, int WindowsBuild,
    string Architecture, long TotalMemoryBytes, long FreeMemoryBytes, bool VirtualizationReady, bool VirtualizationKnown,
    bool VirtualBoxInstalled, bool WorkstationInstalled, Dictionary<string, int> RegisteredVmCounts, string[] InventoryNotes, string ExecutableSha256);

public static class Diagnostics
{
    public static string Version => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public static DiagnosticReport Create(HostInfo host, PlatformFacts facts, VmInventoryResult inventory, string executableSha256 = "") => new(1, DateTime.UtcNow, "Claudome", Version,
        host.Edition, host.Build, facts.Architecture, host.TotalMemory, host.FreeMemory, facts.VirtualizationReady, facts.VirtualizationKnown,
        facts.VBoxInstalled, facts.WorkstationInstalled, inventory.Machines.GroupBy(x => x.Platform).ToDictionary(x => x.Key, x => x.Count()), inventory.Notes.ToArray(), executableSha256);
    public static async Task<DiagnosticReport> Collect(CancellationToken token)
    {
        var host = Machine.Read(); var facts = Platforms.Detect();
        await using var executable = new FileStream(Environment.ProcessPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(executable, token)).ToLowerInvariant();
        return Create(host, facts, await VmInventory.Scan(token), hash);
    }
}
