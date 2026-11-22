using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern int GetWindowRgnBox(IntPtr window, out NativeClipBounds bounds);
    [StructLayout(LayoutKind.Sequential)]
    private record struct NativeClipBounds(int Left, int Top, int Right, int Bottom);

    private async Task VerifyNativeHostFixture(List<object> rows,string directory)
    {
        var original=NativeConsoleHost.CreateWindowEx(0,"STATIC","Claudome synthetic console fixture",unchecked((int)0x00cf0000),-16000,-16000,640,480,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(original==IntPtr.Zero)throw new SetupException("Could not create isolated native fixture window.");
        IntPtr prompt=IntPtr.Zero, textInput=IntPtr.Zero, nativeDesktop=IntPtr.Zero;
        try {
            var originalRegion = NativeConsoleHost.CreateRectRgn(0, 0, 580, 450);
            if (originalRegion == IntPtr.Zero) throw new SetupException("The isolated console region could not be prepared.");
            if (NativeConsoleHost.SetWindowRgn(original, originalRegion, false) == 0)
            {
                NativeConsoleHost.DeleteObject(originalRegion);
                throw new SetupException("The isolated console region could not be applied.");
            }
            prompt=NativeConsoleHost.CreateWindowEx(0,"STATIC","Claudome synthetic owned prompt",unchecked((int)0x00cf0000),-16000,-16000,200,100,original,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            if(prompt==IntPtr.Zero)throw new SetupException("Could not create isolated owned prompt.");
            rows.Add(new{name="native-console-locator-excludes-owned-prompt",passed=ConsoleWindowLocator.IsConsoleRoot(original)&&!ConsoleWindowLocator.IsConsoleRoot(prompt)});
            if(!ConsoleWindowLocator.IsConsoleRoot(original)||ConsoleWindowLocator.IsConsoleRoot(prompt))throw new SetupException("An owned prompt was accepted as a root console.");
            Navigate(7);consoleHost!.Visibility=Visibility.Visible;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();
            consoleHost!.Attach(original);consoleHost.Fit();
            var attached=consoleHost.IsAttached&&NativeConsoleHost.GetParent(original)!=IntPtr.Zero;
            textInput = NativeConsoleHost.CreateWindowEx(0, "EDIT", "", unchecked((int)0x50010000),
                8, 8, 120, 26, original, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            var keyboardScope = consoleHost.Focusable && textInput != IntPtr.Zero &&
                consoleHost.OwnsKeyboardFocus(textInput) && consoleHost.OwnsKeyboardFocus(original) &&
                !consoleHost.OwnsKeyboardFocus(prompt) && !consoleHost.OwnsKeyboardFocus(IntPtr.Zero);
            rows.Add(new { name = "native-keyboard-target-accepts-only-the-attached-console-and-its-descendants", passed = keyboardScope });
            if (!keyboardScope) throw new SetupException("Native keyboard focus scope did not match the embedded console.");
            consoleHost.CancelPendingGuestFocus();
            SendMessage(NativeConsoleHost.GetParent(original), 0x0007, IntPtr.Zero, IntPtr.Zero);
            var passiveFocus = !consoleHost.GuestFocusQueued;
            rows.Add(new { name = "native-container-focus-alone-does-not-steal-host-clicks", passed = passiveFocus });
            if (!passiveFocus) throw new SetupException("Native container focus queued a focus change without a guest click.");
            var viewport = consoleHost.ViewportPixels;
            var fitted = new ConsolePixelSize(viewport.Width - 120, viewport.Height - 40);
            consoleHost.SetFittedSize(fitted);
            var bounds = consoleHost.EmbeddedBounds;
            var centered = bounds == new Rect(60, 20, fitted.Width, fitted.Height);
            rows.Add(new{name="native-guest-viewport-centers-a-fitted-window-in-physical-pixels",passed=centered});
            if (!centered) throw new SetupException("The native display was not centered within its viewport.");
            var naturalFrame = new ConsolePixelSize(430, 310);
            var partialViewport = new Rect(14, 23, 430, 290);
            var outerConsole = new Rect(0, 0, 480, 360);
            var rejectsPartial = NativeConsoleHost.FindGuestFrameViewport([partialViewport], outerConsole, naturalFrame) is null;
            nativeDesktop = NativeConsoleHost.CreateWindowEx(0, "STATIC", "", unchecked((int)0x50000000),
                12, 18, naturalFrame.Width, naturalFrame.Height, original, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (nativeDesktop == IntPtr.Zero) throw new SetupException("The isolated desktop viewport could not be created.");
            consoleHost.ClipToGuestFrame(naturalFrame);
            var clipApplied = GetWindowRgnBox(original, out var clipping) != 0 &&
                clipping == new NativeClipBounds(12, 18, 442, 328);
            rows.Add(new { name = "native-console-hides-the-outer-frame-only-when-the-entire-desktop-fits", passed = rejectsPartial && clipApplied });
            if (!rejectsPartial || !clipApplied) throw new SetupException("The embedded clip did not match the full desktop rectangle.");
            var croppedBounds = consoleHost.EmbeddedBounds;
            var frameCentered = croppedBounds.Left + 12 == (viewport.Width - naturalFrame.Width) / 2 &&
                croppedBounds.Top + 18 == (viewport.Height - naturalFrame.Height) / 2;
            rows.Add(new { name = "native-clipped-desktop-is-centered-using-the-visible-surface", passed = frameCentered });
            if (!frameCentered) throw new SetupException("The fitted console centered its chrome instead of the guest desktop.");
            consoleHost.Visibility = Visibility.Hidden;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var hiddenWhileEditing = consoleHost.IsAttached && !NativeConsoleHost.IsWindowVisible(original);
            consoleHost.Visibility = Visibility.Visible;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var restoredAfterEditing = consoleHost.IsAttached && NativeConsoleHost.IsWindowVisible(original);
            var hiddenDuringCredentialForm = hiddenWhileEditing && restoredAfterEditing;
            rows.Add(new { name = "native-console-can-hide-for-credentials-and-restore-without-detaching", passed = hiddenDuringCredentialForm });
            if (!hiddenDuringCredentialForm) throw new SetupException("The native console did not hide or restore around Windows credential entry.");
            consoleHost.Detach(false);
            var regionRestored = GetWindowRgnBox(original, out var restoredRegion) != 0 &&
                restoredRegion == new NativeClipBounds(0, 0, 580, 450);
            rows.Add(new { name = "native-console-restores-the-original-window-region", passed = regionRestored });
            if (!regionRestored) throw new SetupException("The isolated native window retained the embedded desktop crop.");
            var restored=NativeConsoleHost.GetParent(original)==IntPtr.Zero&&!consoleHost.IsAttached;
            rows.Add(new{name="native-console-HWND-is-reparented-and-restored",passed=attached&&restored});
            if(!attached||!restored)throw new SetupException("Native console fixture was not safely restored.");
            var hidden = !NativeConsoleHost.IsWindowVisible(original) && !consoleHost.FocusGuest();
            rows.Add(new { name = "detached-console-stays-hidden-and-cannot-receive-focus", passed = hidden });
            if (!hidden) throw new SetupException("The detached console became visible or retained focus eligibility.");
            var console = new ConsoleWindow(original, Environment.ProcessId, "fixture.exe");
            ConsoleWindowLocator.RequestClose(console with { ProcessId = 0 }, null);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var foreignIdentityRejected = ConsoleWindowLocator.IsAlive(console);
            ConsoleWindowLocator.RequestClose(console, null);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var closed = !ConsoleWindowLocator.IsAlive(console);
            rows.Add(new { name = "native-console-normal-close-respects-window-process-identity", passed = foreignIdentityRejected && closed });
            if (!foreignIdentityRejected || !closed) throw new SetupException("A normal console close did not verify process identity.");
            JsonFile.Write(Path.Combine(directory,"native-host-fixture.json"),new{type="isolated same-process native window; no VM or existing app touched",attached,restored});
        }finally{consoleHost?.Detach(false);if(consoleHost is not null)consoleHost.Visibility=Visibility.Collapsed;if(nativeDesktop!=IntPtr.Zero)NativeConsoleHost.DestroyWindow(nativeDesktop);if(textInput!=IntPtr.Zero)NativeConsoleHost.DestroyWindow(textInput);if(prompt!=IntPtr.Zero)NativeConsoleHost.DestroyWindow(prompt);NativeConsoleHost.DestroyWindow(original);}
    }
}
