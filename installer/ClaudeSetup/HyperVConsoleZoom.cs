using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace ClaudeSetup;

internal static partial class HyperVConsoleZoom
{
    private static readonly string[][] MenuLabels =
    [
        ["View", "查看", "视图", "檢視"],
        ["Zoom Level", "缩放级别", "缩放等级", "缩放", "縮放層級", "縮放比例", "縮放"],
        ["Auto", "Automatic", "自动", "自動"],
        ["75%"],
        ["50%"]
    ];

    internal static bool MatchesMenu(string name, int level)
    {
        // VMConnect places a mnemonic before a letter, or appends it in parentheses in CJK locales.
        var label = MenuShortcut().Replace(name, "").Replace("&", "").Trim();
        return MenuLabels[level].Contains(label, StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\s*\(&?[A-Za-z]\)$")]
    private static partial Regex MenuShortcut();

    internal static int ChooseZoomMenuLevel(int viewportHeight) =>
        viewportHeight < 560 ? 4 : viewportHeight < 790 ? 3 : 2;

    internal static bool TrySelectAuto(ConsoleWindow window, CancellationToken token) =>
        TrySelectZoomLevel(window, 2, token);

    internal static bool TrySelectZoomLevel(ConsoleWindow window, int targetLevel, CancellationToken token)
    {
        if (!Path.GetFileName(window.Executable).Equals("vmconnect.exe", StringComparison.OrdinalIgnoreCase) ||
            !ConsoleWindowLocator.IsAlive(window)) return false;
        var expanded = new Stack<ExpandCollapsePattern>();
        try
        {
            token.ThrowIfCancellationRequested();
            var root = AutomationElement.FromHandle(window.Handle);
            if (root.Current.ProcessId != window.ProcessId) return false;
            var menu = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuBar));
            if (menu is null) return false;

            for (var level = 0; level < 2; level++)
            {
                var parent = FindMenuItem(menu, level, window.ProcessId, token);
                if (parent is null) return false;
                menu = parent;
                if (!menu.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern)) continue;
                var section = (ExpandCollapsePattern)pattern;
                if (section.Current.ExpandCollapseState != ExpandCollapseState.Collapsed) continue;
                token.ThrowIfCancellationRequested();
                section.Expand();
                expanded.Push(section);
            }

            var target = FindMenuItem(menu, targetLevel, window.ProcessId, token)
                ?? (targetLevel != 2 ? FindMenuItem(menu, 2, window.ProcessId, token) : null);
            if (target is null || !target.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) return false;
            token.ThrowIfCancellationRequested();
            if (!ConsoleWindowLocator.IsAlive(window)) return false;
            ((InvokePattern)invoke).Invoke();
            return true;
        }
        catch (Exception error) when (ProviderUnavailable(error))
        {
            return false;
        }
        finally
        {
            foreach (var section in expanded)
            {
                if (!ConsoleWindowLocator.IsAlive(window)) break;
                try
                {
                    if (section.Current.ExpandCollapseState == ExpandCollapseState.Expanded) section.Collapse();
                }
                catch (Exception error) when (ProviderUnavailable(error)) { }
            }
        }
    }

    private static AutomationElement? FindMenuItem(AutomationElement menu, int level, int pid, CancellationToken token)
    {
        var menuItems = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            token.ThrowIfCancellationRequested();
            foreach (AutomationElement child in menu.FindAll(TreeScope.Descendants, menuItems))
                if (child.Current.ProcessId == pid && child.Current.IsEnabled && MatchesMenu(child.Current.Name, level))
                    return child;
            if (attempt < 3) Thread.Sleep(75);
        }
        return null;
    }

    private static bool ProviderUnavailable(Exception error) =>
        error is InvalidOperationException or ElementNotAvailableException or COMException or UnauthorizedAccessException;
}
