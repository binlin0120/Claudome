using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace ClaudeSetup;

public sealed partial class MainWindow : Window
{
    private static readonly string[] SectionNames =
    [
        "概览", "安装环境", "我的虚拟机", "网络节点", "下载管理", "检测与修复", "本机资料", "虚拟机桌面"
    ];

    private readonly MacHostEnvironment hostEnv = new();
    private readonly KeychainSecretStore secretStore = new();
    private readonly MacGatewayFirewall hostFirewall = new();
    private readonly List<Button> navButtons = [];

    private Appearance appearance = Appearance.Load();
    private SessionState state = new();
    private MacHostFacts? hostFacts;
    private List<PlatformChoice> platformChoices = [];
    private List<ProxyNode> parsedNodes = [];
    private int currentSection;
    private bool deploying;
    private CancellationTokenSource? deployCts;

    // Shared UI inputs
    private readonly TextBox rootBox = new() { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ClaudomeWorkspace") };
    private readonly TextBox vmNameBox = new() { Text = "Claude-Workspace" };
    private readonly TextBox cpuBox = new() { Text = "4" };
    private readonly TextBox memBox = new() { Text = "8192" };
    private readonly TextBox diskBox = new() { Text = "80" };
    private readonly TextBox isoBox = new() { IsReadOnly = true };
    private readonly ComboBox platformBox = new();
    private readonly TextBox networkTextBox = new() { AcceptsReturn = true, Height = 100, Watermark = "粘贴 Clash proxies YAML 或节点链接（仅在本机读取）" };
    private readonly ComboBox nodeBox = new();
    private readonly TextBox userBox = new() { Text = "ClaudeUser" };
    private readonly TextBox passwordBox = new() { PasswordChar = '•', Watermark = "至少 10 位独立虚拟机密码" };
    private readonly CheckBox rememberBox = new() { Content = "使用 macOS Keychain + AES-256-GCM 记住资料", IsChecked = true };
    private readonly TextBox deployLogBox = new() { IsReadOnly = true, AcceptsReturn = true, Height = 120, Text = "尚未开始一键部署。" };

    private IBrush canvasBrush = Brushes.WhiteSmoke;
    private IBrush surfaceBrush = Brushes.White;
    private IBrush accentBrush = SolidColorBrush.Parse("#087F8C");
    private IBrush onAccentBrush = Brushes.White;
    private IBrush inkBrush = SolidColorBrush.Parse("#15202B");
    private IBrush mutedBrush = SolidColorBrush.Parse("#5F6F81");
    private IBrush lineBrush = SolidColorBrush.Parse("#E5EAF0");

    public MainWindow()
    {
        InitializeComponent();
        BuildSidebar();
        ApplyTheme(appearance);
        ThemeToggleButton.Click += (_, _) => OpenAppearanceDialog();
        _ = RefreshHostAsync();
        Navigate(0);
    }

    private void BuildSidebar()
    {
        for (var i = 0; i < SectionNames.Length; i++)
        {
            var index = i;
            var btn = new Button
            {
                Content = SectionNames[i],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 10),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0)
            };
            btn.Click += (_, _) => Navigate(index);
            navButtons.Add(btn);
            NavPanel.Children.Add(btn);
        }
    }

    private void ApplyTheme(Appearance theme)
    {
        appearance = theme;
        var bg = Color.Parse(theme.Background);
        var accent = Color.Parse(theme.Accent);
        var dark = RelativeLuminance(bg) < 0.2;
        canvasBrush = new SolidColorBrush(bg);
        surfaceBrush = new SolidColorBrush(dark ? Mix(bg, Colors.White, 0.05) : Colors.White);
        accentBrush = new SolidColorBrush(accent);
        onAccentBrush = new SolidColorBrush(RelativeLuminance(accent) > 0.25 ? Colors.Black : Colors.White);
        inkBrush = new SolidColorBrush(dark ? Color.Parse("#EEF3F8") : Color.Parse("#15202B"));
        mutedBrush = new SolidColorBrush(dark ? Color.Parse("#9AA8B8") : Color.Parse("#5B6A7A"));
        lineBrush = new SolidColorBrush(dark ? Color.Parse("#273240") : Color.Parse("#E2E8F0"));

        Background = canvasBrush;
        Foreground = inkBrush;
        SidebarBorder.Background = surfaceBrush;
        SidebarBorder.BorderBrush = lineBrush;
        StatusBanner.Background = surfaceBrush;
        StatusBanner.BorderBrush = lineBrush;
        ThemeToggleButton.Background = surfaceBrush;
        ThemeToggleButton.Foreground = inkBrush;
        ThemeToggleButton.BorderBrush = lineBrush;
        UpdateNavStyles();
    }

    private void UpdateNavStyles()
    {
        for (var i = 0; i < navButtons.Count; i++)
        {
            var active = i == currentSection;
            navButtons[i].Background = active ? accentBrush : Brushes.Transparent;
            navButtons[i].Foreground = active ? onAccentBrush : inkBrush;
            navButtons[i].FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R * (1 - amount) + b.R * amount),
        (byte)(a.G * (1 - amount) + b.G * amount),
        (byte)(a.B * (1 - amount) + b.B * amount));

    private static double RelativeLuminance(Color c)
    {
        static double Ch(byte v) { var n = v / 255d; return n <= 0.04045 ? n / 12.92 : Math.Pow((n + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private Border MakeCard(string heading, params Control[] children)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = inkBrush });
        foreach (var child in children)
        {
            if (child.Parent is Panel prior) prior.Children.Remove(child);
            panel.Children.Add(child);
        }
        return new Border
        {
            Background = surfaceBrush,
            BorderBrush = lineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Child = panel
        };
    }

    private Button MakeButton(string text, Action onClick, bool primary = false)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(16, 9),
            CornerRadius = new CornerRadius(8),
            Background = primary ? accentBrush : surfaceBrush,
            Foreground = primary ? onAccentBrush : inkBrush,
            BorderBrush = primary ? accentBrush : lineBrush,
            BorderThickness = new Thickness(1)
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private TextBlock NoteText(string text, bool muted = true) => new()
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Foreground = muted ? mutedBrush : inkBrush
    };

    private async Task RefreshHostAsync()
    {
        hostFacts = await hostEnv.DetectAsync(CancellationToken.None);
        platformChoices = MacHostEnvironment.Recommend(hostFacts);
        platformBox.ItemsSource = platformChoices.Select(p => p.Label).ToList();
        if (platformChoices.Count > 0 && platformBox.SelectedIndex < 0) platformBox.SelectedIndex = 0;
        PlatformBadge.Text = $"{(hostFacts.IsAppleSilicon ? "Apple Silicon" : "Intel")} · macOS {hostFacts.MacOsVersion}";
        if (currentSection is 0 or 1) Navigate(currentSection);
    }

    public void Navigate(int index)
    {
        currentSection = index;
        UpdateNavStyles();
        ContentHost.Children.Clear();
        HeaderTitle.Text = SectionNames[index];

        switch (index)
        {
            case 0:
                HeaderSubtitle.Text = "Mac 版独立控制台 · 与 Windows 版共用 Claudome.Core 状态机与网关核心。";
                ContentHost.Children.Add(MakeCard("当前 Mac 主机",
                    NoteText(hostFacts?.Summary ?? "正在读取 Mac 芯片与虚拟化组件信息…", muted: false),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            MakeButton("开始新环境向导", () => Navigate(1), primary: true),
                            MakeButton("载入已有任务", async () => await LoadExistingTaskAsync()),
                            MakeButton("查看网络节点与测速", () => Navigate(3)),
                            MakeButton("打开虚拟机控制台", () => Navigate(7))
                        }
                    }));
                ContentHost.Children.Add(MakeCard("一键部署与 Mac 双层隔离边界",
                    NoteText("1. 外层防线：macOS 版 Go 网关（darwin/arm64 & darwin/amd64）配合 scoped pfctl 锚点规则，仅放行选定公网 IPv4 节点。\n" +
                             "2. 内层防线：来宾 Windows 11 内保持 Clash Verge（ARM64/x64）+ 出站全阻断防火墙 + AdsPower。\n" +
                             "3. 一键编排：填好向导配置后可直接在下方启动全自动建机、注入应答文件与来宾配置（需在真实 Mac 上配合 Fusion/UTM 验收）。"),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            MakeButton(deploying ? "正在部署…" : "开始 / 继续一键部署", async () => await StartMacDeploymentAsync(), primary: true),
                            MakeButton("暂停部署", () => { deployCts?.Cancel(); BannerText.Text = "已请求暂停 Mac 部署。"; })
                        }
                    },
                    deployLogBox));
                break;

            case 1:
                HeaderSubtitle.Text = "1. 主机检查  ·  2. 选择 Fusion / UTM 与目录  ·  3. 选择 Windows 11 镜像";
                ContentHost.Children.Add(MakeCard("虚拟化方案（按芯片推荐）",
                    platformBox,
                    NoteText(platformChoices.ElementAtOrDefault(Math.Max(0, platformBox.SelectedIndex))?.Reason ?? "")));
                ContentHost.Children.Add(MakeCard("任务目录与虚拟机资源",
                    NoteText("任务根目录"), rootBox,
                    NoteText("虚拟机名称"), vmNameBox,
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                        Children =
                        {
                            WrapField("CPU 核数", cpuBox, 0),
                            WrapField("内存 (MB)", memBox, 1),
                            WrapField("磁盘 (GB)", diskBox, 2)
                        }
                    }));
                ContentHost.Children.Add(MakeCard("Windows 11 ISO 镜像（ARM64 / x64 架构校验）",
                    isoBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            MakeButton("选择本地 ISO", async () => await PickIsoAsync(), primary: true),
                            MakeButton("校验 ISO 架构", async () => await VerifyIsoAsync())
                        }
                    }));
                break;

            case 2:
                HeaderSubtitle.Text = "管理 Mac 上的 VMware Fusion (.vmx) 与 UTM (.utm) 任务环境。";
                ContentHost.Children.Add(MakeCard("已加载任务",
                    NoteText($"当前任务目录：{rootBox.Text}\n虚拟机 ID：{(string.IsNullOrEmpty(state.VmId) ? "尚未创建" : state.VmId)}\n状态：{state.Status}"),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            MakeButton("载入 setup-state.json", async () => await LoadExistingTaskAsync(), primary: true),
                            MakeButton("检测本地 Fusion / UTM 状态", async () => { await RefreshHostAsync(); BannerText.Text = hostFacts?.Summary ?? "已刷新"; })
                        }
                    }));
                break;

            case 3:
                HeaderSubtitle.Text = "导入订阅 / 节点文本，测速并识别 macOS 本机出站网卡（物理网卡 vs 虚拟 TUN）。";
                ContentHost.Children.Add(MakeCard("节点导入与出站接口探测",
                    networkTextBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            MakeButton("解析节点列表", ParseNodesFromInput, primary: true),
                            MakeButton("测速并识别出口/本机接口", async () => await ProbeNodeAsync())
                        }
                    },
                    nodeBox));
                break;

            case 4:
                HeaderSubtitle.Text = "展示 ARM64 / x64 来宾所需组件与官方 SHA-256 核验及下载。";
                var isArm = hostFacts?.IsAppleSilicon ?? true;
                var targetAssets = MacProvisioningMedia.GuestAssets.Where(a => a.Architecture == (isArm ? "arm64" : "x64") || a.ResourceId is "vcredist" or "adspower");
                var rows = targetAssets.Select(a =>
                    NoteText($"• {a.Name} [{a.Architecture}] — {(a.ChecksumVerifiedOnMac ? "已核验 SHA-256" : "待核验")} · {a.Notes}")).ToList<Control>();
                rows.Add(MakeButton("下载并核验当前芯片全部组件", async () => await DownloadAssetsAsync(), primary: true));
                ContentHost.Children.Add(MakeCard("Mac / Win11 来宾组件清单", rows.ToArray()));
                break;

            case 5:
                HeaderSubtitle.Text = "外层 pfctl 锚点 + 来宾防火墙与出口一致性检查。";
                ContentHost.Children.Add(MakeCard("双层防护状态",
                    NoteText($"pfctl 锚点前缀：{(string.IsNullOrEmpty(state.Options.WorkspaceId) ? "未初始化" : MacGatewayFirewall.AnchorName(state.Options.WorkspaceId))}\n" +
                             $"外层网关状态：待在 macOS 主机运行后回读确认。"),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            MakeButton("运行 Mac 离线自检（Contract）", () =>
                            {
                                var outDir = Path.Combine(Path.GetTempPath(), "Claudome-Mac-self-test");
                                var exit = MacSelfTests.Run(outDir);
                                BannerText.Text = exit == 0 ? $"Mac 离线契约自检全部通过（报告位于 {outDir}）" : "Mac 离线自检存在失败项";
                            }, primary: true),
                            MakeButton("撤回 pfctl 防火墙锚点", async () =>
                            {
                                try
                                {
                                    await hostFirewall.ClearAsync(state.Options.WorkspaceId, CancellationToken.None);
                                    BannerText.Text = "已请求清理本任务 pfctl 锚点规则。";
                                }
                                catch (Exception ex) { BannerText.Text = Redaction.SafeError(ex); }
                            })
                        }
                    }));
                break;

            case 6:
                HeaderSubtitle.Text = "凭据通过 macOS Keychain 主密钥 + AES-256-GCM 加密保存在本机。";
                ContentHost.Children.Add(MakeCard("虚拟机账号与本机加密",
                    NoteText("来宾 Windows 用户名"), userBox,
                    NoteText("独立虚拟机密码（>= 10 位）"), passwordBox,
                    rememberBox));
                break;

            case 7:
                HeaderSubtitle.Text = "Mac 端通过拉起并聚焦 Fusion / UTM 原生控制台完成安装与日常操作。";
                ContentHost.Children.Add(MakeCard("连接与启动虚拟机控制台",
                    NoteText("1. 首次建机时自动生成安全引导配置（默认开启双向剪贴板，始终关闭共享文件夹和拖放）。\n" +
                             "2. 点击下方按钮唤起已选择的 VMware Fusion 或 UTM 窗口。"),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            MakeButton("打开 VMware Fusion 控制台", async () => await OpenConsoleAsync("fusion"), primary: true),
                            MakeButton("打开 UTM 控制台", async () => await OpenConsoleAsync("utm"))
                        }
                    }));
                ContentHost.Children.Add(MakeCard("双向剪贴板",
                    NoteText(state.Options.SharedClipboardEnabled
                        ? "当前状态：已默认开启双向剪贴板（需要时可一键关闭，切换后如虚拟机已在运行请重启一次来宾生效）。"
                        : "当前状态：已关闭双向剪贴板（需要时可随时重新开启）。", muted: false),
                    MakeButton(state.Options.SharedClipboardEnabled ? "关闭双向剪贴板" : "启用双向剪贴板",
                        async () => await ToggleSharedClipboardAsync())));
                break;
        }
    }

    private static StackPanel WrapField(string label, Control input, int col)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(col > 0 ? 10 : 0, 0, 0, 0), Children = { new TextBlock { Text = label, FontSize = 12 }, input } };
        Grid.SetColumn(panel, col);
        return panel;
    }

    private async Task PickIsoAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Windows 11 ISO 镜像",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Windows ISO") { Patterns = ["*.iso"] }]
        });
        if (files.Count > 0) isoBox.Text = files[0].Path.LocalPath;
    }

    private async Task VerifyIsoAsync()
    {
        try
        {
            var expectArm64 = hostFacts?.IsAppleSilicon ?? true;
            var arch = await hostEnv.InspectIsoArchitectureAsync(isoBox.Text ?? "", expectArm64, CancellationToken.None);
            BannerText.Text = $"ISO 架构校验通过：{arch}";
        }
        catch (Exception ex) { BannerText.Text = Redaction.SafeError(ex); }
    }

    private void ParseNodesFromInput()
    {
        try
        {
            var list = NetworkImport.Parse(networkTextBox.Text ?? "");
            parsedNodes = list.Nodes;
            nodeBox.ItemsSource = parsedNodes.Select(n => n.ToString()).ToList();
            if (parsedNodes.Count > 0) nodeBox.SelectedIndex = 0;
            BannerText.Text = $"已导入 {parsedNodes.Count} 个节点（跳过 {list.Unsupported} 项）。";
        }
        catch (Exception ex) { BannerText.Text = Redaction.SafeError(ex); }
    }

    private async Task ProbeNodeAsync()
    {
        if (nodeBox.SelectedIndex < 0 || nodeBox.SelectedIndex >= parsedNodes.Count)
        {
            BannerText.Text = "请先导入并选择一个节点。";
            return;
        }
        try
        {
            var selected = parsedNodes[nodeBox.SelectedIndex];
            var result = await NodeExitProbe.ProbeAsync(new SetupOptions { Root = rootBox.Text ?? "" }, selected, selected.Upstream, CancellationToken.None);
            BannerText.Text = $"出口 {result.ExitIp} ({result.CountryCode}) · {result.LatencyMs} ms · {result.OutboundInterfaceSummary}";
        }
        catch (Exception ex) { BannerText.Text = Redaction.SafeError(ex); }
    }

    private async Task ToggleSharedClipboardAsync()
    {
        try
        {
            var next = !state.Options.SharedClipboardEnabled;
            state.Options.Root = rootBox.Text ?? state.Options.Root;
            state.Options.VmName = vmNameBox.Text ?? state.Options.VmName;
            if (state.Options.PlatformId == "utm")
                await UtmBackend.SetSharedClipboardAsync(state, next, CancellationToken.None);
            else
                await FusionBackend.SetSharedClipboardAsync(state, next, CancellationToken.None);
            BannerText.Text = next
                ? "已启用双向剪贴板（如虚拟机当前正在运行，重开/重启虚拟机一次后生效）。"
                : "已关闭双向剪贴板（共享文件夹与拖放仍保持禁用）。";
            Navigate(7);
        }
        catch (Exception ex) { BannerText.Text = Redaction.SafeError(ex); }
    }

    private async Task OpenConsoleAsync(string backendId)
    {
        try
        {
            state.Options.Root = rootBox.Text ?? state.Options.Root;
            state.Options.VmName = vmNameBox.Text ?? state.Options.VmName;
            state.Options.PlatformId = backendId;
            IVmBackend backend = backendId == "utm" ? new UtmBackend() : new FusionBackend();
            await using (backend)
            {
                await backend.Open(state, CancellationToken.None);
            }
            BannerText.Text = $"已请求打开 {backendId} 控制台。";
        }
        catch (Exception ex) { BannerText.Text = Redaction.SafeError(ex); }
    }

    private async Task LoadExistingTaskAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 setup-state.json",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Claudome 任务状态") { Patterns = ["setup-state.json", "*.json"] }]
        });
        if (files.Count == 0) return;
        try
        {
            var loaded = JsonFile.Read<SessionState>(files[0].Path.LocalPath);
            state = loaded;
            rootBox.Text = loaded.Options.Root;
            vmNameBox.Text = loaded.Options.VmName;
            cpuBox.Text = loaded.Options.CpuCount.ToString();
            memBox.Text = loaded.Options.MemoryMb.ToString();
            diskBox.Text = loaded.Options.DiskGb.ToString();
            isoBox.Text = loaded.Options.IsoPath;
            userBox.Text = loaded.Options.UserName;
            rememberBox.IsChecked = loaded.Options.Remember;
            var matchIdx = platformChoices.FindIndex(c => c.Id == loaded.Options.PlatformId);
            if (matchIdx >= 0) platformBox.SelectedIndex = matchIdx;
            if (loaded.Options.Remember)
            {
                var savedSecrets = secretStore.Load(loaded.Options.Private, loaded.CredentialsRevision);
                if (savedSecrets is not null)
                {
                    passwordBox.Text = savedSecrets.VmPassword;
                    networkTextBox.Text = savedSecrets.NetworkText;
                }
            }
            BannerText.Text = $"已载入任务：{loaded.Options.VmName}（{loaded.Status}）";
            Navigate(currentSection);
        }
        catch (Exception ex) { BannerText.Text = Redaction.SafeError(ex); }
    }

    private async Task DownloadAssetsAsync()
    {
        try
        {
            var opts = new SetupOptions { Root = rootBox.Text ?? "" };
            var progress = new Progress<string>(msg =>
            {
                BannerText.Text = msg;
                deployLogBox.Text += Environment.NewLine + msg;
            });
            await MacDeployment.DownloadGuestAssetsAsync(opts, hostFacts?.IsAppleSilicon ?? true, progress, null, CancellationToken.None);
        }
        catch (Exception ex) { BannerText.Text = Redaction.SafeError(ex); }
    }

    private async Task StartMacDeploymentAsync()
    {
        if (deploying) return;
        deploying = true;
        deployCts = new CancellationTokenSource();
        try
        {
            state.Options.Root = rootBox.Text ?? state.Options.Root;
            state.Options.VmName = vmNameBox.Text ?? state.Options.VmName;
            if (int.TryParse(cpuBox.Text, out var parsedCpu)) state.Options.CpuCount = parsedCpu;
            if (int.TryParse(memBox.Text, out var parsedMem)) state.Options.MemoryMb = parsedMem;
            if (int.TryParse(diskBox.Text, out var parsedDisk)) state.Options.DiskGb = parsedDisk;
            state.Options.IsoPath = isoBox.Text ?? "";
            state.Options.UserName = userBox.Text ?? "ClaudeUser";
            state.Options.Remember = rememberBox.IsChecked == true;
            state.Options.AcceptChanges = true;
            state.Options.AcceptWindowsLicense = true;
            var chosen = platformChoices.ElementAtOrDefault(Math.Max(0, platformBox.SelectedIndex))?.Id ?? "fusion";
            state.Options.PlatformId = chosen;
            state.Options.NetworkSource = nodeBox.SelectedIndex >= 0 && nodeBox.SelectedIndex < parsedNodes.Count ? "节点链接 / Clash 配置文本" : "稍后配置";

            var secrets = new Secrets
            {
                VmPassword = passwordBox.Text ?? "",
                NetworkText = networkTextBox.Text ?? "",
                SelectedNodeJson = nodeBox.SelectedIndex >= 0 && nodeBox.SelectedIndex < parsedNodes.Count ? parsedNodes[nodeBox.SelectedIndex].Json : ""
            };
            if (state.Options.Remember && !string.IsNullOrEmpty(secrets.VmPassword))
                secretStore.Save(state.Options.Private, secrets, state.CredentialsRevision);

            deployLogBox.Text = "开始执行 Mac 一键部署流程…";
            var progress = new Progress<string>(msg =>
            {
                BannerText.Text = msg;
                deployLogBox.Text += Environment.NewLine + msg;
            });
            var facts = hostFacts ?? await hostEnv.DetectAsync(deployCts.Token);
            await MacDeployment.DeployAsync(state, secrets, facts, progress, deployCts.Token);
        }
        catch (Exception ex)
        {
            BannerText.Text = Redaction.SafeError(ex);
            deployLogBox.Text += Environment.NewLine + "中断：" + BannerText.Text;
        }
        finally
        {
            deploying = false;
            deployCts?.Dispose();
            deployCts = null;
        }
    }

    private void OpenAppearanceDialog()
    {
        var accentInput = new TextBox { Text = appearance.Accent };
        var bgInput = new TextBox { Text = appearance.Background };
        var presetsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var preset in Appearance.Presets)
        {
            var btn = new Button { Content = preset.Name, Margin = new Thickness(0, 0, 8, 8) };
            btn.Click += (_, _) =>
            {
                accentInput.Text = preset.Accent;
                bgInput.Text = preset.Background;
                ApplyTheme(preset);
            };
            presetsPanel.Children.Add(btn);
        }

        var dialog = new Window
        {
            Title = "外观与配色",
            Width = 460,
            Height = 420,
            MinWidth = 380,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = surfaceBrush,
            Foreground = inkBrush
        };
        dialog.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) dialog.Close(); };
        var content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "选择预设或自定义配色", FontSize = 18, FontWeight = FontWeight.SemiBold },
                presetsPanel,
                new TextBlock { Text = "主题色 (#RRGGBB)" }, accentInput,
                new TextBlock { Text = "背景色 (#RRGGBB)" }, bgInput,
                MakeButton("保存配色", () =>
                {
                    try
                    {
                        var next = new Appearance("自定义", accentInput.Text?.Trim() ?? "", bgInput.Text?.Trim() ?? "");
                        next.Save();
                        ApplyTheme(next);
                        dialog.Close();
                    }
                    catch (Exception ex) { BannerText.Text = Redaction.SafeError(ex); }
                }, primary: true)
            }
        };
        dialog.Content = new ScrollViewer { Content = content };
        _ = dialog.ShowDialog(this);
    }

    internal async Task<bool> RunPreviewChecksAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var rows = new List<object>();
        var failed = 0;

        await RefreshHostAsync();
        for (var i = 0; i < SectionNames.Length; i++)
        {
            Navigate(i);
            UpdateLayout();
        }
        Navigate(0);
        UpdateLayout();

        var size = new PixelSize(Math.Max(960, (int)ClientSize.Width), Math.Max(680, (int)ClientSize.Height));
        using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        rtb.Render(this);
        var shotPath = Path.Combine(outputDirectory, "claudome-mac-overview.png");
        rtb.Save(shotPath);

        var uiOk = File.Exists(shotPath) && new FileInfo(shotPath).Length > 1000 && navButtons.Count == 8 && platformChoices.Count == 2;
        if (!uiOk) failed++;
        rows.Add(new { name = "mac-avalonia-renders-all-8-sections-and-platform-choices", passed = uiOk, screenshot = shotPath });

        var selfExit = await Task.Run(() => MacSelfTests.Run(outputDirectory));
        if (selfExit != 0) failed++;
        rows.Add(new { name = "mac-backend-contract-suite", passed = selfExit == 0 });

        JsonFile.Write(Path.Combine(outputDirectory, "mac-ui-tests.json"), new
        {
            type = "Avalonia UI offscreen preview and macOS backend contracts",
            passed = rows.Count - failed,
            failed,
            tests = rows
        });
        return failed == 0;
    }
}
