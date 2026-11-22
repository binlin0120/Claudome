using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    private static readonly Brush Surface = Brush("#FFFFFF"), Line = Brush("#E5EAF0"), Canvas = Brush("#F4F6FA"), OnAccent = Brush("#FFFFFF");
    private readonly string[] sections = ["概览", "安装环境", "我的虚拟机", "网络节点", "下载管理", "检测与修复", "本机资料", "虚拟机桌面"];
    private readonly string[] glyphs = ["\uE80F", "\uE710", "\uE7F4", "\uE774", "\uE896", "\uE90F", "\uE77B", "\uE7F4"];
    private readonly WrapPanel steps = new() { Margin = new Thickness(0, 0, 0, 18) };
    private readonly TextBlock sectionBadge = new();
    private int section;
    private Appearance appearance = Appearance.Load();
    private FrameworkElement? sidebarBrand;
    private ScrollViewer? sidebarScroll;
    private FrameworkElement? sidebarFooter;
    private Button? desktopSidebarToggle, desktopShutdown;
    private bool desktopSidebarCollapsed;
    private TextBlock? downloadFeatureInfo;
    private ProgressBar? downloadFeatureBar;
    private Border? globalUpdateBanner;
    private TextBlock? globalUpdateBannerText;
    private Button? globalUpdateActionButton;

    private (Button Previous, Button Next) BuildShell()
    {
        Title = "Claudome"; Width = 1180; Height = 860; MinWidth = 980; MinHeight = 720; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14; Foreground = Ink; Background = Canvas;
        Content = ContentRoot; ContentRoot.Background = Canvas;
        SourceInitialized += (_, _) => UpdateNativeCaption();
        ContentRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(228) }); ContentRoot.ColumnDefinitions.Add(new ColumnDefinition());
        var sideGrid = new Grid { Margin = new Thickness(16, 22, 14, 18) };
        sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); sideGrid.RowDefinitions.Add(new RowDefinition()); sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sidebar = new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = sideGrid }; ContentRoot.Children.Add(sidebar);
        var brand = new StackPanel { Margin = new Thickness(8, 0, 0, 22) };
        var brandRow = new StackPanel { Orientation = Orientation.Horizontal };
        var mark = new BitmapImage(new Uri("pack://application:,,,/Brand/Claudome.png"));
        Icon = mark;
        var brandIcon = new Image { Name = "WorkspaceBrandIcon", Source = mark, Width = 40, Height = 40 };
        RenderOptions.SetBitmapScalingMode(brandIcon, BitmapScalingMode.HighQuality);
        brandRow.Children.Add(brandIcon);
        var brandText = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; brandText.Children.Add(new TextBlock { Text = "Claudome", FontWeight = FontWeights.SemiBold, FontSize = 17 }); brandText.Children.Add(new TextBlock { Text = "给 Claude 一个独立的空间", FontSize = 10, Foreground = Muted, Margin = new Thickness(0, 3, 0, 0) }); brandRow.Children.Add(brandText); brand.Children.Add(brandRow);
        sidebarBrand = brand;
        var sidebarHeader = new DockPanel();
        desktopSidebarToggle = Action("‹", (_, _) =>
        {
            desktopSidebarCollapsed = !desktopSidebarCollapsed;
            UpdateDesktopSidebarLayout(section == 7);
        });
        desktopSidebarToggle.Name = "ToggleDesktopSidebar";
        desktopSidebarToggle.Width = 28;
        desktopSidebarToggle.Height = 29;
        desktopSidebarToggle.Padding = new Thickness(0);
        desktopSidebarToggle.Margin = new Thickness(0);
        desktopSidebarToggle.VerticalAlignment = VerticalAlignment.Top;
        desktopSidebarToggle.FontSize = 21;
        DockPanel.SetDock(desktopSidebarToggle, Dock.Right);
        sidebarHeader.Children.Add(desktopSidebarToggle);
        sidebarHeader.Children.Add(brand);
        sideGrid.Children.Add(sidebarHeader);
        sidebarScroll = new ScrollViewer
        {
            Name = "SidebarScroll", Content = navigation,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(sidebarScroll,1);sideGrid.Children.Add(sidebarScroll);
        navigation.Children.Add(Text("工作空间", 10));
        for (var i = 0; i < sections.Length; i++)
        {
            var index = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyphs[i], FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17, Width = 32, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = sections[i], FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var button = Action("", (_, _) => { if (!busy || index==7) Navigate(index); }); button.Content = row; button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Padding = new Thickness(14, 13, 10, 13); button.Margin = new Thickness(0, 0, 0, 6); button.BorderThickness = new Thickness(0); navigationButtons.Add(button); navigation.Children.Add(button);
        }
        var sideBottom = new StackPanel(); sidebarFooter = sideBottom; Grid.SetRow(sideBottom, 2); sideGrid.Children.Add(sideBottom);
        sideBottom.Children.Add(Action("◐  外观与配色", (_, _) => AppearanceDialog()));
        var groupPanel = new StackPanel(); groupPanel.Children.Add(Text("一起交流", 11));
        var group = Action("交流群  1129410798", (_, _) => { Clipboard.SetText("1129410798"); banner.Text = "群号已复制"; }); group.FontSize = 11; group.Background = Pale; group.Foreground = Accent; group.BorderThickness = new Thickness(0); groupPanel.Children.Add(group);
        desktopHiddenFooter=new Border { BorderBrush=Line,BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(0,16,0,0),Margin=new Thickness(0,16,0,0),Child=groupPanel };sideBottom.Children.Add(desktopHiddenFooter);
        sideBottom.Children.Add(new TextBlock { Text = $"v{SelfUpdater.CurrentVersion().ToString(3)} · 李诗恩 · 林琛", FontSize = 10, Foreground = Muted, Margin = new Thickness(12, 12, 0, 4) });
        var body = new Grid { Margin = new Thickness(34, 26, 30, 18) };shellBody=body; Grid.SetColumn(body, 1); ContentRoot.Children.Add(body);
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); body.RowDefinitions.Add(new RowDefinition()); body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel();
        var top = new DockPanel();
        sectionBadge.Text = "本地运行";
        sectionBadge.Foreground = Muted;
        sectionBadge.FontSize = 11;
        sectionBadge.Margin = new Thickness(16, 0, 0, 0);
        sectionBadge.VerticalAlignment = VerticalAlignment.Center;
        var headingActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        desktopShutdown = Action("关闭虚拟机并退出", async (_, _) => await ShutdownDesktop());
        desktopShutdown.Name = "ShutdownDesktop";
        desktopShutdown.Margin = new Thickness(0, 0, 0, 0);
        headingActions.Children.Add(desktopShutdown);
        headingActions.Children.Add(sectionBadge);
        DockPanel.SetDock(headingActions, Dock.Right);
        top.Children.Add(headingActions);
        title.FontSize = 28;
        title.FontWeight = FontWeights.SemiBold;
        top.Children.Add(title);
        heading.Children.Add(top);
        subtitle.Foreground = Muted;
        subtitle.FontSize = 12;
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, 7, 0, 18);
        heading.Children.Add(subtitle);
        heading.Children.Add(steps);
        globalUpdateBanner = BuildGlobalUpdateBanner();
        heading.Children.Add(globalUpdateBanner);
        body.Children.Add(heading);
        var scroll = new ScrollViewer { Content = main, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };shellContentScroll=scroll;
        scroll.SizeChanged+=(_,_)=>FitDesktopViewport();Grid.SetRow(scroll,1);body.Children.Add(scroll);
        var bottom = new Grid { Margin = new Thickness(0, 16, 0, 0) }; bottom.ColumnDefinitions.Add(new ColumnDefinition()); bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetRow(bottom, 2); body.Children.Add(bottom);
        banner.Foreground = Muted; banner.FontSize = 11; banner.TextWrapping = TextWrapping.Wrap; banner.Margin = new Thickness(0, 10, 15, 0); bottom.Children.Add(banner);
        var prev = Action("上一步", (_, _) => Go(Math.Max(0, page - 1))); var after = Action("下一步  →", (_, _) => Go(Math.Min(5, page + 1)), true); footer.Children.Add(prev); footer.Children.Add(after); Grid.SetColumn(footer, 1); bottom.Children.Add(footer);
        ConfigureControlStyles();
        var scrollbarStyle = WorkspaceScrollbarStyle();
        sidebarScroll.Resources[typeof(ScrollBar)] = scrollbarStyle;
        scroll.Resources[typeof(ScrollBar)] = scrollbarStyle;
        ApplyAppearance(appearance);
        return (prev, after);
    }
    private void UpdateDesktopSidebarLayout(bool desktop)
    {
        var collapsed = desktop && desktopSidebarCollapsed;
        ContentRoot.ColumnDefinitions[0].Width = new GridLength(desktop ? collapsed ? 62 : 310 : 228);
        var sidebarVisibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (sidebarBrand is not null) sidebarBrand.Visibility = sidebarVisibility;
        if (sidebarScroll is not null) sidebarScroll.Visibility = sidebarVisibility;
        if (sidebarFooter is not null) sidebarFooter.Visibility = sidebarVisibility;
        if (desktopSidebarToggle is not null)
        {
            var label = collapsed ? "展开左侧指引" : "收起左侧指引";
            desktopSidebarToggle.Content = collapsed ? "›" : "‹";
            desktopSidebarToggle.ToolTip = label;
            System.Windows.Automation.AutomationProperties.SetName(desktopSidebarToggle, label);
            desktopSidebarToggle.Visibility = desktop ? Visibility.Visible : Visibility.Collapsed;
        }
        sectionBadge.Visibility = desktop ? Visibility.Collapsed : Visibility.Visible;
        UpdateShutdownCommand(desktop);
    }

    private void ConfigureControlStyles()
    {
        Resources["Surface"] = Surface; Resources["Ink"] = Ink; Resources["Line"] = Line; Resources["Pale"] = Pale;
        Resources["ScrollThumb"] = Muted; Resources["ScrollThumbActive"] = Accent;
        var comboTemplate = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
              <Grid>
                <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}" ClickMode="Press">
                  <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border CornerRadius="7" Background="{DynamicResource Surface}" BorderBrush="{DynamicResource Line}" BorderThickness="1"/></ControlTemplate></ToggleButton.Template>
                </ToggleButton>
                <ContentPresenter x:Name="SelectionCaption" IsHitTestVisible="False" Margin="12,9,32,9" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}" ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}" VerticalAlignment="Center"/>
                <TextBlock IsHitTestVisible="False" Text="⌄" HorizontalAlignment="Right" Margin="0,0,12,0" VerticalAlignment="Center" Foreground="{DynamicResource Ink}"/>
                <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                  <Border Background="{DynamicResource Surface}" BorderBrush="{DynamicResource Line}" BorderThickness="1" CornerRadius="7" MinWidth="{TemplateBinding ActualWidth}" MaxHeight="300" Padding="4">
                    <ScrollViewer><ItemsPresenter x:Name="ItemsPresenter" KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                  </Border>
                </Popup>
              </Grid>
            </ControlTemplate>
            """);
        foreach (var combo in new[] { source, node, country, timeZone, protocol, platform, windowsImages, upstreamSource, upstreamNode }) { combo.Template = comboTemplate; combo.Foreground = Ink; combo.Background = Surface; }
        foreach (var box in new[] { password, proxyPassword, adsApiKey }) { box.Foreground = Ink; box.Background = Surface; box.BorderBrush = Line; }
        var itemStyle = new Style(typeof(ComboBoxItem)); itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Ink"))); itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface"))); itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        var highlight = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true }; highlight.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Pale"))); itemStyle.Triggers.Add(highlight); Resources[typeof(ComboBoxItem)] = itemStyle;
    }
    private static ControlTemplate RoundedButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        foreach (var property in new[] { (Border.BackgroundProperty, "Background"), (Border.BorderBrushProperty, "BorderBrush"), (Border.BorderThicknessProperty, "BorderThickness"), (Border.PaddingProperty, "Padding") }) border.SetBinding(property.Item1, new Binding(property.Item2) { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent }); content.SetBinding(FrameworkElement.HorizontalAlignmentProperty, new Binding("HorizontalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent }); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(UIElement.OpacityProperty, .85)); template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45)); template.Triggers.Add(disabled);
        return template;
    }
    private static string PreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeEnvironmentSetup", "preferences.json");
    private void RememberTask() => JsonFile.Write(PreferencesPath, new LastTask(state.Options.StatePath));
    private void RestoreLastTask()
    {
        try
        {
            if (!File.Exists(PreferencesPath)) return;
            var last = JsonFile.Read<LastTask>(PreferencesPath);
            if (File.Exists(last.StatePath)) LoadTask(last.StatePath, loadSecrets: true, interactive: false);
        }
        catch { /* Missing or moved workspaces are displayed as empty, without executing anything. */ }
    }
    private sealed record LastTask(string StatePath);
    private void MarkSection(int value)
    {
        section = value;
        for (var i = 0; i < navigationButtons.Count; i++) { navigationButtons[i].Background = i == value ? Pale : Brushes.Transparent; navigationButtons[i].Foreground = i == value ? Accent : Ink; }
    }
    public void Navigate(int value)
    {
        if (busy && value!=7) return;
        SetDesktopSidebar(value==7);
        MarkSection(value); main.Children.Clear(); steps.Visibility = Visibility.Collapsed; footer.Visibility = Visibility.Collapsed; banner.Text = "资料保存在本机，随时可以回来继续。";
        title.Text = sections[value];
        switch (value)
        {
            case 0: DrawOverview(); break;
            case 1: Go(page); break;
            case 2: DrawMachines(); break;
            case 3: subtitle.Text = "接上你已有的订阅，选一个熟悉的节点。"; main.Children.Add(pages[3]); break;
            case 4: DrawDownloads(); break;
            case 5: DrawRecovery(); break;
            case 6: subtitle.Text = "账号和资料，按你的习惯保存。"; main.Children.Add(pages[4]); break;
            case 7: DrawDesktop();break;
        }
        shellContentScroll?.ScrollToTop();
    }
    private void DrawOverview()
    {
        subtitle.Text = "在本机管理虚拟机、网络节点与环境配置。";
        var heroLeft = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heroLeft.Children.Add(new TextBlock { Text = "虚拟工作空间", FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), Foreground = Ink });
        heroLeft.Children.Add(Text(state.SetupFinished ? "回到你已配置的环境继续工作。" : "安装独立的 Windows 环境，或载入之前保存的任务。", 13));
        heroLeft.Children.Add(Row(Action(state.SetupFinished ? "进入虚拟机" : string.IsNullOrEmpty(state.VmId) ? "开始安装  →" : "继续安装  →",
            async (_, _) => { if (state.SetupFinished) await EnterOriginalWorkspace(); else Go(page); }, true), Action("载入已有环境", (_, _) => LoadTask())));
        var hero = new Grid(); hero.ColumnDefinitions.Add(new ColumnDefinition()); hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) }); hero.Children.Add(heroLeft);
        var illustration = new Grid { Width = 180, Height = 145, VerticalAlignment = VerticalAlignment.Center };
        illustration.Children.Add(new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Width = 172, Height = 113, Margin = new Thickness(0, 0, 0, 24) });
        illustration.Children.Add(new Border { Background = Canvas, CornerRadius = new CornerRadius(7), Width = 148, Height = 78, Margin = new Thickness(0, 0, 0, 33), Child = new TextBlock { Text = "\uE7F4", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 36, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        illustration.Children.Add(new Border { Background = Accent, Width = 66, Height = 7, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 15) }); Grid.SetColumn(illustration, 1); hero.Children.Add(illustration);
        main.Children.Add(new Border { Background = Pale, CornerRadius = new CornerRadius(16), Padding = new Thickness(28, 24, 25, 24), Margin = new Thickness(0, 0, 5, 20), Child = hero });
        var host = Machine.Read();
        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 3) };
        stats.Children.Add(Stat("虚拟环境", string.IsNullOrEmpty(state.VmId) ? "尚未创建" : "已创建", string.IsNullOrEmpty(state.VmId) ? "安装好后会出现在这里" : state.Options.VmName));
        stats.Children.Add(Stat("可用内存", $"{host.FreeMemory / 1073741824d:F1} GB", $"共 {host.TotalMemory / 1073741824d:F1} GB"));
        stats.Children.Add(Stat("网络节点", string.IsNullOrEmpty(state.Options.NodeName) ? "由你选择" : "已选择", string.IsNullOrEmpty(state.Options.NodeName) ? "支持已有订阅与手动配置" : state.Options.NodeName)); main.Children.Add(stats);
        main.Children.Add(Card(Text("接下来", 15, false), Text(string.IsNullOrEmpty(state.VmId) ? "选择安装位置和 Windows 镜像，连接已有节点。其余步骤跟着右侧的安装向导完成。" : state.Stage, 12), Row(Action("查看我的虚拟机", (_, _) => Navigate(2)), Action("管理网络节点", (_, _) => Navigate(3)))));
    }
    private static Border Stat(string label, string value, string hint) => Card(Text(label, 11), new TextBlock { Text = value, FontSize = 23, FontWeight = FontWeights.SemiBold, Foreground = Ink, Margin = new Thickness(0, 2, 0, 10) }, Text(hint, 10));
    private void DrawMachines()
    {
        subtitle.Text = "安装好的环境会显示在这里，打开就能继续使用。";
        if (string.IsNullOrEmpty(state.VmId))
        {
            main.Children.Add(Card(Text("还没有专属虚拟机", 20, false), Text("完成安装后，这里会出现你的 Windows 环境。\n已经装过？载入原任务即可继续管理。"), Row(Action("安装一个新环境", (_, _) => Go(0), true), Action("载入已有环境", (_, _) => LoadTask())))); return;
        }
        var status = state.Status;
        main.Children.Add(Card(Text("WINDOWS / 专属环境", 11), Text(state.Options.VmName, 23, false), Text(status, 13), Text($"{state.Options.MemoryMb / 1024d:F0} GB 内存  ·  {state.Options.CpuCount} 个 CPU  ·  {state.Options.DiskGb} GB 虚拟磁盘", 11), Row(Action("进入虚拟机", async (_, _) => await EnterOriginalWorkspace(), true), Action(state.SetupFinished ? "检测与修复" : "继续安装 / 检查", (_, _) => { if(state.SetupFinished) Navigate(5); else Go(5); }), Action("打开存储目录", (_, _) => OpenWorkspace())), Text(state.SetupFinished ? "设置已完成。日常使用从这里进入原桌面。" : "已有安装进度会保留。完成配置与防护检查后可结束设置。", 11)));
    }
    private void DrawDownloads()
    {
        subtitle.Text = "需要什么再下载，已有文件会先校验后复用。";
        downloadFeatureInfo = Text("等待开始", 11); downloadFeatureBar = new ProgressBar { Height = 7, Maximum = 100, Foreground = Accent, Margin = new Thickness(0, 6, 0, 10) };
        main.Children.Add(Card(Text("下载到你的任务目录", 15, false), Text(root.Text, 11), Row(Action("下载 / 继续获取资源", async (_, _) => await Perform(async (o, token) => await Engine().DownloadResources(o, token)), true), Action("暂停", (_, _) => cancellation?.Cancel()), Action("Windows 镜像", (_, _) => Go(2)), Action("打开目录", (_, _) => OpenWorkspace())), downloadFeatureBar, downloadFeatureInfo));
        foreach (var resource in Catalog.Resources)
        {
            var file = Path.Combine(root.Text, "downloads", resource.FileName); var part = file + ".part";
            var text = File.Exists(file) ? "已有文件 · 安装前校验" : File.Exists(part) ? "已保留部分下载" : "尚未下载";
            main.Children.Add(Card(Text(resource.Name, 14, false), Text($"{resource.Size / 1048576d:F1} MB  ·  {text}", 11)));
        }
    }
    private void DrawRecovery()
    {
        subtitle.Text = "先看清状态，再处理需要调整的地方。";
        main.Children.Add(Card(Text("双重防护", 17, false), Text("外层网关在启动或恢复时核对配置，仅在检查通过后建立受控连接。", 12), Text("内层：保留原有 Clash 服务、TUN、来宾防火墙及检测。", 12), Text("当前内层记录：" + (state.NetworkPassed ? "出口检测有通过记录" : "出口尚未通过") + "；" + (state.OutagePassed ? "断线与恢复有通过记录" : "断线与恢复尚未通过") + "。历史记录不代表当前状态。", 11), Text("外层、内层与浏览器业务验收必须分别确认；任何一项待处理，都不标记环境就绪。", 11)));
        var host = Machine.Read();
        main.Children.Add(Card(Text("黑屏与启动诊断",16,false),Text("只读检查原虚拟机的运行状态、来宾工具与 NEM 兼容模式；若虚拟机已定格或白屏无响应，可使用下方受控入口强制关机并切换为单核防死锁兼容配置（不改主机安全功能）。",11),Row(Action("读取虚拟机诊断",async(_,_)=>await ReadVmDiagnostic()),Action("强制关闭无响应虚拟机并修复",async(_,_)=>await ForceRepairVirtualBoxDialog()),Action("打开程序内桌面",(_,_)=>Navigate(7)))));
        foreach (var action in state.PendingActions) main.Children.Add(PendingCard(action));
        main.Children.Add(BuildProtectionCard());
        main.Children.Add(BuildValidationCard());
        main.Children.Add(Card(Text("这台电脑", 15, false), Text(host.Summary, 12), Text(host.FreeMemory < 6L * 1024 * 1024 * 1024 ? "启动默认虚拟机前，请先关闭闲置应用，空出约 6 GB 内存。" : "默认虚拟机的启动内存余量已满足。", 11), Row(Action("重新检查", (_, _) => { RefreshHost(); Navigate(5); }), Action("查看验收报告", (_, _) => ShowReport()), Action("导出诊断信息", async (_, _) => await ExportDiagnostics()))));
        main.Children.Add(Card(Text("当前任务", 15, false), Text(string.IsNullOrEmpty(state.VmId) ? "还没有已载入的环境。" : state.Stage, 12), Text("浏览器检测和登录结果由实际操作确认；安装记录不会冒充当前连通性。", 11), Row(Action("继续安装 / 修复", (_, _) => Go(5)), Action("载入任务", (_, _) => LoadTask()))));
    }
    private void OpenWorkspace()
    {
        try { Paths.ValidateRoot(root.Text); if (!Directory.Exists(root.Text)) { Notice("这个目录还没有创建，开始下载或安装后会自动准备。"); return; } Process.Start(new ProcessStartInfo(root.Text) { UseShellExecute = true }); }
        catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
    }
    private void ApplyAppearance(Appearance value)
    {
        Appearance.Validate(value); appearance = value;
        var bg = Appearance.Color(value.Background); var accent = Appearance.Color(value.Accent); var dark = Appearance.Luminance(bg) < .179;
        void Set(Brush brush, Color color)
        {
            if (PaletteSources.TryGetValue((SolidColorBrush)brush, out var source)) source.Value = color;
            else throw new SetupException("配色资源未能更新，请重新打开程序。");
        }
        Set(Canvas, bg); Set(Accent, accent); Set(Surface, dark ? Appearance.Mix(bg, Colors.White, .045) : Appearance.Mix(bg, Colors.White, .87));
        var ink = Appearance.Mix(Appearance.Legible(bg), bg, .055); if (Appearance.Contrast(ink, bg) < 4.5) ink = Appearance.Legible(bg); Set(Ink, ink);
        var muted = Appearance.Mix(ink, bg, .42); for (var amount = .38; Appearance.Contrast(muted, bg) < 4.5 && amount >= 0; amount -= .04) muted = Appearance.Mix(ink, bg, amount);
        Set(Muted, muted); Set(Line, Appearance.Mix(bg, dark ? Colors.White : Colors.Black, .1));
        Set(Pale, Appearance.Mix(((SolidColorBrush)Surface).Color, accent, dark ? .14 : .07)); Set(OnAccent, Appearance.Legible(accent));
        UpdateNativeCaption();
    }
    private void AppearanceDialog() => BuildAppearanceDialog(value => value.Save()).ShowDialog();

    private Window BuildAppearanceDialog(Action<Appearance> save)
    {
        var dialog = new Window
        {
            Title = "外观与配色", Owner = this,
            Width = 500, Height = 520, MinWidth = 420, MinHeight = 280,
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40),
            ResizeMode = ResizeMode.CanResizeWithGrip,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Surface, Foreground = Ink, FontFamily = FontFamily
        };
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Content = layout;
        var body = new StackPanel { Margin = new Thickness(24, 18, 24, 12) };
        var scroll = new ScrollViewer
        {
            Name = "AppearanceScroll", Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        scroll.Resources["ScrollThumb"] = Muted;
        scroll.Resources["ScrollThumbActive"] = Accent;
        scroll.Resources[typeof(ScrollBar)] = WorkspaceScrollbarStyle();
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if (scroll.ScrollableHeight <= 0 || e.Delta == 0) return;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 3d);
            e.Handled = true;
        };
        layout.Children.Add(scroll);
        body.Children.Add(Text("换个喜欢的颜色", 23, false));
        body.Children.Add(Text("仅保存在本机，随时可以再改。", 12));
        var choices = new WrapPanel(); var accent = Input(appearance.Accent); var background = Input(appearance.Background);
        foreach (var preset in Appearance.Presets)
        {
            var swatch = Action(preset.Name, (_, _) => { accent.Text = preset.Accent; background.Text = preset.Background; ApplyAppearance(preset); }); swatch.BorderBrush = Brush(preset.Accent); choices.Children.Add(swatch);
        }
        body.Children.Add(choices); body.Children.Add(Field("主题色", accent, "支持 #RRGGBB，例如 #087F8C")); body.Children.Add(Field("背景色", background));
        Appearance SelectedColors() => new("自定义", accent.Text.Trim(), background.Text.Trim());
        var actions = Row(
            Action("预览", (_, _) =>
            {
                try { ApplyAppearance(SelectedColors()); }
                catch (Exception ex) { MessageBox.Show(dialog, Redaction.SafeError(ex)); }
            }),
            Action("保存配色", (_, _) =>
            {
                try
                {
                    var value = SelectedColors();
                    ApplyAppearance(value);
                    save(value);
                    dialog.Close();
                }
                catch (Exception ex) { MessageBox.Show(dialog, Redaction.SafeError(ex)); }
            }, true));
        var footer = new Border
        {
            Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 8, 24, 9), Child = actions
        };
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);
        return dialog;
    }
    public void PreviewTheme(bool dark) => ApplyAppearance(dark ? Appearance.Presets[3] : Appearance.Default);
    private void ShowLicenses()
    {
        var dialog = new Window { Title = "组件来源与许可", Owner = this, Width = 760, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Surface, Foreground = Ink };
        dialog.Content = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(18), Text = "项目原创代码：MIT\n开发者：李诗恩 · 林琛\n\n" + string.Join("\n\n", Catalog.Resources.Select(r => r.Name + "\n" + r.License)) + "\n\n" + Catalog.EmbeddedText("Gateway-THIRD-PARTY-NOTICES.txt") };
        dialog.ShowDialog();
    }
}
