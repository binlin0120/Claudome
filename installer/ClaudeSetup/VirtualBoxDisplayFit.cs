using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

internal readonly record struct ConsolePixelSize(int Width, int Height);
internal readonly record struct VBoxDisplayLayout(string ScaleFactor, ConsolePixelSize Viewport, ConsolePixelSize Guest, ConsolePixelSize? FrameAllowance = null, double? LastCheckedAspect = null);

internal static partial class VirtualBoxDisplayFit
{
    internal const string MenuBar = "GUI/MenuBar/Enabled";
    internal const string StatusBar = "GUI/StatusBar/Enabled";
    internal const string Scale = "GUI/ScaleFactor";
    internal const string AutoResize = "GUI/AutoresizeGuest";
    internal const string MouseCapture = "GUI/MouseCapturePolicy";

    [GeneratedRegex("^VideoMode=\\\"(?<width>[0-9]{1,5}),(?<height>[0-9]{1,5}),[1-9][0-9]*\\\"(?:@[^\\r\\n]*)?\\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex VideoMode();

    [GeneratedRegex("^VideoMode=\\\"[0-9]+,[0-9]+,0\\\"@-?[0-9]+,-?[0-9]+[ \\t]+2[ \\t]*\\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BlankMonitor();

    internal static async Task WakeBlankGuest(VirtualBox box, string vmId, string info, CancellationToken token)
    {
        if (!Guid.TryParseExact(vmId, "D", out _) || VmInventory.VBoxValue(info, "VMState") != "running" ||
            !BlankMonitor().IsMatch(info)) return;
        // A transient Shift press/release wakes the monitor without typing into the desktop.
        await box.Run(token, "controlvm", vmId, "keyboardputscancode", "2a", "aa");
    }

    internal static ConsolePixelSize? ReadGuestSize(string info)
    {
        var mode = VideoMode().Match(info);
        if (!mode.Success) return null;
        var width = int.Parse(mode.Groups["width"].Value, CultureInfo.InvariantCulture);
        var height = int.Parse(mode.Groups["height"].Value, CultureInfo.InvariantCulture);
        return width > 0 && height > 0 ? new(width, height) : null;
    }

    internal static VBoxDisplayLayout? Calculate(ConsolePixelSize viewport, ConsolePixelSize guest,
        ConsolePixelSize? scrollViewport = null, VBoxDisplayLayout? previous = null, ConsolePixelSize? frameAllowance = null)
    {
        if (viewport.Width < 100 || viewport.Height < 100 || guest.Width < 1 || guest.Height < 1) return null;

        // The nested Qt display can be smaller than the hosted window even with both toolbars hidden.
        // Reserve room for its frame, then use the measured content when it still has scrollbars.
        var (width, height) = AvailableGuestArea(viewport, frameAllowance);
        if (scrollViewport is { } content)
        {
            width = Math.Min(width, content.Width - 8);
            height = Math.Min(height, content.Height - 8);
        }
        var scale = Math.Min(4, Math.Min(width / (double)guest.Width, height / (double)guest.Height));
        if (previous is { } old && old.Viewport == viewport && old.Guest == guest)
            scale = Math.Min(scale, double.Parse(old.ScaleFactor, CultureInfo.InvariantCulture));
        scale = Math.Floor(scale * 1000) / 1000;
        return scale > 0 ? new(scale.ToString("0.###", CultureInfo.InvariantCulture), viewport, guest, FrameAllowance: frameAllowance) : null;
    }

    internal static async Task Prepare(VirtualBox box, string vmId, CancellationToken token)
    {
        await SetAndVerify(box, vmId, MenuBar, "false", token);
        await SetAndVerify(box, vmId, StatusBar, "false", token);
        await SetAndVerify(box, vmId, AutoResize, "false", token);
    }

    internal static int ReadAdditionsRunLevel(string info) =>
        int.TryParse(VmInventory.VBoxValue(info, "GuestAdditionsRunLevel"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ? level : 0;

    internal static ConsolePixelSize FittedWindowSize(VBoxDisplayLayout layout)
    {
        var reserve = layout.FrameAllowance ?? new ConsolePixelSize(60, 60);
        var frame = ScaledGuestSize(layout);
        return new(Math.Min(frame.Width + Math.Max(0, reserve.Width - 4), layout.Viewport.Width),
            Math.Min(frame.Height + Math.Max(0, reserve.Height - 4), layout.Viewport.Height));
    }

    internal static ConsolePixelSize ScaledGuestSize(VBoxDisplayLayout layout)
    {
        var scale = double.Parse(layout.ScaleFactor, CultureInfo.InvariantCulture);
        return new((int)Math.Ceiling(layout.Guest.Width * scale), (int)Math.Ceiling(layout.Guest.Height * scale));
    }

    internal static bool CanAdaptViewport(ConsolePixelSize viewport) =>
        viewport.Width >= 700 && viewport.Height >= 440 && viewport.Width / (double)viewport.Height is >= 1.18 and <= 2.45;

    internal static ConsolePixelSize? ViewportModeTarget(ConsolePixelSize viewport, ConsolePixelSize guest,
        ConsolePixelSize? frameAllowance = null)
    {
        var (width, height) = AvailableGuestArea(viewport, frameAllowance);
        if (width < 600 || height < 360) return null;
        var aspect = width / (double)height;
        if (aspect is < 1.2 or > 2.4) return null;
        var desiredWidth = Math.Clamp(width, 1280, 1920);
        var targetWidth = (int)Math.Round(Math.Clamp((double)desiredWidth, 600 * aspect, Math.Min(1920, 1440 * aspect)) / 8) * 8;
        var targetHeight = (int)Math.Round(targetWidth / aspect / 8) * 8;
        var guestAspect = guest.Width / (double)guest.Height;
        return Math.Min(aspect, guestAspect) / Math.Max(aspect, guestAspect) < .94
            ? new(targetWidth, targetHeight) : null;
    }

    private static ConsolePixelSize AvailableGuestArea(ConsolePixelSize viewport, ConsolePixelSize? frameAllowance)
    {
        var reserve = frameAllowance ?? new ConsolePixelSize(60, 60);
        return new(viewport.Width - reserve.Width, viewport.Height - reserve.Height);
    }

    internal static async Task<VBoxDisplayLayout?> Fit(VirtualBox box, string vmId, ConsolePixelSize viewport,
        ConsolePixelSize? scrollViewport, VBoxDisplayLayout? previous, CancellationToken token,
        bool adaptRatio = false, bool force = false, ConsolePixelSize? frameAllowance = null)
    {
        // Qt can adjust the guest mode during a scale-factor change. Sampling
        // that response as a new resize request creates a feedback loop.
        var (width, height) = AvailableGuestArea(viewport, frameAllowance);
        var aspect = height > 0 ? width / (double)height : 0;
        var lastCheckedAspect = previous?.LastCheckedAspect;
        var checkRatio = adaptRatio && CanAdaptViewport(viewport) &&
            (force || lastCheckedAspect is null || Math.Abs(aspect - lastCheckedAspect.Value) > .065);
        if (!force && !checkRatio && previous is { } applied && applied.Viewport == viewport &&
            applied.FrameAllowance == frameAllowance) return applied;
        var info = await box.Run(token, "showvminfo", vmId, "--machinereadable");
        if (ReadGuestSize(info) is not { } guest) return previous;
        if (checkRatio && ReadAdditionsRunLevel(info) >= 2)
        {
            lastCheckedAspect = aspect;
            if (ViewportModeTarget(viewport, guest, frameAllowance) is { } target)
            {
                try
                {
                    await box.Run(token, "controlvm", vmId, "setvideomodehint",
                        target.Width.ToString(CultureInfo.InvariantCulture), target.Height.ToString(CultureInfo.InvariantCulture), "32");
                    for (var attempt = 0; attempt < 4; attempt++)
                    {
                        await Task.Delay(250, token);
                        info = await box.Run(token, "showvminfo", vmId, "--machinereadable");
                        if (ReadGuestSize(info) is { } observed)
                        {
                            if (observed != guest) scrollViewport = null;
                            guest = observed;
                            if (observed == target) break;
                        }
                    }
                }
                catch (SetupException) { /* Continue fitting the last valid frame on older or unsupported drivers. */ }
            }
        }
        if (Calculate(viewport, guest, scrollViewport, frameAllowance: frameAllowance) is not { } fit) return previous;
        if (!force && previous is { } unchanged && unchanged.Viewport == viewport && unchanged.Guest == guest &&
            unchanged.ScaleFactor == fit.ScaleFactor && unchanged.LastCheckedAspect == lastCheckedAspect &&
            unchanged.FrameAllowance == frameAllowance) return unchanged;
        await Apply(box, vmId, fit, token);
        return fit with { LastCheckedAspect = lastCheckedAspect };
    }

    internal static Task DisableMouseCapture(VirtualBox box, string vmId, CancellationToken token) =>
        SetAndVerify(box, vmId, MouseCapture, "Disabled", token);

    internal static Task Apply(VirtualBox box, string vmId, VBoxDisplayLayout layout, CancellationToken token) =>
        SetAndVerify(box, vmId, Scale, layout.ScaleFactor, token);

    private static async Task SetAndVerify(VirtualBox box, string vmId, string key, string value, CancellationToken token)
    {
        if (!Guid.TryParseExact(vmId, "D", out _)) throw new SetupException("尚未定位原 VirtualBox 虚拟机。");
        var expected = "Value: " + value;
        var before = await box.Run(token, "getextradata", vmId, key);
        if (before.Trim() == expected) return;
        await box.Run(token, "setextradata", vmId, key, value);
        var result = await box.Run(token, "getextradata", vmId, key);
        if (result.Trim() != expected) throw new SetupException("VirtualBox 显示设置未通过回读，请在检查与维护中重新适配。");
    }
}
