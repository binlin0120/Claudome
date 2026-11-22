using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed record ExistingVm(string Platform, string Id, string Name, string State, string Location);
public sealed record VmInventoryResult(List<ExistingVm> Machines, List<string> Notes);
public static class VmInventory
{
    public static string HyperVFailure(Exception exception)
    {
        var error = exception.GetBaseException();
        if (error is TimeoutException) return "Hyper-V 查询超时，本次未确认其虚拟机数量；可稍后重试。";
        return unchecked((uint)error.HResult) switch {
            0x80041003 or 0x80070005 => "当前账号没有读取 Hyper-V 虚拟机的权限，本次未确认其数量；请由管理员检查 Hyper-V 管理权限。",
            0x8004100E or 0x80041010 or 0x80041011 => "Hyper-V 管理接口不可用；Windows 专业版也需另行启用 Hyper-V，或检查管理组件及重启状态。",
            _ => $"Hyper-V 注册信息暂不可读，本次未确认其数量（诊断码 {unchecked((uint)error.HResult):X8}）。"
        };
    }
    public static async Task<VmInventoryResult> Scan(CancellationToken token, ICommandRunner? runner = null)
    {
        runner ??= new CommandRunner(); var rows = new List<ExistingVm>(); var notes = new List<string>();
        var box = Machine.VBoxPath();
        if (box is not null) {
            try {
                var list = VirtualBox.ParseList(await runner.Run(box, ["list", "vms"], token, 30));
                foreach (var vm in list.Take(200)) {
                    token.ThrowIfCancellationRequested();
                    try { rows.Add(ParseVBoxInfo(vm.Id, vm.Name, await runner.Run(box, ["showvminfo", vm.Id, "--machinereadable"], token, 10))); }
                    catch (SetupException) { rows.Add(new("VirtualBox", vm.Id, vm.Name, "信息暂不可读", "")); }
                }
                if (list.Count > 200) notes.Add("VirtualBox 超过 200 台，已显示前 200 台。");
            } catch (SetupException) { notes.Add("VirtualBox 注册列表读取失败，请检查权限及软件状态。"); }
        } else notes.Add("尚未安装 VirtualBox。");
        try { rows.AddRange(await Task.Run(ReadHyperV, token).WaitAsync(TimeSpan.FromSeconds(20), token)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { notes.Add(HyperVFailure(ex)); }
        if (Workstation.DirectoryPath() is not null) {
          try {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMware", "inventory.vmls");
            if (File.Exists(file) && new FileInfo(file).Length <= 1024 * 1024) rows.AddRange(ParseWorkstationInventory(File.ReadAllText(file)).Take(200));
            else notes.Add("未找到当前用户的 VMware 注册清单。");
          } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add("VMware 注册清单暂不可读；其他平台的扫描结果仍然保留。"); }
        } else notes.Add("尚未安装 VMware Workstation。");
        return new(rows.OrderBy(x => x.Platform).ThenBy(x => x.Name).ToList(), notes);
    }
    public static ExistingVm ParseVBoxInfo(string id, string name, string info)
    {
        string Value(string key) => VBoxValue(info, key);
        var status = Value("VMState") switch { "running" => "运行中", "poweroff" => "已关机", "saved" => "已保存", "paused" => "已暂停", "aborted" => "异常停止", _ => "待检查" };
        return new("VirtualBox", id, name, status, Value("CfgFile"));
    }
    public static string VBoxValue(string info, string key)
    {
        var raw = Regex.Match(info, "^" + Regex.Escape(key) + "=\"((?:\\\\.|[^\"\\\\])*)\"\\r?$", RegexOptions.Multiline).Groups[1].Value;
        if(raw.Length>0)return Regex.Replace(raw, "\\\\([\"\\\\])", "$1");
        return Regex.Match(info,"^"+Regex.Escape(key)+"=([0-9]+|true|false)\\r?$",RegexOptions.Multiline).Groups[1].Value;
    }
    public static List<ExistingVm> ParseWorkstationInventory(string text)
    {
        var values = new Dictionary<string, Dictionary<string, string>>();
        foreach (Match line in Regex.Matches(text, "^vmlist(?<id>[0-9]+)\\.(?<key>config|DisplayName)\\s*=\\s*\"(?<value>[^\"]*)\"", RegexOptions.Multiline)) {
            var id = line.Groups["id"].Value; if (!values.TryGetValue(id, out var item)) values[id] = item = new();
            item[line.Groups["key"].Value] = line.Groups["value"].Value.Replace("|5C", "\\");
        }
        return values.Where(x => x.Value.ContainsKey("config")).Select(x => new ExistingVm("VMware Workstation", x.Key, x.Value.GetValueOrDefault("DisplayName", Path.GetFileNameWithoutExtension(x.Value["config"])), "未读取电源状态", x.Value["config"])).ToList();
    }
    public static List<string> RegisteredWorkstationPaths()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMware", "inventory.vmls");
        if (!File.Exists(file) || new FileInfo(file).Length > 1024*1024) throw new SetupException("VMware 注册清单暂不可核验，旧盘清理待处理。");
        return ParseWorkstationInventory(File.ReadAllText(file)).Select(x=>x.Location).ToList();
    }
    private static List<ExistingVm> ReadHyperV()
    {
        object? locator = null, services = null, results = null;
        try {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator") ?? throw new SetupException("WMI 不可用"));
            services = ((dynamic)locator!).ConnectServer(".", @"root\virtualization\v2");
            results = ((dynamic)services).ExecQuery("SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem", "WQL", 48);
            var rows = new List<ExistingVm>();
            foreach (dynamic vm in (IEnumerable)results) {
                try {
                    var id = (string)vm.Name; if (!Guid.TryParse(id, out _)) continue;
                    var status = Convert.ToInt32(vm.EnabledState) switch { 2 => "运行中", 3 => "已关机", 32768 => "已暂停", 32769 => "已保存", _ => "状态切换中" };
                    rows.Add(new("Hyper-V", id, (string)vm.ElementName, status, "请通过 Hyper-V 管理器查看位置")); if (rows.Count >= 200) break;
                } finally { if (Marshal.IsComObject(vm)) Marshal.ReleaseComObject(vm); }
            }
            return rows;
        } finally { foreach (var item in new[] { results, services, locator }) if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
    }
}
