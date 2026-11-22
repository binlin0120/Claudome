using System.Windows.Controls;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    private readonly StackPanel inventoryPanel = new();
    private async Task ScanMachines()
    {
        if (busy) return; busy = true; inventoryPanel.Children.Clear(); inventoryPanel.Children.Add(Text("正在读取当前用户可见的虚拟机列表…", 12));
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var result = await VmInventory.Scan(timeout.Token); inventoryPanel.Children.Clear();
            inventoryPanel.Children.Add(Text($"本次读取到 {result.Machines.Count} 台已注册虚拟机", 15, false));
            foreach (var vm in result.Machines) {
                inventoryPanel.Children.Add(Card(Text(vm.Name, 14, false), Text(vm.Platform + " · " + vm.State + "\n" + vm.Location, 11), Row(Action("核对 / 载入本助手任务", (_, _) => {
                    var selectedPlatform = state.Options.PlatformId switch { "virtualbox" => "VirtualBox", "hyperv" => "Hyper-V", "workstation" => "VMware Workstation", _ => "" };
                    if (vm.Platform == selectedPlatform && vm.Id.Equals(state.VmId, StringComparison.OrdinalIgnoreCase)) { Navigate(2); return; }
                    Notice("扫描没有修改这台虚拟机。\n\n若由本助手创建，请载入原来的 setup-state.json。\n其他虚拟机需要先核对归属、账号和隔离兼容性，当前不会自动接管或覆盖。"); LoadTask();
                }))));
            }
            foreach (var note in result.Notes) inventoryPanel.Children.Add(Text(note, 11));
            inventoryPanel.Children.Add(Text("仅扫描注册信息，不启动、不删除、不覆盖虚拟机，也不读取其中的账号或浏览器资料。", 11));
        } catch (Exception ex) { inventoryPanel.Children.Clear(); inventoryPanel.Children.Add(Text(Redaction.SafeError(ex), 12)); }
        finally { busy = false; CompleteCloseAfterPause(); }
    }
}
