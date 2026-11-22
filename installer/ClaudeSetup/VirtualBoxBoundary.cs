using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public static class VirtualBoxBoundary
{
    public static async Task Disconnect(VirtualBox box, SessionState state, CancellationToken token)
    {
        if (!Guid.TryParseExact(state.VmId, "D", out _)) throw new SetupException("必须先载入本任务的虚拟机记录。");
        await box.EnsureMachine(state, token);
        var before = await box.Run(token, "showvminfo", state.VmId, "--machinereadable");
        var mode = VmInventory.VBoxValue(before, "VMState");
        if (mode is "poweroff" or "aborted") {
            var args = new List<string> { "modifyvm", state.VmId };
            for (var i = 1; i <= 8; i++) args.AddRange(["--nic" + i, "none"]);
            await box.Run(token, args.ToArray());
        }
        else if (mode is not ("running" or "paused" or "saved")) throw new SetupException("虚拟机正在切换状态，请稍后重试。");
        var info = await box.Run(token, "showvminfo", state.VmId, "--machinereadable");
        await box.EnsureSharing(state,token);
        for (var i = 1; i <= 8; i++) if (VmInventory.VBoxValue(info, "nic" + i) != "none") throw new SetupException("尚未选择节点，但虚拟机存在网络通路。请正常关机后继续，由程序核对断网配置。");
    }
    public static List<string> Arguments(GatewayBinding binding)
    {
        _ = ExternalGateway.Policy(binding, ["1.1.1.1"], 443, "", Guid.NewGuid().ToString("N"));
        var args = new List<string> { "modifyvm", binding.VmId, "--nic1", "generic", "--nic-generic-drv1", "UDPTunnel", "--nic-property1", "dest=127.0.0.1", "--nic-property1", "sport=" + binding.GuestPort, "--nic-property1", "dport=" + binding.ListenPort, "--macaddress1", binding.GuestMac.Replace(":", ""), "--nic-promisc1", "deny", "--cable-connected1", "on" };
        for (var i = 2; i <= 8; i++) args.AddRange(["--nic" + i, "none"]);
        return args;
    }
    public static void CheckInfo(SessionState state, GatewayBinding binding, string info) =>
        CheckInfo(state, binding, info, VirtualBoxClipboard.IsAllowed(state));

    private static void CheckInfo(SessionState state, GatewayBinding binding, string info, bool allowClipboard)
    {
        CheckSharing(info, allowClipboard);
        string Value(string key) => VmInventory.VBoxValue(info, key);
        if (state.VmId != binding.VmId || Value("UUID") != binding.VmId || !Value("nic1").Equals("generic", StringComparison.OrdinalIgnoreCase) || Value("generic1") != "UDPTunnel" || !Value("macaddress1").Equals(binding.GuestMac.Replace(":", ""), StringComparison.OrdinalIgnoreCase) || Value("cableconnected1") != "on") throw new SetupException("虚拟机网关绑定回读不匹配。");
        for (var i = 2; i <= 8; i++) if (Value("nic" + i) != "none") throw new SetupException("发现额外的虚拟机网络通路，网关保持关闭。");
        var expected = Path.Combine(state.Options.VmDirectory, state.Options.VmName + ".vbox");
        if (!Path.GetFullPath(Value("CfgFile")).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) throw new SetupException("虚拟机配置文件不属于当前任务。");
    }
    public static async Task Verify(VirtualBox box, SessionState state, GatewayBinding binding, CancellationToken token)
    {
        var allowClipboard = VirtualBoxClipboard.IsAllowed(state);
        var info = await box.Run(token, "showvminfo", binding.VmId, "--machinereadable");
        CheckInfo(state, binding, info, allowClipboard || VirtualBoxClipboard.IsAllowed(state));
        // VBoxManage's machine-readable output omits generic NIC properties.
        // Query the API; never infer loopback-only from the driver name.
        object? vbox = null, machine = null, nic = null;
        try {
            vbox = Activator.CreateInstance(Type.GetTypeFromProgID("VirtualBox.VirtualBox") ?? throw new SetupException("VirtualBox API 不可用。"));
            machine = ((dynamic)vbox!).FindMachine(binding.VmId); nic = ((dynamic)machine).GetNetworkAdapter(0u); dynamic adapter = nic;
            if ((string)adapter.GenericDriver != "UDPTunnel" || (string)adapter.GetProperty("dest") != "127.0.0.1" || (string)adapter.GetProperty("sport") != binding.GuestPort.ToString() || (string)adapter.GetProperty("dport") != binding.ListenPort.ToString() || (int)adapter.PromiscModePolicy != 1) throw new SetupException("虚拟机 UDP 通道配置不匹配，保持断网。");
        } catch (SetupException) { throw; }
        catch { throw new SetupException("虚拟机外层通道回读未完成，保持断网。"); }
        finally { foreach (var item in new[] { nic, machine, vbox }) if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
    }
    public static string SharingLabel(string key)=>key switch {"clipboard"=>"共享剪贴板","draganddrop"=>"文件拖放","clipboard_file_transfers"=>"剪贴板文件传输","usb"=>"USB 控制器","ehci"=>"USB 2.0 控制器","xhci"=>"USB 3.0 控制器","vrde"=>"远程显示",_=>key};
    public static void CheckSharing(string info, bool allowClipboard = false)
    {
        foreach(var (key,value) in new[]{("clipboard","disabled"),("draganddrop","disabled"),("clipboard_file_transfers","off"),("usb","off"),("ehci","off"),("vrde","off")})
        {
            if (key == "clipboard" && allowClipboard && VmInventory.VBoxValue(info, key) == "bidirectional") continue;
            if(VmInventory.VBoxValue(info,key)!=value) throw new HandoffException(new("虚拟机隔离设置需要恢复："+SharingLabel(key),[SharingLabel(key)+"当前为："+(VmInventory.VBoxValue(info,key) is {Length:>0} actual?actual:"未读取到")+"；要求："+value,"请正常关闭本任务虚拟机，再确认配置范围并继续。程序会恢复已约定的隔离设置，不删除磁盘。"],"vm-sharing"));
        }
        if(Regex.IsMatch(info,"^USBFilterActive[0-9]+=\"on\"",RegexOptions.Multiline|RegexOptions.IgnoreCase)) throw new SetupException("VirtualBox 存在主机 USB 设备直通筛选器，未打开受保护环境。");
        if(Regex.IsMatch(info,"^SharedFolderName",RegexOptions.Multiline|RegexOptions.IgnoreCase)) throw new SetupException("VirtualBox 仍存在共享文件夹，未打开受保护环境。");
    }
    public static async Task<ExternalGateway> Connect(VirtualBox box, SessionState state, GatewayBinding binding, string[] addresses, int port, string host, CancellationToken token,string? bootstrapConfig=null,IProgress<DownloadProgress>? downloads=null,IProgress<string>? messages=null)
    {
        await Prepare(box, state, binding, token);
        await Verify(box, state, binding, token);
        await GatewayFirewall.Ensure(new(state.Options.Root, binding.VmId, binding.GuestPort), token);
        BootstrapCore? bootstrap=null;
        try {
            if(bootstrapConfig is not null)bootstrap=await BootstrapCore.Start(state.Options,bootstrapConfig,token,downloads);
            var gateway=await ExternalGateway.Start(state,binding,addresses,port,host,async ct=>{GatewayFirewall.Verify(new(state.Options.Root,binding.VmId,binding.GuestPort));await Verify(box,state,binding,ct);},token,bootstrap);
            bootstrap=null;
            try{await gateway.ProbeBootstrap(token,messages);return gateway;}catch{await gateway.DisposeAsync();throw;}
        }finally{if(bootstrap is not null)await bootstrap.DisposeAsync();}
    }
    public static async Task Prepare(VirtualBox box, SessionState state, GatewayBinding binding, CancellationToken token)
    {
        if (!Guid.TryParseExact(state.VmId, "D", out _) || state.VmId != binding.VmId) throw new SetupException("网关记录与当前虚拟机不一致。");
        await box.EnsureMachine(state, token);
        var info = await box.Run(token, "showvminfo", state.VmId, "--machinereadable");
        var mode = VmInventory.VBoxValue(info, "VMState");
        await box.EnsureSharing(state,token);
        if (mode is "poweroff" or "aborted") await box.Run(token, Arguments(binding).ToArray());
        else if (mode is not ("running" or "paused" or "saved")) throw new SetupException("虚拟机正在切换状态，请稍后重试。");
        // Saved/paused VMs retain their adapter. Do not modify their settings or
        // discard saved state; require the same binding before resuming.
        CheckInfo(state, binding, await box.Run(token, "showvminfo", state.VmId, "--machinereadable"));
    }
}

public static class BoundaryRuntime
{
    private static ExternalGateway? current;
    private static string currentVm = "";
    private static IAsyncDisposable? relay;
    private static IVmBackend? retainedBackend;
    public static async Task Replace(ExternalGateway? gateway, string vmId, IAsyncDisposable? transport = null)
    {
        var old = current; var oldRelay = relay; var oldBackend = retainedBackend; current = gateway; relay = transport; retainedBackend = null; currentVm = gateway is null ? "" : vmId;
        if (old is not null) await old.DisposeAsync();
        if (oldRelay is not null) await oldRelay.DisposeAsync();
        if (oldBackend is not null) await oldBackend.DisposeAsync();
    }
    internal static string[] EndpointsFor(SessionState state) => currentVm == state.VmId ? current?.EndpointAddresses ?? [] : [];
    internal static int PortFor(SessionState state) => currentVm == state.VmId ? current?.EndpointPort ?? 0 : 0;
    public static Task StopFor(SessionState state) => currentVm == state.VmId ? Replace(null, "") : Task.CompletedTask;
    public static bool HasInstallationNetwork=>current?.IsAlive==true&&current.HasBootstrap;
    internal static bool IsWindowsSignInNetworkActive(SessionState state) =>
        currentVm == state.VmId && current is { IsAlive: true, HasBootstrap: true, IsHeldForWindowsSignIn: true };

    internal static bool TryHoldForWindowsSignIn(SessionState state, IVmBackend backend)
    {
        if (currentVm != state.VmId || current is not { } gateway || !gateway.TryHoldForWindowsSignIn()) return false;
        retainedBackend = backend;
        return true;
    }

    internal static void EndWindowsSignInHold(SessionState state)
    {
        if (currentVm == state.VmId) current?.EndWindowsSignInHold();
    }

    internal static void RevokeWindowsSignInHold(SessionState state)
    {
        if (currentVm == state.VmId) current?.RevokeWindowsSignInHold();
    }

    public static async Task FinishInstallationNetwork(SessionState state,CancellationToken token){Require(state);await current!.FinishBootstrap(token);}
    public static void RetainBackend(IVmBackend backend, SessionState state) { Require(state); retainedBackend = backend; }
    public static IVmBackend? BackendFor(SessionState state) => currentVm == state.VmId && current?.IsAlive == true ? retainedBackend : null;
    public static void Require(SessionState state)
    {
        if (state.Options.NetworkSource == "稍后配置") throw new SetupException("此环境尚未配置网络；请继续安装流程以核对断网网卡。");
        if(current is not null&&currentVm==state.VmId&&!current.IsAlive&&!string.IsNullOrEmpty(current.LastFailure))throw new SetupException("外层通道保护已中断。"+current.LastFailure);
        if (current is null || !current.IsAlive || currentVm != state.VmId) throw new HandoffException(new("需要重新建立外层保护", ["打开原任务，重新选择已保存的节点并点击继续部署。", "应用关闭后网关随之关闭，不会恢复 NAT 或直连。"], "gateway-reconnect"));
    }
    public static async Task VerifyCurrent(SessionState state, CancellationToken token)
    {
        Require(state); var gateway = current!; await gateway.Recheck(token);
        if (!ReferenceEquals(current, gateway)) throw new SetupException("网关在打开虚拟机前发生变化，请重新继续任务。");
    }
}
