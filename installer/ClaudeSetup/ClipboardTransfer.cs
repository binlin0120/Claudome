using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeSetup;

internal static class ClipboardTransfer
{
    internal const int TextLimit = 16_384;
    internal const int ReportLimit = 128 * 1024;

    internal static string RequireText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Contains('\0'))
            throw new SetupException("剪贴板中没有可传送的纯文本。");
        if (text.Length > TextLimit)
            throw new SetupException("剪贴板文本超过 16,384 个字符，请分段复制。");
        return text;
    }

    internal static string ReadResult(string path, string runId, bool toGuest)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > ReportLimit)
            throw new SetupException("未取得有效的剪贴板回读结果。");
        var result = JsonFile.Read<ClipboardResult>(path);
        if (result.RunId != runId)
            throw new SetupException("剪贴板回读与本次操作不匹配。");
        return result.Status switch
        {
            "ok" => toGuest ? "" : RequireText(result.Text),
            "no-text" => throw new SetupException("来宾剪贴板中没有纯文本。"),
            "too-long" => throw new SetupException("来宾剪贴板超过 16,384 个字符，请分段复制。"),
            "no-desktop" => throw new SetupException("请先登录任务内的 Windows 桌面，再使用剪贴板。"),
            "busy" => throw new SetupException("来宾剪贴板暂时被占用，请稍后再试。"),
            _ => throw new SetupException("来宾剪贴板操作未确认完成。")
        };
    }

    internal static async Task<string> Transfer(SessionState state, Secrets credentials, bool toGuest, string? text, CancellationToken token)
    {
        if (!state.Completed.Contains("guest-ready") || !state.Completed.Contains("guest-files"))
            throw new SetupException("请先完成来宾 Windows 初始设置，并等待来宾工具与部署资源就绪。");
        var options = state.Options;
        if (!Guid.TryParseExact(state.VmId, "D", out _))
            throw new SetupException("请先载入本任务的虚拟机。");
        options.Validate(false);
        using var workspaceLock = WorkspaceLock.Acquire(state);
        await using var backend = Backends.Create(options.PlatformId);
        await backend.EnsureMachine(state, token);
        if (!await backend.IsRunning(state, token)) throw new SetupException("请先恢复本任务的虚拟桌面。");

        var runId = Guid.NewGuid().ToString("N");
        var profile = options.GuestSetupDirectory;
        var folder = profile + @"\clipboard-" + runId;
        var powerShell = profile + @"\pwsh\pwsh.exe";
        var remote = folder + @"\ClipboardTransfer.ps1";
        using var helper = new PrivateFile(options.Private, Catalog.EmbeddedText("ClipboardTransfer.ps1"), ".ps1");
        using var request = new PrivateFile(options.Private, JsonSerializer.Serialize(new { text = toGuest ? RequireText(text) : null }), ".json");
        var report = Paths.Under(options.Private, runId + ".clipboard-result.json");
        string[] profileArguments = string.IsNullOrEmpty(options.GuestLocalAppData) ? [] : ["-LocalAppData", options.GuestLocalAppData];
        var copiedHelper = false;
        try
        {
            await backend.EnsureGuestDirectory(state, credentials, folder, token);
            await backend.CopyTo(state, credentials, helper.FileName, remote, token);
            copiedHelper = true;
            await backend.CopyTo(state, credentials, request.FileName, folder + @"\request.json", token);
            await backend.GuestRun(state, credentials, powerShell,
                ["-NoProfile", "-File", remote, "-UserName", options.UserName, "-RunId", runId, "-Direction", toGuest ? "send" : "receive", ..profileArguments], token, 35);
            await backend.CopyFrom(state, credentials, folder + @"\result.json", report, token);
            return ReadResult(report, runId, toGuest);
        }
        finally
        {
            try
            {
                if (copiedHelper)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    await backend.GuestRun(state, credentials, powerShell,
                        ["-NoProfile", "-File", remote, "-UserName", options.UserName, "-RunId", runId, "-Cleanup", ..profileArguments], cleanup.Token, 10);
                }
            }
            // The guest also removes the request as soon as it consumes it; cleanup can fail after shutdown.
            catch (Exception error) when (error is SetupException or OperationCanceledException or IOException) { }
            finally { File.Delete(report); }
        }
    }

    private sealed record ClipboardResult(string RunId, string Status, string? Text);
}

