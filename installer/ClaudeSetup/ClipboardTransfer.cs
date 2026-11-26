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

    private bool WantsNativeClipboard(SessionState target) => target.Options.PlatformId switch
    {
        "virtualbox" => VirtualBoxClipboard.WantsSharedClipboard(target),
        "hyperv" => target.Options.SharedClipboardEnabled && !target.Options.HyperVBasicConsoleConfirmed,
        _ => false
    };

    private void UpdateNativeClipboardControl(string? status = null)
    {
        if (nativeClipboardButton is null || nativeClipboardStatus is null) return;
        var platformId = state.Options.PlatformId;
        var visible = platformId is "virtualbox" or "hyperv";
        nativeClipboardButton.Visibility = nativeClipboardStatus.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        var allowed = WantsNativeClipboard(state);
        nativeClipboardButton.Content = allowed ? "关闭双向剪贴板" : "启用双向剪贴板";
        nativeClipboardStatus.Text = status ?? (platformId == "hyperv"
            ? (allowed
                ? (nativeClipboardReady
                    ? "已核验：Hyper-V 增强会话双向剪贴板（HvSocket）与来宾隔离策略均已生效，可直接 Ctrl+C / Ctrl+V。"
                    : "已设为默认启用 Hyper-V 增强会话双向剪贴板（待第 6 步来宾策略及 HvSocket 会话核验通过后生效；磁盘/USB 保持隔离）。")
                : "Hyper-V 双向剪贴板已关闭（基本会话模式），可使用下方按钮手动传送纯文本。")
            : (allowed ? "来宾工具就绪后自动开启双向剪贴板。" : "双向剪贴板已关闭。"));
    }

    internal static bool IsHyperVClipboardVerified(bool guestReadyCompleted, bool hostEnhanced, bool hvSocket, bool guestPolicyVerified, bool consoleAttached) =>
        guestReadyCompleted && hostEnhanced && hvSocket && guestPolicyVerified && consoleAttached;

    private async Task RefreshDefaultNativeClipboard()
    {
        if (nativeClipboardReady || checkingNativeClipboard || closingDesktop || DateTime.UtcNow < nextNativeClipboardCheck) return;
        if (state.Options.PlatformId == "hyperv")
        {
            if (string.IsNullOrEmpty(state.VmId) || !WantsNativeClipboard(state)) return;
            if (consoleHost is not { IsAttached: true } hvHost) return;
            var hvOriginal = state;
            var hvGuestWindow = hvHost.GuestWindow;
            bool SameHvDesktop() => state.VmId == hvOriginal.VmId && consoleHost == hvHost && hvHost.IsAttached && hvHost.GuestWindow == hvGuestWindow;

            if (!hvOriginal.Completed.Contains("guest-ready"))
            {
                nextNativeClipboardCheck = DateTime.UtcNow.AddSeconds(10);
                UpdateNativeClipboardControl("待核验：正在系统安装阶段（第 6 步写入并回读来宾剪贴板与隔离策略后生效，当前可使用手动传送纯文本）。");
                return;
            }
            checkingNativeClipboard = true;
            nextNativeClipboardCheck = DateTime.UtcNow.AddSeconds(12);
            try
            {
                await using var hv = new HyperV();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(desktopConnectionCancellation?.Token ?? CancellationToken.None);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                var guestVerified = await TrySyncHyperVGuestClipboardPolicy(hv, hvOriginal, true, timeout.Token);
                if (!SameHvDesktop()) return;
                if (!guestVerified)
                {
                    nativeClipboardReady = false;
                    UpdateNativeClipboardControl("待核验：未能确认来宾剪贴板与设备重定向隔离策略（磁盘/USB 仍保持阻断），暂不标记可复制粘贴；可重试或使用手动纯文本传送。");
                    return;
                }
                await hv.SetEnhancedSessionClipboard(hvOriginal, true, timeout.Token);
                var hostReadback = await hv.ReadClipboardHostStatus(hvOriginal, timeout.Token);
                if (!SameHvDesktop()) return;
                nativeClipboardReady = IsHyperVClipboardVerified(
                    hvOriginal.Completed.Contains("guest-ready"),
                    hostReadback.HostEnhanced,
                    hostReadback.HvSocket,
                    guestVerified,
                    hvHost.IsAttached);
                UpdateNativeClipboardControl(nativeClipboardReady
                    ? "已核验：Hyper-V 增强会话（HvSocket）与来宾 8 项隔离策略已生效，可直接 Ctrl+C / Ctrl+V 复制粘贴。"
                    : "待核验：宿主机增强会话或 HvSocket 传输尚未就绪，暂保持基本会话或待重连复核（可使用手动纯文本传送）。");
            }
            catch (Exception error) when (error is SetupException or IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                if (SameHvDesktop())
                {
                    nativeClipboardReady = false;
                    UpdateNativeClipboardControl("待核验：暂未确认 Hyper-V 双向剪贴板状态，稍后自动复核（可使用手动纯文本传送）。");
                }
            }
            finally { checkingNativeClipboard = false; }
            return;
        }
        if (state.Options.PlatformId != "virtualbox") return;
        if (consoleHost is not { IsAttached: true, IsVisible: true } host || !VirtualBoxClipboard.WantsSharedClipboard(state)) return;

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
        if (nativeClipboardButton is null) return;
        var original = state;
        if (original.Options.PlatformId != "hyperv" && consoleHost?.IsAttached != true)
        {
            UpdateNativeClipboardControl("请先连接本任务的虚拟机桌面。");
            return;
        }
        if (original.Options.PlatformId == "hyperv" && string.IsNullOrEmpty(original.VmId))
        {
            UpdateNativeClipboardControl("请先创建或载入本任务的 Hyper-V 虚拟机。");
            return;
        }
        if (busy || connectingConsole || checkingNativeClipboard)
        {
            UpdateNativeClipboardControl("请等待当前操作完成，再调整双向剪贴板。");
            return;
        }
        var enabled = !WantsNativeClipboard(original);
        if (original.Options.PlatformId == "hyperv")
        {
            var prompt = enabled
                ? "启用后，将开启 Hyper-V 增强会话以支持直接复制粘贴（来宾内仍禁止磁盘、USB 与设备重定向）。注意主机会话模式会影响同机其他 Hyper-V 虚拟机，重连控制台即可生效。"
                : "关闭后，将切回 Hyper-V 基本会话并立即禁用来宾剪贴板同步（重启或重连控制台后恢复纯基本会话）。注意主机会话模式会影响同机其他 Hyper-V 虚拟机。";
            if (MessageBox.Show(this, prompt, enabled ? "启用 Hyper-V 双向剪贴板" : "关闭 Hyper-V 双向剪贴板",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        }
        else if (enabled && MessageBox.Show(this,
            "启用后，这台虚拟机与主机可互读可共享的剪贴板内容。剪贴板文件传输保持关闭。你可以随时关闭。",
            "启用双向剪贴板", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        nativeClipboardButton.IsEnabled = false;
        try
        {
            if (original.Options.PlatformId == "hyperv")
            {
                await using var hv = new HyperV();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await hv.SetEnhancedSessionClipboard(original, enabled, timeout.Token);
                var hostReadback = await hv.ReadClipboardHostStatus(original, timeout.Token);
                var guestSynced = await TrySyncHyperVGuestClipboardPolicy(hv, original, enabled, timeout.Token);
                if (state.VmId == original.VmId)
                {
                    state.Options.SharedClipboardEnabled = enabled;
                    state.Options.HyperVBasicConsoleConfirmed = !enabled;
                    state.Save();
                    nativeClipboardReady = enabled && IsHyperVClipboardVerified(
                        state.Completed.Contains("guest-ready"),
                        hostReadback.HostEnhanced,
                        hostReadback.HvSocket,
                        guestSynced,
                        consoleHost?.IsAttached == true);
                    UpdateNativeClipboardControl(enabled
                        ? (nativeClipboardReady
                            ? "已核验：已开启 Hyper-V 增强会话双向剪贴板并确认来宾隔离策略，重连桌面或直接 Ctrl+C / Ctrl+V。"
                            : guestSynced && hostReadback.HostEnhanced && hostReadback.HvSocket
                                ? "已配置并核对宿主与来宾策略，重连或进入桌面会话后即可复制粘贴。"
                                : "已写入增强会话选项，但来宾策略或 HvSocket 会话尚未完全核验（待进入第 6 步或重连桌面复核）。")
                        : (guestSynced ? "已关闭 Hyper-V 双向剪贴板并回读确认来宾同步已禁用。" : "已关闭 Hyper-V 增强会话剪贴板（重连或重启桌面后切换为基本会话）。"));
                }
            }
            else
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
        }
        catch (Exception error)
        {
            UpdateNativeClipboardControl(error is OperationCanceledException ? "剪贴板设置超时，请检查状态后重试。" : Redaction.SafeError(error));
        }
        finally { if (nativeClipboardButton is not null) nativeClipboardButton.IsEnabled = true; }
    }

    private async Task<bool> TrySyncHyperVGuestClipboardPolicy(HyperV hv, SessionState target, bool enabled, CancellationToken token)
    {
        if (!target.Completed.Contains("guest-ready")) return false;
        var credentials = new Secrets { VmPassword = CurrentGuestPassword(), VmMicrosoftAddress = guestMicrosoftAddress };
        if (!target.Options.HasUsableGuestPassword(credentials.VmPassword) && target.Options.Remember && SecretStore.Load(target) is { } saved)
            credentials.VmPassword = saved.VmPassword;
        if (!target.Options.HasUsableGuestPassword(credentials.VmPassword)) return false;
        try
        {
            if (!await hv.IsRunning(target, token)) return false;
            var clipValue = enabled ? 0 : 1;
            var stopClip = enabled ? "" : "; Get-Process -Name rdpclip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue";
            var cmd = "$p='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\Terminal Services';" +
                "if(-not(Test-Path -LiteralPath $p)){New-Item -Path $p -Force|Out-Null};" +
                $"$expected=@{{fDisableClip={clipValue};fDisableCdm=1;fDisableLPT=1;fDisableCam=1;fDisablePNPRedir=1;fDisableAudio=1;fDisableAudioCapture=1;fDisableCcm=1}};" +
                "$expected.GetEnumerator()|ForEach-Object{New-ItemProperty -Path $p -Name $_.Key -PropertyType DWord -Value ([int]$_.Value) -Force|Out-Null};" +
                stopClip +
                "$props=Get-ItemProperty -LiteralPath $p;" +
                "$ok=$true;foreach($k in $expected.Keys){if($null -eq $props.$k -or [int]$props.$k -ne [int]$expected[$k]){$ok=$false}};" +
                "if($ok){'CLIP_POLICY_OK'}else{'CLIP_POLICY_MISMATCH'}";
            var output = await hv.GuestRun(target, credentials, "powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", cmd], token, 30);
            return output.Contains("CLIP_POLICY_OK", StringComparison.Ordinal);
        }
        catch (Exception error) when (error is SetupException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
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
