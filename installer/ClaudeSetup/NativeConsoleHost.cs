using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClaudeSetup;

// Hosts a real, identity-scoped console HWND. No image, RDP server or clipboard bridge.
public sealed class NativeConsoleHost : HwndHost
{
    private IntPtr container,child,originalParent;
    private uint childProcess;
    private long originalStyle,originalExtendedStyle;
    private Rect originalBounds;
    private bool originalVisible;
    private DispatcherOperation? pendingGuestFocus;
    private bool pointerWasInGuest;
    private IntPtr pointerHook;
    private PointerHook? pointerCallback;
    private ConsolePixelSize? fittedSize;
    private bool allowOversizeForClip;
    private IntPtr darkBrush;
    private IntPtr originalClipRegion;
    private Rect? desktopClip;
    [DllImport("gdi32.dll")]private static extern IntPtr CreateSolidBrush(uint colorRef);
    [DllImport("gdi32.dll")]internal static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")]internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
    [DllImport("user32.dll")]private static extern int FillRect(IntPtr hdc, ref NativeRect rect, IntPtr brush);
    [DllImport("user32.dll")]private static extern IntPtr BeginPaint(IntPtr window, out PaintMessage paint);
    [DllImport("user32.dll")]private static extern bool EndPaint(IntPtr window, ref PaintMessage paint);
    [DllImport("user32.dll")]private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("user32.dll")]internal static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("user32.dll")]internal static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]private static extern bool IsWindowEnabled(IntPtr handle);
    [DllImport("user32.dll")]internal static extern bool EnableWindow(IntPtr handle, bool enable);
    [DllImport("user32.dll")]private static extern bool IsChild(IntPtr parent, IntPtr window);
    [DllImport("user32.dll")]private static extern IntPtr GetFocus();
    [DllImport("user32.dll")]private static extern IntPtr SetFocus(IntPtr handle);
    [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]private static extern bool BringWindowToTop(IntPtr handle);

    internal bool OwnsKeyboardFocus(IntPtr handle) =>
        IsAttached && handle != IntPtr.Zero && (handle == child || IsChild(child, handle));

    internal void ReleaseGuestInputCapture()
    {
        CancelPendingGuestFocus();
        ReleaseGuestMouseCapture();
        var root = GetAncestor(container, 2);
        if (root != IntPtr.Zero && root == GetForegroundWindow() && OwnsKeyboardFocus(ForegroundFocus()))
            SetFocus(root);
    }

    internal void CancelPendingGuestFocus() => pendingGuestFocus?.Abort();
    internal bool GuestFocusQueued => pendingGuestFocus?.Status == DispatcherOperationStatus.Pending;

    internal bool FocusGuest()
    {
        if (!IsAttached || !IsWindowVisible(child) || !IsWindowEnabled(child) ||
            GetAncestor(container, 2) != GetForegroundWindow()) return false;
        if (!OwnsKeyboardFocus(ForegroundFocus())) SetFocus(child);
        return OwnsKeyboardFocus(ForegroundFocus());
    }

    protected override bool TabIntoCore(TraversalRequest request) => FocusGuest();
    protected override bool HasFocusWithinCore() => OwnsKeyboardFocus(GetFocus());

    internal static bool IsPointerPress(int message) => message is 0x0201 or 0x0204 or 0x0207 or 0x020B or 0x0246;

    private static IntPtr ForegroundFocus()
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(0, ref info) ? info.Focus : GetFocus();
    }

    private bool PointInViewport(NativePoint point) =>
        container != IntPtr.Zero && GetWindowRect(container, out var rect) &&
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    private void QueueGuestFocus(NativePoint point)
    {
        if (!IsAttached || !PointInViewport(point) || GuestFocusQueued) return;
        var clickedConsole = child;
        pendingGuestFocus = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (child != clickedConsole) return;
            var hit = WindowFromPoint(point);
            if (hit == container || OwnsKeyboardFocus(hit)) FocusGuest();
        });
    }

    private void ReleaseGuestMouseCapture()
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref info) || !OwnsKeyboardFocus(info.Capture)) return;
        if (GetCapture() == info.Capture && ReleaseCapture()) return;
        PostMessage(info.Capture, 0x001f, IntPtr.Zero, IntPtr.Zero); // WM_CANCELMODE
    }

    private IntPtr OnPointer(int code, IntPtr message, IntPtr data)
    {
        const int MouseMove = 0x0200;
        var press = IsPointerPress((int)message);
        // The hook leaves the original pointer event untouched. Native hit testing
        // and focus changes are deferred until after the event can reach WPF or Qt.
        if (code >= 0 && data != IntPtr.Zero && (press || (pointerWasInGuest && (int)message == MouseMove)))
        {
            var point = Marshal.PtrToStructure<NativePoint>(data);
            var inside = PointInViewport(point);
            if (press)
            {
                CancelPendingGuestFocus();
                if (inside) QueueGuestFocus(point);
            }
            if (!inside && pointerWasInGuest && (press || !AnyPointerButtonDown()))
            {
                pointerWasInGuest = false;
                var console = child;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (child == console && !pointerWasInGuest && GetCursorPos(out var cursor) && !PointInViewport(cursor))
                        ReleaseGuestMouseCapture();
                });
            }
            else if (inside && press) pointerWasInGuest = true;
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private static bool AnyPointerButtonDown() =>
        GetAsyncKeyState(0x01) < 0 || GetAsyncKeyState(0x02) < 0 || GetAsyncKeyState(0x04) < 0 ||
        GetAsyncKeyState(0x05) < 0 || GetAsyncKeyState(0x06) < 0;

    protected override IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int EraseBackgroundMessage = 0x0014;
        const int Paint = 0x000F;
        const int ParentNotifyMessage = 0x0210;
        if ((IsPointerPress(message) || (message == ParentNotifyMessage && IsPointerPress((int)(wParam.ToInt64() & 0xffff)))) &&
            GetCursorPos(out var cursor))
            QueueGuestFocus(cursor);
        else if (message == Paint && darkBrush != IntPtr.Zero)
        {
            // STATIC also paints its own light background during WM_PAINT.
            // WS_CLIPCHILDREN keeps this fill outside the embedded window.
            var dc = BeginPaint(hwnd, out var paint);
            if (dc != IntPtr.Zero)
            {
                FillRect(dc, ref paint.Bounds, darkBrush);
                EndPaint(hwnd, ref paint);
            }
            handled = true;
            return IntPtr.Zero;
        }
        else if (message == EraseBackgroundMessage && darkBrush != IntPtr.Zero && GetClientRect(hwnd, out var rect))
        {
            FillRect(wParam, ref rect, darkBrush);
            handled = true;
            return new IntPtr(1);
        }
        return base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }
    private const int StyleIndex=-16,ExtendedStyleIndex=-20;
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern IntPtr CreateWindowEx(int extended,string className,string text,int style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll",SetLastError=true)]internal static extern bool DestroyWindow(IntPtr handle);
    [DllImport("user32.dll",SetLastError=true)]internal static extern IntPtr SetParent(IntPtr child,IntPtr parent);
    [DllImport("user32.dll")]internal static extern IntPtr GetParent(IntPtr child);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetLong(IntPtr handle,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW",SetLastError=true)]private static extern IntPtr SetLong(IntPtr handle,int index,IntPtr value);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr handle,out NativeRect rect);
    [DllImport("user32.dll")]private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);
    private delegate bool VisitChild(IntPtr window, IntPtr state);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr parent, VisitChild visit, IntPtr state);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]private static extern IntPtr GetCapture();
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string? moduleName);
    private delegate IntPtr PointerHook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError=true)]private static extern IntPtr SetWindowsHookEx(int id, PointerHook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretBounds;
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetWindowPos(IntPtr handle,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool RedrawWindow(IntPtr handle,IntPtr updateRect,IntPtr updateRegion,uint flags);
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr handle,int command);
    [DllImport("user32.dll")]private static extern bool IsWindow(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct PaintMessage
    {
        public IntPtr DeviceContext;
        public int Erase;
        public NativeRect Bounds;
        public int Restore, Update;
        public int Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7, Reserved8;
    }
    private bool HasOriginalChild => child != IntPtr.Zero && IsWindow(child) &&
        GetWindowThreadProcessId(child, out var process) != 0 && process == childProcess;
    public bool IsAttached => HasOriginalChild && GetParent(child) == container;
    public IntPtr GuestWindow=>child;
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        if (darkBrush == IntPtr.Zero) darkBrush = CreateSolidBrush(0x001C150E);
        container=CreateWindowEx(0,"STATIC","",unchecked((int)0x56000000),0,0,1,1,parent.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(container==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(this,container);
    }
    private const long EmbeddedStyleStripMask = 0x80000000L | 0x00C00000L | 0x00040000L | 0x00080000L | 0x00020000L | 0x00010000L;
    private const long EmbeddedExStyleStripMask = 0x00040000L | 0x00000004L | 0x00000100L | 0x00000200L | 0x00000001L;
    private const long StandaloneOverlappedMask = 0x00C00000L | 0x00080000L | 0x00040000L | 0x00020000L | 0x00010000L;

    internal static long ComputeEmbeddedChildStyle(long currentStyle) =>
        (currentStyle & ~EmbeddedStyleStripMask) | 0x40000000L;

    internal static long ComputeEmbeddedChildExtendedStyle(long currentExStyle) =>
        currentExStyle & ~EmbeddedExStyleStripMask;

    internal static long ComputeDetachedStandaloneStyle(long savedStyle) =>
        ((savedStyle & ~0x40000000L & ~0x10000000L) | StandaloneOverlappedMask);

    public void Attach(IntPtr handle)
    {
        if(container==IntPtr.Zero||handle==IntPtr.Zero||!IsWindow(handle))throw new SetupException("虚拟机桌面窗口尚未就绪，请重新连接。");
        if(child==handle&&IsAttached){Fit();return;}
        Detach(false);originalParent=GetParent(handle);originalStyle=GetLong(handle,StyleIndex).ToInt64();originalExtendedStyle=GetLong(handle,ExtendedStyleIndex).ToInt64();
        GetWindowRect(handle,out var bounds);originalBounds=new(bounds.Left,bounds.Top,Math.Max(1,bounds.Right-bounds.Left),Math.Max(1,bounds.Bottom-bounds.Top));
        child=handle;GetWindowThreadProcessId(handle,out childProcess);originalVisible=IsWindowVisible(handle);
        originalClipRegion = CreateRectRgn(0, 0, 0, 0);
        if (originalClipRegion != IntPtr.Zero && GetWindowRgn(child, originalClipRegion) == 0)
        {
            DeleteObject(originalClipRegion);
            originalClipRegion = IntPtr.Zero;
        }
        SetLong(child,StyleIndex,new IntPtr(ComputeEmbeddedChildStyle(originalStyle)));
        SetLong(child,ExtendedStyleIndex,new IntPtr(ComputeEmbeddedChildExtendedStyle(originalExtendedStyle)));
        EnableWindow(child,true);
        SetParent(child,container);
        if(GetParent(child)!=container){Detach();throw new SetupException("原控制台无法嵌入当前窗口，可能存在权限或 DPI 差异。虚拟机仍保留运行，可恢复独立窗口查看。");}
        pointerCallback = OnPointer;
        pointerHook = SetWindowsHookEx(14, pointerCallback, GetModuleHandle(null), 0); // WH_MOUSE_LL
        ShowWindow(child,5);Fit(forceRedraw:true);
    }
    internal ConsolePixelSize ViewportPixels
    {
        get => container != IntPtr.Zero && GetClientRect(container, out var bounds)
            ? new(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)
            : default;
    }

    internal void SetFittedSize(ConsolePixelSize? size)
    {
        fittedSize = size;
        Fit();
    }

    internal Rect EmbeddedBounds
    {
        get
        {
            var origin = new NativePoint();
            return IsAttached && ClientToScreen(container, ref origin) && GetWindowRect(child, out var bounds)
                ? new Rect(bounds.Left - origin.X, bounds.Top - origin.Y, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)
                : Rect.Empty;
        }
    }

    internal ConsolePixelSize? MeasureVirtualBoxFrameAllowance()
    {
        if (!IsAttached || !GetWindowRect(child, out var outer)) return null;
        var outerWidth = outer.Right - outer.Left;
        var outerHeight = outer.Bottom - outer.Top;
        for (var panel = GetWindow(child, 5); panel != IntPtr.Zero; panel = GetWindow(panel, 2))
        {
            if (!IsWindowVisible(panel) || GetWindowThreadProcessId(panel, out var owner) == 0 || owner != childProcess ||
                !GetWindowRect(panel, out var inner) || inner.Left < outer.Left || inner.Top < outer.Top ||
                inner.Right > outer.Right || inner.Bottom > outer.Bottom) continue;
            var width = outerWidth - (inner.Right - inner.Left);
            var height = outerHeight - (inner.Bottom - inner.Top);
            if ((width != 0 || height != 0) && width is >= 0 and <= 80 && height is >= 0 and <= 120 &&
                outerWidth - width >= outerWidth * .75 && outerHeight - height >= outerHeight * .75)
                return new(width + 8, height + 8);
        }
        return null;
    }

    internal ConsolePixelSize? VisibleScrollViewport()
    {
        if (!IsAttached || !GetWindowRect(child, out var outer)) return null;
        GetWindowThreadProcessId(child, out var process);
        var minimumWidth = (outer.Right - outer.Left) * 0.65;
        var minimumHeight = (outer.Bottom - outer.Top) * 0.65;
        ConsolePixelSize? result = null;
        EnumChildWindows(child, (parent, _) =>
        {
            if (!IsWindowVisible(parent) || !GetWindowRect(parent, out var parentBounds)) return true;
            if (parentBounds.Right - parentBounds.Left < minimumWidth || parentBounds.Bottom - parentBounds.Top < minimumHeight) return true;
            GetWindowThreadProcessId(parent, out var owner);
            if (owner != process) return true;

            var children = new List<Rect>();
            var seen = 0;
            for (var item = GetWindow(parent, 5); item != IntPtr.Zero && seen < 32; item = GetWindow(item, 2), seen++)
            {
                if (!IsWindowVisible(item) || !GetWindowRect(item, out var bounds)) continue;
                if (bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                    children.Add(new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
            }

            var content = FindScrollingContent(children, minimumWidth, minimumHeight);
            if (content is { } size && (result is null || size.Width * size.Height > result.Value.Width * result.Value.Height))
                result = size;
            return true;
        }, IntPtr.Zero);
        return result;
    }

    [DllImport("user32.dll", CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr handle, System.Text.StringBuilder text, int maxCount);

    internal bool HasOverflowScrollbars()
    {
        if (!IsAttached) return false;
        if (VisibleScrollViewport() is not null) return true;
        var found = false;
        var sb = new System.Text.StringBuilder(64);
        EnumChildWindows(child, (window, _) =>
        {
            if (found) return false;
            if (!IsWindowVisible(window) || GetWindowThreadProcessId(window, out var pid) == 0 || pid != childProcess) return true;
            var style = GetLong(window, StyleIndex).ToInt64();
            if ((style & 0x00300000L) != 0) // WS_VSCROLL | WS_HSCROLL
            {
                found = true;
                return false;
            }
            sb.Clear();
            if (GetClassName(window, sb, sb.Capacity) > 0 &&
                sb.ToString().Contains("ScrollBar", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    internal ConsolePixelSize GetVmConnectContentViewport()
    {
        var outer = ViewportPixels;
        if (!IsAttached || outer.Width < 1 || outer.Height < 1 || !GetWindowRect(child, out var childBounds))
            return new(Math.Max(1, outer.Width - 4), Math.Max(1, outer.Height - 96));

        var outerW = Math.Max(1, childBounds.Right - childBounds.Left);
        var outerH = Math.Max(1, childBounds.Bottom - childBounds.Top);
        ConsolePixelSize? bestCanvas = null;
        var sb = new System.Text.StringBuilder(64);
        EnumChildWindows(child, (window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowThreadProcessId(window, out var pid) == 0 || pid != childProcess) return true;
            if (!GetWindowRect(window, out var r)) return true;
            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            if (w < outerW * 0.5 || h < outerH * 0.4 || (w >= outerW - 2 && h >= outerH - 2)) return true;
            sb.Clear();
            var cls = GetClassName(window, sb, sb.Capacity) > 0 ? sb.ToString() : "";
            if (cls.Contains("ScrollBar", StringComparison.OrdinalIgnoreCase) || cls.Contains("ToolBar", StringComparison.OrdinalIgnoreCase)) return true;
            if (bestCanvas is null || w * h > bestCanvas.Value.Width * bestCanvas.Value.Height)
                bestCanvas = new(w, h);
            return true;
        }, IntPtr.Zero);

        return bestCanvas ?? new(Math.Max(1, outer.Width - 4), Math.Max(1, outer.Height - 96));
    }

    internal enum DisplayFillState
    {
        FullFill,
        MarginsRemaining,
        Unverified
    }

    internal readonly record struct DisplayFillResult(DisplayFillState State, string Detail);

    internal readonly record struct SurfaceCandidate(string ClassName, Rect RelativeBounds, bool IsVisible, uint ProcessId);
    internal readonly record struct ChromeClipPlan(ConsolePixelSize ChildSize, Rect ClipRect, int OffsetX, int OffsetY);

    internal static Rect? SelectCredibleHyperVGuestSurface(IEnumerable<SurfaceCandidate> candidates, Rect childBounds, uint expectedProcessId)
    {
        if (childBounds.Width < 160 || childBounds.Height < 120) return null;
        Rect? best = null;
        foreach (var c in candidates)
        {
            if (!c.IsVisible || c.ProcessId != expectedProcessId) continue;
            var isSurfaceClass = c.ClassName.Equals("IHWindowClass", StringComparison.OrdinalIgnoreCase) ||
                c.ClassName.Equals("OPWindowClass", StringComparison.OrdinalIgnoreCase) ||
                c.ClassName.Equals("UIMainClass", StringComparison.OrdinalIgnoreCase);
            if (!isSurfaceClass) continue;
            var r = c.RelativeBounds;
            if (r.Width < 160 || r.Height < 120) continue;
            if (r.Left < -2 || r.Top < -2 || r.Right > childBounds.Width + 2 || r.Bottom > childBounds.Height + 2) continue;
            if (best is null || r.Width * r.Height > best.Value.Width * best.Value.Height)
                best = r;
        }
        return best;
    }

    internal static ChromeClipPlan? ComputeLiveChromeClipPlan(
        ConsolePixelSize viewport,
        ConsolePixelSize currentChildSize,
        Rect? measuredSurfaceInChild,
        int measuredBottomChromeHeight = 0)
    {
        if (measuredSurfaceInChild is not { } surface ||
            viewport.Width < 1 || viewport.Height < 1 ||
            currentChildSize.Width < 1 || currentChildSize.Height < 1 ||
            surface.Width < 160 || surface.Height < 120)
            return null;

        var chromeLeft = Math.Clamp((int)Math.Round(surface.Left), 0, 80);
        var chromeTop = Math.Clamp((int)Math.Round(surface.Top), 0, 180);
        var chromeRight = chromeLeft;
        var chromeBottom = Math.Clamp(measuredBottomChromeHeight, 0, 80);
        var targetW = Math.Max(viewport.Width, viewport.Width + chromeLeft + chromeRight);
        var targetH = Math.Max(viewport.Height, viewport.Height + chromeTop + chromeBottom);
        var clipRect = new Rect(
            Math.Max(0, Math.Round(surface.Left)),
            Math.Max(0, Math.Round(surface.Top)),
            Math.Max(1, Math.Round(surface.Width)),
            Math.Max(1, Math.Round(surface.Height)));
        var offsetX = (viewport.Width - (int)clipRect.Width) / 2 - (int)clipRect.X;
        var offsetY = (viewport.Height - (int)clipRect.Height) / 2 - (int)clipRect.Y;
        return new(new(targetW, targetH), clipRect, offsetX, offsetY);
    }

    private (Rect? Surface, int BottomBarHeight, ConsolePixelSize ChildSize) MeasureHyperVSurfaceInChild()
    {
        if (!IsAttached || !GetWindowRect(child, out var childRect))
            return (null, 0, default);
        var childW = Math.Max(0, childRect.Right - childRect.Left);
        var childH = Math.Max(0, childRect.Bottom - childRect.Top);
        if (childW < 160 || childH < 120) return (null, 0, new(childW, childH));

        var candidates = new List<SurfaceCandidate>();
        var bottomBarHeight = 0;
        var sb = new System.Text.StringBuilder(64);
        EnumChildWindows(child, (window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowThreadProcessId(window, out var pid) == 0 || pid != childProcess) return true;
            if (!GetWindowRect(window, out var r)) return true;
            var rel = new Rect(r.Left - childRect.Left, r.Top - childRect.Top, r.Right - r.Left, r.Bottom - r.Top);
            sb.Clear();
            var cls = GetClassName(window, sb, sb.Capacity) > 0 ? sb.ToString() : "";
            candidates.Add(new(cls, rel, true, pid));
            // Live-measure bottom status strip (never hardcoded).
            if (rel.Height is >= 14 and <= 64 && rel.Width >= childW * 0.65 && rel.Bottom >= childH - 4 &&
                !cls.Equals("IHWindowClass", StringComparison.OrdinalIgnoreCase) &&
                !cls.Equals("OPWindowClass", StringComparison.OrdinalIgnoreCase) &&
                !cls.Equals("UIMainClass", StringComparison.OrdinalIgnoreCase))
            {
                bottomBarHeight = Math.Max(bottomBarHeight, (int)Math.Round(rel.Height));
            }
            return true;
        }, IntPtr.Zero);

        var surface = SelectCredibleHyperVGuestSurface(candidates, new Rect(0, 0, childW, childH), childProcess);
        return (surface, bottomBarHeight, new(childW, childH));
    }

    internal bool HasLiveGuestClip => desktopClip is not null;

    internal void ClearHyperVChromeClip()
    {
        allowOversizeForClip = false;
        fittedSize = null;
        RestoreChildClip();
        Fit(forceRedraw: true);
    }

    internal bool ClipToLiveHyperVSurface()
    {
        if (!IsAttached) return false;
        var viewport = ViewportPixels;
        if (viewport.Width < 1 || viewport.Height < 1) return false;

        var (surface, bottomBarH, childSize) = MeasureHyperVSurfaceInChild();
        var plan = ComputeLiveChromeClipPlan(viewport, childSize, surface, bottomBarH);
        if (plan is null)
        {
            ClearHyperVChromeClip();
            return false;
        }

        // First expand child by live-measured chrome insets so VMConnect's inner canvas matches the 16:9 viewport.
        if (!allowOversizeForClip || fittedSize != plan.Value.ChildSize)
        {
            allowOversizeForClip = true;
            fittedSize = plan.Value.ChildSize;
            RestoreChildClip();
            Fit(forceRedraw: false);
            (surface, bottomBarH, childSize) = MeasureHyperVSurfaceInChild();
            plan = ComputeLiveChromeClipPlan(viewport, childSize, surface, bottomBarH);
            if (plan is null)
            {
                ClearHyperVChromeClip();
                return false;
            }
        }

        var fit = plan.Value.ClipRect;
        if (desktopClip != fit)
        {
            var region = CreateRectRgn((int)fit.Left, (int)fit.Top, (int)fit.Right, (int)fit.Bottom);
            if (region == IntPtr.Zero) return false;
            if (SetWindowRgn(child, region, true) != 0)
                desktopClip = fit;
            else
            {
                DeleteObject(region);
                return false;
            }
        }
        Fit(forceRedraw: true);
        return true;
    }

    internal Rect? MeasureActualGuestSurfaceRelativeToContainer()
    {
        if (!IsAttached || container == IntPtr.Zero || !GetWindowRect(container, out var hostRect))
            return null;
        var outerW = hostRect.Right - hostRect.Left;
        var outerH = hostRect.Bottom - hostRect.Top;
        if (outerW < 1 || outerH < 1) return null;

        Rect? renderSurface = null;
        var sb = new System.Text.StringBuilder(64);
        EnumChildWindows(child, (window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowThreadProcessId(window, out var pid) == 0 || pid != childProcess) return true;
            if (!GetWindowRect(window, out var r)) return true;
            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            if (w < 160 || h < 120) return true;
            sb.Clear();
            var cls = GetClassName(window, sb, sb.Capacity) > 0 ? sb.ToString() : "";
            // Match actual VMConnect RDP/Basic Session render surfaces rather than outer WinForms chrome.
            var isSurfaceClass = cls.Equals("IHWindowClass", StringComparison.OrdinalIgnoreCase) ||
                cls.Equals("OPWindowClass", StringComparison.OrdinalIgnoreCase) ||
                cls.Equals("UIMainClass", StringComparison.OrdinalIgnoreCase);
            if (!isSurfaceClass) return true;
            var candidate = new Rect(r.Left - hostRect.Left, r.Top - hostRect.Top, w, h);
            if (renderSurface is null || candidate.Width * candidate.Height > renderSurface.Value.Width * renderSurface.Value.Height)
                renderSurface = candidate;
            return true;
        }, IntPtr.Zero);
        return renderSurface;
    }

    internal static DisplayFillResult EvaluateEdgeToEdgeFill(Rect? actualSurface, ConsolePixelSize outerViewport, int tolerancePx = 4)
    {
        if (actualSurface is not { } surface || outerViewport.Width <= 0 || outerViewport.Height <= 0 ||
            surface.Width <= 0 || surface.Height <= 0)
            return new(DisplayFillState.Unverified, "未读取到实际来宾画面边界");

        var leftGap = Math.Max(0, (int)Math.Round(surface.Left));
        var topGap = Math.Max(0, (int)Math.Round(surface.Top));
        var rightGap = Math.Max(0, (int)Math.Round(outerViewport.Width - (surface.Left + surface.Width)));
        var bottomGap = Math.Max(0, (int)Math.Round(outerViewport.Height - (surface.Top + surface.Height)));
        var clipped = surface.Left < -tolerancePx || surface.Top < -tolerancePx ||
            (surface.Left + surface.Width) > outerViewport.Width + tolerancePx ||
            (surface.Top + surface.Height) > outerViewport.Height + tolerancePx;
        if (!clipped && leftGap <= tolerancePx && topGap <= tolerancePx && rightGap <= tolerancePx && bottomGap <= tolerancePx)
            return new(DisplayFillState.FullFill, $"{(int)Math.Round(surface.Width)}x{(int)Math.Round(surface.Height)}");

        return new(DisplayFillState.MarginsRemaining,
            clipped
                ? "画面超出内嵌区边界"
                : $"左{leftGap}/上{topGap}/右{rightGap}/下{bottomGap}px 留边");
    }

    internal static ConsolePixelSize? FindScrollingContent(IReadOnlyList<Rect> children, double minimumWidth, double minimumHeight)
    {
        foreach (var view in children)
        {
            if (view.Width < minimumWidth || view.Height < minimumHeight) continue;
            // A real scrollbar is an adjacent sibling strip spanning the display, not a guest drawing.
            var hasVertical = children.Any(bar => bar.Width is >= 7 and <= 40
                && Math.Abs(bar.Height - view.Height) <= 4 && Math.Abs(bar.Top - view.Top) <= 4
                && Math.Abs(bar.Left - view.Right) <= 4);
            var hasHorizontal = children.Any(bar => bar.Height is >= 7 and <= 40
                && Math.Abs(bar.Width - view.Width) <= 4 && Math.Abs(bar.Left - view.Left) <= 4
                && Math.Abs(bar.Top - view.Bottom) <= 4);
            if (hasVertical || hasHorizontal) return new((int)view.Width, (int)view.Height);
        }
        return null;
    }

    internal static Rect? FindGuestFrameViewport(IEnumerable<Rect> children, Rect outer, ConsolePixelSize frame)
    {
        // Qt may round each axis differently from the control API. Match the
        // actual render surface within the rounding error, not its outer panel.
        return children.Where(view => outer.Contains(view) &&
                Math.Abs(view.Width - frame.Width) <= 2 && Math.Abs(view.Height - frame.Height) <= 2)
            .OrderBy(view => Math.Abs(view.Width - frame.Width) + Math.Abs(view.Height - frame.Height))
            .ThenBy(view => view.Width * view.Height).Select(view => (Rect?)view).FirstOrDefault();
    }

    internal void ClipToGuestFrame(ConsolePixelSize frame)
    {
        if (!IsAttached || !GetWindowRect(child, out var bounds)) return;
        var children = new List<Rect>();
        EnumChildWindows(child, (window, _) =>
        {
            if (GetWindowThreadProcessId(window, out var pid) != 0 && pid == childProcess &&
                IsWindowVisible(window) && GetWindowRect(window, out var view) && view.Right > view.Left && view.Bottom > view.Top)
                children.Add(new Rect(view.Left - bounds.Left, view.Top - bounds.Top, view.Right - view.Left, view.Bottom - view.Top));
            return true;
        }, IntPtr.Zero);
        var outer = new Rect(0, 0, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        var viewport = FindGuestFrameViewport(children, outer, frame);
        if (viewport == desktopClip) return;
        if (viewport is not { } fit)
        {
            RestoreChildClip();
            Fit(forceRedraw: true);
            return;
        }
        var region = CreateRectRgn((int)fit.Left, (int)fit.Top, (int)fit.Right, (int)fit.Bottom);
        if (region == IntPtr.Zero) return;
        // Windows owns the region following a successful SetWindowRgn.
        if (SetWindowRgn(child, region, true) != 0)
        {
            desktopClip = fit;
            Fit();
        }
        else DeleteObject(region);
        RedrawWindow(container, IntPtr.Zero, IntPtr.Zero, 0x0085);
    }

    private void RestoreChildClip()
    {
        if (desktopClip is null) return;
        var region = IntPtr.Zero;
        if (originalClipRegion != IntPtr.Zero)
        {
            region = CreateRectRgn(0, 0, 0, 0);
            if (region == IntPtr.Zero) return;
            if (CombineRgn(region, originalClipRegion, IntPtr.Zero, 5) == 0)
            {
                DeleteObject(region);
                return;
            }
        }
        if (SetWindowRgn(child, region, true) != 0) desktopClip = null;
        else if (region != IntPtr.Zero) DeleteObject(region);
    }

    public void Fit(bool forceRedraw = false)
    {
        if (!IsAttached) return;
        var viewport = ViewportPixels;
        if (viewport.Width < 1 || viewport.Height < 1) return;
        var width = allowOversizeForClip && fittedSize is { } overW
            ? Math.Max(viewport.Width, overW.Width)
            : Math.Min(viewport.Width, fittedSize?.Width ?? viewport.Width);
        var height = allowOversizeForClip && fittedSize is { } overH
            ? Math.Max(viewport.Height, overH.Height)
            : Math.Min(viewport.Height, fittedSize?.Height ?? viewport.Height);
        var position = EmbeddedBounds;
        if (position.Width != width || position.Height != height) RestoreChildClip();
        var x = desktopClip is { } frame ? (viewport.Width - (int)frame.Width) / 2 - (int)frame.X : (viewport.Width - width) / 2;
        var y = desktopClip is { } visible ? (viewport.Height - (int)visible.Height) / 2 - (int)visible.Y : (viewport.Height - height) / 2;

        var currentStyle = GetLong(child, StyleIndex).ToInt64();
        var targetStyle = ComputeEmbeddedChildStyle(currentStyle);
        var styleChanged = currentStyle != targetStyle;
        if (styleChanged) SetLong(child, StyleIndex, new IntPtr(targetStyle));
        var currentExStyle = GetLong(child, ExtendedStyleIndex).ToInt64();
        var targetExStyle = ComputeEmbeddedChildExtendedStyle(currentExStyle);
        if (currentExStyle != targetExStyle)
        {
            SetLong(child, ExtendedStyleIndex, new IntPtr(targetExStyle));
            styleChanged = true;
        }
        if (styleChanged || position != new Rect(x, y, width, height))
        {
            SetWindowPos(child, IntPtr.Zero, x, y, width, height, 0x0014 | 0x0020);
        }
        else if (!forceRedraw) return;
        RedrawWindow(child, IntPtr.Zero, IntPtr.Zero, 0x0585);
        RedrawWindow(container, IntPtr.Zero, IntPtr.Zero, 0x0085);
    }
    public void Detach(bool showOriginal=true)
    {
        if(child==IntPtr.Zero)return;
        if (pointerHook != IntPtr.Zero) UnhookWindowsHookEx(pointerHook);
        pointerHook = IntPtr.Zero;
        pointerCallback = null;
        pointerWasInGuest = false;
        CancelPendingGuestFocus();
        if(HasOriginalChild) {
            ReleaseGuestInputCapture();
            var keepVisible = showOriginal && originalVisible;
            // Always restore the window region BEFORE re-parenting so caption/fullscreen hit-testing is never clipped.
            RestoreChildClip();
            if (!keepVisible) EnableWindow(child, false);
            ShowWindow(child, 0);
            SetParent(child, originalParent);
            SetLong(child, StyleIndex, new(ComputeDetachedStandaloneStyle(originalStyle)));
            SetLong(child, ExtendedStyleIndex, new(originalExtendedStyle));
            if (keepVisible)
            {
                EnableWindow(child, true);
                SetWindowPos(child, IntPtr.Zero, (int)originalBounds.X, (int)originalBounds.Y,
                    (int)originalBounds.Width, (int)originalBounds.Height, 0x0040 | 0x0020);
                ShowWindow(child, 1); // SW_SHOWNORMAL
                BringWindowToTop(child);
                SetForegroundWindow(child);
            }
            else
            {
                SetWindowPos(child, new IntPtr(1), (int)originalBounds.X, (int)originalBounds.Y,
                    (int)originalBounds.Width, (int)originalBounds.Height, 0x0090 | 0x0020);
            }
        }
        if (originalClipRegion != IntPtr.Zero) DeleteObject(originalClipRegion);
        originalClipRegion = IntPtr.Zero;
        desktopClip = null;
        child=IntPtr.Zero;
        childProcess=0;
        fittedSize=null;
        allowOversizeForClip=false;
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info){base.OnRenderSizeChanged(info);Fit();}
    protected override void DestroyWindowCore(HandleRef handle)
    {
        Detach(false);
        DestroyWindow(handle.Handle);
        container = IntPtr.Zero;
        if (darkBrush != IntPtr.Zero) { DeleteObject(darkBrush); darkBrush = IntPtr.Zero; }
    }
}