public sealed partial class SetupWindow
{
    private TextBlock? clipboardActivity;
    private Button? nativeClipboardButton;
    private TextBlock? nativeClipboardStatus;
    private DateTime nextNativeClipboardCheck;
    private bool nativeClipboardReady, checkingNativeClipboard;

    private FrameworkElement CreateClipboardControls()
    {
        clipboardActivity = Text("复制文字后选择方向，再在目标电脑粘贴。", 11);
        nativeClipboardButton = Action("启用双向剪贴板", async (_, _) => await ToggleNativeClipboard());
        nativeClipboardStatus = Text("", 11);
        var manual = new Expander
        {
            Header = "手动传送纯文本", Foreground = Ink, Background = Surface,
            Content = new StackPanel
            {
                Children =
                {
                    Action("主机 → 虚拟机", async (_, _) => await TransferClipboard(true)),
                    Action("虚拟机 → 主机", async (_, _) => await TransferClipboard(false)),
                    clipboardActivity
                }
            }
        };
        UpdateNativeClipboardControl();
        return new StackPanel { Children = { nativeClipboardButton, nativeClipboardStatus, manual } };
    }

    private void UpdateNativeClipboardControl(string? status = null)
    {
        if (nativeClipboardButton is null || nativeClipboardStatus is null) return;
        var visible = state.Options.PlatformId == "virtualbox";
        nativeClipboardButton.Visibility = nativeClipboardStatus.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        var allowed = VirtualBoxClipboard.WantsSharedClipboard(state);
        nativeClipboardButton.Content = allowed ? "关闭双向剪贴板" : "启用双向剪贴板";
        nativeClipboardStatus.Text = status ?? (allowed ? "来宾工具就绪后自动开启双向剪贴板。" : "双向剪贴板已关闭。");
    }

