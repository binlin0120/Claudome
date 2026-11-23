using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public static class TaskCheckpointStore
{
    private static readonly object Sync=new();
    public static string BackupPath(string path)=>path+".previous.json";
    internal static SessionState DeserializeValid(string jsonPath,string expectedDirectory)
    {
        var state=JsonSerializer.Deserialize<SessionState>(File.ReadAllText(jsonPath),JsonFile.Format);
        if(state is null||state.Options is null||string.IsNullOrWhiteSpace(state.Options.Root)||
            string.IsNullOrWhiteSpace(state.Options.VmName)||!Regex.IsMatch(state.Options.WorkspaceId??"","^[0-9a-f]{32}$"))
            throw new JsonException("Missing required SessionState fields.");
        if(!Path.GetFullPath(state.Options.Root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)
            .Equals(expectedDirectory,StringComparison.OrdinalIgnoreCase))
            throw new SetupException("状态文件与所在任务目录不匹配，已拒绝载入。");
        return state;
    }
    public static void Write(SessionState state) => Write(state, settingsPath: null, registryPath: null);

    public static void Write(SessionState state, string? settingsPath, string? registryPath)
    {
        var path=state.Options.StatePath;Paths.RejectLinks(path);
        var directory=Path.GetDirectoryName(Path.GetFullPath(path))!;Directory.CreateDirectory(directory);
        var expectedDirectory=directory.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        var backup=BackupPath(path);Paths.RejectLinks(backup);
        var rotatedBackup = false;
        lock(Sync) {
            var temporary=path+"."+Guid.NewGuid().ToString("N")+".new";
            try {
                var bytes=JsonSerializer.SerializeToUtf8Bytes(state,JsonFile.Format);
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){output.Write(bytes);output.Flush(true);}
                if(File.Exists(path)) {
                    var valid=false;
                    try
                    {
                        var previous=DeserializeValid(path,expectedDirectory);
                        valid=previous.Options.WorkspaceId==state.Options.WorkspaceId;
                    }
                    catch(Exception ex) when(ex is JsonException or IOException or SetupException){}
                    if(valid)
                    {
                        if(File.Exists(backup))
                        {
                            try
                            {
                                _=DeserializeValid(backup,expectedDirectory);
                                var histDir=Path.Combine(directory,"checkpoints-history");
                                Directory.CreateDirectory(histDir);
                                var archived=Path.Combine(histDir,$"setup-state.prev-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
                                File.Copy(backup,archived,overwrite:true);
                            }
                            catch{}
                        }
                        File.Replace(temporary,path,backup,true);
                        rotatedBackup = true;
                    }
                    else File.Move(temporary,path,true); // Preserve a previously valid backup when replacing an invalid primary.
                }else File.Move(temporary,path);
            }finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        if(rotatedBackup)
        {
            var ctx = new StorageCleanupContext(
                OperatingSystem.IsMacOS() ? "mac" : "windows",
                Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory,
                Path.GetTempPath(),
                [directory],
                SettingsPath: settingsPath,
                RegistryPath: registryPath);
            StorageCleanupService.RegisterRecord(new BackupRegistryRecord
            {
                Category = BackupCategory.TaskCheckpoint,
                OwnerPlatform = OperatingSystem.IsMacOS() ? "mac" : "windows",
                SourceFeature = "任务自动存档",
                ScopeId = state.Options.WorkspaceId,
                ScopeLabel = state.Options.VmName,
                Path = Path.GetFullPath(backup),
                Role = "backup",
                OpState = BackupOpState.VerifiedSuccess,
                SizeBytes = File.Exists(backup) ? new FileInfo(backup).Length : 0
            }, registryPath);
            StorageCleanupService.RotateAfterVerifiedSuccess(BackupCategory.TaskCheckpoint, state.Options.WorkspaceId, ctx);
        }
    }
    public static SessionState Read(string path)
    {
        Paths.RejectLinks(path);
        var expectedDirectory=Path.GetDirectoryName(Path.GetFullPath(path))!.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        try{return DeserializeValid(path,expectedDirectory);}
        catch(Exception error) when(error is JsonException or IOException) {
            var backup=BackupPath(path);Paths.RejectLinks(backup);
            if(!File.Exists(backup))
                throw new SetupException("任务文件损坏或缺少必要配置，且未找到上一份自动保存。原虚拟机与磁盘保留。");
            SessionState recovered;
            try{recovered=DeserializeValid(backup,expectedDirectory);}
            catch(Exception backupErr) when(backupErr is JsonException or IOException){throw new SetupException("主存档与上一份自动保存均已损坏或缺少必要配置。");}
            recovered.Status="主存档异常，已从上一份自动保存恢复，请继续原任务";
            return recovered;
        }
    }
}
