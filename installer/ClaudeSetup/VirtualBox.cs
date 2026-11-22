using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public enum VirtualBoxGuestStage
{
    WaitingForAdditions,
    NotReadyYet,
    Ready,
    AuthMismatch,
    SetupStatusUnreadable
}

public sealed partial class VirtualBox(ICommandRunner runner, string? testExecutable = null, IVmConsole? console = null, Action<SetupOptions>? startCheck = null) : IVmBackend
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public async Task Prepare(SetupOptions options, CancellationToken token)
    {
        if (Machine.VBoxPath() is null) {
            var resource = Catalog.Resources.Single(r => r.Id == "vbox");
            var code = await WindowsHostEnvironment.ElevatedInstaller(System.IO.Path.Combine(options.Cache, resource.FileName), ["-silent", "-msiparams", "ADDLOCAL=VBoxApplication", "VBOX_INSTALLDESKTOPSHORTCUT=0", "VBOX_START=0", "REBOOT=ReallySuppress"]);
            if (code is 3010 or 1641) throw new SetupException("VirtualBox 已安装，需要重启。请保存工作并自行重启，再载入原任务继续。");
            if (code != 0 || Machine.VBoxPath() is null) throw new SetupException("VirtualBox 安装未完成，请检查管理员提示后继续。");
        }
        if (!(await Run(token, "--version")).StartsWith("7.2.")) throw new SetupException("当前适配 VirtualBox 7.2 系列；未自动替换你的其他版本。");
    }
    public async Task<bool> IsRunning(SessionState state, CancellationToken token) => (await Run(token, "showvminfo", state.VmId, "--machinereadable")).Contains("VMState=\"running\"");
    public string Executable => testExecutable ?? Machine.VBoxPath() ?? throw new SetupException("VirtualBox 尚未安装，或系统需要重启才能完成安装。");
    public static List<(string Name, string Id)> ParseList(string output)
    {
        var list = new List<(string, string)>();
        foreach (Match m in Regex.Matches(output, "^\"(?<name>.*)\" \\{(?<id>[0-9a-fA-F-]{36})\\}\\s*$", RegexOptions.Multiline)) list.Add((m.Groups["name"].Value, m.Groups["id"].Value));
        return list;
    }
    public Task<string> Run(CancellationToken token, params string[] args) => runner.Run(Executable, args, token);
    public async Task<string> EnsureMachine(SessionState state, CancellationToken token)
    {
        var o = state.Options;
        var inventory = ParseList(await Run(token, "list", "vms"));
        if (!string.IsNullOrEmpty(state.VmId))
        {
            if (!inventory.Any(v => v.Id.Equals(state.VmId, StringComparison.OrdinalIgnoreCase))) throw new SetupException("记录中的虚拟机已不存在，请新建独立任务，不会覆盖其他虚拟机。");
            var info = await Run(token, "showvminfo", state.VmId, "--machinereadable");
            var actual = VmInventory.VBoxValue(info, "CfgFile");
            var expected = Path.Combine(o.VmDirectory, o.VmName + ".vbox");
            if (string.IsNullOrEmpty(actual) || !Path.GetFullPath(actual).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) throw new SetupException("虚拟机目录与任务记录不一致，已停止接管。");
            return state.VmId;
        }
        if (inventory.Any(v => v.Name.Equals(o.VmName, StringComparison.OrdinalIgnoreCase)) || Directory.Exists(o.VmDirectory)) throw new SetupException("同名虚拟机或目录已经存在。请改名，或载入原任务状态继续；不会覆盖现有环境。");
        var id = Guid.NewGuid().ToString();
        await Run(token, "createvm", "--name", o.VmName, "--uuid", id, "--ostype", "Windows11_64", "--basefolder", Paths.Under(o.Root, "machines"), "--register");
        state.VmId = id; state.Save();
        return id;
    }
    public async Task Configure(SessionState state, CancellationToken token)
    {
        var o = state.Options; var id = state.VmId;
        await Run(token, "modifyvm", id, "--memory", o.MemoryMb.ToString(), "--cpus", o.CpuCount.ToString(), "--vram", "128", "--graphicscontroller", "vboxsvga", "--firmware", "efi64", "--tpm-type", "2.0", "--apic", "on", "--x86-x2apic", "on", "--ioapic", "on", "--x86-hpet", "off", "--paravirt-provider", "default", "--nic1", "none", "--clipboard-mode", "disabled", "--drag-and-drop", "disabled", "--audio-enabled", "off");
        await Run(token, "modifyvm", id, "--nat-localhostreachable1", "off", "--nat-dns-pass-domain1", "off", "--clipboard-file-transfers", "disabled", "--usb-ohci", "off", "--usb-ehci", "off", "--usb-xhci", "on", "--mouse", "usbtablet", "--vrde", "off", "--boot1", "disk", "--boot2", "dvd", "--boot3", "none", "--boot4", "none");
        for (var i = 2; i <= 8; i++) await Run(token, "modifyvm", id, "--nic" + i, "none");
        var info = await Run(token, "showvminfo", id, "--machinereadable");
        if (!File.Exists(Path.Combine(o.VmDirectory, o.VmName + ".nvram"))) await Run(token, "modifynvram", id, "inituefivarstore");
        await Run(token, "modifynvram", id, "enrollmssignatures");
        await Run(token, "modifynvram", id, "enrollorclpk");
        await Run(token, "modifynvram", id, "secureboot", "--enable");
        if (!info.Contains("storagecontrollername0=\"SATA\"")) await Run(token, "storagectl", id, "--name", "SATA", "--add", "sata", "--controller", "IntelAhci", "--portcount", "4");
        var disk = Paths.Under(o.VmDirectory, "Windows11.vdi");
        if (!File.Exists(disk)) await Run(token, "createmedium", "disk", "--filename", disk, "--size", (o.DiskGb * 1024).ToString(), "--format", "VDI");
        await Run(token, "storageattach", id, "--storagectl", "SATA", "--port", "0", "--device", "0", "--type", "hdd", "--medium", disk);
        for (var port = 1; port <= 3; port++)
            await Run(token, "storageattach", id, "--storagectl", "SATA", "--port", port.ToString(), "--device", "0", "--type", "dvddrive", "--medium", "emptydrive");
    }
    public async Task InstallWindows(SessionState state, Secrets secrets, CancellationToken token)
    {
        Machine.CheckRun(state.Options, true);
        if (secrets.VmPassword.Length < 10 || secrets.VmPassword.Contains('\r') || secrets.VmPassword.Contains('\n') || secrets.VmPassword.Contains('\0')) throw new SetupException("虚拟机密码至少 10 位，且不能包含换行或空字符。");
        var o = state.Options;
        // This private folder holds Windows' UEFI-compatible unattended seed ISO until setup completes.
        var auxiliary = Paths.Under(o.Private, "unattended"); SecretStore.RestrictDirectory(auxiliary);
        var seedIso = Paths.Under(auxiliary, "windows-seed.iso");
        if (!File.Exists(seedIso))
        {
            var stageDir = Paths.Under(auxiliary, "seed-" + Guid.NewGuid().ToString("N"));
            SecretStore.RestrictDirectory(stageDir);
            try
            {
                File.WriteAllText(Paths.Under(stageDir, "Autounattend.xml"), WindowsSeed.Answer(o, secrets.VmPassword), new UTF8Encoding(false));
                var bootstrapDir = Paths.Under(stageDir, "ClaudeBootstrap");
                Directory.CreateDirectory(bootstrapDir);
                File.WriteAllText(Paths.Under(bootstrapDir, "Initialize.cmd"), Catalog.EmbeddedText("VBoxSeedBootstrap.cmd"), new UTF8Encoding(false));
                await Task.Run(() => WindowsSeed.CreateIso(stageDir, seedIso), token);
            }
            finally
            {
                try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
            }
        }
        var guestAdditions = Path.Combine(Path.GetDirectoryName(Executable) ?? "", "VBoxGuestAdditions.iso");
        await Run(token, "storageattach", state.VmId, "--storagectl", "SATA", "--port", "1", "--device", "0", "--type", "dvddrive", "--medium", o.IsoPath, "--forceunmount");
        if (File.Exists(guestAdditions))
            await Run(token, "storageattach", state.VmId, "--storagectl", "SATA", "--port", "2", "--device", "0", "--type", "dvddrive", "--medium", guestAdditions, "--forceunmount");
        await Run(token, "storageattach", state.VmId, "--storagectl", "SATA", "--port", "3", "--device", "0", "--type", "dvddrive", "--medium", seedIso, "--forceunmount");
        await Run(token, "startvm", state.VmId, "--type", "headless");
    }
    public async Task<bool> EnsureGuestAdditionsMounted(SessionState state, string info, CancellationToken token)
    {
        var vboxDir = Path.GetDirectoryName(Executable);
        if (string.IsNullOrEmpty(vboxDir)) return false;
        var additionsIso = Path.Combine(vboxDir, "VBoxGuestAdditions.iso");
        if (!File.Exists(additionsIso)) return false;

        var slots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(info, "^\"SATA-(?<port>[1-3])-0\"=\"(?<path>[^\"]+)\"", RegexOptions.Multiline))
            slots[match.Groups["port"].Value] = match.Groups["path"].Value.Replace("\\\\", "\\");

        if (slots.Values.Any(p => p.EndsWith("VBoxGuestAdditions.iso", StringComparison.OrdinalIgnoreCase)))
            return true;

        var candidates = new List<string>();
        foreach (var port in new[] { "2", "3" })
        {
            if (slots.TryGetValue(port, out var cur) && (cur == "emptydrive" || cur.EndsWith(".viso", StringComparison.OrdinalIgnoreCase)))
                candidates.Add(port);
        }
        foreach (var port in new[] { "3", "2", "1" })
        {
            if (!candidates.Contains(port)) candidates.Add(port);
        }

        foreach (var port in candidates)
        {
            try
            {
                await Run(token, "storageattach", state.VmId, "--storagectl", "SATA", "--port", port, "--device", "0", "--type", "dvddrive", "--medium", additionsIso, "--forceunmount");
                return true;
            }
            catch (SetupException) { }
        }
        return false;
    }
    public async Task<(VirtualBoxGuestStage Stage, bool AdditionsMounted)> ProbeGuestStage(SessionState state, Secrets secrets, CancellationToken token,
        Action? checkingLogin = null)
    {
        var info = await Run(token, "showvminfo", state.VmId, "--machinereadable");
        if (VirtualBoxDisplayFit.ReadAdditionsRunLevel(info) < 2)
        {
            var mounted = await EnsureGuestAdditionsMounted(state, info, token);
            return (VirtualBoxGuestStage.WaitingForAdditions, mounted);
        }
        checkingLogin?.Invoke();
        return (await ReadWindowsSetupStatus(state, secrets, token), true);
    }

    private async Task<VirtualBoxGuestStage> ReadWindowsSetupStatus(SessionState state, Secrets secrets, CancellationToken token)
    {
        try
        {
            // Use the same profiled Guest Control launch as account synchronization.
            // Signing in is implicit in running the command, so no separate whoami probe is needed.
            var output = await RunGuestAs(state, state.Options.UserName, secrets.VmPassword, @"C:\Windows\System32\cmd.exe",
                ["/d", "/c", @"chcp 65001 >nul & C:\Windows\System32\reg.exe query HKLM\SYSTEM\Setup /v SystemSetupInProgress"],
                token, 20, loadProfile: true, microsoftAddress: MicrosoftLoginAddress(state, secrets));
            var value = Regex.Match(output,
                @"^[ \t]*SystemSetupInProgress[ \t]+REG_DWORD[ \t]+0x(?<flag>[a-f0-9]{1,8})[ \t]*\r?$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (!value.Success) return VirtualBoxGuestStage.SetupStatusUnreadable;
            return value.Groups["flag"].Value.All(c => c == '0')
                ? VirtualBoxGuestStage.Ready : VirtualBoxGuestStage.NotReadyYet;
        }
        catch (SetupException ex)
        {
            if (GuestLoginRejected(ex))
                return VirtualBoxGuestStage.AuthMismatch;
            return VirtualBoxGuestStage.SetupStatusUnreadable;
        }
    }
    public async Task<bool> GuestReady(SessionState state, Secrets secrets, CancellationToken token) =>
        await ReadWindowsSetupStatus(state, secrets, token) == VirtualBoxGuestStage.Ready;
    public async Task CleanupWindowsMedia(SessionState state, CancellationToken token)
    {
        var auxiliary = Paths.Under(state.Options.Private, "unattended");
        if (!Directory.Exists(auxiliary)) return;
        var info = await Run(token, "showvminfo", state.VmId, "--machinereadable");
        foreach (Match match in Regex.Matches(info, "^\"SATA-(?<port>[1-3])-0\"=\"(?<path>[^\"]+)\"", RegexOptions.Multiline))
        {
            var medium = match.Groups["path"].Value.Replace("\\\\", "\\");
            if (medium is "none" or "emptydrive") continue;
            await Run(token, "storageattach", state.VmId, "--storagectl", "SATA", "--port", match.Groups["port"].Value, "--device", "0", "--type", "dvddrive", "--medium", "emptydrive", "--forceunmount");
            if (Path.IsPathFullyQualified(medium) && Path.GetFullPath(medium).StartsWith(auxiliary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                try { await Run(token, "closemedium", "dvd", medium); } catch (SetupException) { }
            }
        }
        foreach (Match match in Regex.Matches(info, "^\"Floppy-(?<port>[0-1])-(?<device>[0-1])\"=\"(?<path>[^\"]+)\"", RegexOptions.Multiline))
        {
            var medium = match.Groups["path"].Value.Replace("\\\\", "\\");
            if (medium is "none" or "emptydrive") continue;
            await Run(token, "storageattach", state.VmId, "--storagectl", "Floppy", "--port", match.Groups["port"].Value, "--device", match.Groups["device"].Value, "--type", "fdd", "--medium", "emptydrive", "--forceunmount");
            if (Path.IsPathFullyQualified(medium) && Path.GetFullPath(medium).StartsWith(auxiliary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                try { await Run(token, "closemedium", "floppy", medium); } catch (SetupException) { }
            }
        }
        Paths.RejectLinks(auxiliary);
        if (Directory.EnumerateFileSystemEntries(auxiliary, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new SetupException("安装临时目录含有链接，已停止清理。");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try { Directory.Delete(auxiliary, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 3) await Task.Delay(350, token);
            }
        }
    }
    private static bool GuestLoginRejected(SetupException error) =>
        new[] { "VERR_AUTHENTICATION_FAILURE", "VERR_LOGON_FAILURE", "VERR_ACCOUNT_RESTRICTED", "VERR_PASSWORD_EXPIRED" }
            .Any(code => error.Message.Contains(code, StringComparison.Ordinal));

    internal async Task<string> VerifyGuestLogin(SessionState state, string userName, string guestPassword, CancellationToken token,
        string microsoftAddress = "")
    {
        await EnsureMachine(state, token);
        var info = await Run(token, "showvminfo", state.VmId, "--machinereadable");
        if (VmInventory.VBoxValue(info, "VMState") != "running")
            throw new SetupException("请先打开本任务的来宾 Windows，再同步账号。");
        if (VirtualBoxDisplayFit.ReadAdditionsRunLevel(info) < 2)
            throw new SetupException("VirtualBox 来宾工具暂未就绪，请在来宾安装或启动 Guest Additions 后重试。");

        try
        {
            var verifyAlias = !string.IsNullOrEmpty(microsoftAddress);
            var output = await RunGuestAs(state, userName, guestPassword, @"C:\Windows\System32\cmd.exe",
                ["/d", "/c", verifyAlias ? "chcp 65001 >nul & whoami & set LOCALAPPDATA" : "chcp 65001 >nul & set LOCALAPPDATA"],
                token, 20, loadProfile: true, microsoftAddress: microsoftAddress);
            if (verifyAlias && !output.Split('\n').Select(line => line.Trim()).Any(line =>
            {
                var separator = line.IndexOf('\\');
                return separator > 0 && line[(separator + 1)..].Equals(userName, StringComparison.OrdinalIgnoreCase);
            }))
                throw new SetupException("微软账号已通过验证，但它在来宾中的实际用户名与填写值不同。请核对 whoami。");
            const string key = "LOCALAPPDATA=";
            var paths = output.Split('\n').Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                .Select(line => line[key.Length..]).ToArray();
            if (paths.Length != 1 || !SetupOptions.IsGuestLocalAppData(paths[0]))
                throw new SetupException("来宾接受了登录，但未找到可用的 Windows 用户配置目录。请先用这个账号登录来宾桌面，再重试。");
            return paths[0];
        }
        catch (SetupException error) when (GuestLoginRejected(error))
        {
            throw new SetupException(string.IsNullOrEmpty(microsoftAddress)
                ? "来宾拒绝登录。如使用微软账号登录 Windows，请勾选“使用微软账号登录”并填写绑定邮箱。"
                : "Windows 拒绝了此微软账号的后台登录。请确认该邮箱的密码可用于来宾 Windows 登录；仅支持 Windows Hello 的登录方式无法用于同步。");
        }
    }

    public Task<string> GuestRun(SessionState state, Secrets secrets, string executable, IEnumerable<string> args, CancellationToken token, int seconds = 300) =>
        RunGuestAs(state, state.Options.UserName, secrets.VmPassword, executable, args, token, seconds,
            loadProfile: state.Options.UsesMicrosoftGuestAccount, microsoftAddress: MicrosoftLoginAddress(state, secrets));

    private static string MicrosoftLoginAddress(SessionState state, Secrets secrets)
    {
        if (!state.Options.UsesMicrosoftGuestAccount) return "";
        if (!SetupOptions.IsMicrosoftGuestAddress(secrets.VmMicrosoftAddress))
            throw new SetupException("请在该来宾桌面重新同步微软账号邮箱和密码。");
        return secrets.VmMicrosoftAddress;
    }

    private static List<string> GuestControlArguments(SessionState state, string operation, string userName,
        string microsoftAddress, string passwordFile)
    {
        var args = new List<string> { "guestcontrol", state.VmId, operation };
        if (string.IsNullOrEmpty(microsoftAddress)) args.AddRange(["--username", userName]);
        else
        {
            if (!SetupOptions.IsMicrosoftGuestAddress(microsoftAddress)) throw new SetupException("请填写有效的微软账号邮箱。");
            // VBoxService appends a supplied domain using the user@domain form.
            // A Microsoft email is already a fully qualified sign-in name.
            args.AddRange(["--username", microsoftAddress]);
        }
        args.AddRange(["--passwordfile", passwordFile]);
        return args;
    }

    private async Task<string> RunGuestAs(SessionState state, string userName, string guestPassword, string executable,
        IEnumerable<string> args, CancellationToken token, int seconds, bool loadProfile = false, string microsoftAddress = "")
    {
        using var passwordFile = new PrivateFile(state.Options.Private, guestPassword);
        var command = GuestControlArguments(state, "run", userName, microsoftAddress, passwordFile.FileName);
        command.AddRange(["--exe", executable, "--wait-stdout", "--wait-stderr", "--timeout", (seconds * 1000).ToString()]);
        if (loadProfile) command.Add("--profile");
        // VBoxManage already uses --exe as argv[0]. After "--" only pass arguments for the guest program.
        command.Add("--");
        command.AddRange(args);
        return await runner.Run(Executable, command, token, seconds + 15);
    }
    public async Task EnsureGuestDirectory(SessionState state, Secrets secrets, string directory, CancellationToken token)
    {
        using var password = new PrivateFile(state.Options.Private, secrets.VmPassword);
        var arguments = GuestControlArguments(state, "mkdir", state.Options.UserName,
            MicrosoftLoginAddress(state, secrets), password.FileName);
        arguments.AddRange(["--parents", directory]);
        await Run(token, arguments.ToArray());
    }

    public async Task<bool> GuestFileExists(SessionState state, Secrets secrets, string file, CancellationToken token)
    {
        using var password = new PrivateFile(state.Options.Private, secrets.VmPassword);
        var arguments = GuestControlArguments(state, "stat", state.Options.UserName, MicrosoftLoginAddress(state, secrets), password.FileName);
        arguments.Add(file);
        try { await Run(token, arguments.ToArray()); return true; }
        catch (SetupException error) when (error.Message.Contains("VERR_FILE_NOT_FOUND", StringComparison.Ordinal) ||
            error.Message.Contains("VERR_PATH_NOT_FOUND", StringComparison.Ordinal)) { return false; }
    }
    public async Task CopyTo(SessionState state, Secrets secrets, string local, string destination, CancellationToken token)
    {
        using var password = new PrivateFile(state.Options.Private, secrets.VmPassword);
        // --target-directory requires a directory, not a renamed target file.
        var arguments = GuestControlArguments(state, "copyto", state.Options.UserName,
            MicrosoftLoginAddress(state, secrets), password.FileName);
        if (destination.EndsWith('\\')) arguments.AddRange(["--target-directory", destination, local]);
        else arguments.AddRange([local, destination]);
        await Run(token, arguments.ToArray());
    }
    public async Task CopyFrom(SessionState state, Secrets secrets, string guest, string destination, CancellationToken token)
    {
        using var password = new PrivateFile(state.Options.Private, secrets.VmPassword);
        var arguments = GuestControlArguments(state, "copyfrom", state.Options.UserName,
            MicrosoftLoginAddress(state, secrets), password.FileName);
        arguments.AddRange([guest, destination]);
        await Run(token, arguments.ToArray());
    }
    public async Task Open(SessionState state, CancellationToken token)
    {
        if (!Guid.TryParse(state.VmId, out _)) throw new SetupException("请先载入有效的虚拟机任务。");
        await EnsureMachine(state, token);
        await EnsureRunning(state,token);
        var presenter = console ?? new VmConsole();
        if (presenter.TryShow(state.VmId)) return;
        presenter.Attach(Path.GetDirectoryName(Executable)!, state.VmId);
        for (var i = 0; i < 15; i++) { await Task.Delay(500, token); if (presenter.TryShow(state.VmId)) return; }
        throw new SetupException("虚拟机在运行，但窗口尚未确认打开。请检查任务栏或 VirtualBox，程序不会强制重启它。");
    }
    public Task<string> RequestShutdown(SessionState state, CancellationToken token) => Run(token, "controlvm", state.VmId, "acpipowerbutton");
}
