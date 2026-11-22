using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ClaudeSetup;

public static class HostRuntime
{
    // Do not overwrite DLLs belonging to an already running management worker.
    // Both the management and verification lanes reuse the same verified files.
    public static void ExtractVerified(string archive, string destination)
    {
        HostPlatform.Environment.RestrictPrivateDirectory(destination); using var zip=ZipFile.OpenRead(archive);
        foreach(var entry in zip.Entries) {
            var path=Paths.Under(destination,entry.FullName.Replace('/',Path.DirectorySeparatorChar));
            if(entry.FullName.EndsWith('/')) {Directory.CreateDirectory(path);continue;}
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if(File.Exists(path)) {
                using var source=entry.Open(); using var current=File.OpenRead(path);
                if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(source),SHA256.HashData(current))) throw new SetupException("本机管理组件与已核验的归档不一致，请关闭本工具后检查任务私有目录中的管理运行库。");
            } else entry.ExtractToFile(path);
        }
    }
}
