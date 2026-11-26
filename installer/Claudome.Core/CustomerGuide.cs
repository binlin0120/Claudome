namespace ClaudeSetup;

public sealed record CustomerGuideStep(string Id,string Title,string Instructions){public override string ToString()=>Title;}
public static class CustomerGuide
{
    internal const string ManualAdsPowerId = "ads-manual";
    internal const string ManualAdsPowerStage = "网络已核验，等待手动配置与检查 AdsPower";
    private const string ManualProfile = "在虚拟机中打开 AdsPower Browser，登录自己的账号。找到已有的 Default Profile，点「打开」旁边的「⋮ → 编辑」。如果没有 Default Profile，先查看是否已有本任务的环境。";
    private const string ManualProxyType = "确认助手已核验来宾网络，Clash 服务和 TUN 保持开启。在「代理信息」保持 No Proxy（本地直连），浏览器流量由虚拟机内的 Clash TUN 转发。";
    private const string ProxyChecks = "IP 查询渠道选 ip-api，「标签页」留空。";
    private const string ManualFingerprint = "将 WebRTC 选为「禁用」。核对时区、地理位置和语言均为「基于 IP」，地理位置权限为「每次询问」，界面语言为「基于语言」。分辨率及其后的 Canvas、WebGL、WebGPU 和硬件噪音等设置保留原样。点击「确定」保存。";

    internal static UserAction ManualAdsPower() => new("手动配置与检查 AdsPower", [
        ManualProfile,
        ManualProxyType + ProxyChecks,
        "按「检测出错问题 → 操作指引与检测网址」核对 Windows 和 WebRTC 等设置，保存后打开该环境，访问 https://ip.net.coffee/claude/ 核对实际出口。"
    ], ManualAdsPowerId);

