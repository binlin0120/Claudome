using System.IO;
using System.Text.Json;

namespace ClaudeSetup;

public static class TaskCheckpointStore
{
    private static readonly object Sync=new();
    public static string BackupPath(string path)=>path+".previous.json";
    public static void Write(SessionState state)
    {
        var path=state.Options.StatePath;Paths.RejectLinks(path);
        var directory=Path.GetDirectoryName(Path.GetFullPath(path))!;Directory.CreateDirectory(directory);
        var backup=BackupPath(path);Paths.RejectLinks(backup);
        lock(Sync) {
            var temporary=path+"."+Guid.NewGuid().ToString("N")+".new";
            try {
                var bytes=JsonSerializer.SerializeToUtf8Bytes(state,JsonFile.Format);
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){output.Write(bytes);output.Flush(true);}
                if(File.Exists(path)) {
                    var valid=false;
                    try{var previous=JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path),JsonFile.Format);valid=previous is not null&&previous.Options.WorkspaceId==state.Options.WorkspaceId&&Path.GetFullPath(previous.Options.Root).Equals(Path.GetFullPath(state.Options.Root),StringComparison.OrdinalIgnoreCase);}
                    catch(JsonException){}catch(IOException){}
                    if(valid)File.Replace(temporary,path,backup,true);
                    else File.Move(temporary,path,true); // Preserve a previously valid backup.
                }else File.Move(temporary,path);
            }finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
    public static SessionState Read(string path)
    {
        Paths.RejectLinks(path);
        try{return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path),JsonFile.Format)??throw new JsonException();}
        catch(Exception error) when(error is JsonException or IOException) {
            var backup=BackupPath(path);Paths.RejectLinks(backup);
            if(!File.Exists(backup))throw new SetupException("任务文件不完整，且未找到上一份自动保存。原虚拟机与磁盘保留。");
            var state=JsonSerializer.Deserialize<SessionState>(File.ReadAllText(backup),JsonFile.Format)??throw new SetupException("上一份自动保存不完整。");
            if(!Path.GetFullPath(state.Options.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(Path.GetDirectoryName(Path.GetFullPath(path))!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))throw new SetupException("自动保存的任务目录不匹配，未恢复。");
            state.Status="已从上一份自动保存恢复，请继续原任务";
            return state;
        }
    }
}
