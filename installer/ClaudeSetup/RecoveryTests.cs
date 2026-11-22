using System.IO;
using System.Text.Json;

namespace ClaudeSetup;

public static class RecoveryTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch (SetupException) { return; } throw new Exception("unsafe recovery accepted"); }
    public static async Task Run(Func<string, Func<Task>, Task> check, string root)
    {
        SessionState Fixture(string label) {
            var state = new SessionState { VmId = Guid.NewGuid().ToString(), Options = new() { Root = Path.Combine(root, label), PlatformId = "workstation", IsoPath = @"D:\Fictional\Windows.iso" } };
            Directory.CreateDirectory(state.Options.VmDirectory);
            JsonFile.Write(Path.Combine(state.Options.VmDirectory, ".claude-workspace.json"), new { vmId = state.VmId, workspaceId = state.Options.WorkspaceId }); return state;
        }
        string Vmx(SessionState s) => Path.Combine(s.Options.VmDirectory, s.Options.VmName + ".vmx");
        string Checkpoint(SessionState s) => Path.Combine(s.Options.VmDirectory, ".vmware-install.json");
        const string vendor = "fixture-vendor-never-executed";
        await check("vmware-retry-after-vmx-write-preserves-encryption-and-tpm", async () => {
            var s = Fixture("vmware-written-config"); var runner = new DiskRunner(); var backend = new Workstation(runner, vendor);
            await backend.Configure(s, CancellationToken.None);
            var settings = WorkstationBoundary.Parse(File.ReadAllText(Vmx(s))); settings["encryption.keySafe"] = "fictional|encrypted-state"; settings["vtpm.present"] = "TRUE";
            WorkstationBoundary.Write(Vmx(s), settings); var before = File.ReadAllBytes(Vmx(s));
            // Simulate interruption after atomic VMX write, before the stage save.
            var point = JsonFile.Read<WorkstationCheckpoint>(Checkpoint(s)); point.Phase = "disk-ready"; JsonFile.Write(Checkpoint(s), point);
            await backend.Configure(s, CancellationToken.None);
            Assert(File.ReadAllBytes(Vmx(s)).SequenceEqual(before) && runner.Creates == 1 && JsonFile.Read<WorkstationCheckpoint>(Checkpoint(s)).Phase == "ready", "existing VMX or encryption state changed, or duplicate disk created");
        });
        await check("vmware-legacy-owned-vmx-resumes-without-rewrite", async () => {
            var s = Fixture("vmware-legacy-config"); var runner = new DiskRunner(); var backend = new Workstation(runner, vendor);
            await backend.Configure(s, CancellationToken.None); File.Delete(Checkpoint(s));
            var settings = WorkstationBoundary.Parse(File.ReadAllText(Vmx(s))); settings.Remove("guestinfo.claudome.vmId"); settings.Remove("guestinfo.claudome.workspaceId"); WorkstationBoundary.Write(Vmx(s), settings);
            var before = File.ReadAllBytes(Vmx(s)); await backend.Configure(s, CancellationToken.None);
            Assert(before.SequenceEqual(File.ReadAllBytes(Vmx(s))) && runner.Creates == 1, "legacy configuration was overwritten");
        });
        await check("vmware-disk-creation-interruption-keeps-original-bytes", async () => {
            var s = Fixture("vmware-partial-disk"); var runner = new DiskRunner { FailOnce = true }; var backend = new Workstation(runner, vendor);
            await Reject(() => backend.Configure(s, CancellationToken.None));
            var first = JsonFile.Read<WorkstationCheckpoint>(Checkpoint(s)).DiskPath; var before = File.ReadAllBytes(first);
            await backend.Configure(s, CancellationToken.None); var point = JsonFile.Read<WorkstationCheckpoint>(Checkpoint(s));
            Assert(point.DiskPath != first && point.PreservedDisks.Contains(first) && File.ReadAllBytes(first).SequenceEqual(before) && runner.Creates == 2, "partial disk overwritten instead of preserved");
        });
        await check("vmware-disk-ready-checkpoint-does-not-recreate-disk", async () => {
            var s = Fixture("vmware-disk-ready"); var disk = Path.Combine(s.Options.VmDirectory, "Windows11.vmdk"); File.WriteAllText(disk, "fictional completed disk");
            JsonFile.Write(Checkpoint(s), new WorkstationCheckpoint { VmId = s.VmId, WorkspaceId = s.Options.WorkspaceId, DiskPath = disk, Phase = "disk-ready" });
            var runner = new DiskRunner(); await new Workstation(runner, vendor).Configure(s, CancellationToken.None);
            Assert(runner.Creates == 0 && File.ReadAllText(disk) == "fictional completed disk" && File.Exists(Vmx(s)), "completed disk recreated");
        });
        foreach (var mutation in new[] { "foreign-disk", "extra-nic", "extra-disk", "memory", "identity" })
            await check("vmware-resume-rejects-" + mutation, async () => {
                var s = Fixture("vmware-bad-" + mutation); var runner = new DiskRunner(); var backend = new Workstation(runner, vendor); await backend.Configure(s, CancellationToken.None);
                var settings = WorkstationBoundary.Parse(File.ReadAllText(Vmx(s)));
                switch (mutation) {
                    case "foreign-disk": settings["scsi0:0.fileName"] = @"C:\OtherProject\Windows11.vmdk"; break;
                    case "extra-nic": settings["ethernet7.present"] = "TRUE"; break;
                    case "extra-disk": settings["scsi0:1.present"] = "TRUE"; settings["scsi0:1.fileName"] = "other.vmdk"; break;
                    case "memory": settings["memsize"] = "8192"; break;
                    case "identity": settings["guestinfo.claudome.workspaceId"] = new string('f', 32); break;
                }
                WorkstationBoundary.Write(Vmx(s), settings); var before = File.ReadAllBytes(Vmx(s)); await Reject(() => backend.Configure(s, CancellationToken.None));
                Assert(before.SequenceEqual(File.ReadAllBytes(Vmx(s))) && runner.Creates == 1, "rejected VMX was changed");
            });
        await check("vmware-ready-config-loss-does-not-drop-encryption-state", async () => {
            var s = Fixture("vmware-lost-vmx"); var runner = new DiskRunner(); var backend = new Workstation(runner, vendor); await backend.Configure(s, CancellationToken.None);
            File.Move(Vmx(s), Vmx(s) + ".preserved"); await Reject(() => backend.Configure(s, CancellationToken.None));
            Assert(!File.Exists(Vmx(s)) && runner.Creates == 1, "missing established VMX silently recreated");
        });
        await check("vmware-checkpoint-wrong-owner-is-rejected-before-command", async () => {
            var s = Fixture("vmware-foreign-checkpoint"); var point = new WorkstationCheckpoint { VmId = s.VmId, WorkspaceId = new string('b',32), DiskPath = Path.Combine(s.Options.VmDirectory,"Windows11.vmdk") }; JsonFile.Write(Checkpoint(s), point);
            var runner = new DiskRunner(); await Reject(() => new Workstation(runner, vendor).Configure(s, CancellationToken.None)); Assert(runner.Creates == 0, "foreign checkpoint reached vendor command");
        });
    }
    private sealed class DiskRunner : ICommandRunner
    {
        public int Creates;
        public bool FailOnce;
        public Task<string> Run(string exe, IEnumerable<string> args, CancellationToken token, int seconds = 120)
        {
            token.ThrowIfCancellationRequested(); var a = args.ToArray(); Assert(a[0] == "-c" && exe.EndsWith("vmware-vdiskmanager.exe"), "unexpected fixture command");
            Assert(!File.Exists(a[^1]), "disk creation would overwrite a file"); Creates++; File.WriteAllText(a[^1], "fictional disk generation " + Creates);
            if (FailOnce) { FailOnce = false; throw new SetupException("injected disk-creation interruption"); }
            return Task.FromResult("");
        }
    }
}
