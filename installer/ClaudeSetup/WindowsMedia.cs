using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed record WindowsImageChoice(int Index,string Name,string Architecture,string Version,string InstallationType,string Edition)
{
    public bool Supported => Index is >=1 and <=20 && Name.Length is >0 and <=160 && !Name.Any(char.IsControl) &&
        Regex.IsMatch(Name,@"\bWindows\s+11\b",RegexOptions.IgnoreCase) &&
        Architecture.Equals("x64",StringComparison.OrdinalIgnoreCase) && InstallationType.Equals("Client",StringComparison.OrdinalIgnoreCase) &&
        System.Version.TryParse(Version,out var version) && version.Major==10 && version.Build>=22000;
    public string Label => $"{Name} · {Architecture} · {Version}（索引 {Index}）";
}
public sealed record WindowsMediaIdentity(string IsoPath,string Sha256,long Bytes,List<WindowsImageChoice> Images)
{
    public void Validate()
    {
        if(!Path.IsPathFullyQualified(IsoPath)||!IsoPath.EndsWith(".iso",StringComparison.OrdinalIgnoreCase)||!Regex.IsMatch(Sha256,"^[a-f0-9]{64}$")||Bytes<1073741824||Images.Count is <1 or >20 ||
            Images.Select(x=>x.Index).Distinct().Count()!=Images.Count||Images.Any(x=>!x.Supported)) throw new SetupException("镜像身份记录不完整或含不支持的系统，请重新读取镜像版本。");
    }
}
public static class WindowsMedia
{
    private static string Record(string workspaceRoot) => Paths.Under(workspaceRoot, Path.Combine("private", "windows-media-identity.json"));
    internal static void RequireIsoFile(string isoPath)
    {
        if (!File.Exists(isoPath)) throw new SetupException("请选择已获得授权的 Windows 11 x64 ISO，或先完成镜像下载。");
        if (new FileInfo(isoPath).Length < 1024L * 1024 * 1024 || !isoPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new SetupException("所选文件不是预期大小的 ISO 镜像。");
    }
    public static WindowsMediaIdentity? Saved(SetupOptions o)
    {
        if(string.IsNullOrWhiteSpace(o.IsoPath)||!Path.IsPathFullyQualified(o.IsoPath))return null;
        var record = Record(o.Root);
        if (!File.Exists(record)) return null;
        var result=JsonFile.Read<WindowsMediaIdentity>(record);result.Validate();
        return Path.GetFullPath(result.IsoPath).Equals(Path.GetFullPath(o.IsoPath),StringComparison.OrdinalIgnoreCase)?result:null;
    }
    public static void RequireSelected(SetupOptions o,string actualHash)
    {
        var media=Saved(o);
        if(media is null||media.Sha256!=actualHash||!media.Images.Any(x=>x.Index==o.WindowsImageIndex))
            throw new HandoffException(new("读取并确认 Windows 镜像版本",["回到 Windows 镜像页，点击“读取镜像版本”，再选择与你的使用许可对应的版本。","当前程序只支持 Windows 11 x64；Windows 10、Server、ARM64、Linux 或无法识别的镜像不会继续安装。","核对所选镜像的官方条款及虚拟机使用授权，然后重新确认安装范围。"],"windows-media"));
    }
    public static async Task<WindowsMediaIdentity> Inspect(string workspaceRoot, string isoPath,
        IProgress<string> messages, IProgress<DownloadProgress> progress, CancellationToken token)
    {
        RequireIsoFile(isoPath);
        isoPath = Path.GetFullPath(isoPath);
        Paths.EnsureWorkspace(workspaceRoot);
        var privateDirectory = Paths.Under(workspaceRoot, "private");
        SecretStore.RestrictDirectory(privateDirectory);

        var resource = Catalog.Resources.Single(x => x.Id == "pwsh");
        using var downloader = new Downloader();
        messages.Report("正在准备已核验的镜像读取工具…");
        var archive = await downloader.Get(resource, Paths.Under(workspaceRoot, "downloads"), progress, token);
        var runtime = Paths.Under(privateDirectory, "media-pwsh");
        HostRuntime.ExtractVerified(archive, runtime);

        messages.Report("正在计算 ISO 文件指纹，大文件可能需要一些时间…");
        var hash = await Downloader.Hash(isoPath, token);
        var runId = Guid.NewGuid().ToString("N");
        using var resultFile = new PrivateFile(privateDirectory, "{}", ".json");
        using var input = new PrivateFile(privateDirectory,
            JsonSerializer.Serialize(new { isoPath, sha256 = hash, runId, outputPath = resultFile.FileName }), ".json");
        using var script = new PrivateFile(privateDirectory, Catalog.EmbeddedText("InspectWindowsMedia.ps1"), ".ps1");
        token.ThrowIfCancellationRequested();
        messages.Report("正在只读检查镜像中的系统版本、架构和安装索引，请确认管理员提示。");
        var exit = await WindowsHostEnvironment.ElevatedInstaller(Path.Combine(runtime, "pwsh.exe"),
            ["-NoLogo", "-NoProfile", "-File", script.FileName, "-InputPath", input.FileName]);
        token.ThrowIfCancellationRequested();

        using var json = JsonDocument.Parse(File.ReadAllText(resultFile.FileName));
        var result = json.RootElement;
        if (exit != 0 || !result.TryGetProperty("runId", out var run) || run.GetString() != runId ||
            !result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new SetupException("未能识别受支持的 Windows 11 x64 镜像。请确认管理员提示，并使用微软官方完整 ISO；本轮没有创建或安装虚拟机。");
        var media = result.GetProperty("identity").Deserialize<WindowsMediaIdentity>(JsonFile.Format)
            ?? throw new SetupException("镜像识别记录缺失。");
        media.Validate();
        if (media.Sha256 != hash || !Path.GetFullPath(media.IsoPath).Equals(isoPath, StringComparison.OrdinalIgnoreCase) ||
            media.Bytes != new FileInfo(isoPath).Length)
            throw new SetupException("镜像在检查时发生变化，请重新读取。");
        JsonFile.Write(Record(workspaceRoot), media);
        return media;
    }
}
