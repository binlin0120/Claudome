using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    private static readonly Brush Surface = Brush("#8FFFFFFF"), ModalSurface = Brush("#FAF8F5"), BackdropVeil = Brush("#20F6F4F0"), Line = Brush("#D7CFC4"), Canvas = Brush("#F6F4F0"), OnAccent = Brush("#FFFFFF");
    private readonly string[] sections = ["空间概览", "安装向导", "我的空间", "节点管理", "资源下载", "安全验真", "账号资料", "专属桌面", "存储清理"];
    private readonly string[] glyphs = ["\uE80F", "\uE710", "\uE7F4", "\uE774", "\uE896", "\uE90F", "\uE77B", "\uE7F4", "\uE7B8"];
    private readonly WrapPanel steps = new() { Margin = new Thickness(0, 0, 0, 14) };
    private readonly TextBlock sectionBadge = new();
    private int section;
    private Appearance appearance = Appearance.Load();
    private Border? configModalFrame, inlineModalOverlay, inlineModalCard, sectionPillBorder, shellTopBar;
    private StackPanel? shellHeading;
    private Grid? inlineModalSlot, shellBottomBar;
    private Button? desktopShutdown, quickOpacityButton, quickThemeButton, topMoreButton, closeConfigButton;
    private Popup? topMorePopup;
    private TextBlock? downloadFeatureInfo;
    private ProgressBar? downloadFeatureBar;
    private Border? globalUpdateBanner;
    private TextBlock? globalUpdateBannerText;
    private Button? globalUpdateActionButton;

    private (Button Previous, Button Next) BuildShell()
    {
        Title = ProductProfile.WindowTitle; Width = 1260; Height = 860; MinWidth = 980; MinHeight = 720; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14; Foreground = Ink; Background = Canvas;
        Content = ContentRoot; ContentRoot.Background = Brushes.Transparent;
        SourceInitialized += (_, _) => UpdateNativeCaption();
        SizeChanged += (_, _) => FitDesktopViewport();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && desktopFullscreen)
            {
                SetDesktopFullscreen(false);
                e.Handled = true;
            }
        };
        ContentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ContentRoot.RowDefinitions.Add(new RowDefinition());
        ContentRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        ContentRoot.ColumnDefinitions.Add(new ColumnDefinition());

        var backdropImage = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Brand/DomeBackdrop.jpg")),
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(backdropImage, BitmapScalingMode.HighQuality);
        Grid.SetRowSpan(backdropImage, 2);
        Grid.SetColumnSpan(backdropImage, 2);
        Panel.SetZIndex(backdropImage, -2);
        ContentRoot.Children.Add(backdropImage);
        var backdropVeil = new Border { Background = BackdropVeil, IsHitTestVisible = false };
        Grid.SetRowSpan(backdropVeil, 2);
        Grid.SetColumnSpan(backdropVeil, 2);
        Panel.SetZIndex(backdropVeil, -1);
        ContentRoot.Children.Add(backdropVeil);

        // V5 顶栏：左侧品牌标识 + 居中胶囊主导航 + 右侧透度/暗夜控制
        var mark = new BitmapImage(new Uri("pack://application:,,,/Brand/Claudome.png"));
        Icon = mark;
        var brandIcon = new Image { Name = "WorkspaceBrandIcon", Source = mark, Width = 36, Height = 36 };
        RenderOptions.SetBitmapScalingMode(brandIcon, BitmapScalingMode.HighQuality);
        var brandRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brandRow.Children.Add(brandIcon);
        var brandText = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        brandText.Children.Add(new TextBlock { Text = ProductProfile.WindowTitle, FontWeight = FontWeights.Bold, FontSize = 15, Foreground = Ink });
        brandText.Children.Add(new TextBlock { Text = ProductProfile.BrandTagline, FontSize = 9.5, Foreground = Muted, Margin = new Thickness(0, 1, 0, 0) });
        brandRow.Children.Add(brandText);

        var topNavRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var moreMenuStack = new StackPanel { MinWidth = 176 };
        var taskResourceButtons = new List<Button>();
        for (var i = 0; i < sections.Length; i++)
        {
            var index = i;
            var isPrimary = i is 0 or 1 or 5 or 7;
            var button = Action(sections[i], (_, _) =>
            {
                if (topMorePopup is not null) topMorePopup.IsOpen = false;
                if (!busy || index == 7) Navigate(index);
            });
            button.BorderThickness = new Thickness(0);
            button.FontSize = 12.5;
            if (isPrimary)
            {
                button.Padding = new Thickness(14, 6, 14, 6);
                button.Margin = new Thickness(2, 0, 2, 0);
                topNavRow.Children.Add(button);
            }
            else
            {
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Padding = new Thickness(12, 7, 12, 7);
                button.Margin = new Thickness(0, 1, 0, 1);
                taskResourceButtons.Add(button);
            }
            navigationButtons.Add(button);
        }
        TextBlock MenuGroupHeading(string heading) => new()
        {
            Text = heading,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Muted,
            Margin = new Thickness(10, 6, 10, 3)
        };
        Border MenuDivider() => new() { Background = Line, Height = 1, Margin = new Thickness(6, 5, 6, 4) };

        var moreAppearanceBtn = Action("◐ 外观与透明度", (_, _) =>
        {
            if (topMorePopup is not null) topMorePopup.IsOpen = false;
            AppearanceDialog();
        });
        moreAppearanceBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
        moreAppearanceBtn.Padding = new Thickness(12, 7, 12, 7);
        moreAppearanceBtn.Margin = new Thickness(0, 1, 0, 1);
        moreAppearanceBtn.BorderThickness = new Thickness(0);
        moreAppearanceBtn.FontSize = 12;

        var moreUpdateBtn = Action(ProductProfile.OnlineUpdatesEnabled ? "⟳ 检查与本地更新" : "⟳ 预发布与本地更新", async (_, _) =>
        {
            if (topMorePopup is not null) topMorePopup.IsOpen = false;
            Navigate(0);
            if (ProductProfile.OnlineUpdatesEnabled) await CheckForUpdatesAsync(interactive: true);
        });
        moreUpdateBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
        moreUpdateBtn.Padding = new Thickness(12, 7, 12, 7);
        moreUpdateBtn.Margin = new Thickness(0, 1, 0, 1);
        moreUpdateBtn.BorderThickness = new Thickness(0);
        moreUpdateBtn.FontSize = 12;

        Button? moreMigrateBtn = null;
        if (ProductProfile.IsHyperVStandalone)
        {
            moreMigrateBtn = Action("↑ 从旧版升级到专线版", (_, _) =>
            {
                if (topMorePopup is not null) topMorePopup.IsOpen = false;
                OpenLegacyMigrationDialog();
            });
            moreMigrateBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
            moreMigrateBtn.Padding = new Thickness(12, 7, 12, 7);
            moreMigrateBtn.Margin = new Thickness(0, 1, 0, 1);
            moreMigrateBtn.BorderThickness = new Thickness(0);
            moreMigrateBtn.FontSize = 12;
        }

        var moreLicenseBtn = Action("ⓘ 组件来源与许可", (_, _) =>
        {
            if (topMorePopup is not null) topMorePopup.IsOpen = false;
            ShowLicenses();
        });
        moreLicenseBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
        moreLicenseBtn.Padding = new Thickness(12, 7, 12, 7);
        moreLicenseBtn.Margin = new Thickness(0, 1, 0, 1);
        moreLicenseBtn.BorderThickness = new Thickness(0);
        moreLicenseBtn.FontSize = 11.5;

        var moreGroupBtn = Action("复制交流群 1129410798", (_, _) =>
        {
            if (topMorePopup is not null) topMorePopup.IsOpen = false;
            Clipboard.SetText("1129410798");
            banner.Text = "群号已复制";
        });
        moreGroupBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
        moreGroupBtn.Padding = new Thickness(12, 7, 12, 7);
        moreGroupBtn.Margin = new Thickness(0, 1, 0, 1);
        moreGroupBtn.BorderThickness = new Thickness(0);
        moreGroupBtn.FontSize = 11.5;

        // Grouped settings menu: 外观 · 任务与资源 · 帮助与关于
        moreMenuStack.Children.Add(MenuGroupHeading("外观"));
        moreMenuStack.Children.Add(moreAppearanceBtn);
        moreMenuStack.Children.Add(MenuDivider());
        moreMenuStack.Children.Add(MenuGroupHeading("任务与资源"));
        foreach (var taskBtn in taskResourceButtons) moreMenuStack.Children.Add(taskBtn);
        moreMenuStack.Children.Add(MenuDivider());
        moreMenuStack.Children.Add(MenuGroupHeading("帮助与关于"));
        moreMenuStack.Children.Add(moreUpdateBtn);
        if (moreMigrateBtn is not null) moreMenuStack.Children.Add(moreMigrateBtn);
        moreMenuStack.Children.Add(moreLicenseBtn);
        moreMenuStack.Children.Add(moreGroupBtn);

        topMoreButton = Action("⚙ 设置 ▾", (_, _) => { if (topMorePopup is not null) topMorePopup.IsOpen = !topMorePopup.IsOpen; });
        topMoreButton.Name = "TopMoreButton";
        topMoreButton.ToolTip = "设置与工具";
        topMoreButton.Padding = new Thickness(12, 6, 12, 6);
        topMoreButton.Margin = new Thickness(2, 0, 2, 0);
        topMoreButton.BorderThickness = new Thickness(0);
        topMoreButton.FontSize = 12.5;
        topMorePopup = new Popup
        {
            PlacementTarget = topMoreButton,
            Placement = PlacementMode.Bottom,
            AllowsTransparency = true,
            StaysOpen = false,
            VerticalOffset = 6,
            Child = new Border { Background = ModalSurface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(6), Child = moreMenuStack }
        };
        // 将主导航按钮按「空间概览 · 安装向导 · 专属桌面 · 安全验真 · 设置」逻辑顺序重排进胶囊栏
        topNavRow.Children.Clear();
        topNavRow.Children.Add(navigationButtons[0]);
        topNavRow.Children.Add(navigationButtons[1]);
        topNavRow.Children.Add(navigationButtons[7]);
        topNavRow.Children.Add(navigationButtons[5]);
        topNavRow.Children.Add(topMoreButton);
        var navCapsule = new Border
        {
            Background = Pale,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = topNavRow
        };

        sectionBadge.Text = "本地运行";
        sectionBadge.Foreground = Ink;
        sectionBadge.FontSize = 11;
        sectionBadge.VerticalAlignment = VerticalAlignment.Center;
        var sectionPill = new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Child = sectionBadge };
        sectionPillBorder = sectionPill;
        var topRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        quickOpacityButton = Action($"◐ 透度 {(int)Math.Round(Appearance.ClampOpacity(appearance.Opacity) * 100)}%", (_, _) => ToggleOpacityPopup());
        quickOpacityButton.Name = "QuickOpacityButton";
        quickOpacityButton.Padding = new Thickness(10, 5, 10, 5);
        quickOpacityButton.Margin = new Thickness(0, 0, 6, 0);
        quickOpacityButton.FontSize = 11.5;
        BuildOpacityPopup();
        quickThemeButton = Action("☾ 暗夜视图", (_, _) => ToggleQuickTheme());
        quickThemeButton.Name = "QuickThemeButton";
        quickThemeButton.Padding = new Thickness(10, 5, 10, 5);
        quickThemeButton.Margin = new Thickness(0, 0, 6, 0);
        quickThemeButton.FontSize = 11.5;
        desktopShutdown = Action("关机退出", async (_, _) => await ShutdownDesktop());
        desktopShutdown.ToolTip = "关闭虚拟机并退出 Claudome";
        desktopShutdown.Name = "ShutdownDesktop";
        desktopShutdown.Padding = new Thickness(10, 5, 10, 5);
        desktopShutdown.Margin = new Thickness(0, 0, 0, 0);
        desktopShutdown.FontSize = 11.5;
        topRight.Children.Add(quickOpacityButton);
        topRight.Children.Add(quickThemeButton);
        topRight.Children.Add(desktopShutdown);
        topRight.Children.Add(sectionPill);

        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topGrid.Children.Add(brandRow);
        Grid.SetColumn(navCapsule, 1); topGrid.Children.Add(navCapsule);
        Grid.SetColumn(topRight, 2); topGrid.Children.Add(topRight);
        var topBar = new Border
        {
            Background = Surface,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(28, 10, 28, 10),
            Child = topGrid
        };
        shellTopBar = topBar;
        Grid.SetColumnSpan(topBar, 2);
        ContentRoot.Children.Add(topBar);

        // 主体工作区与居中半透明配置窗口宿主（桌面操作栏已统一移至专属桌面顶部，无左侧边栏）
        var body = new Grid { Margin = new Thickness(34, 18, 34, 16) };
        shellBody = body;
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); body.RowDefinitions.Add(new RowDefinition()); body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headingDock = new DockPanel();
        closeConfigButton = Action("✕ 返回概览", (_, _) => Navigate(0));
        closeConfigButton.Name = "CloseConfigButton";
        closeConfigButton.Padding = new Thickness(11, 5, 11, 5);
        closeConfigButton.FontSize = 11.5;
        closeConfigButton.Margin = new Thickness(12, 2, 0, 0);
        closeConfigButton.VerticalAlignment = VerticalAlignment.Top;
        closeConfigButton.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(closeConfigButton, Dock.Right);
        headingDock.Children.Add(closeConfigButton);
        var headingTitles = new StackPanel();
        title.FontSize = 25;
        title.FontWeight = FontWeights.Bold;
        headingTitles.Children.Add(title);
        subtitle.Foreground = Ink;
        subtitle.FontSize = 12.5;
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, 4, 0, 12);
        headingTitles.Children.Add(subtitle);
        headingDock.Children.Add(headingTitles);

        var heading = new StackPanel();
        shellHeading = heading;
        heading.Children.Add(headingDock);
        heading.Children.Add(steps);
        globalUpdateBanner = BuildGlobalUpdateBanner();
        heading.Children.Add(globalUpdateBanner);
        body.Children.Add(heading);

        var scroll = new ScrollViewer { Content = main, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        shellContentScroll = scroll;
        scroll.SizeChanged += (_, _) => FitDesktopViewport();
        Grid.SetRow(scroll, 1);
        body.Children.Add(scroll);

        var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        shellBottomBar = bottom;
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(bottom, 2);
        body.Children.Add(bottom);
        banner.Foreground = Ink; banner.FontSize = 11; banner.TextWrapping = TextWrapping.Wrap;
        var bannerPill = new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), Child = banner };
        bottom.Children.Add(bannerPill);
        var prev = Action("上一步", (_, _) => Go(Math.Max(0, page - 1)));
        var after = Action("下一步  →", (_, _) => Go(Math.Min(5, page + 1)), true);
        footer.Children.Add(prev); footer.Children.Add(after);
        Grid.SetColumn(footer, 1); bottom.Children.Add(footer);

        configModalFrame = new Border { Name = "ConfigModalFrame", Child = body };
        Grid.SetRow(configModalFrame, 1);
        Grid.SetColumn(configModalFrame, 1);
        ContentRoot.Children.Add(configModalFrame);

        inlineModalSlot = new Grid();
        inlineModalCard = new Border
        {
            Name = "InlineConfigModal",
            Width = 540,
            MaxHeight = 590,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Surface,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(24, 20, 24, 18),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.16, Color = Colors.Black },
            Child = inlineModalSlot
        };
        inlineModalOverlay = new Border
        {
            Name = "InlineModalOverlay",
            Background = new SolidColorBrush(Color.FromArgb(64, 16, 14, 12)),
            Visibility = Visibility.Collapsed,
            Child = inlineModalCard
        };
        inlineModalOverlay.MouseDown += (_, e) =>
        {
            if (ReferenceEquals(e.Source, inlineModalOverlay)) CloseInlineModal();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && inlineModalOverlay?.Visibility == Visibility.Visible)
            {
                CloseInlineModal();
                e.Handled = true;
            }
        };
        Grid.SetRow(inlineModalOverlay, 1);
        Grid.SetColumn(inlineModalOverlay, 1);
        Panel.SetZIndex(inlineModalOverlay, 20);
        ContentRoot.Children.Add(inlineModalOverlay);
        SizeChanged += (_, _) => UpdateConfigModalSize();

        ConfigureControlStyles();
        var scrollbarStyle = WorkspaceScrollbarStyle();
        scroll.Resources[typeof(ScrollBar)] = scrollbarStyle;
        ApplyAppearance(appearance);
        return (prev, after);
    }
    private static bool IsCenteredConfigSection(int targetSection) => targetSection is 1 or 2 or 3 or 4 or 6 or 8;
    private void UpdateConfigModalSize()
    {
        if (configModalFrame is null) return;
        var isConfig = IsCenteredConfigSection(section);
        var availHeight = ActualHeight > 180 ? ActualHeight - 130 : 660;
        var availWidth = ActualWidth > 140 ? Math.Min(780, ActualWidth - 88) : 760;
        if (isConfig)
        {
            configModalFrame.Width = Math.Max(560, availWidth);
            configModalFrame.MaxHeight = Math.Max(460, availHeight);
        }
        if (inlineModalCard is not null)
            inlineModalCard.MaxHeight = Math.Max(380, availHeight - 24);
    }
    private void ApplySectionSurfaceMode(int targetSection)
    {
        if (configModalFrame is null) return;
        var isConfig = IsCenteredConfigSection(targetSection);
        if (isConfig)
        {
            configModalFrame.HorizontalAlignment = HorizontalAlignment.Center;
            configModalFrame.VerticalAlignment = VerticalAlignment.Center;
            configModalFrame.Background = Surface;
            configModalFrame.BorderBrush = Line;
            configModalFrame.BorderThickness = new Thickness(1);
            configModalFrame.CornerRadius = new CornerRadius(20);
            configModalFrame.Padding = new Thickness(26, 20, 26, 18);
            configModalFrame.Margin = new Thickness(20, 12, 20, 16);
            configModalFrame.Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Opacity = 0.13, Color = Colors.Black };
            if (shellBody is not null) shellBody.Margin = new Thickness(0);
            if (closeConfigButton is not null) closeConfigButton.Visibility = Visibility.Visible;
            title.FontSize = 22.5;
            UpdateConfigModalSize();
        }
        else
        {
            configModalFrame.Width = double.NaN;
            configModalFrame.MaxHeight = double.PositiveInfinity;
            configModalFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
            configModalFrame.VerticalAlignment = VerticalAlignment.Stretch;
            configModalFrame.Background = Brushes.Transparent;
            configModalFrame.BorderThickness = new Thickness(0);
            configModalFrame.CornerRadius = new CornerRadius(0);
            configModalFrame.Padding = new Thickness(0);
            configModalFrame.Margin = new Thickness(0);
            configModalFrame.Effect = null;
            if (shellBody is not null) shellBody.Margin = targetSection == 7 ? new Thickness(20, 10, 20, 10) : new Thickness(34, 18, 34, 16);
            if (closeConfigButton is not null) closeConfigButton.Visibility = Visibility.Collapsed;
            title.FontSize = 26;
        }
    }
    private void UpdateDesktopSidebarLayout(bool desktop)
    {
        ContentRoot.ColumnDefinitions[0].Width = new GridLength(0);
        sectionBadge.Visibility = desktop ? Visibility.Collapsed : Visibility.Visible;
        if (sectionPillBorder is not null) sectionPillBorder.Visibility = desktop ? Visibility.Collapsed : Visibility.Visible;
        UpdateShutdownCommand(desktop);
    }

    private void ConfigureControlStyles()
    {
        Resources["Surface"] = Surface; Resources["ModalSurface"] = ModalSurface; Resources["Ink"] = Ink; Resources["Line"] = Line; Resources["Pale"] = Pale;
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
                  <Border Background="{DynamicResource ModalSurface}" BorderBrush="{DynamicResource Line}" BorderThickness="1" CornerRadius="7" MinWidth="{TemplateBinding ActualWidth}" MaxHeight="300" Padding="4">
                    <ScrollViewer><ItemsPresenter x:Name="ItemsPresenter" KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                  </Border>
                </Popup>
              </Grid>
            </ControlTemplate>
            """);
        foreach (var combo in new[] { source, node, country, timeZone, protocol, platform, windowsImages, upstreamSource, upstreamNode }) { combo.Template = comboTemplate; combo.Foreground = Ink; combo.Background = Surface; }
        foreach (var box in new[] { password, proxyPassword, adsApiKey }) { box.Foreground = Ink; box.Background = Surface; box.BorderBrush = Line; }
        var itemStyle = new Style(typeof(ComboBoxItem)); itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Ink"))); itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("ModalSurface"))); itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        var highlight = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true }; highlight.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Pale"))); itemStyle.Triggers.Add(highlight); Resources[typeof(ComboBoxItem)] = itemStyle;
    }
    private static ControlTemplate RoundedButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        foreach (var property in new[] { (Border.BackgroundProperty, "Background"), (Border.BorderBrushProperty, "BorderBrush"), (Border.BorderThicknessProperty, "BorderThickness"), (Border.PaddingProperty, "Padding") }) border.SetBinding(property.Item1, new Binding(property.Item2) { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent }); content.SetBinding(FrameworkElement.HorizontalAlignmentProperty, new Binding("HorizontalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent }); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(UIElement.OpacityProperty, .85)); template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45)); template.Triggers.Add(disabled);
        return template;
    }
    private static string PreferencesPath => Path.Combine(ProductProfile.AppDataDirectory, "preferences.json");
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
        for (var i = 0; i < navigationButtons.Count; i++)
        {
            navigationButtons[i].Background = i == value ? Surface : Brushes.Transparent;
            navigationButtons[i].Foreground = i == value ? Accent : Ink;
            navigationButtons[i].FontWeight = i == value ? FontWeights.Bold : FontWeights.Normal;
        }
        if (topMoreButton is not null)
        {
            var inMore = value is 2 or 3 or 4 or 6 or 8;
            topMoreButton.Background = inMore ? Surface : Brushes.Transparent;
            topMoreButton.Foreground = inMore ? Accent : Ink;
            topMoreButton.FontWeight = inMore ? FontWeights.Bold : FontWeights.Normal;
        }
    }
    public void Navigate(int value)
    {
        if (busy && value!=7) return;
        if (value != 7 && desktopFullscreen) SetDesktopFullscreen(false);
        CloseInlineModal();
        MarkSection(value);
        ApplySectionSurfaceMode(value);
        SetDesktopSidebar(value==7);
        if (shellHeading is not null && !desktopFullscreen)
            shellHeading.Visibility = value == 7 ? Visibility.Collapsed : Visibility.Visible;
        subtitle.Visibility = value == 7 ? Visibility.Collapsed : Visibility.Visible;
        main.Children.Clear(); steps.Visibility = Visibility.Collapsed; footer.Visibility = Visibility.Collapsed;
        banner.Text = mediaBusy ? "⬇ 后台正在准备 Windows 镜像 · 您可先配置网络节点或账号资料" : "资料保存在本机，随时可以回来继续。";
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
            case 8: DrawStorageCleanup(); break;
        }
        shellContentScroll?.ScrollToTop();
    }
    private bool CanEnterDesktopWorkspace() =>
        !string.IsNullOrEmpty(state.VmId) && ResumePlan.HasVerifiedSystemDiskProof(state);

    private void DrawOverview()
    {
        subtitle.Text = "为 Claude 留出一处安静独立的居所，与日常主机互不打扰。";
        var heroOpacity = Math.Clamp(0.22 + appearance.Opacity * 0.78, 0.25, 0.98);
        var darkCardBg = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((int)Math.Round(heroOpacity * 255), 55, 250), 23, 19, 17));
        var overviewGrid = new Grid { Margin = new Thickness(0, 0, 5, 10) };
        overviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.05, GridUnitType.Star) });
        overviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        overviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star), MinWidth = 264 });
        overviewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        overviewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        overviewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 1. 左上：穹顶主题 Hero 横幅卡片
        var heroGrid = new Grid { MinHeight = 228 };
        var heroArt = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Brand/DomeHero.jpg")),
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = Math.Clamp(0.28 + appearance.Opacity * 0.72, 0.34, 1.0),
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(heroArt, BitmapScalingMode.HighQuality);
        heroGrid.Children.Add(heroArt);
        heroGrid.Children.Add(new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(240, 23, 19, 17), 0.0),
                    new GradientStop(Color.FromArgb(216, 23, 19, 17), 0.38),
                    new GradientStop(Color.FromArgb(95, 23, 19, 17), 0.56),
                    new GradientStop(Color.FromArgb(0, 23, 19, 17), 0.78)
                ]
            }
        });
        var heroCopy = new StackPanel { Margin = new Thickness(26, 18, 26, 18), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 370 };
        var tagPill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 0, 10),
            Background = new SolidColorBrush(Color.FromArgb(30, 255, 244, 232)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 232, 210)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "●  硬件级独立边界 · Windows 11", FontSize = 11, Foreground = new SolidColorBrush(Appearance.Color("#F7DEC9")) }
        };
        heroCopy.Children.Add(tagPill);
        var titleBlock = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        titleBlock.Children.Add(new TextBlock { Text = "让 Claude", FontSize = 29, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Appearance.Color("#FFFAF3")), LineHeight = 35 });
        titleBlock.Children.Add(new TextBlock
        {
            Text = "住进穹顶",
            FontSize = 29,
            FontWeight = FontWeights.Bold,
            LineHeight = 35,
            Foreground = new LinearGradientBrush(Appearance.Color("#FFD9B8"), Appearance.Color("#F2A168"), new Point(0, 0.5), new Point(1, 0.5))
        });
        heroCopy.Children.Add(titleBlock);
        // 2. 右上：本机状态卡 + 预检统一数据源
        var host = Machine.Read();
        var facts = Platforms.Detect();
        var ineligibility = ProductProfile.IsHyperVStandalone ? Machine.HyperVStandaloneIneligibilityReason(host, facts) : null;
        var preflight = ProductProfile.IsHyperVStandalone ? Machine.ReadHyperVPreflight(host, facts) : null;
        var overviewHost = BuildOverviewHostView(host, preflight, ineligibility, !string.IsNullOrEmpty(state.VmId), state.SetupFinished, state.Stage, ProductProfile.IsHyperVStandalone);

        var heroPrimary = Action(overviewHost.PrimaryActionText,
            async (_, _) =>
            {
                if (overviewHost.OpenHostCheck) Go(0);
                else if (state.SetupFinished && CanEnterDesktopWorkspace()) await EnterOriginalWorkspace();
                else Go(page);
            }, true);
        heroPrimary.Background = new SolidColorBrush(Appearance.Color("#FDF3E6"));
        heroPrimary.Foreground = new SolidColorBrush(Appearance.Color("#211A16"));
        heroPrimary.BorderBrush = heroPrimary.Background;
        var heroSecondary = Action("预备镜像与组件", (_, _) => Go(2));
        heroSecondary.Background = new SolidColorBrush(Color.FromArgb(32, 255, 244, 232));
        heroSecondary.Foreground = new SolidColorBrush(Appearance.Color("#F7EBE0"));
        heroSecondary.BorderBrush = new SolidColorBrush(Color.FromArgb(62, 255, 236, 216));
        heroCopy.Children.Add(Row(heroPrimary, heroSecondary));
        heroGrid.Children.Add(heroCopy);
        var heroCard = new Border { Background = darkCardBg, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), ClipToBounds = true, Child = heroGrid };
        overviewGrid.Children.Add(heroCard);

        var hostHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var hostPill = OverviewPill(overviewHost.PillText, overviewHost.PillBgHex, overviewHost.PillFgHex);
        DockPanel.SetDock(hostPill, Dock.Right);
        hostHeader.Children.Add(hostPill);
        hostHeader.Children.Add(Text("本机状态", 15, false));
        var hostRows = new StackPanel();
        hostRows.Children.Add(OverviewRow("操作系统", "Windows " + host.EditionName));
        hostRows.Children.Add(OverviewRow("虚拟化", overviewHost.VirtualizationValue));
        hostRows.Children.Add(OverviewRow("可用内存", $"{host.FreeMemory / 1073741824d:F1} GB / {host.TotalMemory / 1073741824d:F1} GB"));
        hostRows.Children.Add(OverviewRow("逻辑核心", $"{host.Processors} 核"));
        var hostFoot = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var changeDir = Action("更改位置", (_, _) => Go(1));
        changeDir.Padding = new Thickness(10, 4, 10, 4);
        changeDir.FontSize = 11.5;
        changeDir.Margin = new Thickness(0);
        DockPanel.SetDock(changeDir, Dock.Right);
        hostFoot.Children.Add(changeDir);
        hostFoot.Children.Add(new TextBlock { Text = root.Text, FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var hostCard = Card(hostHeader, hostRows, hostFoot);
        hostCard.Margin = new Thickness(0);
        Grid.SetColumn(hostCard, 2);
        overviewGrid.Children.Add(hostCard);

        // 3. 左下：客户 3 阶段主路径（安装向导 → 安全验真 → 专属桌面）
        var stageStepIdx = state.SetupFinished ? 2
            : (state.Completed.Contains("software") ? (state.Protection.FourDimensionsPassed(state) ? 2 : 1) : 0);
        var trackHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var trackPill = OverviewPill(state.SetupFinished ? "空间已落成" : string.IsNullOrEmpty(state.VmId) ? (ineligibility is not null ? "主机受限" : preflight is { RebootPending: true } ? "待重启" : preflight is { AllReady: false } ? "待启用" : "待启程") : stageStepIdx == 1 ? "待安全验真" : "构建中",
            state.SetupFinished ? "#E6F5EE" : ineligibility is not null ? "#FCEAE8" : "#FBF0E8",
            state.SetupFinished ? "#176B4E" : ineligibility is not null ? "#A32D26" : "#963E1B");
        DockPanel.SetDock(trackPill, Dock.Right);
        trackHeader.Children.Add(trackPill);
        trackHeader.Children.Add(Text("三步完成专属空间", 15, false));
        var stepGrid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 2, 0, 10) };
        (string Num, string Title, string Hint)[] stageSpecs = [("01", "安装向导", "核检建机与配置"), ("02", "安全验真", "四维自动 + 浏览器确认"), ("03", "专属桌面", "独立隔离日常使用")];
        for (var i = 0; i < stageSpecs.Length; i++)
        {
            var done = i < stageStepIdx || (i == 1 && state.Protection.FourDimensionsPassed(state)) || (i == 2 && state.SetupFinished);
            var active = i == stageStepIdx && !done;
            var item = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            var badge = new Border
            {
                Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 6),
                Background = done ? new SolidColorBrush(Appearance.Color("#E6F5EE")) : active ? Pale : Surface,
                BorderBrush = active ? Accent : Line,
                BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = done ? "✓" : stageSpecs[i].Num, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = done ? new SolidColorBrush(Appearance.Color("#176B4E")) : active ? Accent : Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            item.Children.Add(badge);
            item.Children.Add(new TextBlock { Text = stageSpecs[i].Title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ink });
            item.Children.Add(new TextBlock { Text = stageSpecs[i].Hint, FontSize = 10.5, Foreground = Muted });
            stepGrid.Children.Add(item);
        }
        var stepProgress = new ProgressBar { Height = 5, Maximum = 100, Value = state.SetupFinished ? 100 : stageStepIdx == 1 ? 66 : !string.IsNullOrEmpty(state.VmId) ? 45 : 20, Foreground = Accent, Background = Line, Margin = new Thickness(0, 2, 0, 10) };
        var trackCard = Card(
            trackHeader,
            stepGrid,
            stepProgress,
            Text(overviewHost.TrackStatusText, 11.5),
            Row(Action("查看我的虚拟机", (_, _) => Navigate(2)), Action("管理网络节点", (_, _) => Navigate(3))));
        trackCard.Margin = new Thickness(0);
        Grid.SetRow(trackCard, 2);
        overviewGrid.Children.Add(trackCard);

        // 4. 右下：当前任务阶段 + 唯一主操作按钮
        var taskHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var taskPill = OverviewPill(string.IsNullOrEmpty(state.VmId) ? "未建机" : state.SetupFinished ? "可进入" : stageStepIdx == 1 ? "待验真" : "安装中", state.SetupFinished ? "#E6F5EE" : "#FDF2DB", state.SetupFinished ? "#176B4E" : "#8A550C");
        DockPanel.SetDock(taskPill, Dock.Right);
        taskHeader.Children.Add(taskPill);
        taskHeader.Children.Add(Text("当前任务", 15, false));
        var taskBadgeRow = new StackPanel { Orientation = Orientation.Horizontal };
        var miniLogo = new Image { Source = Icon, Width = 34, Height = 34, Margin = new Thickness(0, 0, 10, 0) };
        RenderOptions.SetBitmapScalingMode(miniLogo, BitmapScalingMode.HighQuality);
        taskBadgeRow.Children.Add(miniLogo);
        var taskMeta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        taskMeta.Children.Add(new TextBlock { Text = state.Options.VmName, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ink });
        taskMeta.Children.Add(new TextBlock { Text = state.Status, FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        taskBadgeRow.Children.Add(taskMeta);
        var taskInner = new Border { Background = Pale, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 12), Child = taskBadgeRow };
        var canEnter = CanEnterDesktopWorkspace();
        var taskPrimaryLabel = string.IsNullOrEmpty(state.VmId) ? "载入已有环境"
            : state.SetupFinished ? "进入桌面"
            : IsProtectionReadyForFinish() ? "完成设置"
            : state.Completed.Contains("software") && !state.Protection.FourDimensionsPassed(state) ? "开始验真"
            : canEnter ? "进入桌面"
            : "继续安装";
        var taskAction = Action(taskPrimaryLabel,
            async (_, _) =>
            {
                if (string.IsNullOrEmpty(state.VmId)) LoadTask();
                else if (IsProtectionReadyForFinish()) FinishWorkspace();
                else if (state.Completed.Contains("software") && !state.SetupFinished && !state.Protection.FourDimensionsPassed(state)) Navigate(5);
                else if (canEnter) await EnterOriginalWorkspace();
                else Go(5);
            }, true);
        var taskCard = Card(taskHeader, taskInner, taskAction);
        taskCard.Margin = new Thickness(0);
        Grid.SetRow(taskCard, 2);
        Grid.SetColumn(taskCard, 2);
        overviewGrid.Children.Add(taskCard);

        main.Children.Add(overviewGrid);
        main.Children.Add(BuildUpdateCard());
    }
    internal sealed record OverviewHostView(string PillText, string PillBgHex, string PillFgHex, string VirtualizationValue, string TrackStatusText, string PrimaryActionText, bool OpenHostCheck = false);

    internal static OverviewHostView BuildOverviewHostView(HostInfo host, Machine.HyperVHostPreflight? preflight, string? ineligibilityReason, bool hasVm, bool setupFinished, string stageText, bool isHyperVStandalone)
    {
        var primary = setupFinished ? "进入专属空间" : hasVm ? "继续构建空间  →" : "开启专属空间  →";

        if (!isHyperVStandalone)
        {
            var virt = host.VBoxPath is null ? "Hyper-V / 待备" : "Hyper-V / VBox";
            var track = hasVm ? stageText : "本机条件已核对，备好系统镜像即可开启构建。";
            return new("已就绪", "#E6F5EE", "#176B4E", virt, track, primary);
        }
        if (ineligibilityReason is not null)
        {
            var virt = preflight is { EditionEligible: true }
                ? preflight.VirtualizationSummary
                : $"不支持（Windows {host.EditionName}）";
            var track = ineligibilityReason + " 请先查看「安装向导 → 1 电脑检查」。";
            return new("受限", "#FCEAE8", "#A32D26", virt, track, "查看本机条件与解决方案  →", OpenHostCheck: true);
        }
        if (preflight is { RebootPending: true })
        {
            return new("待重启", "#FDF2DB", "#8A550C", "Hyper-V 待重启生效",
                "检测到系统或 Hyper-V 启用等待重启，请先保存工作并重启电脑后再构建。", "查看重启说明  →", OpenHostCheck: true);
        }
        if (preflight is { AllReady: false })
        {
            return new("待启用", "#FDF2DB", "#8A550C", preflight.VmmsSummary,
                "需先在「安装向导 → 1 电脑检查」启用 Hyper-V 功能并启动 vmms 服务。", "先开启 Hyper-V 功能  →", OpenHostCheck: true);
        }
        return new("已就绪", "#E6F5EE", "#176B4E", "Hyper-V 硬件隔离 · 已就绪",
            hasVm ? stageText : "本机硬件与 Hyper-V 条件已就绪，备好系统镜像即可开启构建。", primary);
    }

    private static Border OverviewPill(string text, string bgHex, string fgHex) => new()
    {
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 3, 8, 3),
        Background = new SolidColorBrush(Appearance.Color(bgHex)),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Appearance.Color(fgHex)) }
    };
    private static Border OverviewRow(string label, string value)
    {
        var row = new DockPanel();
        var right = new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Muted });
        return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 7, 0, 7), Child = row };
    }
    private void DrawMachines()
    {
        subtitle.Text = "安装好的环境会显示在这里，打开就能继续使用。";
        if (string.IsNullOrEmpty(state.VmId))
        {
            main.Children.Add(Card(Text("还没有专属虚拟机", 20, false), Text("完成安装后，这里会出现你的 Windows 环境。\n已经装过？载入原任务即可继续管理。"), Row(Action("安装一个新环境", (_, _) => Go(0), true), Action("载入已有环境", (_, _) => LoadTask())))); return;
        }
        var status = state.Status;
        var canEnter = CanEnterDesktopWorkspace();
        main.Children.Add(Card(Text("WINDOWS / 专属环境", 11), Text(state.Options.VmName, 23, false), Text(status, 13), Text($"{state.Options.MemoryMb / 1024d:F0} GB 内存  ·  {state.Options.CpuCount} 个 CPU  ·  {state.Options.DiskGb} GB 虚拟磁盘", 11), Row(Action(canEnter ? "进入虚拟机" : "继续构建空间", async (_, _) => { if (canEnter) await EnterOriginalWorkspace(); else Go(5); }, true), Action(state.SetupFinished ? "检测与修复" : "继续安装 / 检查", (_, _) => { if(state.SetupFinished) Navigate(5); else Go(5); }), Action("打开存储目录", (_, _) => OpenWorkspace()), Action("新建独立环境任务", async (_, _) => await StartIndependentTask())), Text(state.SetupFinished ? "设置已完成。日常使用从这里进入原桌面。" : canEnter ? "已有安装进度会保留。完成配置与防护检查后可结束设置。" : "尚未完成 Windows 系统写入。请先前往「6 开始安装」继续部署。", 11)));
    }
    private void DrawDownloads()
    {
        subtitle.Text = "需要什么再下载，已有文件会先校验后复用。";
        if (Machine.HyperVStandaloneIneligibilityReason() is { } ineligibility)
            main.Children.Add(Card(Text("当前系统受限 · 下载已禁用", 15, false), Text(ineligibility, 12)));
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
        subtitle.Text = "一键顺序完成 4 项自动网络验真，再由本人确认浏览器出口。";
        foreach (var action in state.PendingActions.DistinctBy(a => a.Id)) main.Children.Add(PendingCard(action));
        main.Children.Add(BuildProtectionCard());
        var host = Machine.Read();
        if (!ProductProfile.IsHyperVStandalone)
        {
            main.Children.Add(Card(Text("黑屏与启动诊断", 16, false), Text("只读检查原虚拟机的运行状态、来宾工具与 NEM 兼容模式；若虚拟机已定格或白屏无响应，可使用下方受控入口强制关机并切换为单核防死锁兼容配置（不改主机安全功能）。", 11), Row(Action("读取虚拟机诊断", async (_, _) => await ReadVmDiagnostic()), Action("强制关闭无响应虚拟机并修复", async (_, _) => await ForceRepairVirtualBoxDialog()), Action("进入桌面", (_, _) => Navigate(7)))));
            main.Children.Add(BuildValidationCard());
        }
        main.Children.Add(Card(Text("本机与任务信息", 15, false),
            Text($"{host.Summary}\n当前任务：{(string.IsNullOrEmpty(state.VmId) ? "还没有已载入的环境。" : state.Stage)}", 12),
            Row(Action("查看验收报告", (_, _) => ShowReport()), Action("导出诊断信息", async (_, _) => await ExportDiagnostics()), Action("继续安装", (_, _) => Go(5)), Action("载入任务", (_, _) => LoadTask()))));
    }
    private void OpenWorkspace()
    {
        try { Paths.ValidateRoot(root.Text); if (!Directory.Exists(root.Text)) { Notice("这个目录还没有创建，开始下载或安装后会自动准备。"); return; } Process.Start(new ProcessStartInfo(root.Text) { UseShellExecute = true }); }
        catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
    }
    private void ApplyAppearance(Appearance value)
    {
        Appearance.Validate(value);
        var op = Appearance.ClampOpacity(value.Opacity);
        appearance = value with { Opacity = op };
        var bg = Appearance.Color(value.Background); var accent = Appearance.Color(value.Accent); var dark = Appearance.Luminance(bg) < .179;
        void Set(Brush brush, Color color)
        {
            if (PaletteSources.TryGetValue((SolidColorBrush)brush, out var source)) source.Value = color;
            else throw new SetupException("配色资源未能更新，请重新打开程序。");
        }
        var surfaceBase = dark
            ? Appearance.Mix(bg, Appearance.Color("#322219"), .42)
            : Appearance.Mix(bg, Appearance.Color("#FFFBF4"), .76);
        Set(Canvas, bg);
        Set(Accent, accent);
        Set(Surface, Appearance.WithAlpha(surfaceBase, op));
        Set(ModalSurface, Appearance.WithAlpha(surfaceBase, Math.Max(0.95, op)));
        var lightVeilAlpha = (byte)Math.Clamp((int)Math.Round(30 + (1.0 - op) * 68), 30, 96);
        Set(BackdropVeil, dark ? Color.FromArgb(196, bg.R, bg.G, bg.B) : Color.FromArgb(lightVeilAlpha, 250, 243, 232));
        var inkTarget = dark ? Appearance.Color("#FBF1E6") : Appearance.Color("#251912");
        var ink = Appearance.Contrast(inkTarget, bg) >= 4.5 ? inkTarget : Appearance.Legible(bg);
        Set(Ink, ink);
        var muted = Appearance.Mix(ink, bg, .38); for (var amount = .36; Appearance.Contrast(muted, bg) < 4.5 && amount >= 0; amount -= .04) muted = Appearance.Mix(ink, bg, amount);
        Set(Muted, muted);
        var warmLine = dark
            ? Appearance.Mix(Appearance.Color("#5E4332"), accent, .28)
            : Appearance.Mix(Appearance.Color("#DFC1A3"), accent, .24);
        Set(Line, Appearance.WithAlpha(warmLine, Math.Clamp(0.32 + op * 0.30, 0.32, 0.68)));
        Set(Pale, Appearance.WithAlpha(Appearance.Mix(surfaceBase, accent, dark ? .20 : .13), Math.Clamp(op + 0.14, 0.14, 0.92)));
        Set(OnAccent, Appearance.Legible(accent));
        if (quickOpacityButton is not null) quickOpacityButton.Content = $"◐ 透度 {(int)Math.Round(op * 100)}%";
        if (quickThemeButton is not null) quickThemeButton.Content = dark ? "☀ 云瓷浅色" : "☾ 暗夜视图";
        if (pages.Count > 0 && section == 0) Navigate(0);
        UpdateNativeCaption();
    }
    private Popup? opacityPopup;
    private Slider? quickOpacitySlider;
    private TextBlock? quickOpacityPct;
    private void BuildOpacityPopup()
    {
        quickOpacityPct = new TextBlock { Text = $"{(int)Math.Round(Appearance.ClampOpacity(appearance.Opacity) * 100)}%", FontWeight = FontWeights.Bold, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Right };
        quickOpacitySlider = new Slider { Minimum = 5, Maximum = 100, Value = Math.Round(Appearance.ClampOpacity(appearance.Opacity) * 100), IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 8, 0, 8) };
        quickOpacitySlider.ValueChanged += (_, _) =>
        {
            var next = appearance with { Opacity = Math.Round(quickOpacitySlider.Value) / 100d };
            if (quickOpacityPct is not null) quickOpacityPct.Text = $"{(int)Math.Round(quickOpacitySlider.Value)}%";
            ApplyAppearance(next);
            try { next.Save(); } catch { }
        };
        var popHead = new DockPanel();
        DockPanel.SetDock(quickOpacityPct, Dock.Right);
        popHead.Children.Add(quickOpacityPct);
        popHead.Children.Add(new TextBlock { Text = "界面透明度", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ink });
        var chips = new UniformGrid { Columns = 3, Margin = new Thickness(0, 2, 0, 8) };
        foreach (var (label, pct) in new[] { ("极透 5%", 5), ("玻璃 56%", 56), ("实底 90%", 90) })
        {
            var chip = Action(label, (_, _) => { if (quickOpacitySlider is not null) quickOpacitySlider.Value = pct; });
            chip.Padding = new Thickness(6, 4, 6, 4);
            chip.Margin = new Thickness(2, 0, 2, 0);
            chip.FontSize = 11;
            chips.Children.Add(chip);
        }
        var more = Action("更多配色设置…", (_, _) => { if (opacityPopup is not null) opacityPopup.IsOpen = false; AppearanceDialog(); });
        more.Padding = new Thickness(8, 5, 8, 5);
        more.FontSize = 11;
        more.Margin = new Thickness(0);
        var panel = new StackPanel
        {
            Width = 236,
            Children = { popHead, quickOpacitySlider, chips, new TextBlock { Text = "调低即可直接透过模块清晰看见底层背景", FontSize = 10.5, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap }, more }
        };
        opacityPopup = new Popup
        {
            PlacementTarget = quickOpacityButton,
            Placement = PlacementMode.Bottom,
            AllowsTransparency = true,
            StaysOpen = false,
            VerticalOffset = 6,
            Child = new Border { Background = ModalSurface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 12), Child = panel }
        };
    }
    private void ToggleOpacityPopup()
    {
        if (opacityPopup is null) return;
        if (quickOpacitySlider is not null) quickOpacitySlider.Value = Math.Round(Appearance.ClampOpacity(appearance.Opacity) * 100);
        opacityPopup.IsOpen = !opacityPopup.IsOpen;
    }
    private void ToggleQuickTheme()
    {
        var dark = Appearance.Luminance(Appearance.Color(appearance.Background)) < .179;
        var next = (dark ? Appearance.Default : Appearance.Presets[3]) with { Opacity = appearance.Opacity };
        ApplyAppearance(next);
        next.Save();
    }
    private void CloseInlineModal()
    {
        if (inlineModalSlot is not null) inlineModalSlot.Children.Clear();
        if (inlineModalOverlay is not null) inlineModalOverlay.Visibility = Visibility.Collapsed;
    }
    private void AppearanceDialog() => ShowInlineAppearanceOverlay();
    private void ShowInlineAppearanceOverlay()
    {
        if (inlineModalSlot is null || inlineModalOverlay is null)
        {
            BuildAppearanceDialog(value => value.Save()).ShowDialog();
            return;
        }
        inlineModalSlot.Children.Clear();
        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition());
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var closeBtn = Action("✕", (_, _) => CloseInlineModal());
        closeBtn.Padding = new Thickness(9, 4, 9, 4);
        closeBtn.Margin = new Thickness(0);
        DockPanel.SetDock(closeBtn, Dock.Right);
        header.Children.Add(closeBtn);
        header.Children.Add(new TextBlock { Text = "外观与透明度设置", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center });
        rootGrid.Children.Add(header);

        var body = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        scroll.Resources[typeof(ScrollBar)] = WorkspaceScrollbarStyle();
        Grid.SetRow(scroll, 1);
        rootGrid.Children.Add(scroll);

        body.Children.Add(Text("实时调整模块透明度（5% ~ 100%）与主题色，可直接透过窗口看见穹顶背景。", 12));
        var choices = new WrapPanel(); var accent = Input(appearance.Accent); var background = Input(appearance.Background);
        var opacityLabel = new TextBlock { Text = $"{(int)Math.Round(Appearance.ClampOpacity(appearance.Opacity) * 100)}%", FontWeight = FontWeights.SemiBold, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Right };
        var opacitySlider = new Slider
        {
            Minimum = 5, Maximum = 100, Value = Math.Round(Appearance.ClampOpacity(appearance.Opacity) * 100),
            IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 6, 0, 8)
        };
        foreach (var preset in Appearance.Presets)
        {
            var swatch = Action(preset.Name, (_, _) => { accent.Text = preset.Accent; background.Text = preset.Background; ApplyAppearance(preset with { Opacity = opacitySlider.Value / 100d }); });
            swatch.BorderBrush = Brush(preset.Accent);
            choices.Children.Add(swatch);
        }
        var opacityHeader = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
        DockPanel.SetDock(opacityLabel, Dock.Right);
        opacityHeader.Children.Add(opacityLabel);
        opacityHeader.Children.Add(new TextBlock { Text = "界面模块透明度（5% ~ 100%）", FontSize = 13, Foreground = Ink });
        var opacityPresets = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (label, pct) in new[] { ("极透 5%", 5), ("玻璃 56%", 56), ("实底 90%", 90) })
            opacityPresets.Children.Add(Action(label, (_, _) => { opacitySlider.Value = pct; }));

        body.Children.Add(choices);
        body.Children.Add(Field("主题色", accent, "支持 #RRGGBB，例如 #C9643B"));
        body.Children.Add(Field("背景色", background));
        body.Children.Add(opacityHeader);
        body.Children.Add(opacitySlider);
        body.Children.Add(opacityPresets);
        Appearance SelectedColors() => new("自定义", accent.Text.Trim(), background.Text.Trim()) { Opacity = Math.Round(opacitySlider.Value) / 100d };
        opacitySlider.ValueChanged += (_, _) =>
        {
            opacityLabel.Text = $"{(int)Math.Round(opacitySlider.Value)}%";
            try { ApplyAppearance(SelectedColors()); } catch { }
        };

        var modalActions = new Border
        {
            BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 10, 0, 0),
            Child = Row(
                Action("取消", (_, _) => CloseInlineModal()),
                Action("保存配色", (_, _) =>
                {
                    try
                    {
                        var value = SelectedColors();
                        ApplyAppearance(value);
                        value.Save();
                        CloseInlineModal();
                    }
                    catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
                }, true))
        };
        Grid.SetRow(modalActions, 2);
        rootGrid.Children.Add(modalActions);
        inlineModalSlot.Children.Add(rootGrid);
        inlineModalOverlay.Visibility = Visibility.Visible;
    }

    private Window BuildAppearanceDialog(Action<Appearance> save)
    {
        var dialog = new Window
        {
            Title = "外观与配色", Owner = this,
            Width = 510, Height = 560, MinWidth = 420, MinHeight = 280,
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40),
            ResizeMode = ResizeMode.CanResizeWithGrip,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ModalSurface, Foreground = Ink, FontFamily = FontFamily
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
        body.Children.Add(Text("换个喜欢的颜色与透度", 23, false));
        body.Children.Add(Text("支持将界面模块透明度调至 5% 透出背后穹顶；仅保存在本机。", 12));
        var choices = new WrapPanel(); var accent = Input(appearance.Accent); var background = Input(appearance.Background);
        var opacityLabel = new TextBlock { Text = $"{(int)Math.Round(Appearance.ClampOpacity(appearance.Opacity) * 100)}%", FontWeight = FontWeights.SemiBold, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Right };
        var opacitySlider = new Slider
        {
            Name = "AppearanceOpacitySlider",
            Minimum = 5, Maximum = 100, Value = Math.Round(Appearance.ClampOpacity(appearance.Opacity) * 100),
            IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 6, 0, 8)
        };
        foreach (var preset in Appearance.Presets)
        {
            var swatch = Action(preset.Name, (_, _) => { accent.Text = preset.Accent; background.Text = preset.Background; ApplyAppearance(preset with { Opacity = opacitySlider.Value / 100d }); });
            swatch.BorderBrush = Brush(preset.Accent);
            choices.Children.Add(swatch);
        }
        var opacityHeader = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
        DockPanel.SetDock(opacityLabel, Dock.Right);
        opacityHeader.Children.Add(opacityLabel);
        opacityHeader.Children.Add(new TextBlock { Text = "界面模块透明度（5% ~ 100%）", FontSize = 13, Foreground = Ink });
        var opacityPresets = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (label, pct) in new[] { ("极透 5%", 5), ("玻璃 56%", 56), ("实底 90%", 90) })
        {
            opacityPresets.Children.Add(Action(label, (_, _) => { opacitySlider.Value = pct; }));
        }
        body.Children.Add(choices);
        body.Children.Add(Field("主题色", accent, "支持 #RRGGBB，例如 #C9643B"));
        body.Children.Add(Field("背景色", background));
        body.Children.Add(opacityHeader);
        body.Children.Add(opacitySlider);
        body.Children.Add(opacityPresets);
        Appearance SelectedColors() => new("自定义", accent.Text.Trim(), background.Text.Trim()) { Opacity = Math.Round(opacitySlider.Value) / 100d };
        opacitySlider.ValueChanged += (_, _) =>
        {
            opacityLabel.Text = $"{(int)Math.Round(opacitySlider.Value)}%";
            try { ApplyAppearance(SelectedColors()); } catch { }
        };
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
            Background = ModalSurface, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 8, 24, 9), Child = actions
        };
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);
        return dialog;
    }
    public void PreviewTheme(bool dark, double opacity = Appearance.DefaultOpacity) => ApplyAppearance((dark ? Appearance.Presets[3] : Appearance.Default) with { Opacity = opacity });
    private void ShowLicenses()
    {
        var dialog = new Window { Title = "组件来源与许可", Owner = this, Width = 760, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = ModalSurface, Foreground = Ink };
        dialog.Content = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(18), Text = "项目原创代码：MIT\n开发者：李诗恩 · 林琛\n\n" + string.Join("\n\n", Catalog.Resources.Select(r => r.Name + "\n" + r.License)) + "\n\n" + Catalog.EmbeddedText("Gateway-THIRD-PARTY-NOTICES.txt") };
        dialog.ShowDialog();
    }
}
