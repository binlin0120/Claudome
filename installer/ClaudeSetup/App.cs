using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClaudeSetup;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--gateway-firewall") return GatewayFirewall.Elevated(args[1]);
        if (args.Length == 2 && args[0] == "--scan-machines") { try { JsonFile.Write(args[1], VmInventory.Scan(CancellationToken.None).GetAwaiter().GetResult()); return 0; } catch { return 1; } }
        if (args.Length == 2 && args[0] == "--diagnostics") { try { using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)); JsonFile.Write(args[1], Diagnostics.Collect(timeout.Token).GetAwaiter().GetResult()); return 0; } catch { return 1; } }
        if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "ClaudeSetup-tests")).GetAwaiter().GetResult();
        try { SelfUpdater.CleanupStaleUpdateFiles(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory, Path.GetTempPath()); } catch { }
        var app = new Application();
        app.Exit += (_, _) => Task.Run(() => BoundaryRuntime.Replace(null, "")).GetAwaiter().GetResult();
        app.DispatcherUnhandledException += (_, e) => { MessageBox.Show(Redaction.SafeError(e.Exception), "需要处理", MessageBoxButton.OK, MessageBoxImage.Information); e.Handled = true; };
        var window = new SetupWindow(restoreTask: args.Length == 0 || args[0] != "--preview");
        if (args.Length > 0 && args[0] == "--preview")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.ShowInTaskbar = false; window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -12000; window.Top = -12000;
            window.Loaded += async (_, _) =>
            {
                var directory = args.Length > 1 ? args[1] : Path.GetTempPath(); Directory.CreateDirectory(directory);
                try
                {
                    window.PreviewTheme(false);
                    foreach (var (name, route) in new[] { ("overview", -1), ("virtual-machines", -2), ("network", -3), ("downloads", -4), ("dual-protection", -5), ("storage-cleanup", -6), ("platform-choice", 0), ("create-virtual-machine", 1), ("install-windows", 2), ("install-progress", 5) })
                    {
                        if (route == -1) window.Navigate(0); else if (route == -2) window.Navigate(2); else if (route == -3) window.Navigate(3); else if (route == -4) window.Navigate(4); else if (route == -5) window.Navigate(5); else if (route == -6) window.Navigate(8); else window.Go(route);
                        await Save(name);
                    }
                    await window.VerifyCustomerFeedbackUI(directory);
                    window.PreviewTheme(true); window.Navigate(0); await Save("appearance-dark");
                    async Task Save(string name)
                    {
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)window.ContentRoot.ActualWidth, (int)window.ContentRoot.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window.ContentRoot);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(directory, name + ".png")); png.Save(output);
                    }
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "preview-error.txt"), ex.ToString()); app.Shutdown(1); return; }
                window.Close(); app.Shutdown();
            };
        }
        return app.Run(window);
    }
}

