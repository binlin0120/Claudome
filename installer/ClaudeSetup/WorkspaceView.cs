using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    private Grid? desktopView;
    private Grid? shellBody;
    private ScrollViewer? shellContentScroll;
    private Border? desktopHiddenFooter;
    private StackPanel? desktopGuidePanel;
    private ComboBox? desktopNavigation;
    private bool settingDesktopNavigation;
    private TextBlock? consolePlaceholder,softwareChecklist,guideStepText;
    private ComboBox? guideStepChoice;
    private string guidePhase="";
    private Button? desktopContinue, desktopSyncAuth, desktopRecoverDisplay, desktopForceRepair, desktopManualAdsPower;
    private Border? desktopForceRepairBanner;
    private TextBlock? desktopManualAdsGuide, desktopFinishHint;
    private Button? desktopConfirmAdsExit, desktopFinish;
    private bool recoveringDisplay;
    private string framebufferConflictVm = "";
    private Border? consoleFrame;
    private TextBlock? desktopActivity;
    private ProgressBar? desktopActivityProgress;
    private string latestTaskActivity="",latestTaskDownload="";
    private bool taskFaulted, installationNetworkFailed;
    private NativeConsoleHost? consoleHost;
    private bool fittingVBoxDisplay;
    private bool waitingForGuestFrame;
    private DateTime? waitingForGuestFrameSinceUtc;
    private bool notifiedMultiCoreBlankTimeout;
    private VBoxDisplayLayout? lastVBoxLayout;
    private readonly DispatcherTimer consoleResizeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private TextBlock? consoleStatus,consoleGuide;
    private readonly TextBlock creationNote=Text("首次创建前填写的 CPU、内存与磁盘参数会实际传入虚拟机。",11);
    private bool connectingConsole,autoConnectDesktop,desktopTimerHooked,closingDesktop;
    private readonly DesktopConnectionPolicy desktopConnectionPolicy=new();
    private CancellationTokenSource? desktopConnectionCancellation;
    private ConsoleWindow? lastConsoleWindow;
    private sealed record PendingConsole(string VmId, string Executable, string? Name, string? Config);
    private PendingConsole? pendingConsole;

    private IReadOnlyList<NodeImportIssue> lastImportIssues=[];
    private void ShowImportIssues()=>Notice(lastImportIssues.Count==0?"没有单条导入错误记录。":string.Join("\n",lastImportIssues.Take(12).Select(x=>$"第 {x.Index} 项：{x.Reason}")));
    private readonly DispatcherTimer desktopTimer=new(){Interval=TimeSpan.FromSeconds(2)};
    private void RefreshCreationFields()
    {
        var created=!string.IsNullOrEmpty(state.VmId);
        foreach(var field in new[]{vmName,memory,cpu,disk,user})field.IsReadOnly=created;
        root.IsReadOnly=created;
        useCustomVmConfig.IsEnabled=!created;
        localGuestPasswordControls.Visibility = state.Options.UsesMicrosoftGuestAccount ? Visibility.Collapsed : Visibility.Visible;
        microsoftGuestPasswordHint.Visibility = state.Options.UsesMicrosoftGuestAccount ? Visibility.Visible : Visibility.Collapsed;
        creationNote.Text=created?"原虚拟机已创建，这些新建参数已锁定。CPU 和内存请在正常关机后通过“虚拟机桌面”的资源调整入口修改；磁盘不会被自动缩小或覆盖。":"首次创建前填写的 CPU、内存与磁盘参数会实际传入虚拟机。";
        UpdateCreationCpuNemWarning();
    }
    private void DrawDesktop()
    {
        title.Text="虚拟机桌面";subtitle.Text="左侧查看当前步骤，在右侧操作原来的真实虚拟机。";
        if(desktopView is null) {
            desktopView=new Grid{MinHeight=340,Margin=new Thickness(0,0,0,4)};
            var guide=new StackPanel();desktopGuidePanel=guide;
            consoleStatus=Text("尚未连接桌面",14,false);consoleGuide=Text("",12);
            softwareChecklist=Text("",11);softwareChecklist.Margin=new Thickness(0);desktopActivity=Text("",11);
            var safetyBadgeBox=new Border{Background=Pale,BorderBrush=Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(10,8,10,8),Margin=new Thickness(0,2,0,10),Child=softwareChecklist};
            desktopActivityProgress=new ProgressBar{Height=4,Margin=new Thickness(0,4,0,6),Foreground=Accent,Visibility=Visibility.Collapsed};
            desktopContinue=Action("继续原任务并连接",async(_,_)=>await ResumeDesktopTask(),true);
            desktopSyncAuth=Action("同步虚拟机账号密码",async(_,_)=>await SyncGuestCredentialsDialog());
            desktopManualAdsPower = Action("不用 API，手动使用 AdsPower", (_, _) => SelectManualAdsPower());
            desktopManualAdsGuide = Text("", 12);
            desktopConfirmAdsExit = Action("确认 AdsPower 出口", (_, _) => ConfirmBrowserExit());
            desktopFinish = Action("完成设置", (_, _) => FinishWorkspace(), true);
            desktopFinishHint = Text("", 11);
            desktopRecoverDisplay = Action("保存状态并恢复显示", async (_, _) => await RecoverVirtualBoxDisplay(), true);
            desktopRecoverDisplay.Visibility = Visibility.Collapsed;
            desktopForceRepair = Action("强制关闭并恢复 1 核", async (_, _) => await ForceRepairVirtualBoxDialog(), true);
            var forceRepairText = Text("检测到多核虚拟机长时间停留在 0×0 无画面状态，可能触发了 NEM 跨核死锁。建议强制关闭并自动恢复为 1 核（保留原磁盘与进度）。", 11, false);
            forceRepairText.Margin = new Thickness(0, 0, 0, 6);
            desktopForceRepairBanner = new Border
            {
                Background = Pale, BorderBrush = Accent, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed,
                Child = new StackPanel { Children = { forceRepairText, desktopForceRepair } }
            };
            guide.Children.Add(Card(Text("当前步骤与检查",15,false),consoleStatus,desktopRecoverDisplay,desktopForceRepairBanner,consoleGuide,safetyBadgeBox,
                new ScrollViewer{Content=desktopActivity,MaxHeight=95,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},desktopActivityProgress,desktopContinue,desktopManualAdsPower,desktopManualAdsGuide,
                desktopConfirmAdsExit,desktopFinish,desktopFinishHint,desktopSyncAuth,
                CreateClipboardControls(),
                Action("保存并暂停任务",(_,_)=>PauseCurrentTask()),
                new Expander{Header="检查与维护",Foreground=Ink,Background=Surface,Content=new StackPanel{Children={
                    Action("一键连接网络", async (_, _) => await EnterOriginalWorkspace(networkOnly: true)),
                    Action("备用线路",async(_,_)=>await PickSavedRoute()),
                    Action("防护结果",(_,_)=>Navigate(5)),
                    Action("重新连接桌面",async(_,_)=>{desktopConnectionPolicy.ResumeExplicitly();autoConnectDesktop=true;await ConnectDesktop(requested:true);},true),
                    Action("适配显示区域",async(_,_)=>await FitConnectedConsole(true)),
                    Action("同步虚拟机账号密码",async(_,_)=>await SyncGuestCredentialsDialog()),
                Action("检查 Clash / AdsPower",async(_,_)=>await RunValidation(false,false)),
                Action("查看黑屏与启动诊断",async(_,_)=>await ReadVmDiagnostic()),
                Action("保存状态并恢复显示", async (_, _) => await RecoverVirtualBoxDisplay()),
                Action("强制关闭无响应虚拟机并修复",async(_,_)=>await ForceRepairVirtualBoxDialog()),
                Action("关机后调整 CPU / 内存",(_,_)=>ResourceDialog()),
                Action("打开网络配置", (_, _) => Navigate(3)),
                Text("关闭本程序会收起本任务的控制台。虚拟机、磁盘和安装进度仍会保留。",10) }}}));
            guideStepChoice=Choice([]);guideStepChoice.Template=source.Template;guideStepChoice.DisplayMemberPath="Title";
            guideStepText=Text("",12);
            guideStepChoice.SelectionChanged+=(_,_)=>{if(guideStepText is not null)guideStepText.Text=(guideStepChoice.SelectedItem as CustomerGuideStep)?.Instructions??"";};
            guide.Children.Insert(1,Card(Text("逐步操作指引",15,false),guideStepChoice,new ScrollViewer{Content=guideStepText,MaxHeight=190,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},
                Row(Action("复制检测网址",(_,_)=>{Clipboard.SetText("https://ip.net.coffee/claude");Notice("已复制检测页网址到主机剪贴板。可用双向剪贴板或手动传送纯文本。");}))));
            navigation.Children.Add(guide);
            desktopNavigation=Choice(sections);desktopNavigation.Template=source.Template;desktopNavigation.SelectedIndex=7;desktopNavigation.Margin=new Thickness(0,0,0,12);
            desktopNavigation.SelectionChanged+=(_,_)=>{if(!settingDesktopNavigation && desktopNavigation.SelectedIndex>=0)Navigate(desktopNavigation.SelectedIndex);};navigation.Children.Insert(1,desktopNavigation);
            consoleHost=new NativeConsoleHost{Focusable=true,MinHeight=320,Visibility=Visibility.Collapsed};
            consoleHost.SizeChanged += (_, _) => {
                if (state.Options.PlatformId != "virtualbox" || consoleHost?.IsAttached != true) return;
                consoleResizeTimer.Stop();
                consoleResizeTimer.Start();
            };
            taskSaveStatus=Text("任务进度将自动保存",11);guide.Children.Add(taskSaveStatus);
            consolePlaceholder=new TextBlock{Text="载入原任务后连接虚拟电脑",MaxWidth=680,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Foreground=Muted,FontSize=16,TextWrapping=TextWrapping.Wrap};
            var surface=new Grid();surface.Children.Add(consolePlaceholder);surface.Children.Add(consoleHost);
            consoleFrame=new Border{Background=Surface,BorderBrush=Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),ClipToBounds=true,Child=surface};desktopView.Children.Add(consoleFrame);
            desktopView.Loaded+=async(_,_)=>{autoConnectDesktop=!desktopConnectionPolicy.Suspended;desktopTimer.Start();UpdateDesktopGuide();if(autoConnectDesktop)await ConnectDesktop();};
            desktopView.Unloaded += (_, _) => {
                desktopTimer.Stop();
                consoleResizeTimer.Stop();
                desktopConnectionCancellation?.Cancel();
                ReleaseDesktopWindow();
                lastVBoxLayout = null;
            };
            if (!desktopTimerHooked)
            {
                Deactivated += (_, _) => consoleHost?.CancelPendingGuestFocus();
                PreviewMouseDown += (_, e) =>
                {
                    if (consoleHost?.IsAttached == true && e.OriginalSource is DependencyObject sourceObj && !IsDescendantOfHost(sourceObj))
                        consoleHost.CancelPendingGuestFocus();
                };
                consoleResizeTimer.Tick += async (_, _) => {
                    consoleResizeTimer.Stop();
                    if (connectingConsole || fittingVBoxDisplay || shuttingDownVm)
                    {
                        consoleResizeTimer.Start();
                        return;
                    }
                    await FitConnectedConsole();
                };
                desktopTimer.Tick += async (_, _) => {
                    UpdateDesktopGuide();
                    if (shuttingDownVm) return;
                    if(lastConsoleWindow is not null && !ConsoleWindowLocator.IsAlive(lastConsoleWindow, state.Options.VmName))
                    {
                        ReleaseDesktopWindow();
                        lastConsoleWindow = null;
                        lastVBoxLayout = null;
                        desktopConnectionPolicy.Suspend();
                        autoConnectDesktop = false;
                        SetDesktopConnectionStatus("原控制台已关闭，已停止自动连接。需要时请选择“重新连接桌面”。");
                    }
                    if (consoleHost?.IsAttached == true)
                    {
                        if (state.Options.PlatformId == "virtualbox")
                        {
                            if (lastVBoxLayout is null || (state.Completed.Contains("guest-ready") &&
                                lastVBoxLayout?.LastCheckedAspect is null && VirtualBoxDisplayFit.CanAdaptViewport(consoleHost.ViewportPixels)))
                                await FitConnectedConsole();
                            await RefreshDefaultNativeClipboard();
                        }
                    }
                    else if (autoConnectDesktop && !desktopConnectionPolicy.Suspended && !connectingConsole)
                        await ConnectDesktop();
                };
                desktopTimerHooked = true;
            }
        }
        SetDesktopSidebar(true);main.Children.Add(desktopView);UpdateDesktopGuide();UpdateDesktopActivity();if(consoleHost?.IsAttached!=true&&!connectingConsole)SetDesktopConnectionStatus(DesktopWaitingText());FitDesktopViewport();
        UpdateNativeClipboardControl(nativeClipboardReady ? "双向剪贴板已开启，可直接复制粘贴。" : null);
    }
    private void UpdateDesktopGuide()
    {
        UpdateDesktopActivity();
        if(consoleGuide is null)return;
        consoleGuide.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        consoleGuide.Text = string.IsNullOrEmpty(state.VmId) ? "先通过安装向导创建或载入一个任务。" : state.Stage + "\n" + state.Status;
        var softwarePhase = state.Completed.Contains("guest-ready");
        var phase = softwarePhase ? (state.Options.ManualAdsPower ? "software-manual" : "software") : "windows";
        if(guideStepChoice is not null&&guidePhase!=phase) {
            guidePhase=phase;guideStepChoice.ItemsSource=softwarePhase?CustomerGuide.Software(state.Options):CustomerGuide.Windows(state.Options);guideStepChoice.SelectedIndex=0;
        }
        if(desktopSyncAuth is not null)
        {
            var needsAuthSync = state.PendingActions.Any(a => a.Id == "guest-auth-sync");
            desktopSyncAuth.Visibility = (!string.IsNullOrEmpty(state.VmId) && (!state.Completed.Contains("guest-ready") || needsAuthSync))
                ? Visibility.Visible
                : Visibility.Collapsed;
            desktopSyncAuth.BorderBrush = needsAuthSync ? Accent : Line;
        }
        if(softwareChecklist is not null)
        {
            var outerStatus = BoundaryRuntime.IsWindowsSignInNetworkActive(state)
                ? "外层门禁：安装网络暂时保留（最长 10 分钟）。进入桌面后可同步账号。"
                : BoundaryRuntime.HasInstallationNetwork
                    ? "外层门禁：安装网络通道已连通（仅经所选节点）"
                : !string.IsNullOrEmpty(state.VmId) && BoundaryRuntime.BackendFor(state) is not null
                    ? "外层门禁：已锁定受控出站保护（无直连回退）"
                    : "外层门禁：未激活（默认阻断外网）";
            var innerStatus = ManualAdsPowerReady ? "内层环境：来宾网络已核验，AdsPower 待手动检查"
                : state.NetworkPassed && state.BrowserRuntimePassed
                ? "内层环境：Clash 与专用浏览器参数已核验"
                : state.Completed.Contains("guest-ready")
                    ? "内层环境：待执行 Clash / AdsPower 回读核验"
                    : "内层环境：等待 Windows 与来宾工具就绪";
            var cpuMode = string.IsNullOrEmpty(state.VmId)
                ? "资源策略：待创建虚拟机"
                : state.Options.ManualCpuConfirmed
                    ? $"资源策略：客户自定义配置（{state.Options.CpuCount} 核 · 不自动降核）"
                    : $"资源策略：{state.Options.CpuCount} 核默认防卡死模式";
            softwareChecklist.Text = outerStatus + "\n" + innerStatus + "\n" + cpuMode;
        }
    }
    private void SetDesktopSidebar(bool enabled)
    {
        UpdateDesktopSidebarLayout(enabled);
        foreach(var button in navigationButtons)button.Visibility=enabled?Visibility.Collapsed:Visibility.Visible;
        if(desktopNavigation is not null){settingDesktopNavigation=true;desktopNavigation.SelectedIndex=enabled?7:section;desktopNavigation.Visibility=enabled?Visibility.Visible:Visibility.Collapsed;settingDesktopNavigation=false;}
        if(desktopGuidePanel is not null)desktopGuidePanel.Visibility=enabled?Visibility.Visible:Visibility.Collapsed;
        if(desktopHiddenFooter is not null)desktopHiddenFooter.Visibility=enabled?Visibility.Collapsed:Visibility.Visible;
        if(shellBody is not null)shellBody.Margin=enabled?new Thickness(18,20,20,18):new Thickness(34,26,30,18);
        if(shellContentScroll is not null)shellContentScroll.VerticalScrollBarVisibility=enabled?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto;
    }
    private void FitDesktopViewport()
    {
        if(section==7 && desktopView is not null && shellContentScroll is not null && shellContentScroll.ActualHeight>0)desktopView.Height=Math.Max(340,shellContentScroll.ActualHeight-6);
    }
    private void ReleaseDesktopWindow()
    {
        // A VMware frontend can switch to a different VM within its existing window.
        var switched = lastConsoleWindow is { } window &&
            !ConsoleWindowLocator.IsAlive(window, state.Options.VmName);
        consoleHost?.Detach(showOriginal: switched);
        SetWaitingForGuestFrame(false);
        nativeClipboardReady = false;
        nextNativeClipboardCheck = DateTime.MinValue;
        UpdateNativeClipboardControl();
        if (consoleFrame is not null) consoleFrame.Background = Surface;
    }

    private bool IsDescendantOfHost(DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, consoleHost)) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void CloseDesktopConsole()
    {
        closingDesktop = true;
        autoConnectDesktop = false;
        desktopConnectionPolicy.Suspend();
        desktopConnectionCancellation?.Cancel();
        desktopTimer.Stop();
        consoleResizeTimer.Stop();
        consoleHost?.ReleaseGuestInputCapture();
        ReleaseDesktopWindow();
        ConsoleWindowLocator.Hide(lastConsoleWindow, state.Options.VmName, state.VmId);
    }

    private async Task HidePendingConsole()
    {
        if (pendingConsole is not { } pending) return;
        pendingConsole = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var window = await Task.Run(() => ConsoleWindowLocator.Find(pending.VmId, pending.Executable,
                    pending.Name, pending.Config), timeout.Token).WaitAsync(timeout.Token);
                if (window is not null) { ConsoleWindowLocator.Hide(window, pending.Name ?? "", pending.VmId); return; }
                await Task.Delay(350, timeout.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (SetupException) { }
    }

    private async Task ConnectDesktop(bool requested = false)
    {
        if(!taskSavingEnabled||closingDesktop||shuttingDownVm||connectingConsole||desktopConnectionPolicy.Suspended||(!requested&&taskFaulted)||section!=7)return;
        if(consoleHost is null||string.IsNullOrEmpty(state.VmId))return;
        connectingConsole=true;
        desktopConnectionCancellation?.Dispose();desktopConnectionCancellation=new();
        var token=desktopConnectionCancellation.Token;var connectingVm=state.VmId;
        try {
            SetDesktopConnectionStatus("正在自动连接原虚拟机桌面…");
            var o=state.Options;
            await using var backend=Backends.Create(o.PlatformId);
            await backend.EnsureMachine(state,token);
            if(!await backend.IsRunning(state,token)) {
                desktopConnectionPolicy.ObserveStopped();
                SetDesktopConnectionStatus(DesktopWaitingText());if(!busy)autoConnectDesktop=false;
                return;
            }
            desktopConnectionPolicy.ObserveRunning();
            if (backend is VirtualBox && consoleHost.IsAttached != true && VirtualBox.HasFramebufferConflict(state))
            {
                autoConnectDesktop = false;
                framebufferConflictVm = connectingVm;
                SetDesktopConnectionStatus("检测到 VirtualBox 显示缓冲冲突。可在左侧直接恢复画面。");
                return;
            }
            framebufferConflictVm = "";
            var (executable, vmName, vmConfig) = DesktopConsoleTarget(o);
            if (backend is VirtualBox preparingDisplay)
            {
                await VirtualBoxDisplayFit.Prepare(preparingDisplay, connectingVm, token);
                await VirtualBoxDisplayFit.DisableMouseCapture(preparingDisplay, connectingVm, token);
            }
            var lookup=await Task.Run(()=>{var found=ConsoleWindowLocator.Find(connectingVm,executable,out var exists,vmName,vmConfig);return(Window:found,FrontendExists:exists);},token);
            token.ThrowIfCancellationRequested();
            if(lookup.Window is null&&!lookup.FrontendExists&&desktopConnectionPolicy.TryLaunchFrontend()) {
                // One frontend request per explicit task continuation; never loop launches.
                if(!await backend.IsRunning(state,token)){desktopConnectionPolicy.Suspend();return;}
                pendingConsole = new(connectingVm, executable, vmName, vmConfig);
                if(o.PlatformId=="virtualbox")new VmConsole().Attach(Path.GetDirectoryName(executable)!,connectingVm);
                else {
                    var start=new ProcessStartInfo(executable){UseShellExecute=true};
                    if(o.PlatformId=="hyperv"){start.ArgumentList.Add("localhost");start.ArgumentList.Add(o.VmName);}
                    else {start.ArgumentList.Add("-n");start.ArgumentList.Add(vmConfig!);}
                    Process.Start(start);
                }
            }
            var window=lookup.Window;
            for(var i=0;window is null&&i<12;i++) {
                await Task.Delay(500,token);
                window=await Task.Run(()=>ConsoleWindowLocator.Find(connectingVm,executable,vmName,vmConfig),token);
            }
            if(window is null){SetDesktopConnectionStatus("正在等待原控制台窗口准备，不会重复打开。请处理虚拟机组件的首次提示。");return;}
            if (closingDesktop) { ConsoleWindowLocator.Hide(window, o.VmName, connectingVm); pendingConsole = null; return; }
            token.ThrowIfCancellationRequested();if(section!=7||state.VmId!=connectingVm||desktopConnectionPolicy.Suspended)return;
            pendingConsole = null;
            lastConsoleWindow = window;
            var lastVmInfo = "";
            if (backend is VirtualBox vbox)
            {
                lastVmInfo = await vbox.Run(token, "showvminfo", connectingVm, "--machinereadable");
                if (!state.Completed.Contains("guest-ready") && VirtualBoxDisplayFit.ReadAdditionsRunLevel(lastVmInfo) < 2)
                    _ = await vbox.EnsureGuestAdditionsMounted(state, lastVmInfo, token);
            }
            consoleHost.Visibility=Visibility.Visible;
            if (consoleFrame is not null) consoleFrame.Background = Brush("#0E151C");
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle,token);UpdateLayout();
            lastVBoxLayout = null;
            token.ThrowIfCancellationRequested();
            if (closingDesktop || section != 7 || state.VmId != connectingVm) return;
            consoleHost.SetFittedSize(null);
            consoleHost.Attach(window.Handle);
            consoleHost.Fit(forceRedraw: true);
            desktopConnectionPolicy.ObserveWindow();
            ConsoleWindowLocator.Track(connectingVm, window);
            if (backend is VirtualBox vboxFit)
            {
                const string framePending = "已接入原虚拟机控制台，正在等待来宾画面。可点击右侧按 Shift 唤醒。";
                SetWaitingForGuestFrame(VirtualBoxDisplayFit.ReadGuestSize(lastVmInfo) is null);
                if (waitingForGuestFrame)
                {
                    SetDesktopConnectionStatus(framePending);
                    try { await VirtualBoxDisplayFit.WakeBlankGuest(vboxFit, connectingVm, lastVmInfo, token); }
                    catch (SetupException) { /* Keep the attached console available for manual wake-up. */ }
                }
                lastVBoxLayout = await FitVirtualBoxDisplay(vboxFit, connectingVm, consoleHost, null, token);
                if (lastVBoxLayout is { } fitted) await ApplyEmbeddedDisplayFit(consoleHost, fitted, token);
                SetWaitingForGuestFrame(lastVBoxLayout is null);
                if (waitingForGuestFrame) SetDesktopConnectionStatus(framePending);
                consoleHost.Fit(forceRedraw: true);
            }
            if (!waitingForGuestFrame) SetDesktopConnectionStatus(DesktopConnectedStatus());
            UpdateDesktopActivity();
            if (backend is VirtualBox) await RefreshDefaultNativeClipboard();
        }catch(OperationCanceledException){}
        catch(Exception ex){SetDesktopConnectionStatus(Redaction.SafeError(ex));if(consoleHost?.IsAttached!=true&&consoleHost is not null){consoleHost.Visibility=Visibility.Collapsed;if(consoleFrame is not null)consoleFrame.Background=Surface;}}
        finally{connectingConsole=false;UpdateDesktopActivity();}
    }
    private Task<VBoxDisplayLayout?> FitVirtualBoxDisplay(VirtualBox box, string vmId,
        NativeConsoleHost host, VBoxDisplayLayout? previous, CancellationToken token, bool requested = false) =>
        VirtualBoxDisplayFit.Fit(box, vmId, host.ViewportPixels,
            previous is null ? host.VisibleScrollViewport() : null, previous, token,
            adaptRatio: true, force: requested, frameAllowance: host.MeasureVirtualBoxFrameAllowance());

    private static async Task ApplyEmbeddedDisplayFit(NativeConsoleHost host, VBoxDisplayLayout layout, CancellationToken token)
    {
        var console = host.GuestWindow;
        host.SetFittedSize(VirtualBoxDisplayFit.FittedWindowSize(layout));
        await Task.Delay(175, token);
        if (host.IsAttached && host.IsVisible && host.GuestWindow == console)
            host.ClipToGuestFrame(VirtualBoxDisplayFit.ScaledGuestSize(layout));
    }

    private async Task FitConnectedConsole(bool requested = false)
    {
        var host = consoleHost;
        if (host?.IsAttached != true || host.Visibility != Visibility.Visible || section != 7)
        {
            if (requested) SetDesktopConnectionStatus("请先连接原虚拟机桌面，再适配显示区域。");
            return;
        }
        if (connectingConsole || fittingVBoxDisplay || shuttingDownVm) return;
        if (state.Options.PlatformId != "virtualbox")
        {
            host.Fit();
            return;
        }

        var token = desktopConnectionCancellation?.Token ?? CancellationToken.None;
        var vmId = state.VmId;
        var window = host.GuestWindow;
        fittingVBoxDisplay = true;
        try
        {
            await using var box = new VirtualBox(new CommandRunner());
            if (requested) await VirtualBoxDisplayFit.Prepare(box, vmId, token);
            var previousScale = lastVBoxLayout;
            host.Fit();
            var fit = await FitVirtualBoxDisplay(box, vmId, host, previousScale, token, requested);
            if (fit?.ScaleFactor != previousScale?.ScaleFactor || requested)
                await Task.Delay(125, token); // Let the native GUI process its display-setting notification.
            if (token.IsCancellationRequested || section != 7 || state.VmId != vmId
                || host != consoleHost || host.GuestWindow != window || !host.IsAttached) return;
            lastVBoxLayout = fit;
            if (fit is { } fitted)
            {
                await ApplyEmbeddedDisplayFit(host, fitted, token);
                if (waitingForGuestFrame)
                {
                    SetWaitingForGuestFrame(false);
                    if (!taskFaulted) SetDesktopConnectionStatus(DesktopConnectedStatus());
                    UpdateDesktopActivity();
                }
            }
            host.Fit(forceRedraw: requested);
            if (requested) SetDesktopConnectionStatus(fit.HasValue
                ? $"已适配显示区域。来宾分辨率 {fit.Value.Guest.Width} × {fit.Value.Guest.Height}。"
                : "等待来宾报告画面分辨率，请在桌面显示后重试。");
        }
        catch (OperationCanceledException) { }
        catch (SetupException)
        {
            if (requested) SetDesktopConnectionStatus("VirtualBox 尚未完成画面适配，请稍后重试。");
        }
        finally { fittingVBoxDisplay = false; }
    }
    private string DesktopConnectedStatus() => !busy && state.Status.Contains("暂停")
        ? "已连接原虚拟机桌面。任务已暂停，可在左侧继续。"
        : "已连接原虚拟机桌面。";
    private const string VmOffStatus = "虚拟机已关机。可从这里重新打开原任务。";
    private string DesktopWaitingText()=>taskFaulted?latestTaskActivity:shuttingDownVm?"正在等待原虚拟机正常关机…":busy?
        (consoleHost?.IsAttached == true ? "原虚拟机桌面已连接，正在检查原任务和安装网络。" : "正在准备原任务与安装网络，检查通过后将启动并接入桌面。"):
        string.IsNullOrEmpty(state.VmId)?"请先创建或载入原任务。":state.Status == VmOffStatus ? VmOffStatus : state.Status.Contains("暂停")||desktopConnectionPolicy.Suspended?"原任务已暂停。点击“继续原任务并连接”后恢复。":"原虚拟机尚未运行。点击“继续原任务并连接”启动原任务。";
    private void SetDesktopConnectionStatus(string message)
    {
        if(consoleStatus is not null)consoleStatus.Text=message;
        if(consolePlaceholder is not null){consolePlaceholder.Text=message+"\n\nWindows 安装和软件操作将在这里显示";consolePlaceholder.Visibility=consoleHost?.IsAttached==true?Visibility.Collapsed:Visibility.Visible;}
    }
    private void RecordTaskActivity(string message){latestTaskActivity=message;latestTaskDownload="";UpdateDesktopActivity();}
    private void RecordTaskDownload(string message){latestTaskDownload=message;UpdateDesktopActivity();}
    private bool CanPauseForDisplayRecovery => installationFinished is not null &&
        cancellation?.IsCancellationRequested == false && state.AwaitingGuestTools;

    private void UpdateDesktopActivity()
    {
        UpdateShutdownCommand(section == 7);
        if (desktopGuidePanel is not null) desktopGuidePanel.IsEnabled = !shuttingDownVm;
        if (desktopRecoverDisplay is not null)
        {
            desktopRecoverDisplay.Visibility = !string.IsNullOrEmpty(framebufferConflictVm) &&
                framebufferConflictVm == state.VmId ? Visibility.Visible : Visibility.Collapsed;
            desktopRecoverDisplay.IsEnabled = !recoveringDisplay && !connectingConsole && (!busy || CanPauseForDisplayRecovery);
        }
        var offerMultiCoreRecovery = ShouldOfferMultiCoreBlankRecovery(
            state.Options.PlatformId, state.Options.CpuCount,
            waitingForGuestFrame && consoleHost?.IsAttached == true && !string.IsNullOrEmpty(state.VmId),
            waitingForGuestFrameSinceUtc is { } since ? DateTime.UtcNow - since : TimeSpan.Zero);
        if (desktopForceRepairBanner is not null)
            desktopForceRepairBanner.Visibility = offerMultiCoreRecovery ? Visibility.Visible : Visibility.Collapsed;
        if (desktopForceRepair is not null)
            desktopForceRepair.IsEnabled = !recoveringDisplay && !connectingConsole;
        if (offerMultiCoreRecovery && !notifiedMultiCoreBlankTimeout)
        {
            notifiedMultiCoreBlankTimeout = true;
            SetDesktopConnectionStatus("多核虚拟机持续无画面（0×0），可能发生 NEM 跨核死锁。请点击左侧“强制关闭并恢复 1 核”。");
        }
        if (desktopManualAdsPower is not null)
        {
            desktopManualAdsPower.Visibility = state.Completed.Contains("software") && !state.Options.ManualAdsPower
                ? Visibility.Visible : Visibility.Collapsed;
            desktopManualAdsPower.IsEnabled = !busy && !recoveringDisplay;
        }
        if (desktopManualAdsGuide is not null)
        {
            desktopManualAdsGuide.Text = ManualAdsPowerReady
                ? "在右侧 AdsPower 编辑现有 Default Profile，按指引核对 No Proxy 和浏览器设置。"
                : "已选手动使用 AdsPower。请先连接并核验原任务网络，再使用 No Proxy 环境。";
            desktopManualAdsGuide.Visibility = state.Options.ManualAdsPower ? Visibility.Visible : Visibility.Collapsed;
        }
        var showFinish = state.Completed.Contains("software") && !state.SetupFinished && !string.IsNullOrEmpty(state.VmId);
        if (desktopConfirmAdsExit is not null)
        {
            desktopConfirmAdsExit.Visibility = showFinish && state.Options.ManualAdsPower && state.Protection.Browser.Status != CheckStatus.Passed
                ? Visibility.Visible : Visibility.Collapsed;
            desktopConfirmAdsExit.IsEnabled = !busy && !recoveringDisplay;
        }
        if (desktopFinish is not null)
        {
            desktopFinish.Visibility = showFinish ? Visibility.Visible : Visibility.Collapsed;
            desktopFinish.IsEnabled = !busy && !recoveringDisplay;
        }
        if (desktopFinishHint is not null)
        {
            desktopFinishHint.Visibility = showFinish && !busy ? Visibility.Visible : Visibility.Collapsed;
            desktopFinishHint.Text = FormatCompletionHint(state, BoundaryRuntime.BackendFor(state) is not null);
        }
        if (desktopContinue is not null)
        {
            desktopContinue.IsEnabled = !busy && !recoveringDisplay;
            desktopContinue.Content = busy ? "正在处理…" :
                state.SetupFinished ? "打开原环境" :
                installationNetworkFailed ? "重连安装网络" :
                ManualAdsPowerReady ? "查看 AdsPower 手动指引" :
                state.PendingActions.Any(ResumePlan.IsGuestNetworkProbe) ? "重试来宾联网检查" :
                state.Options.ManualAdsPower ? "继续原任务并连接" :
                ResumePlan.CanResumeBrowser(state) ? "继续 AdsPower 配置与验收" : "继续原任务并连接";
        }
        if(desktopActivity is not null){desktopActivity.Text=latestTaskActivity+(busy&&!string.IsNullOrEmpty(latestTaskDownload)?"\n"+latestTaskDownload:"");desktopActivity.Visibility=!busy&&desktopActivity.Text==consoleStatus?.Text?Visibility.Collapsed:Visibility.Visible;}
        if(desktopActivityProgress is not null){desktopActivityProgress.Visibility=busy?Visibility.Visible:Visibility.Collapsed;desktopActivityProgress.IsIndeterminate=progress.IsIndeterminate;desktopActivityProgress.Value=progress.Value;}
    }
    private bool ManualAdsPowerReady => state.Options.ManualAdsPower && state.NetworkPassed &&
        BoundaryRuntime.BackendFor(state) is not null;

    private void SelectManualAdsPower()
    {
        if (busy || recoveringDisplay || !state.Completed.Contains("software")) return;
        try
        {
            using var workspaceLock = WorkspaceLock.Acquire(state);
            var saved = JsonFile.Read<SessionState>(state.Options.StatePath);
            if (saved.VmId != state.VmId || saved.Options.WorkspaceId != state.Options.WorkspaceId)
                throw new SetupException("原任务记录已变化，请重新载入原任务后再切换 AdsPower 模式。");
            saved.Options.ManualAdsPower = true;
            saved.BrowserRuntimePassed = saved.BrowserReopenPassed = saved.TranslationPassed = false;
            saved.RestartPassed = saved.OutagePassed = false;
            saved.RestartBootBefore = "";
            saved.Protection.Browser = new();
            saved.Protection.Outage = new();
            saved.PendingActions.RemoveAll(ResumePlan.IsBrowserHandoff);
            var activeNetwork = saved.NetworkPassed && BoundaryRuntime.BackendFor(saved) is not null;
            if (saved.NetworkPassed)
            {
                saved.PendingActions.Add(CustomerGuide.ManualAdsPower());
                saved.Stage = activeNetwork ? CustomerGuide.ManualAdsPowerStage : "手动使用 AdsPower：待恢复原任务网络";
            }
            saved.Save();
            state = saved;
            manualAdsPower.IsChecked = true;
            RefreshPending();
            UpdateDesktopGuide();
            RecordTaskActivity(activeNetwork ? CustomerGuide.ManualAdsPowerStage : "已保存手动使用 AdsPower。原任务联网通过后，按左侧指引配置。");
        }
        catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
    }

    private async Task ResumeDesktopTask()
    {
        if (busy) return;
        if (state.SetupFinished)
        {
            await EnterOriginalWorkspace();
            return;
        }
        if (ManualAdsPowerReady)
        {
            if (consoleHost?.IsAttached != true)
            {
                desktopConnectionPolicy.ResumeExplicitly();
                autoConnectDesktop = true;
                await ConnectDesktop(requested:true);
            }
            var handoff = CustomerGuide.ManualAdsPower();
            Notice(handoff.Title + "\n\n" + string.Join("\n", handoff.Steps));
            return;
        }
        if(string.IsNullOrEmpty(state.VmId)){Go(1);Notice("请先创建或载入一个任务，再继续连接。");return;}
        if(consent.IsChecked!=true)
        {
            if (MessageBox.Show(this,
                "继续原任务将复用当前虚拟机与磁盘、按配置检查启动状态并连接本任务专用网络通道（不会覆盖其他虚拟机）。\n\n确认安装范围并立即继续吗？",
                "继续原任务并连接", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            consent.IsChecked = true;
        }
        await StartInstall();
    }
    private async Task ReadVmDiagnostic()
    {
        if(string.IsNullOrEmpty(state.VmId)){Notice("请先载入原任务。");return;}
        try {
            var result=await VmDiagnostics.Read(state,CancellationToken.None);
            Notice($"当前状态：{result.State}\nCPU：{result.CpuCount}　内存：{result.MemoryMb} MB\n来宾工具：{(result.GuestToolsReady?"已有运行记录":"尚未就绪")}\n\n"+string.Join("\n\n",result.Actions));
        }catch(Exception ex){Notice(Redaction.SafeError(ex));}
    }
    private async Task RecoverVirtualBoxDisplay()
    {
        if (recoveringDisplay) return;
        if (connectingConsole || (busy && !CanPauseForDisplayRecovery))
        {
            Notice("当前步骤正在处理，请等它结束或先保存并暂停任务。");
            return;
        }
        var taskState = state;
        if (taskState.Options.PlatformId != "virtualbox" || string.IsNullOrEmpty(taskState.VmId) ||
            !VirtualBox.HasFramebufferConflict(taskState))
        {
            Notice("未在本任务的当前 VirtualBox 日志中检测到显示缓冲冲突，请先查看黑屏与启动诊断。");
            return;
        }
        var explanation = busy
            ? "会先保存部署进度并暂停当前的来宾工具等待，再保存虚拟机的运行状态并恢复画面。恢复后可在原进度继续部署。"
            : "会短暂暂停本任务的虚拟机，保存运行状态，等 VirtualBox 确认保存后再恢复画面。";
        if (MessageBox.Show(this, explanation + "\n\n是否继续？",
            "保存状态并恢复显示", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (connectingConsole || closingDesktop || closeAfterPause || section != 7 ||
            !ReferenceEquals(state, taskState) || (busy && !CanPauseForDisplayRecovery)) return;

        recoveringDisplay = true;
        UpdateDesktopActivity();
        var recovered = false;
        var recoveryStarted = false;
        try
        {
            if (busy && installationFinished is { } stopping)
            {
                PauseCurrentTask();
                SetDesktopConnectionStatus("正在保存部署进度并结束来宾工具等待…");
                try { await stopping.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
                catch (TimeoutException)
                {
                    SetDesktopConnectionStatus("当前步骤还在收尾。等任务显示已暂停，再点“保存状态并恢复显示”。");
                    return;
                }
            }

            if (busy || closingDesktop || closeAfterPause || section != 7 || !ReferenceEquals(state, taskState)) return;
            busy = true;
            recoveryStarted = true;
            AllowEditing(false);
            desktopConnectionPolicy.Suspend();
            autoConnectDesktop = false;
            desktopConnectionCancellation?.Cancel();
            ReleaseDesktopWindow();
            lastConsoleWindow = null;
            pendingConsole = null;
            consoleHost!.Visibility = Visibility.Collapsed;
            SetDesktopConnectionStatus("正在保存原虚拟机运行状态并准备恢复画面…");
            await using var box = new VirtualBox(new CommandRunner());
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            await box.RecoverFramebufferConflict(taskState, timeout.Token);
            framebufferConflictVm = "";
            recovered = true;
        }
        catch (OperationCanceledException)
        {
            SetDesktopConnectionStatus("显示恢复等待超时。请查看虚拟机当前状态；如果显示已保存，可以继续原任务。");
        }
        catch (Exception error) { SetDesktopConnectionStatus(Redaction.SafeError(error)); }
        finally
        {
            recoveringDisplay = false;
            if (recoveryStarted)
            {
                busy = false;
                AllowEditing(true);
                CompleteCloseAfterPause();
            }
            UpdateDesktopActivity();
        }

        if (recovered && !closeAfterPause)
        {
            desktopConnectionPolicy.ResumeExplicitly();
            autoConnectDesktop = true;
            await ConnectDesktop(requested: true);
        }
    }

    private async Task ForceRepairVirtualBoxDialog()
    {
        if (state.Options.PlatformId != "virtualbox" || string.IsNullOrEmpty(state.VmId))
        {
            Notice("此一键防卡死修复入口当前用于已创建的 VirtualBox 任务虚拟机。");
            return;
        }
        if (MessageBox.Show(this,
            "仅在虚拟机画面定格、白屏/黑屏或单核满载且无法在来宾内正常关机时使用。\n\n将强制关闭本任务虚拟机（虚拟机内未保存的临时内容会丢失，但虚拟磁盘与部署进度完整保留），并自动启用 1 核防死锁兼容配置。\n\n确定立即执行吗？",
            "强制关闭无响应虚拟机并修复", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if (busy)
            {
                PauseCurrentTask();
                for (var i = 0; busy && i < 30; i++) await Task.Delay(250);
                if (busy) { Notice("当前步骤正在收尾，请稍等几秒后再次点击。"); return; }
            }
            desktopConnectionPolicy.Suspend();
            autoConnectDesktop = false;
            desktopConnectionCancellation?.Cancel();
            ReleaseDesktopWindow();
            ConsoleWindowLocator.Hide(lastConsoleWindow, state.Options.VmName, state.VmId);
            lastConsoleWindow = null;
            await BoundaryRuntime.Replace(null, "");

            busy = true;
            AllowEditing(false);
            SetDesktopConnectionStatus("正在强制关闭无响应虚拟机并写入单核防死锁配置…");
            using var repairLock = WorkspaceLock.Acquire(state);
            var box = new VirtualBox(new CommandRunner());
            await box.ForceStopAndRepair(state, CancellationToken.None, new Progress<string>(msg =>
            {
                log.AppendText(msg + Environment.NewLine);
                banner.Text = msg;
                RecordTaskActivity(msg);
            }));
            updatingCpuRecommendation = true;
            try
            {
                cpu.Text = state.Options.CpuCount.ToString();
                memory.Text = state.Options.MemoryMb.ToString();
                useCustomVmConfig.IsChecked = state.Options.ManualCpuConfirmed;
            }
            finally { updatingCpuRecommendation = false; }
            RefreshCreationFields();
            UpdateDesktopGuide();
            SetDesktopConnectionStatus("已完成强制关机与单核防卡死修复。点击“继续原任务并连接”重新启动。");
            Notice("已安全关闭卡死的虚拟机，并启用 1 核防卡死兼容配置（原磁盘与进度保留）。\n\n现在可直接点击左侧“继续原任务并连接”启动。");
        }
        catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
        finally
        {
            busy = false;
            AllowEditing(true);
            UpdateDesktopActivity();
            CompleteCloseAfterPause();
        }
    }
    internal static bool ShouldOfferMultiCoreBlankRecovery(string platformId, int cpuCount, bool waitingForFrame, TimeSpan elapsed) =>
        platformId == "virtualbox" && cpuCount >= 2 && waitingForFrame && elapsed >= TimeSpan.FromSeconds(25);

    private void SetWaitingForGuestFrame(bool waiting)
    {
        waitingForGuestFrame = waiting;
        if (waiting)
        {
            waitingForGuestFrameSinceUtc ??= DateTime.UtcNow;
            return;
        }
        waitingForGuestFrameSinceUtc = null;
        notifiedMultiCoreBlankTimeout = false;
    }

    private void ResourceDialog()
    {
        if(busy){Notice("请先暂停并等待当前步骤结束，或使用“强制关闭无响应虚拟机并修复”。");return;}
        if(state.Options.PlatformId!="virtualbox"||string.IsNullOrEmpty(state.VmId)){Notice("此关机后调整入口当前用于本任务的 VirtualBox 虚拟机。");return;}
        var dialog = BuildResourceDialog(out _, out _, out _);
        dialog.ShowDialog();
    }

    internal Window BuildResourceDialog(out TextBox cpuInput, out TextBox memoryInput, out TextBlock nemWarningText,
        bool? hostNemOverride = null, string? hostEditionOverride = null)
    {
        var cpuField = Input(state.Options.CpuCount.ToString());
        var memField = Input(state.Options.MemoryMb.ToString());
        cpuInput = cpuField;
        memoryInput = memField;
        var keepCustom = Check("按我选择的自定义资源运行（后续启动不自动覆盖为单核防卡死配置）");
        keepCustom.IsChecked = true;
        var warningText = Text("", 12, false);
        warningText.Margin = new Thickness(0);
        nemWarningText = warningText;
        var warningBox = new Border
        {
            Name = "ResourceNemWarningBanner",
            Background = Pale, BorderBrush = Accent, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 6, 0, 8), Visibility = Visibility.Collapsed,
            Child = warningText
        };
        void RefreshNemWarning()
        {
            var warning = int.TryParse(cpuField.Text.Trim(), out var parsed)
                ? SetupOptions.MultiCoreNemWarning(state.Options.PlatformId, parsed, hostNemOverride, hostEditionOverride)
                : null;
            warningText.Text = warning ?? "";
            warningBox.Visibility = string.IsNullOrEmpty(warning) ? Visibility.Collapsed : Visibility.Visible;
        }
        cpuField.TextChanged += (_, _) => RefreshNemWarning();
        RefreshNemWarning();

        var panel = new StackPanel { Margin = new Thickness(24, 18, 24, 14) };
        panel.Children.Add(Text("先在来宾中正常关机，再应用资源修改（Hyper-V 兼容模式下推荐 1 核避免多核卡死；若勾选下方自定义选项，程序将严格保留你的配置）。若虚拟机已卡死无法关机，可点下方强制关闭并修复。", 12));
        panel.Children.Add(Field("虚拟 CPU 数量", cpuField));
        panel.Children.Add(Field("内存（MB）", memField));
        panel.Children.Add(keepCustom);
        panel.Children.Add(warningBox);

        var layout = new DockPanel();
        var dialog = new Window
        {
            Title = "调整原虚拟机资源", Owner = this,
            Width = 500, Height = 520, MinWidth = 440, MinHeight = 360,
            MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 40),
            ResizeMode = ResizeMode.CanResizeWithGrip,
            Content = layout, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Surface, Foreground = Ink, FontFamily = FontFamily
        };
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } };
        var footer = Row(
            Action("关机后应用并回读", async (_, _) =>
            {
                if (!int.TryParse(cpuField.Text, out var count) || !int.TryParse(memField.Text, out var amount))
                { Notice("请填写整数。"); return; }
                var warning = SetupOptions.MultiCoreNemWarning(state.Options.PlatformId, count, hostNemOverride, hostEditionOverride);
                if (!string.IsNullOrEmpty(warning) && MessageBox.Show(dialog,
                    warning + $"\n\n仍确定按 {count} 核写入并应用吗？",
                    "多核兼容性风险提醒", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                try
                {
                    var box = new VirtualBox(new CommandRunner());
                    await box.ChangePoweredOffResources(state, count, amount, CancellationToken.None, keepCustom.IsChecked == true);
                    updatingCpuRecommendation = true;
                    try { cpu.Text = count.ToString(); memory.Text = amount.ToString(); useCustomVmConfig.IsChecked = state.Options.ManualCpuConfirmed; }
                    finally { updatingCpuRecommendation = false; }
                    RefreshCreationFields();
                    dialog.Close();
                    Notice("已回读确认新的 CPU 和内存，原磁盘保留。回到原任务继续启动和验收。");
                }
                catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
            }, true),
            Action("卡死无法关机？强制修复", async (_, _) => { dialog.Close(); await ForceRepairVirtualBoxDialog(); }));
        footer.Margin = new Thickness(24, 6, 24, 14);
        var footerBorder = new Border
        {
            Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footer
        };
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        layout.Children.Add(footerBorder);
        layout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return dialog;
    }
    private async Task SyncGuestCredentialsDialog()
    {
        if (string.IsNullOrEmpty(state.VmId)) { Notice("请先载入原任务。"); return; }
        if (busy)
        {
            if (!CanPauseForDisplayRecovery || installationFinished is not { } stopping)
            {
                SetDesktopConnectionStatus("当前操作正在运行。结束后可同步虚拟机账号。");
                return;
            }
            PauseCurrentTask();
            SetDesktopConnectionStatus("正在结束来宾工具等待，稍后将打开账号同步窗口…");
            try { await stopping.Task.WaitAsync(TimeSpan.FromSeconds(40)); }
            catch (TimeoutException) { Notice("当前步骤还在收尾。等任务已暂停后，再点同步账号。"); return; }
        }
        if (busy || closingDesktop || closeAfterPause || section != 7) return;

        var original = state;
        var virtualBox = original.Options.PlatformId == "virtualbox";
        var userInput = Input(original.Options.UserName);
        var useMicrosoft = Check("使用微软账号登录 Windows");
        useMicrosoft.Foreground = Ink;
        useMicrosoft.IsChecked = original.Options.UsesMicrosoftGuestAccount;
        var microsoftAddress = Input(guestMicrosoftAddress);
        var microsoftField = Field("微软账号邮箱", microsoftAddress);
        microsoftField.Visibility = useMicrosoft.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        useMicrosoft.Checked += (_, _) => microsoftField.Visibility = Visibility.Visible;
        useMicrosoft.Unchecked += (_, _) => microsoftField.Visibility = Visibility.Collapsed;
        var guestPassword = new PasswordBox { Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 8, 10, 8),
            Background = Canvas, Foreground = Ink, BorderBrush = Line };
        var status = Text("", 12);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 14) };
        panel.Children.Add(Text("使用来宾 Windows 的实际账号", 18, false));
        panel.Children.Add(Text(
            "先在右侧来宾登录 Windows。打开来宾的终端，输入 whoami，填写反斜杠后的用户名。登录画面上的显示名称可能不同。\n\n" +
            "用微软账号登录时，勾选下方选项并填绑定邮箱及微软账号密码。同步验证要求密码。", 12));
        panel.Children.Add(Field("实际 Windows 用户名", userInput));
        if (virtualBox)
        {
            panel.Children.Add(useMicrosoft);
            panel.Children.Add(microsoftField);
        }
        panel.Children.Add(Field("Windows 账号密码", guestPassword,
            original.Options.Remember ? "本任务已开启记住资料，验证后由当前 Windows 用户加密保存。" : ""));
        panel.Children.Add(status);

        var layout = new DockPanel();
        var dialog = new Window
        {
            Title = "同步虚拟机账号密码", Owner = this,
            Width = 560, Height = 460, MinHeight = 340, MinWidth = 460,
            MaxHeight = Math.Max(340, SystemParameters.WorkArea.Height - 40),
            Content = layout, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Surface, Foreground = Ink, FontFamily = FontFamily, ShowInTaskbar = false
        };
        var verifying = false;
        var resumeNow = false;
        dialog.Closing += (_, args) => args.Cancel = verifying;
        var footer = Row();
        footer.Children.Add(Action(virtualBox ? "验证并继续部署" : "保存并继续部署", async (_, _) => await Apply(true), true));
        footer.Children.Add(Action(virtualBox ? "仅验证并保存" : "仅保存", async (_, _) => await Apply(false)));
        var back = Action("返回虚拟机", (_, _) => dialog.Close());
        back.IsCancel = true;
        footer.Children.Add(back);
        footer.Margin = new Thickness(24, 6, 24, 16);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Loaded += (_, _) => guestPassword.Focus();

        async Task Apply(bool continueTask)
        {
            if (verifying) return;
            var account = userInput.Text.Trim();
            var microsoft = virtualBox && useMicrosoft.IsChecked == true;
            var loginAddress = microsoft ? microsoftAddress.Text.Trim() : "";
            var secret = guestPassword.Password;
            if (!Regex.IsMatch(account, @"^[\p{L}\p{N}](?:[\p{L}\p{N}_.-]| [\p{L}\p{N}_.-]){0,19}$") ||
                account.Contains("..", StringComparison.Ordinal) ||
                new[] { "administrator", "guest", "defaultaccount", "system", "wdagutilityaccount" }.Contains(account.ToLowerInvariant()))
            {
                status.Text = "请填写 whoami 中反斜杠后的有效用户名（最多 20 位）。";
                return;
            }
            if (microsoft && !SetupOptions.IsMicrosoftGuestAddress(loginAddress))
            {
                status.Text = "请输入关联到来宾 Windows 的微软账号邮箱。";
                return;
            }
            if (secret.Length < (microsoft ? 1 : 10) || secret.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                status.Text = microsoft ? "请输入来宾 Windows 正在使用的微软账号密码。" : "请输入至少 10 位的 Windows 账号密码。";
                return;
            }

            var saved = false;
            verifying = true;
            footer.IsEnabled = userInput.IsEnabled = guestPassword.IsEnabled = useMicrosoft.IsEnabled = microsoftAddress.IsEnabled = false;
            status.Text = virtualBox ? "正在验证本任务的 Windows 账号…" : "正在保存账号…";
            try
            {
                using var taskLock = WorkspaceLock.Acquire(original);
                if (File.Exists(original.Options.StatePath) &&
                    JsonFile.Read<SessionState>(original.Options.StatePath).UpdatedUtc > original.UpdatedUtc)
                    throw new SetupException("另一窗口保存了更新的任务记录，请重新载入原任务后继续。");
                var localAppData = "";
                if (virtualBox)
                {
                    await using var box = new VirtualBox(new CommandRunner());
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    localAppData = await box.VerifyGuestLogin(original, account, secret, timeout.Token, loginAddress);
                }
                CommitGuestCredentials(original, account, secret, localAppData, loginAddress, virtualBox);
                resumeNow = continueTask;
                saved = true;
            }
            catch (OperationCanceledException) { status.Text = "验证超时。请确认来宾 Windows 正在运行后重试。"; }
            catch (IOException) { status.Text = "任务目录被占用或保存失败。请核对权限，并确认只有一个助手在操作。"; }
            catch (Exception error) { status.Text = Redaction.SafeError(error); }
            finally
            {
                verifying = false;
                footer.IsEnabled = userInput.IsEnabled = guestPassword.IsEnabled = useMicrosoft.IsEnabled = microsoftAddress.IsEnabled = true;
            }
            if (saved) dialog.DialogResult = true;
        }

        var host = consoleHost;
        var nativeVisibility = host?.Visibility;
        host?.ReleaseGuestInputCapture();
        if (host?.IsAttached == true) host.Visibility = Visibility.Hidden;
        var accepted = false;
        try { accepted = dialog.ShowDialog() == true; }
        finally
        {
            guestPassword.Clear();
            microsoftAddress.Clear();
            if (host is not null && nativeVisibility.HasValue) host.Visibility = nativeVisibility.Value;
        }

        if (accepted)
        {
            SetDesktopConnectionStatus(virtualBox ? "来宾账号验证并保存成功，可以继续部署。" : "已保存来宾账号，可以继续部署。");
            if (resumeNow) await ResumeDesktopTask();
        }
    }

    private void CommitGuestCredentials(SessionState original, string account, string secret, string localAppData, string loginAddress, bool verified)
    {
        var updated = JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(original, JsonFile.Format), JsonFile.Format)
            ?? throw new SetupException("无法读取当前任务记录。");
        var oldSetupDirectory = updated.Options.GuestSetupDirectory;
        var changedAccount = !account.Equals(updated.Options.UserName, StringComparison.OrdinalIgnoreCase);
        updated.Options.UserName = account;
        updated.Options.GuestLocalAppData = localAppData;
        updated.Options.UsesMicrosoftGuestAccount = !string.IsNullOrEmpty(loginAddress);
        if (changedAccount || !oldSetupDirectory.Equals(updated.Options.GuestSetupDirectory, StringComparison.OrdinalIgnoreCase))
        {
            updated.Completed.RemoveAll(step => step is "guest-ready" or "guest-files" or "software");
            updated.GuestFilesStamp = updated.GuestSettingsStamp = updated.RestartBootBefore = "";
            updated.SetupFinished = false;
            updated.Protection = new();
            updated.ClashConfigured = updated.BrowserProfileConfigured = updated.NetworkPassed = updated.OutagePassed =
                updated.BrowserRuntimePassed = updated.BrowserReopenPassed = updated.TranslationPassed = updated.RestartPassed =
                updated.ApplicationAccepted = false;
        }
        updated.PendingActions.RemoveAll(action => action.Id == "guest-auth-sync");
        updated.Status = verified ? "来宾账号验证通过，可以继续部署" : "来宾账号已保存，可以继续部署";

        var rememberCredentials = updated.Options.Remember;
        Secrets? previous = null;
        var incoming = AppliedLogin();
        incoming.VmPassword = secret;
        incoming.VmMicrosoftAddress = loginAddress;
        if (rememberCredentials)
        {
            previous = SecretStore.Load(updated);
            SecretStore.SaveMerged(updated.Options.Private, incoming, updated.Options.NetworkSource == "稍后配置", !secretsLoaded, updated.CredentialsRevision);
        }
        try { updated.Save(); }
        catch
        {
            if (rememberCredentials)
            {
                if (previous is null) SecretStore.Forget(updated);
                else SecretStore.Save(updated.Options.Private, previous, updated.CredentialsRevision);
            }
            throw;
        }

        state = updated;
        RememberAppliedSecrets(incoming);
        guestMicrosoftAddress = loginAddress;
        user.Text = account;
        guestMicrosoftPassword = updated.Options.UsesMicrosoftGuestAccount ? secret : "";
        if (updated.Options.UsesMicrosoftGuestAccount)
        {
            showPassword.IsChecked = false;
            password.Clear();
            passwordVisible.Clear();
        }
        else
        {
            password.Password = secret;
            if (showPassword.IsChecked == true) passwordVisible.Text = secret;
        }
        RefreshCreationFields();
        RefreshPending();
        UpdateDesktopGuide();
    }
}
