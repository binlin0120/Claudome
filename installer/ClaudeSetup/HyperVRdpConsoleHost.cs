using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace ClaudeSetup;

internal enum HyperVDesktopMode
{
    None,
    EmbeddedRdp,
    StandaloneRdp,
    FallbackVmConnect
}

internal readonly record struct HyperVRdpConnectionProfile(
    string Server,
    int Port,
    string VmId,
    string Pcb,
    int AuthenticationLevel,
    bool NegotiateSecurityLayer,
    bool EnableCredSspSupport,
    string AuthenticationServiceClass,
    bool DisableCredentialsDelegation,
    bool SmartSizing,
    bool RedirectDrives,
    bool RedirectPrinters,
    bool RedirectPorts,
    bool RedirectSmartCards,
    bool RedirectPosDevices,
    bool RedirectDevices,
    bool RedirectDynamicDevices,
    bool RedirectDynamicDrives,
    int AudioRedirectionMode,
    bool AudioCaptureRedirectionMode,
    bool RedirectClipboard)
{
    internal static HyperVRdpConnectionProfile CreateVerifiedBasic(string vmId)
    {
        if (!Guid.TryParseExact(vmId, "D", out var parsed))
            throw new SetupException("当前任务缺少已核验的虚拟机标识，无法建立 B 路线控制台连接。");
        var normalized = parsed.ToString("D");
        return new(
            Server: "localhost",
            Port: 2179,
            VmId: normalized,
            Pcb: normalized, // Basic VMRDP strictly omits ";EnhancedMode=1" so host Enhanced Session stays disabled.
            AuthenticationLevel: 0,
            NegotiateSecurityLayer: false,
            EnableCredSspSupport: true,
            AuthenticationServiceClass: "Microsoft Virtual Machine Console Service",
            DisableCredentialsDelegation: true,
            SmartSizing: true,
            RedirectDrives: false,
            RedirectPrinters: false,
            RedirectPorts: false,
            RedirectSmartCards: false,
            RedirectPosDevices: false,
            RedirectDevices: false,
            RedirectDynamicDevices: false,
            RedirectDynamicDrives: false,
            AudioRedirectionMode: 2,
            AudioCaptureRedirectionMode: false,
            RedirectClipboard: false);
    }
}

internal readonly record struct HyperVRdpFrameLayout(
    Rect Bounds,
    bool IsSource16By9,
    int SourceWidth,
    int SourceHeight,
    string StatusText);

internal static class HyperVRdpDisplayGeometry
{
    private const double Target16By9 = 16.0 / 9.0;
    private const double AspectTolerance = 0.03;

    internal static bool Is16By9(int width, int height) =>
        width > 0 && height > 0 && Math.Abs((width / (double)height) - Target16By9) <= AspectTolerance;

    internal static Rect ComputeCenteredAspectFrame(double availableWidth, double availableHeight, double aspectRatio)
    {
        if (availableWidth <= 0 || availableHeight <= 0 || aspectRatio <= 0 ||
            double.IsNaN(availableWidth) || double.IsNaN(availableHeight) ||
            double.IsInfinity(availableWidth) || double.IsInfinity(availableHeight))
            return new Rect(0, 0, 0, 0);

        var widthFromHeight = availableHeight * aspectRatio;
        double width;
        double height;
        if (widthFromHeight <= availableWidth)
        {
            width = Math.Floor(widthFromHeight);
            height = Math.Floor(availableHeight);
        }
        else
        {
            width = Math.Floor(availableWidth);
            height = Math.Floor(availableWidth / aspectRatio);
        }
        width = Math.Max(1, Math.Min(width, availableWidth));
        height = Math.Max(1, Math.Min(height, availableHeight));
        var x = Math.Max(0, Math.Floor((availableWidth - width) / 2.0));
        var y = Math.Max(0, Math.Floor((availableHeight - height) / 2.0));
        return new Rect(x, y, width, height);
    }