public sealed partial class SetupWindow : Window
{
    private static readonly Brush Ink = Brush("#172B3A"), Muted = Brush("#617684"), Accent = Brush("#087E8B"), Pale = Brush("#EDF6F6");
    public Grid ContentRoot { get; } = new();
    private readonly string[] titles = ["检查这台电脑", "创建或载入虚拟机", "准备 Windows 镜像", "选择网络与节点", "保存本机资料", "部署与验收"];
    private readonly string[] subtitles = ["先了解可用资源，保留你已有的软件和资料。", "先扫描已有环境，或新建独立虚拟机并选择保存位置。", "使用官方镜像，已有 ISO 可以直接选择。", "已有订阅和节点可以继续用，不需要重新购买。", "资料留在这台电脑，按需传给虚拟机。", "每一步显示实际结果，中断后可以接着完成。"];
    private readonly StackPanel navigation = new(), main = new(), footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock title = new(), subtitle = new(), hostText = new(), hostNote = new(), banner = new();
    private readonly List<StackPanel> pages = [];
    private readonly List<Button> navigationButtons = [];
    private readonly ComboBox platform = Choice([]), fixedNtfsDrives = Choice([]), candidateIsos = Choice([]);
    private readonly TextBlock platformReason = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, LineHeight = 22 };
    private readonly TextBox root = Input(), vmName = Input("Claude-Workspace"), memory = Input("4096"), cpu = Input("2"), disk = Input("80"), iso = Input(), isoUrl = Input(), isoHash = Input(), imageIndex = Input("1"), user = Input("ClaudeUser"), subscription = Input(), expectedIp = Input();
    private readonly TextBox networkText = Input("", true), passwordVisible = Input(), log = Input("", true);
    private readonly PasswordBox password = new() { FontSize = 14, Padding = new Thickness(10), Height = 42, BorderBrush = Line, Background = Surface, Foreground = Ink };
    private readonly StackPanel localGuestPasswordControls = new();
    private readonly TextBlock microsoftGuestPasswordHint = Text("此任务使用微软账号。请在「虚拟机桌面 → 同步虚拟机账号密码」更新。", 11);
    private readonly ComboBox source = Choice(["已有订阅链接", "节点链接 / 本地配置", "手动 HTTP / SOCKS5 节点", "稍后配置"]), node = Choice([]), country = Choice(RegionCatalog.All.Select(r => r.Code)), timeZone = Choice(TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id));
    private readonly ComboBox protocol = Choice(["socks5", "http", "https"]);
    private readonly TextBox server = Input(), port = Input("1080"), proxyUser = Input();
    private readonly PasswordBox adsApiKey = new() { FontSize = 14, Padding = new Thickness(10), Height = 42 };
    private readonly StackPanel pendingPanel = new();
    private Border? hyperVConsoleCard;
    private readonly PasswordBox proxyPassword = new() { FontSize = 14, Padding = new Thickness(10), Height = 42 };
    private readonly CheckBox manualAdsPower = Check("手动使用 AdsPower（无需本机 API）");
    private readonly CheckBox remember = Check("在本机加密记住必要资料（不勾选则仅本次使用）"), showPassword = Check("显示虚拟机密码"), license = Check("我已核对所选 Windows 的许可与来宾使用授权"), consent = Check("我已阅读组件许可，并确认所选虚拟机方案的安装与配置范围"), outage = Check("对本次新建来宾执行短暂的代理停止与恢复测试"), useCustomVmConfig = Check("使用我填写的自定义资源配置（不自动启用 VirtualBox 防卡死降核）");
    private readonly TextBlock cpuNemWarning = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 2, 0, 8), Visibility = Visibility.Collapsed };
    private readonly TextBlock updateStatusText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8) };
    private readonly ProgressBar updateProgressBar = new() { Minimum = 0, Maximum = 100, Height = 6, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed, Foreground = Accent };
    private Border? overviewUpdateCard;
    private Button? checkUpdateButton, applyUpdateButton, localZipUpdateButton;
    private UpdateCheckResult? pendingUpdateCheck;
    private bool checkingUpdates;
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Height = 8, Margin = new Thickness(0, 12, 0, 10), Foreground = Accent };
    private readonly TextBlock progressText = new() { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button previous, next;
    private int page;
    private bool busy, refreshingPlatforms, secretsLoaded = true, updatingCpuRecommendation;
    private string guestMicrosoftAddress = "";
    private string guestMicrosoftPassword = "";
    private int platformInvalidations;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource? installationFinished;
    private SessionState state = new();
    private List<ProxyNode> nodes = [];
    private string importedInput = "";

    public SetupWindow(bool restoreTask = true)
    {
        (previous, next) = BuildShell();
        root.Text = state.Options.Root; ApplyRecommendedCpuIfNotCustom();
        country.ItemsSource = RegionCatalog.All.Select(r => new ChoiceItem(r.Code, r.Label)).ToList();
        country.DisplayMemberPath = "Label"; country.SelectedValuePath = "Id";
        timeZone.ItemsSource = TimeZoneInfo.GetSystemTimeZones().Select(z => new ChoiceItem(z.Id, z.DisplayName)).ToList(); timeZone.DisplayMemberPath = "Label"; timeZone.SelectedValuePath = "Id";
        source.SelectedIndex = 3; protocol.SelectedIndex = 0;
        country.SelectionChanged += (_, _) => {
            if (updatingCpuRecommendation) return;
            if (RegionCatalog.DefaultWindowsTimeZone(country.SelectedValue?.ToString()) is { } recommendedZone)
                timeZone.SelectedValue = recommendedZone;
        };
        cpu.TextChanged += (_, _) => {
            if (!updatingCpuRecommendation && string.IsNullOrEmpty(state.VmId))
            {
                var selectedPlatform = platform.SelectedValue?.ToString() ?? "virtualbox";
                if (int.TryParse(cpu.Text.Trim(), out var parsed) && parsed != SetupOptions.RecommendedCpuCount(selectedPlatform))
                    useCustomVmConfig.IsChecked = true;
            }
            UpdateCreationCpuNemWarning();
        };
        useCustomVmConfig.Unchecked += (_, _) => ApplyRecommendedCpuIfNotCustom();
        BuildPages(); ApplyRecommendedCpuIfNotCustom(); RefreshCreationFields(); RefreshHost(); if (restoreTask) RestoreLastTask(); Navigate(0);
        platform.SelectionChanged += (_, _) => { if (!refreshingPlatforms) platformInvalidations++; };
        InitializeTaskSaving(restoreTask);
        if (restoreTask) Loaded += async (_, _) =>
        {
            SurfacePendingUpdateArtifacts();
            await CheckForUpdatesAsync(interactive: false);
        };
    }
    private static SolidColorBrush Brush(string color)
    {
        var source = new PaletteColor { Value = (Color)ColorConverter.ConvertFromString(color) };
        var brush = new SolidColorBrush();
        System.Windows.Data.BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new System.Windows.Data.Binding("Value") { Source = source, Mode = System.Windows.Data.BindingMode.OneWay });
        PaletteSources.Add(brush, source);
        return brush;
    }
    private static System.Runtime.CompilerServices.ConditionalWeakTable<SolidColorBrush, PaletteColor> PaletteSources => paletteSources ??= new();
    private static System.Runtime.CompilerServices.ConditionalWeakTable<SolidColorBrush, PaletteColor>? paletteSources;
    private sealed class PaletteColor : DependencyObject
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(Color), typeof(PaletteColor));
        public Color Value { get => (Color)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    }
    private static TextBox Input(string text = "", bool multiline = false) => new() { Text = text, Padding = new Thickness(10), FontSize = 13, MinHeight = 42, BorderBrush = Line, Background = Surface, Foreground = Ink, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
    private static ComboBox Choice(IEnumerable<string> items) => new() { ItemsSource = items.ToList(), MinHeight = 40, Padding = new Thickness(8), FontSize = 13, Background = Surface, BorderBrush = Line, Foreground = Ink };
    private static CheckBox Check(string text) => new() { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 10, 0, 6), VerticalContentAlignment = VerticalAlignment.Center };
    private static TextBlock Text(string text, double size = 13, bool muted = true) => new() { Text = text, FontSize = size, Foreground = muted ? Muted : Ink, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.6, Margin = new Thickness(0, 0, 0, 12) };
    private static Button Action(string label, RoutedEventHandler click, bool primary = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(17, 10, 17, 10), Margin = new Thickness(0, 4, 8, 4), Background = primary ? Accent : Surface, Foreground = primary ? OnAccent : Ink, BorderBrush = primary ? Accent : Line, BorderThickness = new Thickness(1), FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand };
        button.Template = RoundedButtonTemplate(); button.Click += click; return button;
    }
    private static Border Card(params UIElement[] content)
    {
        var panel = new StackPanel(); foreach (var item in content) panel.Children.Add(item);
        return new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(22), Margin = new Thickness(0, 0, 5, 16), Child = panel };
    }
    private static StackPanel Field(string label, UIElement control, string? hint = null)
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 14) }; box.Children.Add(Text(label, 13, false)); box.Children.Add(control);
        if (hint is not null) { var note = Text(hint, 11); note.Margin = new Thickness(0, 5, 0, 0); box.Children.Add(note); } return box;
    }
    private static StackPanel Row(params UIElement[] children) { var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 10) }; foreach (var child in children) row.Children.Add(child); return row; }
    private void BuildPages()
    {
        for (int i = 0; i < 6; i++) pages.Add(new StackPanel());
        hostText.FontSize = 16; hostText.TextWrapping = TextWrapping.Wrap; hostText.LineHeight = 29; hostText.Margin = new Thickness(0, 0, 0, 14);
        hostNote.TextWrapping = TextWrapping.Wrap; hostNote.Foreground = Muted; hostNote.LineHeight = 23;
        var hostActions = Row(Action("重新检查", (_, _) => RefreshHost()), Action("载入已有安装任务", (_, _) => LoadTask()));
        if (ProductProfile.IsHyperVStandalone)
            hostActions.Children.Add(Action("启用 / 核对 Hyper-V 功能", async (_, _) => await EnableHyperVHostFeatureAsync()));
        pages[0].Children.Add(Card(Text("当前电脑", 16, false), hostText, hostNote, hostActions));
        platform.DisplayMemberPath = "Label"; platform.SelectedValuePath = "Id";
        platform.SelectionChanged += (_, _) => { if(hyperVConsoleCard is not null) hyperVConsoleCard.Visibility = platform.SelectedValue?.ToString() == "hyperv" ? Visibility.Visible : Visibility.Collapsed; ApplyRecommendedCpuIfNotCustom(); UpdateCreationCpuNemWarning(); if (platform.SelectedItem is PlatformChoice choice) { platformReason.Text = choice.Reason + "\n" + string.Join("\n", choice.Instructions); RefreshConsent(); } };
        RefreshPlatforms();
        pages[0].Children.Add(Card(Field("选择虚拟机方案（按这台电脑的推荐度排序）", platform), platformReason, Text("推荐度综合系统版本、架构、硬件虚拟化与已有软件；自动部署能力单独标明。Mac 在项目中另有 Fusion / Parallels / UTM 流程，此 Windows 程序不在 Mac 上运行。", 11)));
        pages[0].Children.Add(Card(Text("这次会做什么", 16, false), Text("创建独立的 Windows 虚拟机，准备网络代理与 AdsPower 环境。你可以继续使用已有订阅，自行选择节点。"), Text("客户无需安装 Codex。安装到哪些目录、哪些步骤需要管理员确认，都会在开始前说明。已有虚拟机不会被覆盖。"), Text("当前为开发试用版：Windows 10 与内嵌控制台尚需干净机器端到端验收，程序会如实显示待处理项。", 12), Row(Action("查看组件来源与许可", (_, _) => ShowLicenses()))));
        pages[1].Children.Add(Card(Text("已有虚拟机？", 16, false), Text("先查看当前电脑已注册的环境，再决定新建还是载入原任务。", 12), Row(Action("扫描本机已有虚拟机", async (_, _) => await ScanMachines()), Action("载入已有安装任务", (_, _) => LoadTask())), inventoryPanel));
        cpuNemWarning.Foreground = Accent;
        useCustomVmConfig.Visibility = ProductProfile.IsHyperVStandalone ? Visibility.Collapsed : Visibility.Visible;
        var cpuFieldHint = ProductProfile.IsHyperVStandalone
            ? "Hyper-V 独立版默认推荐 2 核；如需调整可在创建前直接修改。"
            : "开启虚拟化安全（Hyper-V 兼容模式）的电脑上 VirtualBox 默认推荐 1 核以彻底避免多核跨核中断死锁；若你自行修改核数或勾选下方自定义开关，程序将按你选择的配置运行，不自动启用防卡死降核。";
        fixedNtfsDrives.DisplayMemberPath = "DisplayLabel";
        fixedNtfsDrives.SelectedValuePath = "SuggestedWorkspacePath";
        var driveQuickRow = Row(Action("使用所选 NTFS 盘建议目录", (_, _) =>
        {
            if (!string.IsNullOrEmpty(state.VmId)) { Notice("原任务目录已固定。新建请使用独立任务；不会移动或覆盖现有虚拟机。"); return; }
            if (fixedNtfsDrives.SelectedValue is string suggested && !string.IsNullOrWhiteSpace(suggested)) root.Text = suggested;
        }));
        pages[1].Children.Add(Card(
            Field("数据与虚拟磁盘目录", root, "建议至少预留 95 GB；可选择本机任意固定 NTFS 盘（无需强制 C/D 盘）。"),
            Field("快速选择本机可用 NTFS 分区", fixedNtfsDrives),
            Row(Action("选择文件夹", (_, _) => { if(!string.IsNullOrEmpty(state.VmId)){Notice("原任务目录已固定。新建请使用独立任务；不会移动或覆盖现有虚拟机。");return;}var d = new OpenFolderDialog(); if (d.ShowDialog() == true) root.Text = d.FolderName; }), driveQuickRow),
            Field("虚拟机名称", vmName, "使用英文字母、数字、短横线或下划线。"),
            Field("来宾内存（MB）", memory),
            Field("虚拟 CPU 数量", cpu, cpuFieldHint),
            useCustomVmConfig, cpuNemWarning, Field("虚拟磁盘最大容量（GB）", disk), creationNote));
        iso.IsReadOnly = true;
        candidateIsos.DisplayMemberPath = "DisplayLabel";
        candidateIsos.SelectedValuePath = "IsoPath";
        pages[2].Children.Add(Card(
            Text("方式一：选择本机已有或扫描到的 Windows 11 ISO", 17, false),
            Text("如本机已有官方 Windows 11 x64 ISO，可直接浏览选择，或点击扫描本机常见下载目录/NTFS 分区。", 12),
            Field("Windows 11 x64 镜像路径", iso),
            Row(Action("选择已下载的 ISO", (_, _) => { var d = new OpenFileDialog { Filter = "Windows ISO|*.iso" }; if (d.ShowDialog() == true) { iso.Text = d.FileName; isoHash.Clear(); } }),
                Action("扫描本机已有 ISO", (_, _) => ScanCandidateIsos())),
            Field("扫描到的本机候选 ISO", candidateIsos),
            Row(Action("采用所选候选 ISO", (_, _) => { if (candidateIsos.SelectedValue is string foundIso && !string.IsNullOrWhiteSpace(foundIso)) { iso.Text = foundIso; isoHash.Clear(); banner.Text = "已选中候选 ISO，请点击下方「读取镜像版本」。"; } }))));
        pages[2].Children.Add(Card(
            Text("方式二：粘贴微软官网下载链接，由程序自主下载", 17, false),
            Text("前往微软官网选择「Windows 11（多版本 ISO）」→ 选择简体中文或英文 → 右键「立即下载（64位）」复制链接粘贴到下方，即可由程序高速下载并记录指纹（也可再按传统方式下载微软媒体创建工具）。", 12),
            Field("微软官网 ISO 下载直链（HTTPS）", isoUrl, "仅接受微软官方下载域（*.microsoft.com / windowsupdate.com），支持断点续传。"),
            Field("微软官网公布的 SHA-256（可选，建议填写以严格校验）", isoHash, "填写后下载完成立即核验；留空则在下载完成后由程序自动计算并记录指纹。"),
            Row(Action("打开微软 Windows 11 官网下载页", (_, _) => OpenUrl("https://www.microsoft.com/software-download/windows11")),
                Action("从官网链接下载 ISO", async (_, _) => await DownloadIsoFromUrlAsync(), true),
                Action("暂停下载", (_, _) => cancellation?.Cancel())),
            Row(Action("改用微软媒体创建工具下载", async (_, _) => await Perform(async (o, token) => { await Engine().PrepareWindowsMedia(o, token); Notice("微软工具已打开。确认许可 → 选择 ISO 文件（不要选 U 盘）→ 保存镜像。下载完成后回到上方选择该 ISO。"); })))));
        windowsImages.DisplayMemberPath="Label";windowsImages.SelectedValuePath="Index";
        windowsImages.SelectionChanged+=(_,_)=>{license.IsChecked=false;RefreshConsent();};
        iso.TextChanged+=(_,_)=>ClearMedia();
        pages[2].Children.Add(Card(Text("确认本次安装的系统",16,false),Text("读取将临时只读挂载 ISO，并用微软 DISM 查询系统信息，需要管理员确认；不会创建虚拟机、安装系统或修改镜像。",11),Row(Action("读取镜像版本",async(_,_)=>await InspectMedia(),true),Action("查看微软软件许可条款",(_,_)=>OpenUrl("https://www.microsoft.com/useterms"))),Field("选择实际安装版本",windowsImages),mediaDetails,license));
        ClearMedia();
        networkText.Height = 105;
        source.SelectionChanged += (_, _) =>
        {
            nodes = [];
            node.ItemsSource = null;
            importedInput = "";
            currentFinalSubscription = false;
        };
        pages[3].Children.Add(BuildRouteActions());
        var subscriptionField = Field("已有订阅链接", subscription, "仅在本机读取，不发送到聊天或我们的服务器。");
        var configField = Field("节点链接 / Clash 配置文本", networkText);
        var importRow = Row(Action("选择本地配置", (_, _) => { var d = new OpenFileDialog { Filter = "节点配置|*.yaml;*.yml;*.json;*.txt|全部文件|*.*" }; if (d.ShowDialog() == true) { if (new FileInfo(d.FileName).Length > NetworkImport.MaximumBytes) { Notice("文件超过 8 MB。"); return; } networkText.Text = File.ReadAllText(d.FileName); source.SelectedIndex = 1; } }), Action("读取节点列表", async (_, _) => await ParseNodes()),Action("查看未导入项",(_,_)=>ShowImportIssues()));
        pages[3].Children.Add(Card(Field("你准备使用哪种网络配置？", source), subscriptionField, configField, importRow, Text("选择稍后配置时，先完成虚拟机与软件安装，网络、节点地区和时区会保留待配置状态。", 11)));
        var manual = new Expander { Header = "手动填写 HTTP / SOCKS5 节点", Margin = new Thickness(0, 0, 5, 16) };
        manual.Content = Card(Field("协议", protocol), Field("服务器", server), Field("端口", port), Field("账号（可选）", proxyUser), Field("密码（可选）", proxyPassword), Row(Action("采用手动节点", (_, _) => ManualNode()))); pages[3].Children.Add(manual);
        var nodeProbeNote = Text("选好节点后可点击下方按钮先行测速，自动识别真实出口国家/地区并匹配对应城市时区（全程仅经所选节点探测，不改本机代理）。", 11);
        var nodeCard = Card(
            Field("由你选择要使用的节点", node, "不会自动挑选或跨地区切换。这里选择最终出口；需要前置代理时，展开下面的链式配置。"),
            Row(Action("测速并自动识别出口地区与时区", async (_, _) => await ProbeSelectedNode(), true)),
            nodeProbeNote,
            Field("最终出口国家或地区", country, "按实际出口核对，不凭节点名称猜测。"),
            Field("节点所在城市的 Windows 时区", timeZone),
            Field("预期固定出口 IPv4（可选）", expectedIp, "独享/静态住宅 IP 可填入用于严格锁死出口；机场动态轮换节点请留空。"),
            outage);
        pages[3].Children.Add(nodeCard);
        BuildChainPage();
        void UpdateNetworkFields()
        {
            subscriptionField.Visibility = source.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            configField.Visibility = source.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            importRow.Visibility = source.SelectedIndex is 0 or 1 ? Visibility.Visible : Visibility.Collapsed;
            manual.Visibility = source.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            manual.IsExpanded = source.SelectedIndex == 2;
            nodeCard.Visibility = source.SelectedIndex == 3 ? Visibility.Collapsed : Visibility.Visible;
        }
        source.SelectionChanged += (_, _) => UpdateNetworkFields(); UpdateNetworkFields();
        passwordVisible.Visibility = Visibility.Collapsed;
        showPassword.Checked += (_, _) => { passwordVisible.Text = password.Password; passwordVisible.Visibility = Visibility.Visible; password.Visibility = Visibility.Collapsed; };
        showPassword.Unchecked += (_, _) => { password.Password = passwordVisible.Text; password.Visibility = Visibility.Visible; passwordVisible.Visibility = Visibility.Collapsed; passwordVisible.Clear(); };
        localGuestPasswordControls.Children.Add(Field("独立虚拟机密码", password, "至少 10 位。它是虚拟机的真实密码，不是主机 PIN。"));
        localGuestPasswordControls.Children.Add(passwordVisible);
        localGuestPasswordControls.Children.Add(showPassword);
        localGuestPasswordControls.Children.Add(Row(
            Action("生成独立密码", (_, _) =>
            {
                var value = "C!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)) + "a9";
                password.Password = value;
                if (showPassword.IsChecked == true) passwordVisible.Text = value;
            }),
            Action("复制账号和密码", (_, _) =>
            {
                var value = showPassword.IsChecked == true ? passwordVisible.Text : password.Password;
                if (value.Length == 0) return;
                Clipboard.SetText("虚拟机账号：" + user.Text + "\r\n虚拟机密码：" + value);
                Notice("已复制。请由你本人妥善保存，不要发到聊天或公开仓库。");
            })));
        pages[4].Children.Add(Card(Field("虚拟机 Windows 账号", user), localGuestPasswordControls, microsoftGuestPasswordHint, remember,
            Text("勾选后使用当前 Windows 用户的本机加密机制保存。换电脑不能直接复制使用；不勾选时，重开安装器需要重新填写。", 11)));
        pages[4].Children.Add(Card(manualAdsPower, Field("AdsPower API Key（自动配置时选填）", adsApiKey, "手动使用时无须开启 API 或填写密钥。自动配置时，如果接口要求密钥，可从 AdsPower 的 API 页面复制到这里。")));
        log.IsReadOnly = true; log.Height = 175;
        pages[5].Children.Add(Card(Text("准备好后开始", 16, false), Text("安装流程：核验资源 → 安装虚拟机组件 → 安装 Windows → 配置所选网络 → 安装 AdsPower → 回读结果。遇到需要本人操作的步骤会暂停提示。"), consent, Row(Action("仅下载并核验资源", async (_, _) => await Perform(async (o, token) => await Engine().DownloadResources(o, token))), Action("开始 / 继续部署", async (_, _) => await StartInstall(), true), Action("暂停", (_, _) => { cancellation?.Cancel(); banner.Text = "已请求暂停；进行中的系统安装或来宾配置步骤会先结束。"; })), progress, progressText, log));
        pages[5].Children.Add(BuildValidationCard());
        pages[5].Children.Add(pendingPanel);
        hyperVConsoleCard = Card(Text("Hyper-V 主机会话设置", 15, false), Text("Hyper-V 默认允许仅限剪贴板的增强会话（来宾内已通过策略禁用磁盘、USB、音频与设备重定向）。如曾手动关闭双向剪贴板并切换为基本会话，可在所有受保护环境正常关机后恢复主机增强会话原值；其他 Hyper-V 虚拟机也会受影响。", 11), Row(Action("恢复主机增强会话设置", async (_, _) => {
            if (busy || state.Options.PlatformId != "hyperv") { Notice("请先载入原来的 Hyper-V 任务。"); return; }
            if (MessageBox.Show(this, "恢复主机增强会话设置会结束本任务的基本会话保护。请先正常关闭虚拟机。是否继续？", "恢复主机设置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await Perform(async (_, token) => await Engine().RestoreHyperVConsole(state, token));
        })));
        hyperVConsoleCard.Visibility = platform.SelectedValue?.ToString() == "hyperv" ? Visibility.Visible : Visibility.Collapsed;
        pages[5].Children.Add(hyperVConsoleCard);
        pages[5].Children.Add(Card(Text("完成后的检查", 16, false), Text("环境安装完成后，在 AdsPower 专用窗口先打开指定检测页，确认网络、DNS、WebRTC 和时区，再登录 Claude。安装成功不会被写成账号或浏览器已经验收。"), Row(Action("进入虚拟机桌面",(_,_)=>Navigate(7)), Action("打开任务目录", (_, _) => { try { Paths.ValidateRoot(root.Text); if (Directory.Exists(root.Text)) Process.Start(new ProcessStartInfo(root.Text) { UseShellExecute = true }); } catch (Exception ex) { Notice(Redaction.SafeError(ex)); } }), Action("查看本机验收报告", (_, _) => ShowReport()))));
        pages[5].Children.Add(Card(Text("恢复选项", 16, false), Text("仅撤回本工具在这台来宾中设置的 Clash 服务配置和网络规则，并恢复记录的防火墙默认值；已有虚拟磁盘和浏览器数据保留。同时关闭外层通道，虚拟机保留断网状态。", 11), Row(Action("撤回来宾网络配置", async (_, _) =>
        {
            if (busy) return;
            if (Path.GetFullPath(root.Text) != Path.GetFullPath(state.Options.Root) || user.Text != state.Options.UserName) { Notice("请先载入要撤回的原任务，保持任务目录与账号一致。"); return; }
            if (MessageBox.Show(this, "将停止本工具的来宾代理、移除本工具的网络规则，并恢复此前记录的防火墙默认值。来宾防火墙会恢复原值，外层通道同时关闭；再次使用前需要重新配置。继续吗？", "撤回来宾网络配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await Perform(async (_, token) => await Engine().RollbackNetwork(state, AppliedLogin(), token), useSavedOptions: true);
        }))));
    }
    public void Go(int index)
    {
        SetDesktopSidebar(false);
        page = index; MarkSection(1);
        title.Text = "准备你的专属环境"; subtitle.Text = titles[index] + " · " + subtitles[index];
        main.Children.Clear(); main.Children.Add(pages[index]); shellContentScroll?.ScrollToTop();
        steps.Visibility = Visibility.Visible; steps.Children.Clear(); footer.Visibility = Visibility.Visible;
        var labels = new[] { "电脑检查", "虚拟机与位置", "Windows 镜像", "网络节点", "本机资料", "开始安装" };
        for (int i = 0; i < labels.Length; i++)
        {
            var target = i; var button = Action((i + 1) + "  " + labels[i], (_, _) => { if (!busy) Go(target); });
            button.FontSize = 11; button.Padding = new Thickness(10, 8, 10, 8); button.BorderThickness = new Thickness(0);
            button.Background = i == index ? Accent : Surface; button.Foreground = i == index ? OnAccent : Muted;
            steps.Children.Add(button);
        }
        previous.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible; next.Visibility = index == 5 ? Visibility.Collapsed : Visibility.Visible;
        banner.Text = "准备到哪一步，下次就从哪里继续。";
    }
    private void RefreshHost()
    {
        try
        {
            var host = Machine.Read();
            hostText.Text = host.Summary;
            if (platform.Items.Count > 0) RefreshPlatforms();

            var memoryNote = host.FreeMemory < 6L * 1024 * 1024 * 1024
                ? "可以先下载资源。启动 4 GB 虚拟机前，请至少空出约 6 GB 内存。程序不会自动关闭你的应用。"
                : "当前空闲内存满足默认 4 GB 来宾的启动余量，实际启动前还会重新检查。";
            if (ProductProfile.IsHyperVStandalone)
            {
                var preflight = Machine.ReadHyperVPreflight(host);
                hostNote.Text = (Machine.HyperVStandaloneIneligibilityReason(host) is { } reason
                    ? reason
                    : "当前电脑符合 Hyper-V 独立版要求（Windows 11 专业版 / 企业版 x64）。") +
                    "\n" + preflight.SummaryLine + "\n\n" + memoryNote;
            }
            else
            {
                hostNote.Text = Machine.IsHomeEdition(host.Edition)
                    ? Machine.HomeEditionHyperVNotice + "\n\n" + memoryNote
                    : memoryNote;
            }
            var drives = Machine.GetFixedNtfsDrives();
            fixedNtfsDrives.ItemsSource = drives;
            if (drives.Count > 0 && fixedNtfsDrives.SelectedIndex < 0)
                fixedNtfsDrives.SelectedIndex = 0;
        }
        catch (Exception ex) { hostText.Text = "当前检查未完成"; hostNote.Text = Redaction.SafeError(ex); }
    }

    private void ScanCandidateIsos()
    {
        try
        {
            var found = WindowsMedia.DiscoverCandidateIsos();
            candidateIsos.ItemsSource = found;
            if (found.Count > 0)
            {
                candidateIsos.SelectedIndex = 0;
                if (string.IsNullOrWhiteSpace(iso.Text)) iso.Text = found[0].IsoPath;
                Notice($"已扫描到 {found.Count} 个候选 ISO 镜像（大于 1 GB）。请确认后点击下方「读取镜像版本」。");
            }
            else
            {
                Notice("常见目录（下载、桌面及各 NTFS 盘根目录/ISO 文件夹）中暂未发现大于 1 GB 的 ISO 镜像，可手动浏览选择或粘贴官网链接下载。");
            }
        }
        catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
    }

    private async Task EnableHyperVHostFeatureAsync()
    {
        await Perform(async (_, token) =>
        {
            await Engine().EnableHyperVFeatureAsync(state, token);
            RefreshHost();
            Notice("Hyper-V 功能与虚拟机管理服务（vmms）均已就绪，可以继续下一步配置。");
        });
    }

    private async Task DownloadIsoFromUrlAsync()
    {
        await Perform(async (o, token) =>
        {
            var (path, computedSha) = await Engine().DownloadIso(o, token);
            iso.Text = path;
            isoHash.Text = computedSha;
            Notice($"Windows ISO 已下载完成并记录 SHA-256 指纹：\n{path}\n\n请接着点击下方「读取镜像版本」选择要安装的版本（如 Windows 11 专业版）。");
        });
    }
    private void ApplyRecommendedCpuIfNotCustom()
    {
        if (!string.IsNullOrEmpty(state.VmId) || useCustomVmConfig.IsChecked == true)
        {
            UpdateCreationCpuNemWarning();
            return;
        }
        var selectedPlatform = platform.SelectedValue?.ToString() ?? state.Options.PlatformId;
        updatingCpuRecommendation = true;
        try { cpu.Text = SetupOptions.RecommendedCpuCount(selectedPlatform).ToString(); }
        finally { updatingCpuRecommendation = false; }
        UpdateCreationCpuNemWarning();
    }
    private void UpdateCreationCpuNemWarning()
    {
        if (ProductProfile.IsHyperVStandalone)
        {
            cpuNemWarning.Text = "";
            cpuNemWarning.Visibility = Visibility.Collapsed;
            return;
        }
        var selectedPlatform = platform.SelectedValue?.ToString() ?? state.Options.PlatformId;
        var warning = string.IsNullOrEmpty(state.VmId) && int.TryParse(cpu.Text.Trim(), out var parsed)
            ? SetupOptions.MultiCoreNemWarning(selectedPlatform, parsed)
            : null;
        cpuNemWarning.Text = warning ?? "";
        cpuNemWarning.Visibility = string.IsNullOrEmpty(warning) ? Visibility.Collapsed : Visibility.Visible;
    }
    private SetupOptions Options()
    {
        int Number(TextBox field, string label) => int.TryParse(field.Text.Trim(), out var n) ? n : throw new SetupException(label + "请填写整数。");
        var o = new SetupOptions { Root = root.Text.Trim(), VmName = vmName.Text.Trim(), MemoryMb = Number(memory, "内存"), CpuCount = Number(cpu, "CPU"), DiskGb = Number(disk, "磁盘"), IsoPath = iso.Text.Trim(), IsoUrl = isoUrl.Text.Trim(), IsoSha256 = isoHash.Text.Trim().ToLowerInvariant(), WindowsImageIndex = Number(imageIndex, "镜像索引"), UserName = user.Text.Trim(), NetworkSource = source.SelectedItem?.ToString() ?? "稍后配置", NodeName = (node.SelectedItem as ProxyNode)?.Name ?? "", Country = country.SelectedValue?.ToString() ?? "US", TimeZoneId = timeZone.SelectedValue?.ToString() ?? "Pacific Standard Time", Remember = remember.IsChecked == true, AcceptChanges = consent.IsChecked == true, AcceptWindowsLicense = license.IsChecked == true && windowsImages.SelectedItem is WindowsImageChoice && selectedMedia?.Sha256 == isoHash.Text.Trim(), TestOutage = outage.IsChecked == true, ManualAdsPower = manualAdsPower.IsChecked == true, WorkspaceId = state.Options.WorkspaceId };
        o.ChainEnabled = source.SelectedIndex!=3 && (chain.IsChecked == true || (node.SelectedItem as ProxyNode)?.Upstream is not null);
        o.ChainFromImport=o.ChainEnabled && chain.IsChecked!=true;
        o.UpstreamSource=upstreamSource.SelectedItem?.ToString() ?? "节点链接 / 本地配置";o.UpstreamName=(upstreamNode.SelectedItem as ProxyNode)?.Name ?? (node.SelectedItem as ProxyNode)?.Upstream?.Name ?? "";
        o.PlatformId = platform.SelectedValue?.ToString() ?? "virtualbox";
        o.VmwareSecurityConfirmed = state.Options.VmwareSecurityConfirmed;
        o.HyperVBasicConsoleConfirmed = state.Options.HyperVBasicConsoleConfirmed;
        o.SharedClipboardEnabled = state.Options.SharedClipboardEnabled;
        o.ManualCpuConfirmed = !string.IsNullOrEmpty(state.VmId)
            ? state.Options.ManualCpuConfirmed
            : (useCustomVmConfig.IsChecked == true || o.CpuCount != SetupOptions.RecommendedCpuCount(o.PlatformId));
        o.Validate(false); return o;
    }
    private string CurrentGuestPassword()
    {
        if (state.Options.UsesMicrosoftGuestAccount) return guestMicrosoftPassword;
        return showPassword.IsChecked == true ? passwordVisible.Text : password.Password;
    }
    private Secrets ReadSecrets() => ChainSecrets(new() { VmPassword = CurrentGuestPassword(), VmMicrosoftAddress = guestMicrosoftAddress, SubscriptionUrl = subscription.Text.Trim(), NetworkText = networkText.Text, SelectedNodeJson = (node.SelectedItem as ProxyNode)?.Json ?? "", ExpectedExitIp = expectedIp.Text.Trim(), AdsPowerApiKey = adsApiKey.Password });
    private Deployment Engine() => new(new Progress<string>(message => { log.AppendText(message + Environment.NewLine); log.ScrollToEnd(); banner.Text = message; RecordTaskActivity(message); }), new Progress<DownloadProgress>(p => { progressText.Text = p.Display; RecordTaskDownload(p.Display); progress.IsIndeterminate=p.Total is not >0; progress.Value = p.Total > 0 ? p.Received * 100d / p.Total.Value : 0; if (downloadFeatureInfo is not null) downloadFeatureInfo.Text = p.Display; if (downloadFeatureBar is not null) downloadFeatureBar.Value = progress.Value; }), new Progress<InstallationProgress>(ShowInstallationProgress));
    private void ShowInstallationProgress(InstallationProgress value)
    {
        progressText.Text=value.Display;banner.Text=value.Display;RecordTaskActivity(value.Display);
        if(framebufferConflictVm != state.VmId && !desktopConnectionPolicy.Suspended && !closeAfterPause && busy && value.Display.StartsWith("4 / 7") && value.Display.Contains("等待") && !string.IsNullOrEmpty(state.VmId) && !state.Completed.Contains("guest-ready")) {autoConnectDesktop=true;if(section!=7)Navigate(7);}
        progress.IsIndeterminate=value.Percent is null;
        if(value.Percent is double percent)progress.Value=percent;
    }
    private void AllowEditing(bool allowed)
    {
        for(var i=0;i<navigationButtons.Count;i++)navigationButtons[i].IsEnabled=allowed || i==7;
        foreach(var panel in pages.Take(5)) panel.IsEnabled=allowed;
        consent.IsEnabled=allowed; license.IsEnabled=allowed && windowsImages.SelectedItem is WindowsImageChoice; previous.IsEnabled=next.IsEnabled=allowed;
        if(desktopNavigation is not null)desktopNavigation.IsEnabled=allowed;
        RefreshCreationFields();UpdateDesktopActivity();
    }
    private async Task Perform(Func<SetupOptions, CancellationToken, Task> operation, bool useSavedOptions = false)
    {
        if (Machine.HyperVStandaloneIneligibilityReason() is { } ineligibility)
        {
            Notice(ineligibility);
            return;
        }
        if (busy) { Notice("请等待当前操作完成，或先暂停。"); return; }
        try { taskFaulted=false;installationNetworkFailed=false;latestTaskDownload="";RecordTaskActivity("正在核对原任务与所选节点…"); var o = useSavedOptions ? state.Options : Options(); busy = true; AllowEditing(false);SetDesktopConnectionStatus(DesktopWaitingText()); cancellation = new(); await operation(o, cancellation.Token); }
        catch(OperationCanceledException){state.Status="已暂停，可继续原任务";RecordTaskActivity("任务已暂停，原任务与磁盘保留。");SetDesktopConnectionStatus("任务已暂停，点击继续原任务后恢复。");SaveCurrentTask();}
        catch (Exception ex)
        {
            taskFaulted = true;
            installationNetworkFailed = ex is BootstrapProbeException;
            var signInNetwork = ex is HandoffException { Action.Id: "guest-auth-sync" } &&
                BoundaryRuntime.IsWindowsSignInNetworkActive(state);
            var message = signInNetwork
                ? "安装网络已暂时保留。请先在虚拟机中完成 Windows 登录，再同步来宾账号密码。"
                : Redaction.SafeError(ex);
            RecordTaskActivity(message);
            SetDesktopConnectionStatus(section == 7 && installationNetworkFailed ? "安装网络未连通，可继续操作原桌面。" : message);
            log.AppendText(message + Environment.NewLine);
            var guestNetworkProbeFailed = state.PendingActions.Any(ResumePlan.IsGuestNetworkProbe);
            if (!closeAfterPause && !(section == 7 && (installationNetworkFailed || signInNetwork || guestNetworkProbeFailed))) Notice(message);
        }
        finally { busy = false; if (!string.IsNullOrEmpty(state.VmId)) { updatingCpuRecommendation = true; try { cpu.Text = state.Options.CpuCount.ToString(); memory.Text = state.Options.MemoryMb.ToString(); useCustomVmConfig.IsChecked = state.Options.ManualCpuConfirmed; } finally { updatingCpuRecommendation = false; } } SaveCurrentTask();progress.IsIndeterminate=false; AllowEditing(true); cancellation?.Dispose(); cancellation = null; RefreshHost(); RefreshPending(); CompleteCloseAfterPause(); }
    }
    private async Task StartInstall(bool applyRoute = false, bool checkSavedNetwork = false, bool reconfigureGuestNetwork = false)
    {
        if (installationFinished is not null || recoveringDisplay) return;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        installationFinished = finished;
        try
        {
            if (state.SetupFinished && !applyRoute && !checkSavedNetwork && !reconfigureGuestNetwork)
            {
                await EnterOriginalWorkspace();
                return;
            }
            await Perform((o, token) => InstallOriginalTask(o, token, applyRoute, checkSavedNetwork, reconfigureGuestNetwork));
        }
        finally
        {
            installationFinished = null;
            finished.TrySetResult();
            UpdateDesktopActivity();
        }
    }

    private async Task InstallOriginalTask(SetupOptions o, CancellationToken token, bool applyRoute, bool checkSavedNetwork, bool reconfigureGuestNetwork)
    {
        var secrets = ReadSecrets();
        secrets.BackupRoutes = [.. (pendingBackupRoutes ?? appliedSecrets?.BackupRoutes ?? [])];
        if (platform.SelectedItem is PlatformChoice selected && (!selected.Compatible || !selected.Automatic)) throw new SetupException(selected.Name + "：" + selected.Reason + "\n\n" + string.Join("\n", selected.Instructions));
        if (o.NetworkSource != "稍后配置" && (country.SelectedValue is null || timeZone.SelectedValue is null)) throw new SetupException("请按节点实际地区选择国家和城市时区。程序不会默认采用美国时区。");
        var currentNetworkInput = o.NetworkSource == "已有订阅链接" ? subscription.Text.Trim() : o.NetworkSource == "手动 HTTP / SOCKS5 节点" ? ManualFingerprint() : networkText.Text;
        if (o.NetworkSource != "稍后配置" && (string.IsNullOrWhiteSpace(currentNetworkInput) || node.SelectedItem is not ProxyNode || importedInput != currentNetworkInput)) throw new SetupException("请先读取并选择一个节点；网络资料已变化或尚未载入时需重新读取，避免使用旧配置。");
        if (state.Completed.Contains("software"))
        {
            if (applyRoute)
            {
                await RefreshSelectedRoutes(o, secrets, token);
                await ApplyConfiguredRoute(o, secrets, token);
                return;
            }
            var saved = AppliedLogin();
            if (state.Options.NetworkSource != "稍后配置")
            {
                if (saved.SelectedNodeJson.Length == 0) throw new SetupException("请先载入或重新填写已应用线路。");
                if (o.NetworkSource != state.Options.NetworkSource ||
                    !NetworkImport.SameSavedDefinition(secrets.SelectedNodeJson, saved.SelectedNodeJson) ||
                    !NetworkImport.SameSavedDefinition(secrets.UpstreamNodeJson, saved.UpstreamNodeJson) ||
                    secrets.ExpectedExitIp != saved.ExpectedExitIp ||
                    o.Country != state.Options.Country || o.TimeZoneId != state.Options.TimeZoneId)
                    throw new SetupException("存在尚未应用的网络草稿。请点「应用线路」，或恢复原配置后继续 AdsPower。");
                secrets = saved;
                o.NodeName = state.Options.NodeName;
            }
            else if (o.NetworkSource != "稍后配置")
                throw new SetupException("请在「网络节点」点击「应用线路」。");
        }
        else await RefreshSelectedRoutes(o, secrets, token);
        if (File.Exists(o.StatePath))
        {
            var existing = JsonFile.Read<SessionState>(o.StatePath);
            existing.RequireResumeIdentity(o);
            if (!string.IsNullOrEmpty(existing.VmId) && (existing.Options.VmName != o.VmName || existing.Options.UserName != o.UserName || existing.Options.MemoryMb != o.MemoryMb || existing.Options.DiskGb != o.DiskGb || existing.Options.CpuCount != o.CpuCount)) throw new SetupException("任务已创建虚拟机，名称、账号和资源分配已固定。请载入原任务继续；需要另建时选择新目录。");
            if (existing.Options.PlatformId != o.PlatformId) throw new SetupException("已有任务的虚拟机方案已固定；切换方案请新建独立任务目录。");
            o.ManualCpuConfirmed = existing.Options.ManualCpuConfirmed;
            o.GuestLocalAppData = existing.Options.GuestLocalAppData;
            o.UsesMicrosoftGuestAccount = existing.Options.UsesMicrosoftGuestAccount;
            state = existing;
        }
        state.Options = o; RememberTask();
        desktopConnectionPolicy.ResumeExplicitly();
        if (consoleHost?.IsAttached == true)
        {
            desktopConnectionPolicy.ObserveRunning();
            desktopConnectionPolicy.ObserveWindow();
        }
        else
        {
            desktopConnectionCancellation?.Cancel();
            lastConsoleWindow = null;
        }
        await Engine().Run(state, secrets, token, preserveUnloadedSecrets: !secretsLoaded, checkSavedNetwork: checkSavedNetwork, reconfigureGuestNetwork: reconfigureGuestNetwork);
        secretsLoaded = true;
        RememberAppliedSecrets(secrets);
        var adsPending = state.PendingActions.FirstOrDefault(action => action.Id is "adspower" or "browser-active" or CustomerGuide.ManualAdsPowerId);
        if (adsPending is not null)
            Notice(adsPending.Title + "\n\n" + string.Join("\n", adsPending.Steps));
        else
            Notice(state.Stage + "\n\n进入虚拟机桌面，按左侧待办完成浏览器验收。");
    }
    private async Task ParseNodes()
    {
        if (busy) return;
        try
        {
            busy = true; cancellation = new(); AllowEditing(false); var selected = source.SelectedItem?.ToString();
            if (selected == "稍后配置") { nodes = []; node.ItemsSource = nodes; Notice("已选择稍后配置网络。安装结果会保留网络待配置状态。"); return; }
            if (selected == "手动 HTTP / SOCKS5 节点") { ManualNode(); return; }
            var text = selected == "已有订阅链接" ? await NetworkImport.Fetch(subscription.Text.Trim(), cancellation.Token) : networkText.Text;
            var parsed = NetworkImport.Parse(text);
            ReplaceFinalDraft(parsed);
            Notice($"载入 {nodes.Count} 个支持的节点。请由你选择一个；可导入不代表已测通。" + (parsed.Unsupported > 0 ? $"另有 {parsed.Unsupported} 个未支持的节点未导入。" : ""));
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { }
        catch (Exception ex) { if (!closeAfterPause) Notice(Redaction.SafeError(ex)); }
        finally { busy = false; cancellation?.Dispose(); cancellation = null; AllowEditing(true); CompleteCloseAfterPause(); }
    }
    private async Task ProbeSelectedNode()
    {
        if (busy) { Notice("请等待当前操作完成，或先暂停。"); return; }
        if (node.SelectedItem is not ProxyNode selectedNode) { Notice("请先导入并选择一个节点，再执行出口测速与识别。"); return; }
        ProxyNode? upstream = null;
        if (chain.IsChecked == true)
        {
            if (upstreamNode.SelectedItem is not ProxyNode explicitUpstream) { Notice("已开启链式配置，请先选择前置节点。"); return; }
            upstream = explicitUpstream;
        }
        else if (selectedNode.Upstream is not null) upstream = selectedNode.Upstream;

        try
        {
            busy = true;
            cancellation = new();
            AllowEditing(false);
            banner.Text = "正在经所选节点测速并识别出口国家与时区…";
            var probeOptions = new SetupOptions { Root = root.Text.Trim() };
            var result = await NodeExitProbe.ProbeAsync(probeOptions, selectedNode, upstream, cancellation.Token,
                new Progress<DownloadProgress>(p => { progressText.Text = p.Display; banner.Text = p.Display; }));
            updatingCpuRecommendation = true;
            try
            {
                if (result.SupportedRegion) country.SelectedValue = result.CountryCode;
                if (!string.IsNullOrEmpty(result.WindowsTimeZoneId)) timeZone.SelectedValue = result.WindowsTimeZoneId;
            }
            finally { updatingCpuRecommendation = false; }

            var regionHint = result.SupportedRegion
                ? $"已自动选中出口地区：{result.CountryCode}"
                : $"检测到出口地区 {result.CountryCode}（当前列表未内置该地区自动语言模板，请确认节点地区）";
            var zoneHint = string.IsNullOrEmpty(result.WindowsTimeZoneId)
                ? "请手动核对 Windows 时区"
                : $"已自动匹配时区：{result.WindowsTimeZoneId}" + (string.IsNullOrEmpty(result.IanaTimeZone) ? "" : $"（{result.IanaTimeZone}）");
            var ifaceNote = string.IsNullOrEmpty(result.OutboundInterfaceSummary) ? "" : $" · {result.OutboundInterfaceSummary}";
            var ifaceLine = string.IsNullOrEmpty(result.OutboundInterfaceSummary) ? "" : $"\n宿主机出站接口：{result.OutboundInterfaceSummary}";
            banner.Text = $"检测服务已返回出口资料（{result.LatencyMs} ms）· {regionHint}{ifaceNote}";
            if (MessageBox.Show(this,
                $"出口检测服务响应耗时 {result.LatencyMs} ms\n" +
                $"检测出口 IP：{result.ExitIp}{ifaceLine}\n" +
                $"{regionHint}\n{zoneHint}\n\n" +
                "是否将该 IP 填入“预期固定出口 IPv4”？（独享静态 IP 推荐选择“是”；机场动态节点请选择“否”留空）",
                "节点出口测速与自动匹配", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                expectedIp.Text = result.ExitIp;
            }
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { }
        catch (NodeProbeException error)
        {
            banner.Text = "出口资料待核对，可手动选择地区和时区。";
            if (!closeAfterPause) Notice(error.Message);
        }
        catch (Exception ex) { if (!closeAfterPause) Notice(Redaction.SafeError(ex)); }
        finally { busy = false; cancellation?.Dispose(); cancellation = null; AllowEditing(true); CompleteCloseAfterPause(); }
    }
    private string ManualFingerprint() => JsonSerializer.Serialize(new { scheme = protocol.SelectedItem?.ToString(), host = server.Text, port = port.Text, user = proxyUser.Text, password = proxyPassword.Password });
    private void ManualNode()
    {
        try
        {
            if (!int.TryParse(port.Text, out var number)) throw new SetupException("请填写正确的端口。");
            var scheme = protocol.SelectedItem?.ToString() ?? "socks5";
            var proxy = NetworkImport.Validate(new() { ["name"] = "手动节点", ["type"] = scheme == "https" ? "http" : scheme, ["server"] = server.Text.Trim(), ["port"] = number, ["username"] = proxyUser.Text, ["password"] = proxyPassword.Password, ["tls"] = scheme == "https" });
            source.SelectedIndex = 2; nodes = [proxy]; node.ItemsSource = nodes; node.SelectedIndex = 0; importedInput = ManualFingerprint(); Notice("已采用手动节点。请核对出口地区与时区。");
        }
        catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
    }
    private async Task StartIndependentTask()
    {
        if (busy || installationFinished is not null || recoveringDisplay || string.IsNullOrEmpty(state.VmId)) return;
        var previous = state;
        if (MessageBox.Show(this,
            "将保存当前任务并暂停其受控连接，然后打开独立的新建向导。旧虚拟机、磁盘和任务记录会保留。若原 ISO 仍在，将预填路径供重新核验。继续吗？",
            "新建独立环境任务", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!SaveCurrentTask(showFailure: true)) return;

        busy = true;
        try
        {
            desktopTimer.Stop();
            desktopConnectionCancellation?.Cancel();
            await BoundaryRuntime.StopFor(previous);
            var options = new SetupOptions();
            var suffix = options.WorkspaceId[..8];
            options.Root = Path.Combine(Path.GetDirectoryName(previous.Options.Root) ?? options.Root, $"ClaudeWorkspace-{suffix}");
            options.VmName = $"Claude-Workspace-{suffix}";
            if (File.Exists(previous.Options.IsoPath)) options.IsoPath = previous.Options.IsoPath;
            ApplyLoadedTask(new SessionState { Options = options }, loadSecrets: false);
            log.Clear();
            Go(0);
            banner.Text = "已打开新任务。请选择独立目录并重新核对镜像和安装选项。";
        }
        catch (Exception error) { Notice(Redaction.SafeError(error)); }
        finally { busy = false; CompleteCloseAfterPause(); }
    }

    internal static bool TryRejectUnsupportedTaskForEdition(SessionState loaded, out string message,
        HostInfo? hostOverride = null, PlatformFacts? factsOverride = null)
    {
        if (!ProductProfile.IsHyperVStandalone)
        {
            message = "";
            return false;
        }
        var platformId = loaded.Options?.PlatformId ?? "";
        if (!string.Equals(platformId, "hyperv", StringComparison.OrdinalIgnoreCase))
        {
            message = $"此任务使用的是「{platformId}」方案，不属于 {ProductProfile.WindowTitle}。程序已保留原任务目录、虚拟机和凭据未作任何改动；请继续使用原版 Claudome 打开该任务。";
            return true;
        }
        if (Machine.HyperVStandaloneIneligibilityReason(hostOverride, factsOverride) is { } hostReason)
        {
            message = hostReason;
            return true;
        }
        message = "";
        return false;
    }

    private void LoadTask(string? selectedPath = null, bool loadSecrets = true, bool interactive = true)
    {
        try
        {
            if (selectedPath is null) { var dialog = new OpenFileDialog { Filter = "安装状态|setup-state.json" }; if (dialog.ShowDialog() != true) return; selectedPath = dialog.FileName; }
            var loaded = JsonFile.Read<SessionState>(selectedPath);
            if (loaded.Options is null || string.IsNullOrWhiteSpace(loaded.Options.Root))
                throw new SetupException("状态文件缺少有效的任务目录。");
            if (!Path.GetFullPath(selectedPath).Equals(Path.GetFullPath(loaded.Options.StatePath), StringComparison.OrdinalIgnoreCase))
                throw new SetupException("状态文件位置与任务目录不一致，请选择原目录中的文件。");
            if (TryRejectUnsupportedTaskForEdition(loaded, out var rejectReason))
            {
                if (interactive) Notice(rejectReason); else banner.Text = rejectReason;
                return;
            }
            loaded.Options.Validate(false);
            _ = BoundaryRuntime.Replace(null, "");
            var credentialsNote = ApplyLoadedTask(loaded, loadSecrets, interactive);
            RememberTask();
            log.Text = "已载入：" + state.Stage + "\n状态：" + state.Status + (string.IsNullOrEmpty(credentialsNote) ? "" : "\n" + credentialsNote) + "\n请重新确认安装范围后继续。"; Go(5);
            if (!string.IsNullOrEmpty(credentialsNote)) banner.Text = credentialsNote;
        }
        catch (Exception ex) { if (interactive) Notice(Redaction.SafeError(ex)); else banner.Text = Redaction.SafeError(ex); }
    }
    private string ApplyLoadedTask(SessionState loaded, bool loadSecrets, bool interactive = true)
    {
        // A task without saved secrets must never inherit the previous task's inputs.
        ClearChainInputs();
        showPassword.IsChecked = false;
        password.Clear(); passwordVisible.Clear(); adsApiKey.Clear(); proxyPassword.Clear();
        guestMicrosoftAddress = "";
        guestMicrosoftPassword = "";
        foreach (var input in new[] { subscription, networkText, expectedIp, server, proxyUser, isoUrl }) input.Clear();
        protocol.SelectedIndex = 0; port.Text = "1080";
        nodes = []; node.ItemsSource = null; importedInput = ""; ClearMedia();
        ReleaseDesktopWindow();
        state = loaded;
        appliedSecrets = null;
        routeMeasurements.Clear();
        pendingBackupRoutes = null;
        UpdateRouteSummary();
        taskFaulted=false;installationNetworkFailed=false;latestTaskActivity="";latestTaskDownload="";
        if(state.PendingActions.RemoveAll(a=>a.Id=="isolation-boundary")>0){state.Status="旧任务已载入，请重新检查当前方案";state.Stage="按当前流程重新确认镜像与隔离配置";}
        var o = state.Options;
        platform.SelectedValue = o.PlatformId; RefreshPending();
        updatingCpuRecommendation = true;
        try
        {
            root.Text = o.Root; vmName.Text = o.VmName; memory.Text = o.MemoryMb.ToString(); cpu.Text = string.IsNullOrEmpty(state.VmId) && !o.ManualCpuConfirmed ? SetupOptions.RecommendedCpuCount(o.PlatformId).ToString() : o.CpuCount.ToString(); useCustomVmConfig.IsChecked = o.ManualCpuConfirmed; disk.Text = o.DiskGb.ToString(); iso.Text = o.IsoPath; isoHash.Text = o.IsoSha256; imageIndex.Text = o.WindowsImageIndex.ToString(); user.Text = o.UserName; source.SelectedItem = o.NetworkSource; country.SelectedValue = o.Country; timeZone.SelectedValue = o.TimeZoneId; remember.IsChecked = o.Remember; outage.IsChecked = o.TestOutage; manualAdsPower.IsChecked = o.ManualAdsPower;
        }
        finally { updatingCpuRecommendation = false; }
        chain.IsChecked=o.ChainEnabled&&!o.ChainFromImport;upstreamSource.SelectedItem=o.UpstreamSource;
        if(WindowsMedia.Saved(o) is { } media) ApplyMedia(media,o.WindowsImageIndex);
        desktopTimer.Stop();autoConnectDesktop=false;desktopConnectionPolicy.ResumeExplicitly();lastConsoleWindow=null;pendingConsole=null;if(desktopGuidePanel is not null)navigation.Children.Remove(desktopGuidePanel);if(desktopNavigation is not null)navigation.Children.Remove(desktopNavigation);desktopView=null;consoleHost=null;desktopGuidePanel=null;desktopNavigation=null;guideStepChoice=null;guideStepText=null;guidePhase="";
        RefreshCreationFields();taskFaulted=false;latestTaskActivity="";latestTaskDownload="";
        consent.IsChecked = false; license.IsChecked = false;
        secretsLoaded = !o.Remember || loadSecrets;
        Secrets? saved = null;
        if (loadSecrets && o.Remember)
        {
            try { saved = SecretStore.Load(loaded); }
            catch (SetupException ex)
            {
                secretsLoaded = false;
                if (interactive) throw;
                remember.IsChecked = false;
                return ex.Message;
            }
        }
        if (saved is not null)
        {
            RememberAppliedSecrets(saved);
            LoadChainSecrets(saved);
            if (o.UsesMicrosoftGuestAccount)
            {
                guestMicrosoftAddress = saved.VmMicrosoftAddress;
                guestMicrosoftPassword = saved.VmPassword;
            }
            else password.Password = saved.VmPassword;
            subscription.Text = saved.SubscriptionUrl; networkText.Text = saved.NetworkText; expectedIp.Text = saved.ExpectedExitIp; adsApiKey.Password = saved.AdsPowerApiKey;
            if (!string.IsNullOrEmpty(saved.SelectedNodeJson))
            {
                var parsed = NetworkImport.Parse("{\"proxies\":[" + saved.SelectedNodeJson + "]}"); nodes = parsed.Nodes; node.ItemsSource = nodes; node.SelectedIndex = 0;
                if (o.NetworkSource == "手动 HTTP / SOCKS5 节点")
                {
                    using var json = JsonDocument.Parse(saved.SelectedNodeJson); var n = json.RootElement;
                    server.Text = parsed.Nodes[0].Server; port.Text = parsed.Nodes[0].Port.ToString();
                    protocol.SelectedItem = n.GetProperty("type").GetString() == "http" && n.TryGetProperty("tls", out var tls) && tls.ValueKind == JsonValueKind.True ? "https" : n.GetProperty("type").GetString();
                    proxyUser.Text = n.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
                    proxyPassword.Password = n.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
                }
                RestoreChainRelationship(saved,o);
                importedInput = o.NetworkSource == "已有订阅链接" ? subscription.Text.Trim() : o.NetworkSource == "手动 HTTP / SOCKS5 节点" ? ManualFingerprint() : networkText.Text;
            }
        }
        return "";
    }
    private Border BuildGlobalUpdateBanner()
    {
        globalUpdateBannerText = new TextBlock
        {
            FontSize = 12,
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        globalUpdateActionButton = Action("立即原地更新", async (_, _) => await ApplyPendingOnlineUpdateAsync(), true);
        globalUpdateActionButton.Padding = new Thickness(13, 6, 13, 6);
        globalUpdateActionButton.Margin = new Thickness(12, 0, 6, 0);
        var dismiss = Action("稍后再说", (_, _) =>
        {
            if (globalUpdateBanner is not null) globalUpdateBanner.Visibility = Visibility.Collapsed;
        });
        dismiss.Padding = new Thickness(11, 6, 11, 6);
        dismiss.Margin = new Thickness(0);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { globalUpdateActionButton, dismiss }
        };
        DockPanel.SetDock(actions, Dock.Right);
        var layout = new DockPanel();
        layout.Children.Add(actions);
        layout.Children.Add(globalUpdateBannerText);
        return new Border
        {
            Name = "GlobalUpdateBanner",
            Background = Pale,
            BorderBrush = Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 14),
            Visibility = Visibility.Collapsed,
            Child = layout
        };
    }
    private Border BuildUpdateCard()
    {
        if (overviewUpdateCard is null)
        {
            var current = SelfUpdater.CurrentVersion().ToString(3);
            updateStatusText.Text = ProductProfile.OnlineUpdatesEnabled
                ? $"当前版本：{ProductProfile.WindowTitle} v{current}。支持联网检查最新版（SHA-256 校验后本地原地替换重启），也支持直接选取收到的 .zip 压缩包原地升级，不改动任务与虚拟机。"
                : $"当前版本：{ProductProfile.WindowTitle} v{current}。在线更新暂未开放，请前往预发布页手动下载更新包，或直接选取收到的同版系 .zip 压缩包本地原地升级，不改动任务与虚拟机。";
            checkUpdateButton = ProductProfile.OnlineUpdatesEnabled
                ? Action("检查在线更新", async (_, _) => await CheckForUpdatesAsync(interactive: true))
                : Action("打开预发布页", (_, _) => OpenUrl(ProductProfile.PreReleasePageUrl));
            applyUpdateButton = Action("一键更新并重启", async (_, _) => await ApplyPendingOnlineUpdateAsync(), true);
            applyUpdateButton.Visibility = Visibility.Collapsed;
            localZipUpdateButton = Action("从本地 ZIP 更新", async (_, _) => await ApplyLocalZipUpdateAsync());
            overviewUpdateCard = Card(
                Text("版本与本地更新", 15, false),
                updateStatusText,
                updateProgressBar,
                Row(applyUpdateButton, checkUpdateButton, localZipUpdateButton));
            overviewUpdateCard.Padding = new Thickness(20, 16, 20, 14);
            overviewUpdateCard.Margin = new Thickness(0, 0, 5, 8);
        }
        if (overviewUpdateCard.Parent is Panel prior) prior.Children.Remove(overviewUpdateCard);
        return overviewUpdateCard;
    }
    private void SetUpdateButtonsEnabled(bool enabled)
    {
        if (checkUpdateButton is not null) checkUpdateButton.IsEnabled = enabled;
        if (applyUpdateButton is not null) applyUpdateButton.IsEnabled = enabled;
        if (localZipUpdateButton is not null) localZipUpdateButton.IsEnabled = enabled;
    }
    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (!ProductProfile.OnlineUpdatesEnabled)
        {
            if (interactive)
                updateStatusText.Text = $"在线更新暂未开放（当前为 {ProductProfile.WindowTitle} v{SelfUpdater.CurrentVersion().ToString(3)}），请前往预发布页手动下载或使用下方“从本地 ZIP 更新”。";
            return;
        }
        if (checkingUpdates) return;
        checkingUpdates = true;
        SetUpdateButtonsEnabled(false);
        if (interactive) updateStatusText.Text = "正在检查最新版本…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var result = await SelfUpdater.CheckForUpdateAsync(SelfUpdater.CurrentVersion(), token: cts.Token);
            pendingUpdateCheck = result;
            if (result.HasUpdate && result.Manifest is { } m)
            {
                ShowAvailableUpdateNotice(result, m);
            }
            else if (interactive)
            {
                updateStatusText.Text = result.StatusMessage;
                if (applyUpdateButton is not null) applyUpdateButton.Visibility = Visibility.Collapsed;
                if (globalUpdateBanner is not null) globalUpdateBanner.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex) when (interactive)
        {
            updateStatusText.Text = "检查更新未成功（可确认代理或直接点击右侧“从本地 ZIP 更新”）：" + Redaction.SafeError(ex);
        }
        catch { /* Keep startup background check silent when offline. */ }
        finally
        {
            checkingUpdates = false;
            SetUpdateButtonsEnabled(true);
        }
    }
    internal void ShowAvailableUpdateNotice(UpdateCheckResult result, ReleaseManifest m)
    {
        pendingUpdateCheck = result;
        updateStatusText.Text = $"发现新版本 v{m.Version}（{m.ZipName}），已就绪下载校验与本地原地更新。";
        if (applyUpdateButton is not null)
        {
            applyUpdateButton.Content = $"一键更新到 v{m.Version} 并重启";
            applyUpdateButton.Visibility = Visibility.Visible;
        }
        if (globalUpdateBannerText is not null)
            globalUpdateBannerText.Text = $"发现新版本 v{m.Version}（当前 v{SelfUpdater.CurrentVersion().ToString(3)}）：支持通过 SHA-256 校验后在本地原地升级，虚拟机和任务目录完全保留。";
        if (globalUpdateActionButton is not null)
            globalUpdateActionButton.Content = $"一键更新到 v{m.Version}";
        if (globalUpdateBanner is not null)
            globalUpdateBanner.Visibility = Visibility.Visible;
        banner.Text = $"发现新版本 v{m.Version}，点击上方提示条即可直接在本地升级。";
    }
    internal static (string Message, string ActionLabel, bool ClearErrorMarker)? DescribePendingUpdateNotice(SelfUpdater.PendingUpdateNotice pending)
    {
        if (pending.IsUpdateInProgress)
        {
            return (
                "后台正在执行原地更新替换，请等待当前更新完成后再试。",
                "",
                false);
        }
        if (pending.IsRollbackIncomplete)
        {
            var filesText = pending.UnrecoveredFiles is { Length: > 0 }
                ? string.Join("、", pending.UnrecoveredFiles)
                : "部分核心文件";
            return (
                $"上次原地更新失败且回滚未完成，以下文件仍未恢复原版：{filesText}。关闭占用这些文件的程序后，点右侧按钮继续从本地备份恢复。",
                "继续恢复原版",
                false);
        }
        if (!string.IsNullOrWhiteSpace(pending.ErrorReason))
        {
            if (pending.IsOriginalGroupVerifiedRestored)
            {
                return (
                    $"上次原地更新未完成，已核验原版程序及必需文件完整（{pending.ErrorReason}）。关闭占用文件后可点右侧按钮重试。",
                    "重试检查更新",
                    true);
            }
            return (
                $"上次原地更新异常且未能确认原版完整（{pending.ErrorReason}）。请核实或点击右侧按钮继续恢复后重试。",
                "继续恢复原版",
                false);
        }
        if (!string.IsNullOrWhiteSpace(pending.DeferredFile))
        {
            return (
                $"主程序已更新，但随包文件 {pending.DeferredFile} 上次被占用尚未替换。",
                "补全随包文件",
                false);
        }
        return null;
    }

    private void SurfacePendingUpdateArtifacts()
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
        var pending = SelfUpdater.ReadPendingUpdateNotice(appDir);
        if (DescribePendingUpdateNotice(pending) is not { } display)
            return;

        updateStatusText.Text = display.Message;
        if (globalUpdateBannerText is not null && globalUpdateBanner is not null && globalUpdateActionButton is not null)
        {
            globalUpdateBannerText.Text = display.Message;
            if (string.IsNullOrEmpty(display.ActionLabel))
            {
                globalUpdateActionButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                globalUpdateActionButton.Content = display.ActionLabel;
                globalUpdateActionButton.Visibility = Visibility.Visible;
            }
            globalUpdateBanner.Visibility = Visibility.Visible;
        }
        banner.Text = display.Message;
        if (display.ClearErrorMarker)
            SelfUpdater.ClearUpdateErrorMarker(appDir);
    }

    private async Task ApplyPendingOnlineUpdateAsync()
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
        var pending = SelfUpdater.ReadPendingUpdateNotice(appDir);
        if (pending.IsUpdateInProgress)
        {
            Notice("后台正在执行原地更新替换，请等待当前更新完成后再试。");
            return;
        }
        if (pending.IsRollbackIncomplete)
        {
            try
            {
                var resumed = SelfUpdater.ResumePendingRollback(appDir);
                updateStatusText.Text = resumed.Message;
                banner.Text = resumed.Message;
                if (resumed.Succeeded)
                {
                    if (globalUpdateBanner is not null) globalUpdateBanner.Visibility = Visibility.Collapsed;
                }
                else if (globalUpdateBannerText is not null && globalUpdateBanner is not null && globalUpdateActionButton is not null)
                {
                    globalUpdateBannerText.Text = resumed.Message;
                    globalUpdateActionButton.Content = "继续恢复原版";
                    globalUpdateBanner.Visibility = Visibility.Visible;
                }
                Notice(resumed.Message);
                return;
            }
            catch (Exception ex) { Notice("继续恢复原版未成功：" + Redaction.SafeError(ex)); return; }
        }
        if (!string.IsNullOrWhiteSpace(pending.DeferredFile))
        {
            try
            {
                SelfUpdater.RetryDeferredSideFiles(appDir);
                updateStatusText.Text = $"已补全更新随包文件（{pending.DeferredFile}）。";
                if (globalUpdateBanner is not null) globalUpdateBanner.Visibility = Visibility.Collapsed;
                Notice(updateStatusText.Text);
                return;
            }
            catch (Exception ex) { Notice("补全随包文件未成功：" + Redaction.SafeError(ex)); return; }
        }
        if (pendingUpdateCheck is not { HasUpdate: true, Manifest: { } manifest } check)
        {
            if (SelfUpdater.TryGetPendingUpdateBlocker(appDir, out var blockerMsg))
            {
                Notice(blockerMsg);
                return;
            }
            await CheckForUpdatesAsync(interactive: true);
            return;
        }
        if (SelfUpdater.TryGetPendingUpdateBlocker(appDir, out var preDownloadBlocker))
        {
            Notice(preDownloadBlocker);
            return;
        }
        await ExecuteLocalSwapUpdateAsync(
            manifest.Version,
            (progressReporter, token) => SelfUpdater.DownloadAndStageAsync(manifest, check.DownloadUrl, SelfUpdater.CurrentVersion(), Path.GetTempPath(), progressReporter, token));
    }
    private async Task ApplyLocalZipUpdateAsync()
    {
        var currentExe = Environment.ProcessPath;
        var appDir = Path.GetDirectoryName(currentExe ?? "") ?? AppContext.BaseDirectory;
        if (SelfUpdater.TryGetPendingUpdateBlocker(appDir, out var blockerMsg))
        {
            Notice(blockerMsg);
            return;
        }
        var dialog = new OpenFileDialog { Filter = $"{ProductProfile.WindowTitle} 更新包|{ProductProfile.PackagePrefix}_*_win-x64.zip|ZIP 压缩包|*.zip", Title = $"选择新版 {ProductProfile.WindowTitle} 安装包" };
        if (dialog.ShowDialog() != true) return;
        var current = SelfUpdater.CurrentVersion();
        var stagingDir = Path.Combine(Path.GetTempPath(), "claudome-stage-" + Guid.NewGuid().ToString("N"));
        await ExecuteLocalSwapUpdateAsync(
            "本地包",
            (_, token) => SelfUpdater.ExtractAndVerifyPackageAsync(dialog.FileName, stagingDir, current, null, null, allowSameVersion: false, token),
            stagingDir);
    }
    private async Task ExecuteLocalSwapUpdateAsync(
        string targetLabel,
        Func<IProgress<double>, CancellationToken, Task<StagedUpdate>> stagePackage,
        string? cleanupDirOnFailure = null)
    {
        if (busy) { Notice("当前正在执行部署或网络操作，请等待完成后再更新。"); return; }
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            Notice("当前未从独立 Claudome.exe 启动，无法执行原地文件替换。");
            return;
        }
        var appDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
        if (SelfUpdater.TryGetPendingUpdateBlocker(appDir, out var preBlockerMsg))
        {
            Notice(preBlockerMsg);
            return;
        }
        var confirm = MessageBox.Show(this,
            $"将验证并应用更新（{targetLabel}），随后自动重启 Claudome。\n\n• 你的虚拟机、磁盘和任务配置会完整保留；\n• 如果当前正在运行受控网络网关，重启期间会暂停网关保护，新版打开后重新连接即可。\n\n是否立即更新并重启？",
            "确认原地更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetUpdateButtonsEnabled(false);
        updateProgressBar.Visibility = Visibility.Visible;
        updateProgressBar.IsIndeterminate = false;
        updateProgressBar.Value = 0;
        updateStatusText.Text = $"正在下载并校验 {targetLabel}（SHA-256 与版本检查）…";
        StagedUpdate? staged = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var progressReporter = new Progress<double>(pct =>
            {
                updateProgressBar.Value = pct;
                updateStatusText.Text = $"正在准备更新包… {pct:F0}%";
            });
            staged = await stagePackage(progressReporter, cts.Token);
            if (SelfUpdater.TryGetPendingUpdateBlocker(appDir, out var postStageBlocker))
                throw new SetupException(postStageBlocker);
            updateProgressBar.Value = 100;
            updateStatusText.Text = $"校验通过（v{staged.Version}），正在准备重启替换…";

            await BoundaryRuntime.Replace(null, "");
            if (desktopTimer is not null) desktopTimer.Stop();
            SelfUpdater.StartSwapAndRestart(staged, currentExe, Environment.ProcessId, relaunch: true);
            Close();
        }
        catch (Exception ex)
        {
            if (staged is not null) try { Directory.Delete(staged.StagingDirectory, true); } catch { }
            else if (!string.IsNullOrEmpty(cleanupDirOnFailure)) try { Directory.Delete(cleanupDirOnFailure, true); } catch { }
            updateProgressBar.Visibility = Visibility.Collapsed;
            updateStatusText.Text = "更新已取消或校验未通过：" + Redaction.SafeError(ex);
            Notice(updateStatusText.Text);
            SetUpdateButtonsEnabled(true);
        }
    }
    private void ShowReport()
    {
        try
        {
            var path = Paths.Under(root.Text, "acceptance.json");
            if (!File.Exists(path)) throw new SetupException("尚未生成实际验收报告。");
            var reportText = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(reportText);
            var claimedSuccess = (doc.RootElement.TryGetProperty("setupFinished", out var sf) && sf.GetBoolean()) ||
                                 (doc.RootElement.TryGetProperty("browserAcceptance", out var ba) && ba.GetString() == "user-confirmed");
            if (claimedSuccess && (!state.NetworkPassed || state.Protection.Browser.Status != CheckStatus.Passed || (!state.SetupFinished && doc.RootElement.TryGetProperty("setupFinished", out var sf2) && sf2.GetBoolean())))
                throw new SetupException("检测到本地验收报告与当前防护状态不一致（可能已过期或复核未通过），请先重新核验当前防护。");
            Notice(reportText);
        }
        catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
    }
    private void Notice(string text) => MessageBox.Show(this, text, ProductProfile.WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private sealed record ChoiceItem(string Id, string Label);
}
