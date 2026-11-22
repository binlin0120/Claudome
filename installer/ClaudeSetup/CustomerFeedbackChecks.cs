using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    // Runs only in --preview: deterministic customer scenarios, no real nodes,
    // passwords, VM writes, platform installation or deployment operations.
    internal async Task VerifyCustomerFeedbackUI(string directory)
    {
        var rows = new List<object>(); var failed = 0;
        async Task Caption(string name, ComboBox combo, string expected)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout(); combo.ApplyTemplate();
            var presenter = combo.Template.FindName("SelectionCaption", combo) as ContentPresenter;
            var text = presenter is null ? "caption_missing" : string.Concat(Descendants<TextBlock>(presenter).Select(x => x.Text));
            var passed = text == expected; if (!passed) failed++;
            rows.Add(new { name, passed, expected, actual = text });
        }
        async Task Click(Button button)
        {
            button.BringIntoView();
            UpdateLayout();
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
        }
        Button WorkspaceAction(string caption) => Descendants<Button>(main).Single(b => b.Content as string == caption);

        PreviewTheme(false);
        Appearance? savedAppearance = null;
        var appearanceDialog = BuildAppearanceDialog(value => savedAppearance = value);
        appearanceDialog.Height = 300;
        appearanceDialog.ShowActivated = false;
        appearanceDialog.ShowInTaskbar = false;
        appearanceDialog.Show();
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            appearanceDialog.UpdateLayout();
            var colorScroll = Descendants<ScrollViewer>(appearanceDialog).Single(s => s.Name == "AppearanceScroll");
            var firstColor = Descendants<TextBox>(colorScroll).First();
            var saveButton = Descendants<Button>(appearanceDialog).Single(b => b.Content as string == "保存配色");
            var previewButton = Descendants<Button>(appearanceDialog).Single(b => b.Content as string == "预览");
            firstColor.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            appearanceDialog.UpdateLayout();
            var scrolled = colorScroll.VerticalOffset > 0;
            var actionBottom = saveButton.TranslatePoint(new Point(0, saveButton.ActualHeight), appearanceDialog).Y;
            var visibleActions = previewButton.IsVisible && saveButton.IsVisible && actionBottom <= appearanceDialog.ActualHeight;
            firstColor.Text = "#B85F39";
            await Click(previewButton);
            var previewed = appearance.Accent == "#B85F39" && savedAppearance is null;
            await Click(saveButton);
            var resizable = appearanceDialog.ResizeMode == ResizeMode.CanResizeWithGrip;
            var passed = scrolled && visibleActions && previewed && resizable && savedAppearance?.Accent == "#B85F39" && !appearanceDialog.IsVisible;
            if (!passed) failed++;
            rows.Add(new { name = "appearance-dialog-wheel-and-fixed-preview-save-actions-work-in-a-short-window", passed });
        }
        finally
        {
            if (appearanceDialog.IsVisible) appearanceDialog.Close();
            PreviewTheme(false);
        }
        var escDialog = BuildAppearanceDialog(_ => { });
        escDialog.ShowActivated = false;
        escDialog.ShowInTaskbar = false;
        escDialog.Show();
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var source = System.Windows.PresentationSource.FromVisual(escDialog)!;
            escDialog.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, System.Windows.Input.Key.Escape)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var escClosed = !escDialog.IsVisible;
            if (!escClosed) failed++;
            rows.Add(new { name = "appearance-dialog-closes-on-escape-and-allows-resizing", passed = escClosed });
        }
        finally
        {
            if (escDialog.IsVisible) escDialog.Close();
        }
        var resourceDialog = BuildResourceDialog(out var resCpu, out _, out var resWarning, hostNemOverride: true, hostEditionOverride: "CoreCountrySpecific");
        resourceDialog.ShowActivated = false;
        resourceDialog.ShowInTaskbar = false;
        resourceDialog.Show();
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            resourceDialog.UpdateLayout();
            var bannerBorder = Descendants<Border>(resourceDialog).Single(b => b.Name == "ResourceNemWarningBanner");
            resCpu.Text = "2";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            resourceDialog.UpdateLayout();
            var shownForMultiCore = bannerBorder.Visibility == Visibility.Visible && resWarning.Text.Contains("家庭版") && resWarning.Text.Contains("0x0");
            resCpu.Text = "1";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            resourceDialog.UpdateLayout();
            var hiddenForSingleCore = bannerBorder.Visibility == Visibility.Collapsed;
            var applyButton = Descendants<Button>(resourceDialog).Single(b => b.Content as string == "关机后应用并回读");
            var applyBottom = applyButton.TranslatePoint(new Point(0, applyButton.ActualHeight), resourceDialog).Y;
            var resourceOk = shownForMultiCore && hiddenForSingleCore && applyButton.IsVisible && applyBottom <= resourceDialog.ActualHeight;
            if (!resourceOk) failed++;
            rows.Add(new { name = "resource-dialog-warns-on-home-nem-multicore-and-keeps-actions-visible", passed = resourceOk });
        }
        finally
        {
            if (resourceDialog.IsVisible) resourceDialog.Close();
        }
        var lightCaption = WorkspaceCaptionColor();
        var lightApplied = UpdateNativeCaption();
        PreviewTheme(true);
        var darkCaption = WorkspaceCaptionColor();
        var darkApplied = UpdateNativeCaption();
        var brandIcon = Descendants<Image>(ContentRoot).Single(i => i.Name == "WorkspaceBrandIcon");
        var brandingReady = Icon is BitmapImage { PixelWidth: 256, PixelHeight: 256 } &&
            ReferenceEquals(Icon, brandIcon.Source);
        if (!brandingReady) failed++;
        rows.Add(new { name = "native-window-and-sidebar-use-the-packaged-product-icon", passed = brandingReady });
        var captionReady = Title == "Claudome" && lightCaption != darkCaption &&
            Appearance.Contrast(darkCaption, Appearance.Legible(darkCaption)) >= 4.5 &&
            (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || lightApplied && darkApplied);
        if (!captionReady) failed++;
        rows.Add(new { name = "native-caption-displays-only-the-product-name-and-applies-theme", passed = captionReady });
        PreviewTheme(false);
        Navigate(0); UpdateLayout();
        await Click(WorkspaceAction("查看我的虚拟机"));
        var overviewRoutes = section == 2;
        await Click(WorkspaceAction("安装一个新环境"));
        overviewRoutes &= section == 1 && page == 0 && main.Children.Contains(pages[0]);
        await Click(next);
        overviewRoutes &= page == 1 && main.Children.Contains(pages[1]);
        await Click(previous);
        overviewRoutes &= page == 0;
        await Click(navigationButtons[0]);
        await Click(WorkspaceAction("管理网络节点"));
        overviewRoutes &= section == 3 && main.Children.Contains(pages[3]);
        if (!overviewRoutes) failed++;
        rows.Add(new { name = "overview-actions-and-wizard-controls-navigate-when-invoked", passed = overviewRoutes });
        Go(0);
        var pro = Platforms.Recommend(new("Windows", "x64", "Professional", true, true, 16, false, false));
        platform.ItemsSource = pro;
        foreach (var choice in pro) {
            platform.SelectedItem = choice; await Caption("platform-" + choice.Id, platform, choice.Label);
            var text = (consent.Content as TextBlock)?.Text ?? ""; var passed = text.Contains(choice.Name) && (choice.Id != "hyperv" || !text.Contains("VirtualBox"));
            if (!passed) failed++; rows.Add(new { name = "consent-" + choice.Id, passed });
            passed=hyperVConsoleCard?.Visibility==(choice.Id=="hyperv"?Visibility.Visible:Visibility.Collapsed);
            if(!passed)failed++;rows.Add(new{name="hyperv-console-card-"+choice.Id,passed});
        }
        platform.SelectedIndex = 0; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ContentRoot.ActualWidth, (int)ContentRoot.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(ContentRoot);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, "customer-pro-platform.png"))) encoder.Save(file);
        Go(5); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle); UpdateLayout();
        var wrapping=consent.Content is TextBlock label && label.TextWrapping==TextWrapping.Wrap && label.ActualHeight>20;
        if(!wrapping)failed++;rows.Add(new{name="consent-wraps-without-clipped-installation-scope",passed=wrapping});
        Go(2); var fixtureMedia=new WindowsMediaIdentity(@"D:\Fixture\Windows.iso",new string('a',64),2L<<30,[new(1,"Windows 11 Home","x64","10.0.26100.1","Client","Core"),new(6,"Windows 11 Pro","x64","10.0.26100.1","Client","Professional")]);
        var previousUser = user.Text;
        var previousMemory = memory.Text;
        var previousIso = iso.Text;
        iso.Text = " " + fixtureMedia.IsoPath + " ";
        memory.Text = "";
        foreach (var fixtureUser in new[] { "", "12345" })
        {
            user.Text = fixtureUser;
            var readInvoked = false;
            var selectedIndex = fixtureUser.Length == 0 ? 1 : 6;
            imageIndex.Text = selectedIndex.ToString();
            await InspectMedia((workspace, selectedIso, _, _, _) =>
            {
                readInvoked = workspace == root.Text.Trim() && selectedIso == fixtureMedia.IsoPath;
                return Task.FromResult(fixtureMedia);
            });
            var passed = readInvoked && Equals(windowsImages.SelectedValue, selectedIndex) &&
                license.IsChecked != true && !busy && pages[2].IsEnabled;
            if (!passed) failed++;
            rows.Add(new { name = fixtureUser.Length == 0
                ? "media-read-with-blank-account-and-memory"
                : "media-read-with-digit-led-guest-account", passed });
        }
        user.Text = previousUser;
        memory.Text = previousMemory;
        iso.Text = previousIso;
        ApplyMedia(fixtureMedia,1);await Caption("windows-image-caption",windowsImages,fixtureMedia.Images[0].Label);
        license.IsChecked=true;windowsImages.SelectedValue=6;RefreshConsent();
        var updated=license.IsChecked==false&&(license.Content as TextBlock)?.Text.Contains("Windows 11 Pro")==true&&(consent.Content as TextBlock)?.Text.Contains("Windows 11 Pro")==true;
        if(!updated)failed++;rows.Add(new{name="license-text-and-approval-follow-selected-image",passed=updated});
        ClearMedia();var cleared=license.IsChecked==false&&!license.IsEnabled&&(consent.Content as TextBlock)?.Text.Contains("待识别")==true;
        if(!cleared)failed++;rows.Add(new{name="unknown-image-has-no-windows-license-approval",passed=cleared});
        Go(3); source.SelectedIndex = 1;
        var fixture = NetworkImport.Parse("socks5://node.example:1080#FixtureNode").Nodes.Single(); node.ItemsSource = new[] { fixture }; node.SelectedIndex = 0;
        await Caption("proxy-node", node, fixture.ToString());
        foreach (var value in new[] { "US", "SG" }) { country.SelectedValue = value; await Caption("country-" + value, country, ((ChoiceItem)country.SelectedItem).Label); }
        foreach (var value in new[] { "UTC", "Pacific Standard Time" }) { timeZone.SelectedValue = value; await Caption("time-zone-" + value, timeZone, ((ChoiceItem)timeZone.SelectedItem).Label); }
        var before=platformInvalidations;var selectedPlatform=platform.SelectedValue?.ToString();RefreshPlatforms();
        var unchanged=platformInvalidations==before && platform.SelectedValue?.ToString()==selectedPlatform;
        if(!unchanged)failed++;rows.Add(new{name="host-refresh-does-not-revoke-unchanged-gateway",passed=unchanged});
        AllowEditing(false);var locked=pages.Take(5).All(x=>!x.IsEnabled)&&navigationButtons.Take(7).All(x=>!x.IsEnabled)&&navigationButtons[7].IsEnabled&&!consent.IsEnabled;AllowEditing(true);
        if(!locked)failed++;rows.Add(new{name="deployment-inputs-locked-while-running",passed=locked});
        foreach (var savedWithoutLoading in new[] { false, true }) {
            password.Password = "fictional-previous-password"; showPassword.IsChecked = true;
            passwordVisible.Text = "fictional-visible-password"; subscription.Text = "https://example.invalid/fictional-token";
            networkText.Text = "fictional-config"; proxyPassword.Password = "fictional-proxy-password"; proxyUser.Text = "previous-user";
            server.Text = "previous.example"; expectedIp.Text = "1.1.1.1"; adsApiKey.Password = "fictional-api-key";
            chain.IsChecked=true;upstreamSubscription.Text="https://example.invalid/previous-upstream";upstreamText.Text="previous-upstream-secret";
            upstreamNode.ItemsSource=new[]{fixture};upstreamNode.SelectedIndex=0;upstreamImported="previous-upstream";
            nodes = [fixture]; node.ItemsSource = nodes; node.SelectedIndex = 0; importedInput = "previous-input";
            ApplyLoadedTask(new SessionState { Options = new() { Root = Path.Combine(directory, "absent-task-" + savedWithoutLoading), NetworkSource = "节点链接 / 本地配置", Remember = savedWithoutLoading } }, false);
            var clean = ReadSecrets();
            var isolated = clean.VmPassword == "" && passwordVisible.Text == "" && clean.SubscriptionUrl == "" && clean.NetworkText == "" && clean.SelectedNodeJson == "" && clean.ExpectedExitIp == "" && clean.AdsPowerApiKey == "" && clean.UpstreamNodeJson=="" && clean.UpstreamNetworkText=="" && clean.UpstreamSubscriptionUrl=="" && upstreamImported=="" && chain.IsChecked==false && proxyPassword.Password == "" && proxyUser.Text == "" && server.Text == "" && importedInput == "" && selectedMedia is null && showPassword.IsChecked == false;
            if (!isolated) failed++; rows.Add(new { name = "task-load-clears-previous-secrets-remember-" + savedWithoutLoading, passed = isolated });
        }
        var localTask = state;
        var authTask = new SessionState { Options = new() { Root = Path.Combine(directory, "microsoft-auth-fixture"), PlatformId = "virtualbox", Remember = true } };
        ApplyLoadedTask(authTask, false);
        password.Password = "fictional-local-password";
        showPassword.IsChecked = true;
        const string fixtureMail = "test-user@example.invalid";
        const string fixturePassword = "fictional-microsoft-password";
        CommitGuestCredentials(authTask, "test-user", fixturePassword, @"C:\Users\test-user\AppData\Local", fixtureMail, true);
        bool MicrosoftPasswordIsolated() =>
            localGuestPasswordControls.Visibility == Visibility.Collapsed &&
            microsoftGuestPasswordHint.Visibility == Visibility.Visible && showPassword.IsChecked == false &&
            password.Password.Length == 0 && passwordVisible.Text.Length == 0 &&
            CurrentGuestPassword() == fixturePassword && ReadSecrets().VmMicrosoftAddress == fixtureMail;
        var protectedAfterSync = MicrosoftPasswordIsolated();
        ApplyLoadedTask(TaskCheckpointStore.Read(state.Options.StatePath), true);
        var protectedAfterRestore = MicrosoftPasswordIsolated();
        ApplyLoadedTask(localTask, false);
        var clearedOnTaskSwitch = CurrentGuestPassword().Length == 0 && ReadSecrets().VmMicrosoftAddress.Length == 0 &&
            localGuestPasswordControls.Visibility == Visibility.Visible;
        var protectedCredential = protectedAfterSync && protectedAfterRestore && clearedOnTaskSwitch;
        if (!protectedCredential) failed++;
        rows.Add(new { name = "microsoft-guest-password-isolated-from-local-password-controls", passed = protectedCredential });
        foreach(var value in new[]{new InstallationProgress("正在写入 Windows 系统映像",37),new InstallationProgress("等待 Windows 和来宾工具"),new InstallationProgress("旧盘清理完成，自动安装已结束",100)}) {
            ShowInstallationProgress(value);
            var ok=progressText.Text==value.Display&&progress.IsIndeterminate==(value.Percent is null)&&(value.Percent is null||progress.Value==value.Percent);
            if(!ok)failed++;rows.Add(new{name="installation-progress-"+(value.Percent?.ToString()??"unknown"),passed=ok});
        }
        Go(3);source.SelectedIndex=1;chain.IsChecked=true;upstreamSource.SelectedIndex=1;
        node.ItemsSource=new[]{fixture};node.SelectedIndex=0;
        var first=NetworkImport.Parse("socks5://first.example:1080#FixtureUpstream").Nodes.Single();upstreamNode.ItemsSource=new[]{first};upstreamNode.SelectedIndex=0;
        await Caption("upstream-node-caption",upstreamNode,first.ToString());
        var twoHop=ReadSecrets().UpstreamNodeJson==first.Json && Options().ChainEnabled;
        if(!twoHop)failed++;rows.Add(new{name="explicit-chain-keeps-final-and-upstream-separate",passed=twoHop});
        var auto=fixture with{Upstream=first};chain.IsChecked=false;node.ItemsSource=new[]{auto};node.SelectedIndex=0;
        var automatic=ReadSecrets().UpstreamNodeJson==first.Json && Options().ChainFromImport;
        if(!automatic)failed++;rows.Add(new{name="imported-chain-retained-without-explicit-override",passed=automatic});
        var savedChain=ReadSecrets();var savedOptions=Options();nodes=[fixture];node.ItemsSource=nodes;node.SelectedIndex=0;RestoreChainRelationship(savedChain,savedOptions);
        var restored=(node.SelectedItem as ProxyNode)?.Upstream?.Name==first.Name;
        if(!restored)failed++;rows.Add(new{name="restored-import-chain-keeps-refresh-relationship",passed=restored});
        chain.IsChecked=true;upstreamNode.BringIntoView();
        await SaveAlignmentView("chain-settings");
        Go(5);((FrameworkElement)pages[5].Children[1]).BringIntoView();
        await SaveAlignmentView("validation-actions");
        async Task SaveAlignmentView(string name) {
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();
            var shot=new RenderTargetBitmap((int)ContentRoot.ActualWidth,(int)ContentRoot.ActualHeight,96,96,PixelFormats.Pbgra32);shot.Render(ContentRoot);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(shot));
            using var file=File.Create(Path.Combine(directory,name+".png"));png.Save(file);
        }
        Navigate(7);await SaveAlignmentView("embedded-workspace");
        var oneSidebar=desktopGuidePanel is not null && navigation.Children.Contains(desktopGuidePanel) && desktopView!.Children.Count==1 && ContentRoot.ColumnDefinitions[0].Width.Value==310;
        if(!oneSidebar)failed++;rows.Add(new{name="workspace-navigation-and-guide-share-one-sidebar",passed=oneSidebar});
        var desktopPanel = desktopView;
        var expandedWidth = desktopPanel!.ActualWidth;
        var sidebarToggle = desktopSidebarToggle ?? throw new SetupException("Missing desktop sidebar toggle.");
        await Click(sidebarToggle);
        var fold = section == 7 && ContentRoot.ColumnDefinitions[0].Width.Value == 62 &&
            this.sidebarScroll?.Visibility == Visibility.Collapsed && sidebarToggle.IsVisible &&
            desktopPanel.ActualWidth > expandedWidth && main.Children.Contains(desktopPanel);
        PreviewTheme(true);
        await SaveAlignmentView("desktop-guide-collapsed-dark");
        await Click(sidebarToggle);
        fold &= ContentRoot.ColumnDefinitions[0].Width.Value == 310 &&
            this.sidebarScroll?.Visibility == Visibility.Visible && main.Children.Contains(desktopPanel);
        await SaveAlignmentView("desktop-guide-expanded-dark");
        PreviewTheme(false);
        if (!fold) failed++;
        rows.Add(new { name = "desktop-chevron-collapses-and-restores-the-same-guide-and-viewport", passed = fold });

        var sidebarScroll = Descendants<ScrollViewer>(ContentRoot).Single(s => s.Name == "SidebarScroll");
        var maintenance = Descendants<Expander>(desktopGuidePanel!).Single(e => e.Header as string == "检查与维护");
        maintenance.IsExpanded = true;
        sidebarScroll.ScrollToTop();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        UpdateLayout();
        var rail = sidebarScroll.Template.FindName("PART_VerticalScrollBar", sidebarScroll) as ScrollBar;
        rail?.ApplyTemplate();
        var track = rail?.Template.FindName("PART_Track", rail) as Track;
        var railReady = rail is { ActualWidth: <= 12, IsVisible: true } &&
            track?.Thumb is not null && sidebarScroll.ScrollableHeight > 0;
        if (railReady)
        {
            var pageDown = track!.IncreaseRepeatButton;
            if (pageDown.Command is System.Windows.Input.RoutedCommand command)
                command.Execute(null, pageDown);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            railReady = sidebarScroll.VerticalOffset > 0;
        }
        if (!railReady) failed++;
        rows.Add(new { name = "slim-sidebar-scrollbar-preserves-access-to-maintenance", passed = railReady });
        Button MaintenanceAction(string label) => ((StackPanel)maintenance.Content).Children.OfType<Button>().Single(b => b.Content as string == label);
        await Click(MaintenanceAction("防护结果"));
        var maintenanceRoutes = section == 5;
        await Click(WorkspaceAction("打开程序内桌面"));
        maintenanceRoutes &= section == 7;
        await Click(MaintenanceAction("打开网络配置"));
        maintenanceRoutes &= section == 3;
        if (!maintenanceRoutes) failed++;
        rows.Add(new { name = "desktop-maintenance-actions-open-the-actual-recovery-and-network-pages", passed = maintenanceRoutes });
        Navigate(7);
        maintenance.IsExpanded = false;
        var savedDesktopStatus=state.Status;var savedDesktopVm=state.VmId;
        state.VmId="11111111-1111-1111-1111-111111111111";state.Status="已暂停，可继续原任务";latestTaskActivity="";latestTaskDownload="";SetDesktopConnectionStatus(DesktopWaitingText());UpdateDesktopGuide();
        var openedShutdownPrompt = false;
        var dismissShutdown = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        dismissShutdown.Tick += (_, _) =>
        {
            var dialog = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.Title == "关闭虚拟机");
            if (dialog is null) return;
            dismissShutdown.Stop();
            openedShutdownPrompt = Descendants<Button>(dialog).Any(b => b.Content as string == "关机并退出");
            dialog.DialogResult = false;
        };
        dismissShutdown.Start();
        try { await Click(desktopShutdown!); }
        finally { dismissShutdown.Stop(); }
        var shutdownRoute = openedShutdownPrompt && !shuttingDownVm && !busy && state.Status == "已暂停，可继续原任务";
        if (!shutdownRoute) failed++;
        rows.Add(new { name = "desktop-shutdown-button-opens-confirmation-and-honors-cancel", passed = shutdownRoute });
        await SaveAlignmentView("desktop-paused");
        var pausedConsistent=consoleStatus!.Text.Contains("已暂停")&&consolePlaceholder!.Text.Contains("已暂停")&&!consolePlaceholder.Text.Contains("正在自动连接")&&desktopContinue!.IsEnabled&&desktopContinue.Content.ToString()!.Contains("继续原任务");
        if(!pausedConsistent)failed++;rows.Add(new{name="paused-desktop-has-visible-resume-and-no-false-connecting-status",passed=pausedConsistent});
        busy=true;AllowEditing(false);SetDesktopConnectionStatus(DesktopWaitingText());UpdateDesktopGuide();progress.IsIndeterminate=false;progress.Value=37;RecordTaskActivity("正在准备安装网络组件");RecordTaskDownload("安装网络组件 · 37%");
        await SaveAlignmentView("desktop-network-progress");
        var liveProgress=!desktopContinue!.IsEnabled&&desktopActivity!.Text.Contains("37%")&&desktopActivity.Text.Contains("安装网络组件")&&desktopActivityProgress!.Value==37&&desktopActivityProgress.Visibility==Visibility.Visible&&!consolePlaceholder!.Text.Contains("已暂停");
        if(!liveProgress)failed++;rows.Add(new{name="network-preparation-progress-visible-on-desktop",passed=liveProgress});
        busy=false;AllowEditing(true);taskFaulted=true;RecordTaskActivity(ExternalGateway.BootstrapFailureMessage("BOOTSTRAP_DNS_TLS_FAILED"));SetDesktopConnectionStatus(DesktopWaitingText());UpdateDesktopGuide();
        await SaveAlignmentView("desktop-network-failure");
        var visibleFailure=consolePlaceholder!.Text.Contains("BOOTSTRAP_DNS_TLS_FAILED")&&desktopActivity!.Text.Contains("BOOTSTRAP_DNS_TLS_FAILED")&&desktopContinue!.IsEnabled&&desktopActivityProgress!.Visibility==Visibility.Collapsed;
        if(!visibleFailure)failed++;rows.Add(new{name="network-failure-remains-visible-instead-of-infinite-connecting",passed=visibleFailure});
        taskFaulted = false;
        framebufferConflictVm = state.VmId;
        busy = true;
        installationFinished = new TaskCompletionSource();
        using (var installToken = new CancellationTokenSource())
        {
            cancellation = installToken;
            SetDesktopConnectionStatus("检测到 VirtualBox 显示缓冲冲突。可在左侧直接恢复画面。");
            UpdateDesktopActivity();
            var otherStepBlocked = desktopRecoverDisplay is { Visibility: Visibility.Visible, IsEnabled: false };
            state.AwaitingGuestTools = true;
            UpdateDesktopActivity();
            await SaveAlignmentView("desktop-framebuffer-recovery");
            var enabledWhileWaiting = desktopRecoverDisplay is { Visibility: Visibility.Visible, IsEnabled: true } &&
                desktopContinue is { IsEnabled: false };
            installToken.Cancel();
            UpdateDesktopActivity();
            var blockedWhileCancelling = desktopRecoverDisplay is { IsEnabled: false };
            var passed = otherStepBlocked && enabledWhileWaiting && blockedWhileCancelling;
            if (!passed) failed++;
            rows.Add(new { name = "framebuffer-recovery-offered-during-guest-wait-and-blocked-during-other-stages", passed });
        }
        cancellation = null;
        installationFinished = null;
        state.AwaitingGuestTools = false;
        framebufferConflictVm = "";
        busy = false;
        latestTaskActivity="";latestTaskDownload="";state.VmId=savedDesktopVm;state.Status=savedDesktopStatus;UpdateDesktopActivity();SetDesktopConnectionStatus(DesktopWaitingText());
        state.Completed.Add("guest-ready");UpdateDesktopGuide();
        var checklistText=string.Join("\n",guideStepChoice!.Items.Cast<CustomerGuideStep>().Select(x=>x.Instructions));
        var checksShown=checklistText.Contains("Clash")&&checklistText.Contains("AdsPower")&&checklistText.Contains("ip.net.coffee/claude")&&guideStepChoice.Items.Count>=8;
        if(!checksShown)failed++;rows.Add(new{name="post-configuration-shows-Clash-and-AdsPower-checklist",passed=checksShown});
        await SaveAlignmentView("software-checklist");state.Completed.Remove("guest-ready");UpdateDesktopGuide();
        var protection = BuildProtectionCard();
        protection.Measure(new Size(780, double.PositiveInfinity));
        protection.Arrange(new Rect(0, 0, 780, protection.DesiredSize.Height));
        var auditControls = Descendants<Button>(protection).Select(b => b.Content?.ToString()).ToArray();
        var auditButtonReady = auditControls.Contains("核验当前防护") && auditControls.Contains("启用来宾阻断审核并重测");
        if (!auditButtonReady) failed++;
        rows.Add(new { name = "protection-card-offers-explicit-guest-block-audit-action", passed = auditButtonReady });

        var beforeCompletionFixture = state;
        state = new SessionState
        {
            VmId = "33333333-3333-3333-3333-333333333333",
            Options = new() { ManualAdsPower = true },
            Completed = ["software"],
            NetworkPassed = true,
            CoreConfigSha256 = new string('b', 64),
            Protection = new()
            {
                ConfigHash = new string('b', 64),
                Boundary = new(CheckStatus.Passed),
                Exit = new(CheckStatus.Passed),
                Dns = new(CheckStatus.Unverified)
            }
        };
        UpdateDesktopActivity();
        var manualCompletionEntry = desktopFinish is { Visibility: Visibility.Visible, IsEnabled: true } &&
            desktopConfirmAdsExit is { Visibility: Visibility.Visible, IsEnabled: true } &&
            state.Protection.PendingChecks(state).SequenceEqual(new[] { "AdsPower 窗口出口", "DNS 与直连旁路" });
        state.Protection.Browser = new(CheckStatus.Passed);
        state.Protection.Dns = new(CheckStatus.Passed);
        UpdateDesktopActivity();
        manualCompletionEntry &= desktopConfirmAdsExit is { Visibility: Visibility.Collapsed } &&
            state.Protection.PendingChecks(state).Length == 0 &&
            FormatCompletionHint(state, connected: true).Contains("可以完成设置");
        state.SetupFinished = true;
        UpdateDesktopActivity();
        manualCompletionEntry &= desktopFinish is { Visibility: Visibility.Collapsed };
        if (!manualCompletionEntry) failed++;
        rows.Add(new { name = "desktop-shows-finish-and-manual-browser-confirmation-with-current-pending-checks", passed = manualCompletionEntry });
        state = beforeCompletionFixture;
        UpdateDesktopActivity();

        using (var fixedIpReport = System.Text.Json.JsonDocument.Parse("""{"networkPassed":false,"exitCode":"IP_MISMATCH"}"""))
        using (var countryReport = System.Text.Json.JsonDocument.Parse("""{"networkPassed":false,"exitCode":"COUNTRY_MISMATCH"}"""))
        using (var reconnectReport = System.Text.Json.JsonDocument.Parse("""{"networkPassed":true,"exitCode":""}"""))
        {
            var now = DateTime.UtcNow;
            var ip = ProtectionEvidence.GuestExit(fixedIpReport.RootElement, now);
            var location = ProtectionEvidence.GuestExit(countryReport.RootElement, now);
            var recovered = ProtectionEvidence.GuestExit(reconnectReport.RootElement, now);
            var createsCurrentEvidence = ip.Status == CheckStatus.Failed && ip.Evidence.Contains("固定 IPv4") &&
                location.Status == CheckStatus.Failed && location.Evidence.Contains("国家或地区") &&
                recovered.Status == CheckStatus.Passed && recovered.CheckedUtc == now;
            if (!createsCurrentEvidence) failed++;
            rows.Add(new { name = "outlet-reports-distinguish-fixed-ip-and-region-and-accept-a-fresh-reconnect", passed = createsCurrentEvidence });
        }

        var savedVmId=state.VmId;state.VmId="11111111-1111-1111-1111-111111111111";RefreshCreationFields();
        var fieldsLocked=cpu.IsReadOnly && memory.IsReadOnly && disk.IsReadOnly && root.IsReadOnly;
        if(!fieldsLocked)failed++;rows.Add(new{name="existing-vm-creation-fields-explicitly-locked",passed=fieldsLocked});
        state.VmId=savedVmId;RefreshCreationFields();
        await VerifyNativeHostFixture(rows,directory);
        var desktopInputControls = desktopGuidePanel is not null &&
            Descendants<Expander>(desktopGuidePanel).Any(control => control.Header?.ToString() == "手动传送纯文本") &&
            Descendants<Button>(desktopGuidePanel).All(button => button.Content?.ToString() != "聚焦虚拟机键盘") &&
            Descendants<Expander>(desktopGuidePanel).Any(control => control.Header?.ToString() == "检查与维护" &&
                (control.Content as Panel)?.Children.OfType<Button>().Any(button => button.Content?.ToString() == "强制关闭无响应虚拟机并修复") == true);
        if (!desktopInputControls) failed++;
        rows.Add(new { name = "desktop-exposes-text-sharing-and-keyboard-controls", passed = desktopInputControls });
        var previousVm = state.VmId;
        var previousPlatform = state.Options.PlatformId;
        state.VmId = "11111111-1111-1111-1111-111111111111";
        UpdateNativeClipboardControl();
        var defaultClipboard = nativeClipboardButton?.Content?.ToString() == "关闭双向剪贴板" &&
            nativeClipboardStatus?.Text.Contains("自动开启") == true;
        state.Options.PlatformId = "hyperv";
        state.Options.SharedClipboardEnabled = true;
        state.Options.HyperVBasicConsoleConfirmed = false;
        UpdateNativeClipboardControl();
        var hypervDefaultClipboard = nativeClipboardButton?.Visibility == Visibility.Visible &&
            nativeClipboardButton?.Content?.ToString() == "关闭双向剪贴板" &&
            nativeClipboardStatus?.Text.Contains("增强会话双向剪贴板") == true;
        state.Options.SharedClipboardEnabled = false;
        UpdateNativeClipboardControl();
        var hypervOptOutClipboard = nativeClipboardButton?.Content?.ToString() == "启用双向剪贴板" &&
            nativeClipboardStatus?.Text.Contains("已关闭") == true;
        state.Options.SharedClipboardEnabled = true;
        state.Options.PlatformId = previousPlatform;
        state.VmId = previousVm;
        UpdateNativeClipboardControl();
        if (!defaultClipboard) failed++;
        rows.Add(new { name = "virtualbox-desktop-shows-clipboard-on-by-default", passed = defaultClipboard });
        if (!hypervDefaultClipboard || !hypervOptOutClipboard) failed++;
        rows.Add(new { name = "hyperv-desktop-shows-clipboard-on-by-default-and-toggle", passed = hypervDefaultClipboard && hypervOptOutClipboard });
        var clickFocus = new[] { 0x0201, 0x0204, 0x0207, 0x020B, 0x0246 }.All(NativeConsoleHost.IsPointerPress) &&
            new[] { 0x0200, 0x0202, 0x020A, 0x0100 }.All(message => !NativeConsoleHost.IsPointerPress(message));
        if (!clickFocus) failed++;
        rows.Add(new { name = "native-keyboard-focus-follows-pointer-presses-only", passed = clickFocus });
        var cpuPolicyVerified = SetupOptions.RecommendedCpuCount("virtualbox", true) == 1 &&
            SetupOptions.RecommendedCpuCount("virtualbox", false) == 2 &&
            SetupOptions.RecommendedCpuCount("hyperv", true) == 2 &&
            Descendants<CheckBox>(pages[1]).Contains(useCustomVmConfig);
        if (!cpuPolicyVerified) failed++;
        rows.Add(new { name = "nem-cpu-policy-defaults-to-1-core-and-exposes-custom-resource-toggle", passed = cpuPolicyVerified });
        country.SelectedValue = "JP";
        var cageInspiredUpgrades = RegionCatalog.All.Count >= 22 &&
            timeZone.SelectedValue?.ToString() == "Tokyo Standard Time" &&
            Descendants<Button>(pages[3]).Any(b => b.Content?.ToString() == "测速并自动识别出口地区与时区") &&
            softwareChecklist is not null && softwareChecklist.Text.Contains("外层门禁") && softwareChecklist.Text.Contains("内层环境") && softwareChecklist.Text.Contains("资源策略") &&
            desktopGuidePanel is not null && Descendants<Button>(desktopGuidePanel).Any(b => b.Content?.ToString() == "复制检测网址");
        if (!cageInspiredUpgrades) failed++;
        rows.Add(new { name = "cage-inspired-node-probe-timezone-sync-and-live-dual-layer-badge", passed = cageInspiredUpgrades });
        var autoRestoreDir = Path.Combine(directory, "auto-restore-fixture");
        if (Directory.Exists(autoRestoreDir)) Directory.Delete(autoRestoreDir, true);
        Paths.EnsureWorkspace(autoRestoreDir);
        var autoState = new SessionState { VmId = "22222222-2222-2222-2222-222222222222", Stage = "6 / 7  配置来宾环境", Status = "已暂停，可继续原任务", Options = new() { Root = autoRestoreDir, Remember = true, NetworkSource = "已有订阅链接", NodeName = fixture.Name, Country = "US", TimeZoneId = "Pacific Standard Time" } };
        autoState.Save();
        SecretStore.Save(autoState.Options.Private, new Secrets { VmPassword = "FictionalAutoRestore!1", SubscriptionUrl = "https://example.invalid/auto-sub", SelectedNodeJson = fixture.Json, AdsPowerApiKey = "fictional-auto-ads-key" });
        ApplyLoadedTask(autoState, loadSecrets: true, interactive: false);
        var restoredSecrets = ReadSecrets();
        var guideUnified = CustomerGuide.Software(autoState.Options).Any(s => s.Id == "ads-proxy" && s.Instructions.Contains("127.0.0.1") && s.Instructions.Contains("17897") && s.Instructions.Contains("保留助手写入"));
        var autoRestorePassed = restoredSecrets.VmPassword == "FictionalAutoRestore!1" && restoredSecrets.AdsPowerApiKey == "fictional-auto-ads-key" && restoredSecrets.SubscriptionUrl == "https://example.invalid/auto-sub" && restoredSecrets.SelectedNodeJson == fixture.Json && importedInput == "https://example.invalid/auto-sub" && consent.IsChecked == false && guideUnified;
        if (!autoRestorePassed) failed++;
        rows.Add(new { name = "auto-restore-loads-remembered-secrets-and-unified-adspower-guide", passed = autoRestorePassed });
        var comparisonState = Clone(autoState.Options);
        var oldSelection = (ProxyNode)node.SelectedItem;
        var equivalent = """
            proxies:
              - {port: "1080", server: NODE.EXAMPLE, type: socks5, name: FixtureNode}
            """;
        var refreshedSecrets = ReadSecrets();
        var refreshCount = 0;
        await RefreshSelectedRoutes(comparisonState, refreshedSecrets, CancellationToken.None,
            (_, _) => { refreshCount++; return Task.FromResult(equivalent); });
        var equivalentPassed = refreshCount == 1 && refreshedSecrets.SelectedNodeJson == oldSelection.Json &&
            NetworkImport.SameDefinition(oldSelection, NetworkImport.Parse(equivalent).Nodes.Single());
        if (!equivalentPassed) failed++;
        rows.Add(new { name = "restored-subscription-accepts-equivalent-reordered-node", passed = equivalentPassed });

        var changed = """
            proxies:
              - {name: FixtureNode, type: socks5, server: node.example, port: 1080, password: changed-mock-value}
              - {name: FixtureOther, type: socks5, server: other.example, port: 1080}
            """;
        var blocked = false;
        try
        {
            await RefreshSelectedRoutes(comparisonState, refreshedSecrets, CancellationToken.None,
                (_, _) => { refreshCount++; return Task.FromResult(changed); });
        }
        catch (SetupException error) { blocked = error.Message.Contains("同名节点"); }
        var replacedDraft = blocked && refreshCount == 2 && node.SelectedIndex == -1 && nodes.Count == 2 &&
            AppliedLogin().SelectedNodeJson == oldSelection.Json;
        node.SelectedIndex = 0;
        var newlySelected = ReadSecrets();
        await RefreshSelectedRoutes(comparisonState, newlySelected, CancellationToken.None,
            (_, _) => throw new SetupException("An explicit selection must not re-fetch the subscription"));
        replacedDraft &= newlySelected.SelectedNodeJson == nodes[0].Json && appliedSecrets?.SelectedNodeJson == oldSelection.Json;
        if (!replacedDraft) failed++;
        rows.Add(new { name = "real-subscription-change-refreshes-draft-and-requires-explicit-reselection", passed = replacedDraft });

        // Draft edits cannot replace the route or fixed-exit data used by maintenance.
        source.SelectedIndex = 1;
        subscription.Text = "https://example.invalid/unsaved-draft";
        expectedIp.Text = "203.0.113.100";
        var draftIsolated = AppliedLogin().SelectedNodeJson == fixture.Json &&
            AppliedLogin().SubscriptionUrl == "https://example.invalid/auto-sub" && AppliedLogin().ExpectedExitIp.Length == 0 &&
            Descendants<Button>(pages[3]).Any(b => b.Content?.ToString() == "应用线路");
        if (!draftIsolated) failed++;
        rows.Add(new { name = "network-draft-does-not-change-applied-route", passed = draftIsolated });

        var revised = Clone(autoState);
        revised.CredentialsRevision = Guid.NewGuid().ToString("N");
        SecretStore.Save(revised.Options.Private, new Secrets { ExpectedExitIp = "203.0.113.42" }, revised.CredentialsRevision);
        var versionedSecrets =
            SecretStore.Load(JsonFile.Read<SessionState>(autoState.Options.StatePath))?.SelectedNodeJson == fixture.Json &&
            SecretStore.Load(revised)?.ExpectedExitIp == "203.0.113.42";
        revised.DeferCheckpoint = true;
        revised.SetupFinished = true;
        revised.Save();
        versionedSecrets &= !JsonFile.Read<SessionState>(autoState.Options.StatePath).SetupFinished;
        revised.DeferCheckpoint = false;
        revised.Save();
        var prior = JsonFile.Read<SessionState>(TaskCheckpointStore.BackupPath(autoState.Options.StatePath));
        versionedSecrets &= SecretStore.Load(JsonFile.Read<SessionState>(autoState.Options.StatePath))?.ExpectedExitIp == "203.0.113.42" &&
            SecretStore.Load(prior)?.SelectedNodeJson == fixture.Json;
        if (!versionedSecrets) failed++;
        rows.Add(new { name = "route-credentials-follow-atomic-checkpoint-and-previous-version", passed = versionedSecrets });

        autoState.Options.ManualAdsPower = true;
        var manualGuide = CustomerGuide.Software(autoState.Options).ToDictionary(step => step.Id, step => step.Instructions);
        var oldManualAction = new UserAction("旧版手动指引", ["请填写 127.0.0.1:17897"], CustomerGuide.ManualAdsPowerId);
        var manualCard = PendingCard(oldManualAction);
        manualCard.Measure(new Size(440, double.PositiveInfinity));
        manualCard.Arrange(new Rect(0, 0, 440, manualCard.DesiredSize.Height));
        var manualCardText = string.Join("\n", Descendants<TextBlock>(manualCard).Select(block => block.Text));
        var matchesManualChecklist = manualGuide["ads-login"].Contains("Default Profile") &&
            manualGuide["ads-os"].Contains("User-Agent 保持「全部」") &&
            manualGuide["ads-proxy"].Contains("No Proxy") && manualGuide["ads-proxy"].Contains("Clash TUN") &&
            manualGuide["ads-proxy"].Contains("ip-api") && manualGuide["ads-proxy"].Contains("留空") &&
            manualGuide["ads-fingerprint"].Contains("「基于 IP」") && manualGuide["ads-fingerprint"].Contains("「每次询问」") &&
            manualGuide["ads-fingerprint"].Contains("WebRTC 选为「禁用」") &&
            !manualGuide.Values.Any(value => value.Contains("17897")) &&
            manualCardText.Contains("No Proxy") && !manualCardText.Contains("17897");
        if (!matchesManualChecklist) failed++;
        rows.Add(new { name = "manual-adspower-checklist-replaces-stale-socks5-handoff", passed = matchesManualChecklist });
        Navigate(0);
        var hasLocalUpdaterControls = Descendants<Button>(main).Any(b => b.Content?.ToString() == "检查在线更新") &&
            Descendants<Button>(main).Any(b => b.Content?.ToString() == "从本地 ZIP 更新") &&
            updateStatusText.Text.Contains($"v{SelfUpdater.CurrentVersion().ToString(3)}") &&
            globalUpdateBanner?.Visibility == Visibility.Collapsed;
        var fakeManifest = new ReleaseManifest("9.9.9", "v9.9.9", "Claudome_9.9.9_win-x64.zip", new string('a', 64), new string('b', 64), "fixture", "");
        ShowAvailableUpdateNotice(new UpdateCheckResult(true, fakeManifest, "https://example.invalid/Claudome_9.9.9_win-x64.zip", "fixture"), fakeManifest);
        hasLocalUpdaterControls &= globalUpdateBanner?.Visibility == Visibility.Visible &&
            globalUpdateBannerText?.Text.Contains("v9.9.9") == true &&
            globalUpdateActionButton?.Content?.ToString() == "一键更新到 v9.9.9";
        if (globalUpdateBanner is not null) globalUpdateBanner.Visibility = Visibility.Collapsed;
        if (applyUpdateButton is not null) applyUpdateButton.Visibility = Visibility.Collapsed;
        pendingUpdateCheck = null;
        if (!hasLocalUpdaterControls) failed++;
        rows.Add(new { name = "overview-exposes-in-place-local-updater-controls", passed = hasLocalUpdaterControls });
        Directory.Delete(autoRestoreDir, true);
        ApplyLoadedTask(new SessionState { Options = new() { Root = Path.Combine(directory, "clean-reset-fixture") } }, false);
        JsonFile.Write(Path.Combine(directory, "customer-feedback-ui-tests.json"), new { type = "synthetic WPF visual captions; no VM deployment", passed = rows.Count - failed, failed, tests = rows });
        if (failed != 0) throw new SetupException("Customer UI regression checks failed.");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