    internal static HyperVRdpFrameLayout ComputeForSource(double availableWidth, double availableHeight, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            var fallback = ComputeCenteredAspectFrame(availableWidth, availableHeight, Target16By9);
            return new(fallback, true, 0, 0, "已连接基本会话（等待来宾报告源分辨率，已启用连续缩放）。");
        }
        var is16By9 = Is16By9(sourceWidth, sourceHeight);
        var ratio = is16By9 ? Target16By9 : sourceWidth / (double)sourceHeight;
        var bounds = ComputeCenteredAspectFrame(availableWidth, availableHeight, ratio);
        var status = is16By9
            ? $"已连续铺满 16:9 桌面（源画面 {sourceWidth}×{sourceHeight} · 设备隔离已锁定）。"
            : $"已按原比例完整显示来宾桌面（源画面 {sourceWidth}×{sourceHeight}，非 16:9 无裁切 · 设备隔离已锁定）。";
        return new(bounds, is16By9, sourceWidth, sourceHeight, status);
    }

    internal static bool ShouldUseRouteB(string platformId, bool guestReady, bool explicitFallbackToA) =>
        platformId == "hyperv" && guestReady && !explicitFallbackToA;
}

[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpExtendedSettings
{
    void set_Property([MarshalAs(UnmanagedType.BStr)] string propertyName, [In] ref object value);
    void get_Property([MarshalAs(UnmanagedType.BStr)] string propertyName, out object value);
}

[ComImport, Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class HyperVRdpEventSink(Action<string, int, int> callback) : IMsTscAxEvents
{
    public void OnConnecting() => callback("OnConnecting", 0, 0);
    public void OnConnected() => callback("OnConnected", 0, 0);
    public void OnLoginComplete() => callback("OnLoginComplete", 0, 0);
    public void OnDisconnected(int discReason) => callback("OnDisconnected", discReason, 0);
    public void OnFatalError(int errorCode) => callback("OnFatalError", errorCode, 0);
    public void OnWarning(int warningCode) => callback("OnWarning", warningCode, 0);
    public void OnRemoteDesktopSizeChange(int width, int height) => callback("OnRemoteDesktopSizeChange", width, height);
}

internal sealed class HyperVRdpAxControl : WinForms.AxHost
{
    // Microsoft RDP Client Control - version 9 (public Basic VMRDP + SmartSizing + IMsRdpExtendedSettings)
    private const string Rdp9Clsid = "a3bc03a0-041d-42e3-ad22-882b7865c9c5";
    private object? ocx;
    private IConnectionPoint? eventPoint;
    private int eventCookie;
    private HyperVRdpEventSink? sink;

    internal Action<string, int, int>? EventReceived;

    public HyperVRdpAxControl() : base(Rdp9Clsid)
    {
        Dock = WinForms.DockStyle.Fill;
    }

    internal object? Ocx => ocx;

    protected override void AttachInterfaces()
    {
        ocx = GetOcx();
        if (ocx is IConnectionPointContainer cpc)
        {
            var iid = typeof(IMsTscAxEvents).GUID;
            try
            {
                cpc.FindConnectionPoint(ref iid, out eventPoint);
                if (eventPoint is not null)
                {
                    sink = new HyperVRdpEventSink((ev, a, b) => EventReceived?.Invoke(ev, a, b));
                    eventPoint.Advise(sink, out eventCookie);
                }
            }
            catch { }
        }
    }

    protected override void DetachSink()
    {
        if (eventPoint is not null && eventCookie != 0)
        {
            try { eventPoint.Unadvise(eventCookie); } catch { }
            eventCookie = 0;
            eventPoint = null;
        }
        base.DetachSink();
    }
}

internal sealed class HyperVRdpConsoleHost : IDisposable
{
    private readonly WindowsFormsHost host = new();
    private HyperVRdpAxControl? control;
    private TaskCompletionSource<bool>? pendingConnect;
    private TaskCompletionSource<bool>? pendingDisconnect;
    private long sessionGeneration;
    private bool intentionalDisconnect;
    private bool disposed;

    public FrameworkElement View => host;
    public bool IsConnected { get; private set; }
    public string ConnectedVmId { get; private set; } = "";
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }
    internal long ActiveGeneration => Volatile.Read(ref sessionGeneration);

    public event Action<int, int>? SourceSizeChanged;
    public event Action<string>? Connected;
    public event Action<bool, int, string>? Disconnected;

    internal static bool ShouldProcessGenerationEvent(long eventGeneration, long currentGeneration) =>
        eventGeneration > 0 && eventGeneration == currentGeneration;

    private void EnsureControl()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (control is not null) return;
        control = new HyperVRdpAxControl { EventReceived = HandleComEvent };
        host.Child = control;
        control.CreateControl();
    }

    private void HandleComEvent(string name, int arg1, int arg2)
    {
        var eventGeneration = Volatile.Read(ref sessionGeneration);
        host.Dispatcher.BeginInvoke(() =>
        {
            if (disposed || !ShouldProcessGenerationEvent(eventGeneration, Volatile.Read(ref sessionGeneration)))
                return;
            switch (name)
            {
                case "OnRemoteDesktopSizeChange":
                    if (arg1 > 0 && arg2 > 0)
                    {
                        SourceWidth = arg1;
                        SourceHeight = arg2;
                        SourceSizeChanged?.Invoke(arg1, arg2);
                    }
                    break;
                case "OnConnected":
                case "OnLoginComplete":
                    if (!IsConnected)
                    {
                        IsConnected = true;
                        pendingConnect?.TrySetResult(true);
                        Connected?.Invoke(ConnectedVmId);
                    }
                    break;
                case "OnDisconnected":
                    var expected = intentionalDisconnect;
                    intentionalDisconnect = false;
                    IsConnected = false;
                    var desc = "";
                    try
                    {
                        if (control?.Ocx is not null)
                        {
                            dynamic ocx = control.Ocx;
                            int extReason = (int)ocx.ExtendedDisconnectReason;
                            desc = (string)ocx.GetErrorDescription((uint)arg1, (uint)extReason);
                        }
                    }
                    catch { }
                    pendingConnect?.TrySetResult(false);
                    pendingDisconnect?.TrySetResult(true);
                    Disconnected?.Invoke(expected, arg1, desc);
                    break;
                case "OnFatalError":
                    IsConnected = false;
                    pendingConnect?.TrySetResult(false);
                    pendingDisconnect?.TrySetResult(true);
                    Disconnected?.Invoke(false, arg1, $"RDP 控件致命错误 ({arg1})");
                    break;
            }
        });
    }

    public async Task<bool> ConnectAsync(string vmId, CancellationToken token)
    {
        var profile = HyperVRdpConnectionProfile.CreateVerifiedBasic(vmId);
        if (!await DisconnectAndWaitAsync(TimeSpan.FromSeconds(3)))
            throw new SetupException("未能确认上一个 B 路线桌面会话断开，已阻止重复连接以避免会话冲突。");
        token.ThrowIfCancellationRequested();

        EnsureControl();
        if (control?.Ocx is null)
            throw new SetupException("无法初始化本机 RDP 桌面控件，请回退原生控制台。");

        Interlocked.Increment(ref sessionGeneration);
        SourceWidth = 0;
        SourceHeight = 0;
        ConnectedVmId = profile.VmId;
        intentionalDisconnect = false;
        pendingConnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        ApplyProfile(control.Ocx, profile);
        dynamic ocx = control.Ocx;
        ocx.Connect();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var ok = await pendingConnect.Task.WaitAsync(timeout.Token);
            if (!ok) await DisconnectAndWaitAsync(TimeSpan.FromSeconds(2));
            return ok;
        }
        catch (OperationCanceledException)
        {
            await DisconnectAndWaitAsync(TimeSpan.FromSeconds(2));
            if (token.IsCancellationRequested) throw;
            return false;
        }
        finally
        {
            pendingConnect = null;
        }
    }

    private bool IsOcxStillConnected()
    {
        if (control?.Ocx is null) return false;
        try
        {
            dynamic ocx = control.Ocx;
            return (short)ocx.Connected != 0;
        }
        catch
        {
            return IsConnected;
        }
    }

    public async Task<bool> DisconnectAndWaitAsync(TimeSpan timeout)
    {
        if (disposed || control?.Ocx is null)
        {
            IsConnected = false;
            ConnectedVmId = "";
            return true;
        }
        var shouldDisconnect = IsConnected || IsOcxStillConnected();
        if (!shouldDisconnect)
        {
            IsConnected = false;
            ConnectedVmId = "";
            return true;
        }

        intentionalDisconnect = true;
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingDisconnect = waiter;
        var confirmed = false;
        try
        {
            dynamic ocx = control.Ocx;
            ocx.Disconnect();
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await waiter.Task.WaitAsync(cts.Token);
                confirmed = true;
            }
            catch (OperationCanceledException)
            {
                confirmed = !IsOcxStillConnected();
            }
        }
        catch
        {
            confirmed = !IsOcxStillConnected();
        }
        finally
        {
            pendingDisconnect = null;
            intentionalDisconnect = false;
            if (confirmed)
            {
                Interlocked.Increment(ref sessionGeneration);
                IsConnected = false;
                ConnectedVmId = "";
            }
        }
        return confirmed;
    }

    internal static void ApplyProfile(object rawOcx, HyperVRdpConnectionProfile p)
    {
        try
        {
            dynamic ocx = rawOcx;
            ocx.Server = p.Server;
            ocx.ColorDepth = 32;
            ocx.DesktopWidth = 1920;
            ocx.DesktopHeight = 1080;

            dynamic adv2 = ocx.AdvancedSettings2 ?? throw new SetupException("缺少 RDP AdvancedSettings2 接口。");
            adv2.RDPPort = p.Port;
            adv2.SmartSizing = p.SmartSizing;
            adv2.RedirectDrives = p.RedirectDrives;
            adv2.RedirectPrinters = p.RedirectPrinters;
            adv2.RedirectPorts = p.RedirectPorts;
            adv2.RedirectSmartCards = p.RedirectSmartCards;
            adv2.ConnectToServerConsole = false;

            dynamic adv5 = ocx.AdvancedSettings5 ?? throw new SetupException("缺少 RDP AdvancedSettings5 接口。");
            adv5.AuthenticationLevel = p.AuthenticationLevel;
            adv5.RedirectClipboard = p.RedirectClipboard;

            dynamic adv7 = ocx.AdvancedSettings7 ?? throw new SetupException("缺少 RDP AdvancedSettings7 接口。");
            adv7.PCB = p.Pcb;
            adv7.EnableCredSspSupport = p.EnableCredSspSupport;
            adv7.AudioRedirectionMode = p.AudioRedirectionMode;
            adv7.AudioCaptureRedirectionMode = p.AudioCaptureRedirectionMode;

            dynamic adv8 = ocx.AdvancedSettings8 ?? throw new SetupException("缺少 RDP AdvancedSettings8 接口。");
            adv8.NegotiateSecurityLayer = p.NegotiateSecurityLayer;

            dynamic adv9 = ocx.AdvancedSettings9 ?? throw new SetupException("缺少 RDP AdvancedSettings9 接口。");
            adv9.RedirectDynamicDevices = p.RedirectDynamicDevices;
            adv9.RedirectDynamicDrives = p.RedirectDynamicDrives;
            adv9.AuthenticationServiceClass = p.AuthenticationServiceClass;

            if (rawOcx is not IMsRdpExtendedSettings ext)
                throw new SetupException("RDP 控件不支持 IMsRdpExtendedSettings，无法锁定凭据与设备隔离策略。");

            SetMandatoryExt(ext, "AuthenticationServiceClass", p.AuthenticationServiceClass);
            SetMandatoryExt(ext, "DisableCredentialsDelegation", p.DisableCredentialsDelegation);
            SetMandatoryExt(ext, "NegotiateSecurityLayer", p.NegotiateSecurityLayer);
            SetMandatoryExt(ext, "EnableCredSspSupport", p.EnableCredSspSupport);
            SetMandatoryExt(ext, "PCB", p.Pcb);
            SetMandatoryExt(ext, "RedirectDevices", p.RedirectDevices);
            SetMandatoryExt(ext, "RedirectDrives", p.RedirectDrives);
            SetMandatoryExt(ext, "RedirectPrinters", p.RedirectPrinters);
            SetMandatoryExt(ext, "RedirectPorts", p.RedirectPorts);
            SetMandatoryExt(ext, "RedirectSmartCards", p.RedirectSmartCards);
            SetMandatoryExt(ext, "RedirectClipboard", p.RedirectClipboard);
            SetMandatoryExt(ext, "AudioPlaybackMode", p.AudioRedirectionMode);
            SetMandatoryExt(ext, "AudioCaptureMode", 0);

            // Optional hardening properties that vary by Windows build
            try { ocx.AdvancedSettings3.RedirectPOSDevices = p.RedirectPosDevices; } catch { }
            try { adv7.PublicMode = false; } catch { }
        }
        catch (SetupException) { throw; }
        catch (Exception ex)
        {
            throw new SetupException($"B 路线桌面隔离或目标绑定参数写入失败，已拒绝建立未锁定会话：{Redaction.SafeError(ex)}");
        }
    }

    private static void SetMandatoryExt(IMsRdpExtendedSettings ext, string name, object value)
    {
        try
        {
            var v = value;
            ext.set_Property(name, ref v);
        }
        catch (Exception ex)
        {
            throw new SetupException($"无法写入 B 路线强制安全参数 {name}：{Redaction.SafeError(ex)}");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (control?.Ocx is not null)
            {
                intentionalDisconnect = true;
                dynamic ocx = control.Ocx;
                if ((short)ocx.Connected != 0) ocx.Disconnect();
            }
        }
        catch { }
        try { host.Child = null; } catch { }
        try { control?.Dispose(); } catch { }
        try { host.Dispose(); } catch { }
        control = null;
        IsConnected = false;
        ConnectedVmId = "";
    }
}

