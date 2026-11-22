using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Claudome.Transport;

namespace ClaudeSetup;

public static class WorkstationBoundary
{
    public static Dictionary<string, string> Parse(string source)
    {
        if (source.Length > 4 * 1024 * 1024) throw new SetupException("VMware 配置过大，未读取。");
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in source.Split('\n')) {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var match = Regex.Match(line.Trim(), "^([a-zA-Z0-9_.:-]+)\\s*=\\s*\"([^\"]*)\"(?:\\s*#.*)?$");
            if (!match.Success || !settings.TryAdd(match.Groups[1].Value, Decode(match.Groups[2].Value))) throw new SetupException("VMware 配置格式不明确或存在重复项，已停止修改。");
        }
        return settings;
    }
    public static Dictionary<string,string> SharingDisabled() => new(StringComparer.OrdinalIgnoreCase) {
        ["sharedFolder.maxNum"]="0", ["isolation.tools.copy.disable"]="TRUE", ["isolation.tools.paste.disable"]="TRUE", ["isolation.tools.dnd.disable"]="TRUE",
        ["isolation.tools.hgfs.disable"]="TRUE", ["isolation.tools.getHostInfo.disable"]="TRUE", ["isolation.device.connectable.disable"]="TRUE", ["usb.present"]="FALSE", ["ehci.present"]="FALSE", ["usb_xhci.present"]="FALSE"
    };
    public static Dictionary<string, string> Required(BridgeSpec spec)
    {
        spec.Validate(); var values = SharingDisabled(); values["vmci0.present"]="TRUE"; values["vmci0.unrestricted"]="FALSE"; values["guestinfo.claudome.serviceId"]=spec.ServiceId;
        for (var i=0;i<32;i++) values["ethernet"+i+".present"]="FALSE"; return values;
    }
    public static void CheckOffline(Dictionary<string, string> settings)
    {
        if (!settings.GetValueOrDefault("ethernet0.present", "").Equals("FALSE",StringComparison.OrdinalIgnoreCase)) throw new SetupException("VMware 首张网卡禁用状态缺失。");
        if (settings.Any(x => Regex.IsMatch(x.Key, "^ethernet[0-9]+\\.present$", RegexOptions.IgnoreCase) && !x.Value.Equals("FALSE", StringComparison.OrdinalIgnoreCase))) throw new SetupException("VMware 仍有普通虚拟网卡，外层网关保持关闭。");
        foreach(var expected in SharingDisabled())
            if(!settings.GetValueOrDefault(expected.Key, "").Equals(expected.Value,StringComparison.OrdinalIgnoreCase)) throw new SetupException("VMware 共享、剪贴板或设备直通禁用状态未通过回读。");
    }
    public static bool SecurityReady(Dictionary<string,string> settings) =>
        settings.GetValueOrDefault("firmware", "").Equals("efi",StringComparison.OrdinalIgnoreCase) &&
        settings.GetValueOrDefault("uefi.secureBoot.enabled", "").Equals("TRUE",StringComparison.OrdinalIgnoreCase) &&
        settings.GetValueOrDefault("vtpm.present", "").Equals("TRUE",StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(settings.GetValueOrDefault("encryption.keySafe"));
    public static void Check(Dictionary<string, string> settings, BridgeSpec spec)
    {
        CheckOffline(settings);
        foreach (var expected in Required(spec)) {
            if (!settings.ContainsKey(expected.Key) && expected.Key.StartsWith("ethernet") && expected.Key != "ethernet0.present") continue;
            if (!settings.TryGetValue(expected.Key, out var value) || !value.Equals(expected.Value, StringComparison.OrdinalIgnoreCase)) throw new SetupException("VMware 总线通道或共享禁用设置与当前任务不符。");
        }
    }
    private static string Decode(string value) => Regex.Replace(value, "(?:\\|[0-9a-fA-F]{2})+", match => new UTF8Encoding(false,true).GetString(Convert.FromHexString(match.Value.Replace("|",""))));
    public static void Write(string path, Dictionary<string, string> settings)
    {
        Paths.RejectLinks(path); Paths.RejectLinks(path+".new");
        if (settings.Any(x=>x.Key.Contains('\n')||x.Value.Contains('\n')||x.Value.Contains('\r'))) throw new SetupException("VMware 配置包含无效换行。");
        File.WriteAllLines(path+".new", settings.Select(x=>x.Key+" = \""+x.Value.Replace("|","|7C").Replace("\"","|22")+"\""),new UTF8Encoding(false)); File.Move(path+".new",path,true);
    }
}