    private async Task RefreshDefaultNativeClipboard()
    {
        if (nativeClipboardReady || checkingNativeClipboard || closingDesktop || DateTime.UtcNow < nextNativeClipboardCheck ||
            consoleHost is not { IsAttached: true, IsVisible: true } host || !VirtualBoxClipboard.WantsSharedClipboard(state)) return;

        var original = state;
        var guestWindow = host.GuestWindow;
        bool SameDesktop() => state.VmId == original.VmId && consoleHost == host && host.IsAttached && host.GuestWindow == guestWindow;
        checkingNativeClipboard = true;
        nextNativeClipboardCheck = DateTime.UtcNow.AddSeconds(8);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(desktopConnectionCancellation?.Token ?? CancellationToken.None);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var box = new VirtualBox(new CommandRunner());
            var enabled = await VirtualBoxClipboard.RestoreDefault(box, original, timeout.Token);
            if (!SameDesktop()) return;
            nativeClipboardReady = enabled;
            if (enabled) UpdateNativeClipboardControl("双向剪贴板已开启，可直接复制粘贴。");
        }
        catch (Exception error) when (error is SetupException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (SameDesktop()) UpdateNativeClipboardControl("双向剪贴板暂未确认，助手会自动重试。");
        }
        finally { checkingNativeClipboard = false; }
    }

    private async Task ToggleNativeClipboard()
    {
        if (nativeClipboardButton is null || consoleHost?.IsAttached != true)
        {
            UpdateNativeClipboardControl("请先连接本任务的虚拟机桌面。");
            return;
        }
        if (busy || connectingConsole || checkingNativeClipboard)
        {
            UpdateNativeClipboardControl("请等待当前操作完成，再调整双向剪贴板。");
            return;
        }
        var original = state;
        var enabled = !VirtualBoxClipboard.WantsSharedClipboard(original);
        if (enabled && MessageBox.Show(this,
            "启用后，这台虚拟机与主机可互读可共享的剪贴板内容。剪贴板文件传输保持关闭。你可以随时关闭。",
            "启用双向剪贴板", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        nativeClipboardButton.IsEnabled = false;
        try
        {
            await using var box = new VirtualBox(new CommandRunner());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await VirtualBoxClipboard.Change(box, original, enabled, timeout.Token);
            if (state.VmId == original.VmId)
            {
                nativeClipboardReady = enabled;
                UpdateNativeClipboardControl(enabled ? "双向剪贴板已开启，可直接复制粘贴。" : "双向剪贴板已关闭。");
            }
        }
        catch (Exception error)
        {
            UpdateNativeClipboardControl(error is OperationCanceledException ? "剪贴板设置超时，请检查状态后重试。" : Redaction.SafeError(error));
        }
        finally { if (nativeClipboardButton is not null) nativeClipboardButton.IsEnabled = true; }
    }

    private async Task TransferClipboard(bool toGuest)
    {
        void Status(string message) { if (clipboardActivity is not null) clipboardActivity.Text = message; }
        if (busy) { Status("请等待当前任务结束，或保存并暂停后再用剪贴板。"); return; }
        if (consoleHost?.IsAttached != true) { Status("请先连接原虚拟机桌面。"); return; }
        if (!state.Completed.Contains("guest-files")) { Status("来宾尚在安装，请完成 Windows 初始设置并等待来宾工具与部署资源就绪。"); return; }
        try
        {
            var credentials = new Secrets
            {
                VmPassword = CurrentGuestPassword(),
                VmMicrosoftAddress = guestMicrosoftAddress
            };
            bool HasLogin() => state.Options.HasUsableGuestPassword(credentials.VmPassword) &&
                (!state.Options.UsesMicrosoftGuestAccount || SetupOptions.IsMicrosoftGuestAddress(credentials.VmMicrosoftAddress));
            if (!HasLogin() && state.Options.Remember && SecretStore.Load(state) is { } saved)
            {
                credentials.VmPassword = saved.VmPassword;
                credentials.VmMicrosoftAddress = saved.VmMicrosoftAddress;
            }
            if (!HasLogin()) { Status("请先在虚拟机桌面同步本任务的 Windows 账号，再使用剪贴板。"); return; }
            var text = toGuest ? ClipboardTransfer.RequireText(Clipboard.GetText(TextDataFormat.UnicodeText)) : null;
            busy = true;
            cancellation = new(TimeSpan.FromSeconds(95));
            AllowEditing(false);
            Status("正在传送纯文本…");
            var received = await ClipboardTransfer.Transfer(state, credentials, toGuest, text, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!toGuest) Clipboard.SetText(received, TextDataFormat.UnicodeText);
            Status(toGuest ? "文字已放入虚拟机剪贴板。点击右侧输入框后按 Ctrl+V。" : "文字已放入主机剪贴板，可以在主机按 Ctrl+V。");
            if (toGuest && !closeAfterPause) consoleHost?.FocusGuest();
        }
        catch (ExternalException) { Status("主机剪贴板暂时无法读取或写入，请稍后再试。"); }
        catch (OperationCanceledException) { Status("剪贴板操作已中止或超时，可以确认来宾桌面后重试。"); }
        catch (SetupException error) { Status(Redaction.SafeError(error)); }
        catch (Exception) { Status("剪贴板传送未确认完成。请确认来宾已登录、来宾工具在运行，并检查任务目录权限。"); }
        finally
        {
            busy = false; cancellation?.Dispose(); cancellation = null;
            AllowEditing(true); CompleteCloseAfterPause();
        }
    }
}
