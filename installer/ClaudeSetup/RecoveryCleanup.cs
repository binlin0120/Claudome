using System.IO;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed record RecoveryFile(string Path, long Length, DateTime LastWriteUtc);
public static class RecoveryCleanup
{
    public static List<RecoveryFile> Plan(SessionState state, string active, IEnumerable<string> candidates, IEnumerable<string> referenced)
    {
        var directory = Path.GetFullPath(state.Options.VmDirectory);
        var links = referenced.Select(Path.GetFullPath).Append(Path.GetFullPath(active)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<RecoveryFile>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase)) {
            var path = Paths.Under(directory, candidate);
            if (!Path.GetDirectoryName(path)!.Equals(directory, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(path), "^Windows11(?:-retry-[a-f0-9]{32})?\\.vmdk$"))
                throw new SetupException("旧盘清理范围不属于本任务，未删除文件。");
            if (links.Contains(path)) throw new SetupException("待清理旧盘仍被虚拟机引用，未删除文件。");
            if (File.Exists(path)) { var file = new FileInfo(path); result.Add(new(path, file.Length, file.LastWriteTimeUtc)); }
        }
        return result;
    }
    public static RecoveryCleanupResult Workstation(SessionState state, IEnumerable<string> registeredConfigurations)
    {
        var root = state.Options.VmDirectory;
        var marker = JsonFile.Read<Dictionary<string,string>>(Paths.Under(root,".claude-workspace.json"));
        if (marker.GetValueOrDefault("vmId") != state.VmId || marker.GetValueOrDefault("workspaceId") != state.Options.WorkspaceId) throw new SetupException("清理任务身份不符。");
        var file = Paths.Under(root,".vmware-install.json");
        if (!File.Exists(file)) return new(0,0);
        var point = JsonFile.Read<WorkstationCheckpoint>(file);
        if (point.VmId != state.VmId || point.WorkspaceId != state.Options.WorkspaceId || point.Phase != "ready") throw new SetupException("新系统的磁盘任务尚未就绪，暂不清理旧盘。");
        if (point.PreservedDisks.Count == 0) return new(0,0);
        var current = Paths.Under(root,state.Options.VmName+".vmx");
        WorkstationProvisioning.ValidateExisting(state,WorkstationBoundary.Parse(File.ReadAllText(current)),point.DiskPath);
        var configs = registeredConfigurations.Append(current).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var references = new List<string>();
        foreach (var path in configs) {
            Paths.RejectLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length > 4*1024*1024) throw new SetupException("虚拟机引用清单暂不可核验，旧盘清理待处理。");
            var directory = Path.GetDirectoryName(path)!;
            // Snapshot chains can refer to a base disk indirectly; postpone cleanup.
            if (Directory.EnumerateFiles(directory,"*.vmsn").Any() || Directory.EnumerateFiles(directory,"*.vmss").Any()) throw new SetupException("存在快照或保存状态，旧盘清理待处理。");
            foreach(var snapshotFile in Directory.EnumerateFiles(directory,"*.vmsd")) {
                if(new FileInfo(snapshotFile).Length>1024*1024)throw new SetupException("快照元数据暂不可核验，旧盘清理待处理。");
                var snapshots=WorkstationBoundary.Parse(File.ReadAllText(snapshotFile));
                if(snapshots.GetValueOrDefault("snapshot.numSnapshots","0")!="0")throw new SetupException("存在快照磁盘链，旧盘清理待处理。");
            }
            foreach(var item in WorkstationBoundary.Parse(File.ReadAllText(path)).Where(p=>p.Key.EndsWith(".fileName",StringComparison.OrdinalIgnoreCase)&&p.Value.EndsWith(".vmdk",StringComparison.OrdinalIgnoreCase)))
                references.Add(Path.GetFullPath(Path.Combine(directory,item.Value)));
        }
        var plan = Plan(state,point.DiskPath,point.PreservedDisks,references); // complete dry-run before any deletion
        var deleted = 0;
        foreach (var item in plan) {
            try {
                Paths.RejectLinks(item.Path);var now = new FileInfo(item.Path);
                if (now.Length != item.Length || now.LastWriteTimeUtc != item.LastWriteUtc) continue;
                File.Delete(item.Path); deleted++;
                point.PreservedDisks.RemoveAll(x=>Path.GetFullPath(x).Equals(item.Path,StringComparison.OrdinalIgnoreCase));
                JsonFile.Write(file,point);
            } catch (Exception ex) when(ex is IOException or UnauthorizedAccessException) { /* retry on the next resume */ }
        }
        point.PreservedDisks.RemoveAll(x=>!File.Exists(x));JsonFile.Write(file,point);
        return new(deleted,point.PreservedDisks.Count);
    }
}
