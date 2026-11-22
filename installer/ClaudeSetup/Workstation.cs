using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Claudome.Transport;

namespace ClaudeSetup;

public sealed class Workstation(ICommandRunner? commandRunner = null, string? testDirectory = null) : IVmBackend
{
    private Vix? api;
    private readonly ICommandRunner runner = commandRunner ?? new CommandRunner();
    public static string? DirectoryPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\VMware, Inc.\VMware Workstation");
        var candidates = new[] { key?.GetValue("InstallPath")?.ToString(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VMware", "VMware Workstation"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VMware", "VMware Workstation") };
        return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, "vmware.exe")));
    }
    private static string Config(SessionState s) => Paths.Under(s.Options.VmDirectory, s.Options.VmName + ".vmx");
    public async Task Prepare(SetupOptions o, CancellationToken token)
    {
        if (DirectoryPath() is not null) return;
        var resource = Catalog.Resources.Single(r => r.Id == "pwsh"); var archive = Paths.Under(o.Cache, resource.FileName);
        if (await Downloader.Hash(archive, token) != resource.Sha256) throw new SetupException("PowerShell 组件校验失败。");
        var runtime = Paths.Under(o.Private, "host-pwsh"); SecretStore.RestrictDirectory(o.Private); HostRuntime.ExtractVerified(archive, runtime);
        // Broadcom's current download flow can require the customer's own sign-in.
        // Resume without asking for URLs/hashes: find a downloaded official signed installer.
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var candidate = Directory.Exists(downloads) ? Directory.EnumerateFiles(downloads, "VMware*Workstation*.exe").OrderByDescending(File.GetLastWriteTimeUtc).Take(20).FirstOrDefault() : null;
        if (candidate is null) {
            Process.Start(new ProcessStartInfo("https://knowledge.broadcom.com/external/article/368734/download-desktop-hypervisor-workstation.html") { UseShellExecute = true });
            throw new HandoffException(new("获取 VMware 官方安装器", ["程序已打开 Broadcom 官方入口；如要求登录，请使用你自己的账号完成登录及许可确认。", "将 Windows 的 Workstation Pro 完整安装器下载到默认“下载”文件夹，然后回到助手继续。", "程序会自动识别安装文件、核验发布者数字签名并安装，无需填写链接或校验值。"], "workstation-download"));
        }
        using var verify = new PrivateFile(o.Private, "param([string]$Installer)\n$s=Get-AuthenticodeSignature -LiteralPath $Installer\n@{valid=($s.Status -eq 'Valid');subject=$s.SignerCertificate.Subject}|ConvertTo-Json -Compress", ".ps1");
        using var signature = JsonDocument.Parse(await runner.Run(Path.Combine(runtime, "pwsh.exe"), ["-NoLogo", "-NoProfile", "-File", verify.FileName, "-Installer", candidate], token));
        if (!signature.RootElement.GetProperty("valid").GetBoolean() || !(signature.RootElement.GetProperty("subject").GetString() ?? "").Contains("VMware", StringComparison.OrdinalIgnoreCase) && !(signature.RootElement.GetProperty("subject").GetString() ?? "").Contains("Broadcom", StringComparison.OrdinalIgnoreCase)) throw new SetupException("下载文件的发布者数字签名未通过 VMware / Broadcom 校验，未安装。请重新从官方入口获取。");
        var local = Paths.Under(o.Cache, "VMware-verified-setup.exe"); Paths.RejectLinks(candidate);
        File.Copy(candidate, local, true);
        var hash = await Downloader.Hash(local, token); if (hash != await Downloader.Hash(candidate, token)) throw new SetupException("安装器复制校验失败。");
        JsonFile.Write(Paths.Under(o.Private, "workstation-installer-verification.json"), new { sha256 = hash, signatureValid = true, publisher = signature.RootElement.GetProperty("subject").GetString(), integrityType = "publisher signature; local fingerprint", dateUtc = DateTime.UtcNow });
        var exit = await WindowsHostEnvironment.ElevatedInstaller(local, ["/s", "/v", "/qn EULAS_AGREED=1 REBOOT=ReallySuppress"]);
        if (exit is 3010 or 1641) throw new SetupException("Workstation 安装要求重启。请保存工作并自行重启，再载入任务继续。");
        if (exit != 0 || DirectoryPath() is null) throw new SetupException("Workstation 尚未安装完成，请查看管理员提示后继续。");
    }
    public Task<string> EnsureMachine(SessionState s, CancellationToken token)
    {
        var marker = Paths.Under(s.Options.VmDirectory, ".claude-workspace.json");
        if (!string.IsNullOrEmpty(s.VmId)) {
            if (!File.Exists(marker)) throw new SetupException("VMware 任务标识缺失，未接管现有虚拟机。");
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            if (json.RootElement.GetProperty("vmId").GetString() != s.VmId || json.RootElement.GetProperty("workspaceId").GetString() != s.Options.WorkspaceId) throw new SetupException("VMware 任务身份不匹配。");
        } else {
            if (Directory.Exists(s.Options.VmDirectory)) throw new SetupException("同名 VMware 目录已存在，未覆盖。请改名或载入原任务。");
            Directory.CreateDirectory(s.Options.VmDirectory); s.VmId = Guid.NewGuid().ToString();
            JsonFile.Write(marker, new { vmId = s.VmId, workspaceId = s.Options.WorkspaceId }); s.Save();
        }
        return Task.FromResult(s.VmId);
    }
    public async Task Configure(SessionState s, CancellationToken token)
    {
        await EnsureMachine(s, token);
        await WorkstationProvisioning.Configure(s, testDirectory ?? DirectoryPath() ?? throw new SetupException("Workstation 尚未安装。"), runner, token);
    }
    public async Task InstallWindows(SessionState s, Secrets secrets, CancellationToken token)
    {
        var seed = Paths.Under(s.Options.Private, "windows-seed.iso");
        if (!File.Exists(seed)) {
            var folder = Paths.Under(s.Options.Private, "windows-seed"); SecretStore.RestrictDirectory(folder);
            File.WriteAllText(Paths.Under(folder, "Autounattend.xml"), WindowsSeed.Answer(s.Options, secrets.VmPassword), new UTF8Encoding(false));
            var bootstrap = Paths.Under(folder, "ClaudeBootstrap"); Directory.CreateDirectory(bootstrap);
            File.WriteAllText(Paths.Under(bootstrap, "Initialize.cmd"), Catalog.EmbeddedText("WorkstationInitialize.cmd"), new UTF8Encoding(false));
            JsonFile.Write(Paths.Under(bootstrap, "identity.json"), new { vmId = s.VmId, workspaceId = s.Options.WorkspaceId });
            var pwsh = Catalog.Resources.Single(r => r.Id == "pwsh"); File.Copy(Path.Combine(s.Options.Cache, pwsh.FileName), Paths.Under(bootstrap, "pwsh.zip"), true);
            await Task.Run(() => WindowsSeed.CreateIso(folder, seed), token);
        }
        if (!s.Options.VmwareSecurityConfirmed || !WorkstationBoundary.SecurityReady(WorkstationBoundary.Parse(File.ReadAllText(Config(s))))) {
            s.Options.VmwareSecurityConfirmed = false; s.Save();
            await Show(s, token, false);
            throw new HandoffException(new("首次确认 VMware 加密与虚拟 TPM", ["在已打开的 VMware 窗口中保持虚拟机关机，打开“虚拟机设置 → 选项 → 访问控制 → 加密”。", "选择仅加密支持 TPM 所需文件，设置并自行保存加密密码；可选择让 VMware 记住密码。", "在硬件中添加“可信平台模块（TPM）”，确认 UEFI 与安全启动已开启，保存设置。", "不要启动其他虚拟机。完成后点击下方“已完成安全设置，继续”，其余安装由程序继续。"], "workstation-security"));
        }
        Machine.CheckRun(s.Options, true); await Show(s, token, true);
    }
    private async Task<Vix> Api(SessionState s, CancellationToken token)
    {
        await EnsureMachine(s, token); api ??= new(DirectoryPath() ?? throw new SetupException("未检测到 Workstation。")); await api.Open(Config(s), token); return api;
    }
    private string VmrunExecutable => Path.Combine(testDirectory ?? DirectoryPath() ??
        throw new SetupException("Workstation 尚未安装。"), "vmrun.exe");

    internal async Task ShutdownOriginal(SessionState state, CancellationToken token)
    {
        await EnsureMachine(state, token);
        if (!await IsRunning(state, token)) return;
        await runner.Run(VmrunExecutable, ["stop", Config(state), "soft"], token, 75);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!await IsRunning(state, token)) return;
            await Task.Delay(1000, token);
        }
        throw new SetupException("尚未确认 VMware 来宾已关机。请检查来宾操作系统和 VMware Tools。");
    }

    public async Task<bool> IsRunning(SessionState s, CancellationToken token)
    {
        // Listing does not need a guest login or decryption password. A user may
        // have pressed Play during the security handoff; never infer power state
        // from the confirmation checkbox before rewriting the VMX.
        var list=await runner.Run(VmrunExecutable,["list"],token,20); var expected=Path.GetFullPath(Config(s));
        return list.Split('\n').Select(x=>x.Trim().Trim('"')).Where(Path.IsPathFullyQualified).Any(x=>Path.GetFullPath(x).Equals(expected,StringComparison.OrdinalIgnoreCase));
    }
    private Task Show(SessionState s, CancellationToken token, bool startVm)
    {
        token.ThrowIfCancellationRequested(); var start = new ProcessStartInfo(Path.Combine(DirectoryPath()!, "vmware.exe")) { UseShellExecute = true };
        if (startVm) start.ArgumentList.Add("-x"); start.ArgumentList.Add(Config(s)); Process.Start(start); return Task.CompletedTask;
    }
    public async Task Open(SessionState s, CancellationToken token)
    {
        await EnsureMachine(s, token); if (!await IsRunning(s, token)) Machine.CheckVmStart(s.Options); await Show(s, token, true);
        await PlatformConsole.Confirm(Path.Combine(DirectoryPath()!,"vmware.exe"),s.Options.VmName,()=>IsRunning(s,token),token);
    }
    private async Task<Vix> Login(SessionState s, Secrets secrets, CancellationToken token)
    {
        var vix = await Api(s, token); await vix.Login(s.Options.UserName, secrets.VmPassword, token); return vix;
    }
    public async Task<bool> GuestReady(SessionState s, Secrets secrets, CancellationToken token)
    {
        try {
            var vix = await Login(s, secrets, token); var file = Paths.Under(s.Options.Private, "workstation-ready.json");
            await vix.Copy(@"C:\ProgramData\ClaudeBootstrap\ready.json", file, false, token);
            using var result = JsonDocument.Parse(File.ReadAllText(file)); return result.RootElement.GetProperty("ready").GetBoolean();
        } catch (SetupException) { return false; }
    }
    public async Task CleanupWindowsMedia(SessionState s, CancellationToken token)
    {
        var seed = Paths.Under(s.Options.Private, "windows-seed.iso");
        if (!File.Exists(seed)) return;
        var vix = await Api(s, token);
        const string cleanup = "$ErrorActionPreference='Stop';$shell=New-Object -ComObject Shell.Application;Get-CimInstance Win32_CDROMDrive|Where-Object {$_.VolumeName -eq 'CLAUDESETUP'}|ForEach-Object {$shell.NameSpace(17).ParseName($_.Drive).InvokeVerb('Eject')};@('C:\\Windows\\Panther\\unattend.xml','C:\\Windows\\Panther\\Unattend\\unattend.xml')|ForEach-Object {if(Test-Path -LiteralPath $_){Remove-Item -LiteralPath $_ -Force}}";
        await vix.Run(@"C:\ProgramData\ClaudeBootstrap\pwsh\pwsh.exe", ["-NoLogo", "-NoProfile", "-Command", cleanup], token, 60);
        var xml = Paths.Under(s.Options.Private, "windows-seed/Autounattend.xml");
        if (File.Exists(xml)) File.Delete(xml);
        try { File.Delete(seed); }
        catch (IOException) { throw new HandoffException(new("弹出临时安装介质", ["在 VMware 中断开标为 CLAUDESETUP 的光盘，并取消该光驱“启动时连接”。", "这是首次安装的临时应答介质，包含可还原的虚拟机密码。请处理后回到助手继续，程序会重试清理。"], "workstation-media")); }
    }
    public async Task<string> GuestRun(SessionState s, Secrets secrets, string executable, IEnumerable<string> args, CancellationToken token, int seconds = 300)
    {
        var vix = await Login(s, secrets, token); await vix.Run(executable, args, token, seconds); return "completed";
    }
    public async Task<bool> GuestFileExists(SessionState s, Secrets secrets, string file, CancellationToken token) =>
        await (await Login(s, secrets, token)).FileExists(file, token);
    public async Task CopyTo(SessionState s, Secrets secrets, string local, string destination, CancellationToken token)
    {
        var vix = await Login(s, secrets, token); await vix.Copy(local, destination.EndsWith('\\') ? destination + Path.GetFileName(local) : destination, true, token);
    }
    public async Task CopyFrom(SessionState s, Secrets secrets, string guest, string destination, CancellationToken token)
    {
        var vix = await Login(s, secrets, token); await vix.Copy(guest, destination, false, token);
    }
    public async Task<RecoveryCleanupResult> CleanupRecovery(SessionState s, Secrets secrets, CancellationToken token)
    {
        if (!await GuestReady(s,secrets,token)) throw new SetupException("新 Windows 尚未就绪，暂不删除旧盘。");
        var path=Paths.Under(s.Options.Private,"cleanup-guest-identity.json");
        await CopyFrom(s,secrets,@"C:\ProgramData\ClaudeBootstrap\identity.json",path,token);
        using var identity=JsonDocument.Parse(File.ReadAllText(path));
        if(identity.RootElement.GetProperty("vmId").GetString()!=s.VmId || identity.RootElement.GetProperty("workspaceId").GetString()!=s.Options.WorkspaceId) throw new SetupException("新系统身份不匹配，未清理旧盘。");
        var point=Paths.Under(s.Options.VmDirectory,".vmware-install.json");
        if(!File.Exists(point)||JsonFile.Read<WorkstationCheckpoint>(point).PreservedDisks.Count==0)return new(0,0);
        return RecoveryCleanup.Workstation(s,VmInventory.RegisteredWorkstationPaths());
    }
    public ValueTask DisposeAsync() { api?.Dispose(); return ValueTask.CompletedTask; }
    public async Task VerifyOffline(SessionState state, CancellationToken token)
    {
        await EnsureMachine(state, token); var settings = WorkstationBoundary.Parse(File.ReadAllText(Config(state))); WorkstationBoundary.CheckOffline(settings);
    }
    public async Task PrepareBoundary(SessionState state, BridgeSpec spec, CancellationToken token)
    {
        await EnsureMachine(state, token); if (spec.VmId != state.VmId || spec.WorkspaceId != state.Options.WorkspaceId) throw new SetupException("VMware 通道不属于当前任务。");
        var path=Config(state); var settings=WorkstationBoundary.Parse(File.ReadAllText(path));
        var saved=Directory.EnumerateFiles(state.Options.VmDirectory,"*.vmss").Any();
        if (!saved && !await IsRunning(state, token)) {
            foreach(var item in WorkstationBoundary.Required(spec)) settings[item.Key]=item.Value;
            foreach(var key in settings.Keys.Where(x=>System.Text.RegularExpressions.Regex.IsMatch(x,"^ethernet[0-9]+\\.present$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray()) settings[key]="FALSE";
            WorkstationBoundary.Write(path,settings);
        }
        await VerifyBoundary(state,spec,token);
    }
    public async Task VerifyBoundary(SessionState state, BridgeSpec spec, CancellationToken token)
    {
        await EnsureMachine(state, token); WorkstationBoundary.Check(WorkstationBoundary.Parse(File.ReadAllText(Config(state))),spec);
    }
}
