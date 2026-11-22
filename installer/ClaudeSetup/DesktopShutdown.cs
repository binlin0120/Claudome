using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    private bool shuttingDownVm;

    private void UpdateShutdownCommand(bool desktop)
    {
        if (desktopShutdown is null) return;
        var hasTask = Guid.TryParseExact(state.VmId, "D", out _);
        desktopShutdown.Visibility = desktop && hasTask ? Visibility.Visible : Visibility.Collapsed;
        desktopShutdown.IsEnabled = hasTask && !busy && !shuttingDownVm && !recoveringDisplay && !connectingConsole && !enteringWorkspace;
        desktopShutdown.Content = shuttingDownVm ? "正在关机…" : "关闭虚拟机并退出";
    }

    private bool ConfirmDesktopShutdown()
    {
        var dialog = new Window
        {
            Owner = this, Title = "关闭虚拟机",
            Width = 442, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Surface,
            Foreground = Ink, FontFamily = FontFamily, ShowInTaskbar = false
        };
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(Text($"请先保存「{state.Options.VmName}」中打开的文件。Claudome 会等待 Windows 正常关机，再退出这台虚拟机的控制台。", 14));
        var cancel = Action("取消", (_, _) => dialog.DialogResult = false);
        cancel.IsCancel = true;
        var confirm = Action("关机并退出", (_, _) => dialog.DialogResult = true, true);
        content.Children.Add(Row(cancel, confirm));
        dialog.Content = content;
        return dialog.ShowDialog() == true;
    }

    private async Task ShutdownDesktop()
    {
        if (desktopShutdown?.IsEnabled != true || section != 7) return;
        if (!ConfirmDesktopShutdown() || !taskSavingEnabled) return;

        shuttingDownVm = true;
        try
        {
            await Perform(async (_, token) =>
            {
                using var locked = WorkspaceLock.Acquire(state);
                RequireCurrentCheckpoint(state, JsonFile.Read<SessionState>(state.Options.StatePath));
                if (!SaveCurrentTask(showFailure: true)) return;
                desktopConnectionPolicy.Suspend();
                autoConnectDesktop = false;
                desktopConnectionCancellation?.Cancel();
                RecordTaskActivity("正在等待 Windows 正常关机…");
                await using var backend = Backends.Create(state.Options.PlatformId);
                await Backends.ShutdownOriginal(backend, state, token);

                state.Status = VmOffStatus;
                state.NetworkPassed = false;
                SaveCurrentTask();
                ReleaseDesktopWindow();
                lastConsoleWindow = null;
                lastVBoxLayout = null;
                await BoundaryRuntime.StopFor(state);
                await ExitStoppedConsole(state.Options, state.VmId);
                RecordTaskActivity(VmOffStatus);
                SetDesktopConnectionStatus(VmOffStatus);
            }, useSavedOptions: true);
        }
        finally
        {
            shuttingDownVm = false;
            UpdateDesktopActivity();
        }
    }

    private static (string Executable, string? Name, string? Config) DesktopConsoleTarget(SetupOptions options) =>
        options.PlatformId switch
        {
            "virtualbox" => (Path.Combine(Path.GetDirectoryName(Machine.VBoxPath() ??
                throw new SetupException("VirtualBox 尚未安装。"))!, "VirtualBoxVM.exe"), null, null),
            "hyperv" => (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmconnect.exe"), options.VmName, null),
            "workstation" => (Path.Combine(Workstation.DirectoryPath() ??
                throw new SetupException("VMware Workstation 尚未安装。"), "vmware.exe"),
                options.VmName, Paths.Under(options.VmDirectory, options.VmName + ".vmx")),
            _ => throw new SetupException("所选平台尚未提供控制台适配器。")
        };

    private static async Task ExitStoppedConsole(SetupOptions options, string vmId)
    {
        ConsoleWindowLocator.Forget(vmId);
        // VMware's Workstation window can host several guests. The soft stop
        // exits this guest's VM process; leave the shared manager running.
        if (options.PlatformId == "workstation") return;

        var target = DesktopConsoleTarget(options);
        await Task.Run(async () =>
        {
            var window = ConsoleWindowLocator.Find(vmId, target.Executable, out var frontendExists, target.Name, target.Config);
            if (window is not null)
                ConsoleWindowLocator.RequestClose(window, target.Name);
            for (var attempt = 0; frontendExists && attempt < 10; attempt++)
            {
                await Task.Delay(400);
                _ = ConsoleWindowLocator.Find(vmId, target.Executable, out frontendExists, target.Name, target.Config);
            }
            if (frontendExists)
                throw new SetupException("虚拟机已正常关机。控制台窗口仍在退出，可在虚拟化软件中查看。");
        });
    }
}