    public static string Country(string code)=>RegionCatalog.All.FirstOrDefault(r=>r.Code.Equals(code,StringComparison.OrdinalIgnoreCase))?.Label??"所选节点的实际出口地区";
    public static IReadOnlyList<CustomerGuideStep> Windows(SetupOptions options)=>new[] {
        new CustomerGuideStep("language","1. Windows 语言","使用中文镜像时选择简体中文；使用英文镜像时可选 English (United States)。显示语言按本人习惯选择，不决定网络出口。"),
        new("region","2. 国家或地区","国家/地区按本任务出口选择："+Country(options.Country)+"。美国线路就选美国，语言和键盘可以继续使用中文。"),
        new("keyboard","3. 键盘与输入法","需要中文输入时选择微软拼音；英文输入可保留 US 键盘。是否添加第二种键盘按本人习惯，暂不需要时点“跳过”。"),
        new("network","4. 连接网络","程序会经所选节点自动准备安装网络，检查通过后在 Windows 点“下一步”。仍显示“无 Internet”时点「检测出错问题 → 一键连接网络」，复用原任务检查。\n\n“安装驱动程序”会打开驱动文件夹选择器；只有明确缺少网卡驱动时才使用，不是连接网络按钮。不要随便选择安装光盘目录。"),
        new("account","5. 账号与密码","如已自动进入桌面，直接继续。需要输入本地账号时使用本任务设置的独立账号“"+options.UserName+"”及本人保存的虚拟机密码。微软账号、验证码与许可页面由本人决定和完成；不要在反馈截图中显示密码。"),
        new("privacy","6. 隐私与附加服务","隐私选项按本人需要选择。出现 OneDrive、Microsoft 365 或设备用途页面时，可按实际按钮选择跳过或继续；程序不会替你订阅、付款或处理账号验证。"),
        new("updates","7. 更新和首次启动","Windows 更新、自动重启与“请稍候”期间保持助手开启。程序保留原磁盘与安装进度。持续没有变化时查看「检测出错问题 → 黑屏与启动诊断」，不要反复新建虚拟机。"),
        new("tools","8. 来宾工具与桌面","新建任务会自动完成本地账号与来宾工具安装。若此前手动完成了 Windows 初始设置：进入桌面后" + (options.PlatformId == "virtualbox" ? "程序会自动挂载来宾工具光盘，在虚拟机打开「此电脑」双击 VirtualBox Guest Additions 运行 VBoxWindowsAdditions.exe 安装；" : "请在桌面顶部操作栏核对连接与剪贴板状态；") + "若账号或密码与助手记录不同，点击「检测出错问题 → 同步虚拟机账号密码」（或顶部操作栏「同步虚拟机密码」）即可自动进入第 5/7 步。")
    };
    public static IReadOnlyList<CustomerGuideStep> Software(SetupOptions options)=>new[] {
        new CustomerGuideStep("ads-login","1. 打开 AdsPower 和专用环境", options.ManualAdsPower
            ? ManualProfile
            : "在虚拟机中打开 AdsPower Browser，登录自己的账号。使用助手创建并绑定的 Claude 专用环境（名称形如 Claude-xxxxxxxx）。如果要自己配置现有环境，可在桌面顶部操作栏切换到手动模式。"),
        new("ads-os","2. 检查基础设置","在「基础设置」核对操作系统为 Windows。若当前选中 Linux 的企鹅图标，切换到 Windows 图标。User-Agent 保持「全部」。"),
        new("ads-proxy","3. 检查代理信息", options.ManualAdsPower
            ? ManualProxyType + "\n\n" + ProxyChecks
            : "保留助手写入的自定义 SOCKS5（127.0.0.1:17897）。" + ProxyChecks),
        new("ads-fingerprint","4. 检查指纹配置", options.ManualAdsPower
            ? ManualFingerprint
            : "确认 WebRTC 为「禁用」。核对助手写入的时区和语言与实际出口一致。地理位置权限可保持「每次询问」，界面语言可保持「基于语言」。分辨率及其后的设置保留原样。"),
        new("clash","5. Clash 检查","核对 Clash Verge 服务模式和 TUN 已开启，使用本任务专用配置与所选节点。保持助手及保护通道开启；不要关闭 Clash，也不要改成 NAT 或直连来绕过检查。"),
        new("leak-check","6. 首次网络检测","在 AdsPower 点「打开」，在浏览器访问 https://ip.net.coffee/claude/ 。核对国家/地区为 "+Country(options.Country)+"，并检查出口、DNS、IPv6、WebRTC 和时区。\n\n不要求评分或每个项目全绿；国家正确本身不能证明没有泄露。存在本地地址或未完成检查时，保留提示继续排查。"),
        new("claude","7. 登录与收藏","完成检测后保留同一个 AdsPower 窗口，新开标签访问 https://claude.ai/ ，由本人登录并完成一次对话，再收藏到书签栏。账号、验证码、付款由本人处理。官方 Claude 客户端需要单独检查网络与登录，不能直接沿用浏览器的验收结果。"),
        new("daily","8. 以后每天使用","打开助手并恢复原任务 → 等待网络与虚拟桌面自动连接 → 打开 AdsPower 原专用环境 → 先检查连接状态，再使用 Claude 书签。重开、更新或网络变化后重新检测。\n\n不要随意生成新指纹、删除环境、关闭 Clash，或在其他浏览器混用账号。关闭助手时先保存并暂停；下次继续原任务即可。"),
        new("cli-conhost","9. 命令行（conhost / Claude Code）代理提示","如需在虚拟机内的 conhost / PowerShell / cmd 等命令行窗口运行 Claude Code 或其他 CLI 工具，请注意：命令行窗口不会由 AdsPower 指纹浏览器自动接管，也不建议依赖隐式系统代理，需按实际使用的终端会话自行核对或手动设置代理环境变量（如 HTTP_PROXY / HTTPS_PROXY，仅指向本机受控端口，勿绕过 Clash TUN）。")
    };
}
