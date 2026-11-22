using System.Net;

namespace ClaudeSetup;

public static class IsolationPolicy
{
    public static bool PublicIPv4(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        if (b[0] is 0 or 10 or 127 || b[0] >= 224) return false;
        if (b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31) return false;
        if (b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2)) return false;
        if (b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100) || b[0] == 203 && b[1] == 0 && b[2] == 113) return false;
        return true;
    }
    public static string[] ValidateEndpoints(IEnumerable<string> values)
    {
        var addresses = values.Distinct().ToArray();
        if (addresses.Length == 0 || addresses.Any(x => !IPAddress.TryParse(x, out var ip) || !PublicIPv4(ip))) throw new SetupException("隔离模式只接受经过核验的公网 IPv4 节点。主机代理、回环、局域网、链路本地及保留地址不可作为出口。");
        return addresses;
    }
    public static void RequireSupportedPlatform(SessionState state)
    {
        // This is a provider preflight, not an authorization. Boot and Open must
        // additionally read back zero NICs or a live, host-owned gateway.
        if (state.Options.PlatformId is "virtualbox" or "hyperv" or "workstation") return;
        throw new HandoffException(new("所选平台没有可用的受控通道", ["请选择已实现的平台。", "不会切换到普通 NAT、桥接或主机代理来跳过边界检查。"], "isolation-platform"));
    }
}