internal sealed class HyperVStandaloneRdpWindow : Window
{
    private readonly Grid root = new();
    private readonly Border topBar;
    private readonly TextBlock statusText;
    private readonly Button fullscreenBtn, returnBtn;
    private readonly Border frameBorder;
    private readonly HyperVRdpConsoleHost rdpHost = new();
    private readonly Func<Task> onReturnToEmbedded;
    private readonly string vmId;
    private bool isFullscreen;
    private WindowStyle savedStyle = WindowStyle.SingleBorderWindow;
    private WindowState savedState = WindowState.Normal;
    private ResizeMode savedResize = ResizeMode.CanResize;
    private bool returning;

    public HyperVStandaloneRdpWindow(string vmName, string vmId, Func<Task> onReturnToEmbedded)
    {
        this.vmId = vmId;
        this.onReturnToEmbedded = onReturnToEmbedded;
        Title = $"Claudome 独立桌面 · {vmName}";
        Width = 1360;
        Height = 820;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(20, 18, 16));
        FontFamily = new FontFamily("Microsoft YaHei UI");

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        statusText = new TextBlock
        {
            Text = "正在连接独立桌面…",
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        fullscreenBtn = MakeButton("⛶ 全屏", (_, _) => SetFullscreen(!isFullscreen));
        returnBtn = MakeButton("返回内嵌桌面", async (_, _) => await ReturnToEmbeddedAsync());
        var rightBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rightBar.Children.Add(fullscreenBtn);
        rightBar.Children.Add(returnBtn);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(rightBar, Dock.Right);
        dock.Children.Add(rightBar);
        dock.Children.Add(statusText);

        topBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(32, 29, 27)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(64, 58, 54)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 5, 12, 5),
            Child = dock
        };
        topBar.SizeChanged += (_, _) => UpdateFrame();
        root.Children.Add(topBar);

        frameBorder = new Border
        {
            Background = Brushes.Black,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = rdpHost.View
        };
        Grid.SetRow(frameBorder, 1);
        root.Children.Add(frameBorder);
        Content = root;

        rdpHost.SourceSizeChanged += (w, h) =>
        {
            var layout = HyperVRdpDisplayGeometry.ComputeForSource(root.ActualWidth, Math.Max(0, root.ActualHeight - topBar.ActualHeight), w, h);
            statusText.Text = layout.StatusText;
            UpdateFrame();
        };
        rdpHost.Connected += _ =>
        {
            var layout = HyperVRdpDisplayGeometry.ComputeForSource(root.ActualWidth, Math.Max(0, root.ActualHeight - topBar.ActualHeight), rdpHost.SourceWidth, rdpHost.SourceHeight);
            statusText.Text = layout.StatusText;
            UpdateFrame();
        };
        rdpHost.Disconnected += (expected, code, desc) =>
        {
            if (!expected)
                statusText.Text = string.IsNullOrWhiteSpace(desc) ? $"独立桌面连接已断开（{code}）。" : $"独立桌面连接已断开：{desc}";
        };

        SizeChanged += (_, _) => UpdateFrame();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && isFullscreen)
            {
                SetFullscreen(false);
                e.Handled = true;
            }
        };
        Loaded += async (_, _) =>
        {
            UpdateFrame();
            try
            {
                var ok = await rdpHost.ConnectAsync(this.vmId, CancellationToken.None);
                if (!ok) statusText.Text = "未能连接到独立桌面，可点击「返回内嵌桌面」或回退原生控制台。";
            }
            catch (Exception ex) { statusText.Text = Redaction.SafeError(ex); }
        };
        Closing += (_, _) => rdpHost.Dispose();
    }

    private static Button MakeButton(string text, RoutedEventHandler onClick)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 11.5,
            Background = new SolidColorBrush(Color.FromRgb(54, 49, 46)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(92, 84, 78)),
            Cursor = Cursors.Hand
        };
        btn.Click += onClick;
        return btn;
    }

    private void SetFullscreen(bool enabled)
    {
        if (isFullscreen == enabled) return;
        isFullscreen = enabled;
        if (enabled)
        {
            savedStyle = WindowStyle;
            savedState = WindowState;
            savedResize = ResizeMode;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = savedStyle;
            ResizeMode = savedResize;
            WindowState = savedState;
        }
        fullscreenBtn.Content = isFullscreen ? "✕ 退出全屏" : "⛶ 全屏";
        UpdateLayout();
        UpdateFrame();
    }

    private void UpdateFrame()
    {
        var availW = root.ActualWidth;
        var availH = Math.Max(0, root.ActualHeight - topBar.ActualHeight);
        if (availW <= 0 || availH <= 0) return;
        var layout = HyperVRdpDisplayGeometry.ComputeForSource(availW, availH, rdpHost.SourceWidth, rdpHost.SourceHeight);
        if (layout.Bounds.Width > 0 && layout.Bounds.Height > 0)
        {
            frameBorder.Width = layout.Bounds.Width;
            frameBorder.Height = layout.Bounds.Height;
        }
    }

    public async Task<bool> CloseCleanlyAsync()
    {
        var disconnected = await rdpHost.DisconnectAndWaitAsync(TimeSpan.FromSeconds(3));
        if (!disconnected)
        {
            statusText.Text = "未能确认独立桌面断开，已保留当前窗口以避免会话冲突。请稍后重试。";
            return false;
        }
        rdpHost.Dispose();
        if (IsVisible) Close();
        return true;
    }

    private async Task ReturnToEmbeddedAsync()
    {
        if (returning) return;
        returning = true;
        try
        {
            var disconnected = await rdpHost.DisconnectAndWaitAsync(TimeSpan.FromSeconds(3));
            if (!disconnected)
            {
                statusText.Text = "未能确认独立桌面断开，已保留当前窗口以避免会话冲突。请稍后重试。";
                return;
            }
            rdpHost.Dispose();
            Close();
            await onReturnToEmbedded();
        }
        finally { returning = false; }
    }
}
