using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeSetup;

public sealed partial class Deployment
{
    internal async Task<string> MeasureCurrentGuest(SessionState state, Secrets secrets, CancellationToken token)
    {
        var backend = BoundaryRuntime.BackendFor(state) ?? throw new SetupException("请先恢复当前来宾的受控网络。");
        using var taskLock = WorkspaceLock.Acquire(state);
        await BoundaryRuntime.VerifyCurrent(state, token);
        using var report = await ReadValidation(backend, state, secrets, false, token, guestOnly: true, measure: true);
        await BoundaryRuntime.VerifyCurrent(state, token);
        if (!report.RootElement.TryGetProperty("measurement", out var measurement) || measurement.ValueKind != JsonValueKind.Object)
            throw new SetupException("未取得来宾样本，请先检查联网和来宾权限。");
        var samples = measurement.GetProperty("samples").EnumerateArray().ToArray();
        if (samples.Length == 0) return "来宾测速未能验证，2 次短下载均未完成。";
        return $"来宾 HTTPS 首字节 {samples.Average(s => s.GetProperty("latencyMs").GetDouble()):F0} ms · " +
            $"短下载 {samples.Average(s => s.GetProperty("mbps").GetDouble()):F2} Mbps · 失败 {2 - samples.Length}/2";
    }

    internal async Task ReadProtection(SessionState state, Secrets secrets, CancellationToken token, bool guestOnly = false, bool enableBlockAudit = false)
    {
        var backend = BoundaryRuntime.BackendFor(state)
            ?? throw new SetupException("请先恢复原任务的受控网络。");
        using var taskLock = WorkspaceLock.Acquire(state);
        var checkedAt = DateTime.UtcNow;
        try
        {
            await BoundaryRuntime.VerifyCurrent(state, token);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            state.Protection.Boundary = new(CheckStatus.Failed, checkedAt, "外层网关或虚拟机通道回读失败");
            state.NetworkPassed = false;
            await BoundaryRuntime.StopFor(state);
            state.Save();
            if (!state.DeferCheckpoint) WriteProtectionReport(state);
            throw;
        }

        try
        {
            using var report = await ReadValidation(backend, state, secrets, false, token, guestOnly, enableBlockAudit: enableBlockAudit);
            ApplyProtection(state, report.RootElement, checkedAt, guestOnly);
            await BoundaryRuntime.VerifyCurrent(state, token);
            if (state.Protection.Boundary.Status == CheckStatus.Failed ||
                state.Protection.Exit.Status == CheckStatus.Failed || state.Protection.Dns.Status == CheckStatus.Failed)
                await BoundaryRuntime.StopFor(state);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var connected = BoundaryRuntime.BackendFor(state) is not null;
            if (enableBlockAudit && connected)
                throw new SetupException("未取得本轮审核结果。请确认是否在来宾中允许了管理员提示，再重新核验。");
            state.Protection.Boundary = new(connected ? CheckStatus.Unverified : CheckStatus.Failed,
                checkedAt, connected ? "没有可用的本轮来宾检查报告" : "外层网关已断开");
            state.Protection.Exit = new(CheckStatus.Unverified, checkedAt, "未取得可验证的出口记录");
            state.Protection.Dns = new(CheckStatus.Unverified, checkedAt, "未取得本轮 DNS 记录");
            state.NetworkPassed = false;
            if (!connected) await BoundaryRuntime.StopFor(state);
            messages.Report(Redaction.SafeError(ex));
        }
        state.Save();
        if (!state.DeferCheckpoint) WriteProtectionReport(state);
    }

    private static void ApplyProtection(SessionState state, JsonElement report, DateTime now, bool guestOnly)
    {
        var p = state.Protection;
        var boot = report.GetProperty("bootUtc").GetString() ?? "";
        if (p.ConfigHash != state.CoreConfigSha256 || p.BootUtc != boot ||
            (!string.IsNullOrEmpty(p.VmId) && p.VmId != state.VmId) ||
            (!string.IsNullOrEmpty(p.WorkspaceId) && p.WorkspaceId != state.Options.WorkspaceId))
            p = state.Protection = new() { WorkspaceId = state.Options.WorkspaceId, VmId = state.VmId, ConfigHash = state.CoreConfigSha256, BootUtc = boot };
        else
        {
            p.WorkspaceId = state.Options.WorkspaceId;
            p.VmId = state.VmId;
        }
        bool Flag(string key) => report.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.True;
        string Code(string key) => report.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() ?? "" : "";
        var runtimeCode = Code("runtimeCode");
        var runtimeMismatchDetail = Code("runtimeMismatchDetail");
        p.Boundary = Flag("clashRuntimePassed") ? new(CheckStatus.Passed, now, "外层与来宾有效策略已回读")
            : new(runtimeCode == "MISMATCH" ? CheckStatus.Failed : CheckStatus.Unverified, now,
                runtimeCode == "MISMATCH" && !string.IsNullOrEmpty(runtimeMismatchDetail)
                    ? "来宾有效策略不符：" + runtimeMismatchDetail
                    : "来宾有效策略待核对");
        p.Exit = ProtectionEvidence.GuestExit(report, now);
        if (!state.Options.ManualAdsPower && !guestOnly)
        {
            p.Browser = Flag("browserExitPassed") ? new(CheckStatus.Passed, now, "AdsPower 实际窗口出口已核对")
                : new(CheckStatus.Unverified, now, "AdsPower 实际窗口尚待核对");
        }
        p.BlockAuditNote = Code("blockAuditCode") switch
        {
            "ALREADY_ENABLED" => "来宾阻断审核已开启",
            "ENABLED_NOW" => "已开启并回读来宾阻断审核",
            "ENABLE_FAILED" => "未能启用来宾阻断审核，请检查管理员权限或组策略",
            "STATUS_UNKNOWN" => "未能读取来宾审核设置",
            _ => ""
        };
        var dnsDetail = "";
        if (report.TryGetProperty("dnsEvidence", out var dnsEv) && dnsEv.ValueKind == JsonValueKind.Object)
        {
            var dohProvider = dnsEv.TryGetProperty("dohProvider", out var dp) && dp.ValueKind == JsonValueKind.String ? dp.GetString() ?? "" : "";
            var dohReason = dnsEv.TryGetProperty("dohReason", out var dr) && dr.ValueKind == JsonValueKind.String ? dr.GetString() ?? "" : "";
            var physical = dnsEv.TryGetProperty("physicalProbe", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() ?? "" : "";
            var dohText = !string.IsNullOrEmpty(dohProvider) ? $"DoH({dohProvider})={dohReason}" : $"DoH={dohReason}";
            dnsDetail = $"{dohText} · 物理探针={physical}";
        }
        p.DnsEvidenceDetail = dnsDetail;
        var dnsSuffix = dnsDetail.Length > 0 ? $"（{dnsDetail}）" : "";
        p.Dns = Code("dnsCode") switch
        {
            "PASSED" => new(CheckStatus.Passed, now, "DoH 查询成功，物理上行直连 DNS 被拒绝" + dnsSuffix),
            "BYPASS" => new(CheckStatus.Failed, now, "发现可从上行尝试绕过受控通道的配置或流量" + dnsSuffix),
            "DOH_UNVERIFIED" => new(CheckStatus.Unverified, now, "加密 DNS 测试未返回有效响应" + dnsSuffix),
            "BLOCK_AUDIT_DISABLED" => new(CheckStatus.Unverified, now, "加密 DNS 已响应；来宾未开启阻断审核，可点击「启用来宾阻断审核并重测」" + dnsSuffix),
            "NO_DENIAL_EVIDENCE" => new(CheckStatus.Unverified, now, "加密 DNS 已响应，未取得直连 DNS 的阻断证据" + dnsSuffix),
            "PHYSICAL_PROBE_UNVERIFIED" => new(CheckStatus.Unverified, now, "加密 DNS 已响应，无法完成物理上行探测" + dnsSuffix),
            _ => new(CheckStatus.Unverified, now, "缺少 DNS 实测或 Windows 拒绝记录" + dnsSuffix)
        };
        if (Flag("outageTested"))
        {
            var outageStage = Code("outageStage");
            var stageLabel = outageStage switch
            {
                "precheck-trace" => "断线前基准出口检查失败",
                "stop-core" => "停核确认超时或核心被拉起",
                "outage-block-check" => "停核期间阻断校验未通过",
                "outage-unverified" => "停核期间未取得物理旁路拒绝证据",
                "restart-core" => "停核后重启 Clash 核心失败",
                "recovery-runtime" => "重启后 Clash 运行配置回读未就绪",
                "recovery-trace" => "重启后出口回读失败",
                _ => ""
            };
            var outageError = Code("outageError");
            var errPart = string.IsNullOrEmpty(outageError) ? "" : $" · 码={outageError}";
            var blockedOk = Flag("outageBlockedVerified");
            var recoveryOk = Flag("outageRecoveryVerified");
            var stageSuffix = string.IsNullOrEmpty(stageLabel) ? "" : $"（阶段：{stageLabel}{errPart} · 阻断={(blockedOk ? "通过" : "未过")} · 恢复={(recoveryOk ? "通过" : "未过")}）";
            p.Outage = Flag("outagePassed")
                ? new(CheckStatus.Passed, now, state.Options.ManualAdsPower
                    ? "来宾停核阻断及恢复通过 · AdsPower 停核待手动验收"
                    : "来宾和 AdsPower 停核阻断及恢复通过")
                : new(Code("outageCode") == "FAILED" ? CheckStatus.Failed : CheckStatus.Unverified, now,
                    "未取得足够的阻断或恢复证据" + stageSuffix);
        }
        state.NetworkPassed = p.Boundary.Status == CheckStatus.Passed && p.Exit.Status == CheckStatus.Passed &&
            p.Dns.Status != CheckStatus.Failed;
    }

    internal static void WriteAcceptanceReport(SessionState state, bool claudeConversationConfirmed = false)
    {
        var path = Paths.Under(state.Options.Root, "acceptance.json");
        var priorClaude = "not-tested";
        if (!claudeConversationConfirmed && File.Exists(path))
        {
            try
            {
                using var existing = JsonDocument.Parse(File.ReadAllText(path));
                if (existing.RootElement.TryGetProperty("claudeLoginAndConversation", out var prop) &&
                    prop.GetString() == "user-confirmed" &&
                    state.Protection.Browser.Status == CheckStatus.Passed &&
                    state.NetworkPassed)
                    priorClaude = "user-confirmed";
            }
            catch { }
        }
        var browserAcceptance = state.Protection.Browser.Status switch
        {
            CheckStatus.Passed => "user-confirmed",
            CheckStatus.Failed => "failed",
            _ => "not-tested"
        };
        JsonFile.Write(path, new
        {
            dateUtc = DateTime.UtcNow,
            vmCreated = !string.IsNullOrEmpty(state.VmId),
            guestTools = state.Completed.Contains("guest-ready"),
            softwareInstalled = state.Completed.Contains("software"),
            networkPassed = state.NetworkPassed,
            outagePassed = state.OutagePassed,
            browserRuntimePassed = state.BrowserRuntimePassed,
            browserReopenPassed = state.BrowserReopenPassed,
            translationPassed = state.TranslationPassed,
            restartPassed = state.RestartPassed,
            restartPending = state.RestartBootBefore.Length > 0,
            browserAcceptance,
            claudeLoginAndConversation = claudeConversationConfirmed ? "user-confirmed" : priorClaude,
            setupFinished = state.SetupFinished,
            status = state.Status
        });
    }

    internal static void WriteProtectionReport(SessionState state)
    {
        JsonFile.Write(Paths.Under(state.Options.Root, "protection-report.json"), new
        {
            checkedUtc = DateTime.UtcNow,
            setupFinished = state.SetupFinished,
            networkVerified = state.NetworkPassed,
            hostNetwork = NodeEndpointResolver.InspectHostNetworkContext().Summary,
            boundary = SafeCheck(state.Protection.Boundary),
            guestExit = SafeCheck(state.Protection.Exit),
            browserExit = SafeCheck(state.Protection.Browser),
            dns = SafeCheck(state.Protection.Dns),
            dnsDetail = state.Protection.DnsEvidenceDetail,
            blockAudit = state.Protection.BlockAuditNote,
            outage = SafeCheck(state.Protection.Outage)
        });
        if (!state.DeferCheckpoint && File.Exists(Paths.Under(state.Options.Root, "acceptance.json")))
            WriteAcceptanceReport(state);
    }

    private static object SafeCheck(ProtectionCheck check) =>
        new { status = check.Status.ToString(), checkedUtc = check.CheckedUtc, evidence = check.Evidence };
}

public sealed partial class SetupWindow
{
    private bool verificationRunning;

    private static string FormatCheckStatus(CheckStatus status, bool running) =>
        running ? "检查中" : status switch
        {
            CheckStatus.Passed => "通过",
            CheckStatus.Failed => "失败",
            CheckStatus.Unverified => "未能验证",
            _ => "未测试"
        };

    private static (string BgHex, string FgHex) StatusBadgeColors(CheckStatus status, bool running) =>
        running ? ("#FDF2DB", "#8A550C") : status switch
        {
            CheckStatus.Passed => ("#E6F5EE", "#176B4E"),
            CheckStatus.Failed => ("#FCEAE8", "#A32D26"),
            CheckStatus.Unverified => ("#FDF2DB", "#8A550C"),
            _ => ("#F3EFEA", "#6A5D52")
        };

    private string ProtectionSummary()
    {
        static string Show(ProtectionCheck item) => FormatCheckStatus(item.Status, false) +
            (item.CheckedUtc is { } time ? $" · {time.ToLocalTime():MM-dd HH:mm} · {item.Evidence}" : "");
        var p = state.Protection;
        var network = BoundaryRuntime.BackendFor(state) is not null && state.NetworkPassed ? "当前受控连接已核对" : "当前连接待核对";
        var hostNet = NodeEndpointResolver.InspectHostNetworkContext();
        return $"{network}\n宿主网络状态　{hostNet.Summary}\n1. 防护与运行配置　{Show(p.Boundary)}\n2. 来宾出口　{Show(p.Exit)}\n" +
            $"3. DNS 与旁路　{Show(p.Dns)}" +
            (p.BlockAuditNote.Length == 0 ? "" : $"\nWindows 阻断审核　{p.BlockAuditNote}") +
            $"\n4. 断线阻断与恢复　{Show(p.Outage)}\n手动浏览器出口确认（本人核对）　{Show(p.Browser)}";
    }

    private Border BuildCheckRow(string titleText, ProtectionCheck check, bool running, Button? recoveryAction = null, string? subtitleHint = null)
    {
        var (bg, fg) = StatusBadgeColors(check.Status, running);
        var badge = OverviewPill(FormatCheckStatus(check.Status, running), bg, fg);
        var dock = new DockPanel { LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(badge);
        if (recoveryAction is not null)
        {
            recoveryAction.Padding = new Thickness(10, 4, 10, 4);
            recoveryAction.FontSize = 11.5;
            recoveryAction.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(recoveryAction);
        }
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);

        var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        left.Children.Add(new TextBlock { Text = titleText, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ink });
        var detail = !string.IsNullOrWhiteSpace(check.Evidence)
            ? check.Evidence + (check.CheckedUtc is { } t ? $"（{t.ToLocalTime():MM-dd HH:mm}）" : "")
            : (subtitleHint ?? "尚未运行本次检查。");
        left.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        dock.Children.Add(left);

        return new Border
        {
            Background = Pale,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 7),
            Child = dock
        };
    }

    private async Task StartOneClickVerificationAsync()
    {
        if (busy || verificationRunning) return;
        if (BoundaryRuntime.BackendFor(state) is null)
        {
            Notice("请先在「专属桌面」恢复本任务的受控连接，再点击「开始验真」。");
            return;
        }
        var choice = MessageBox.Show(this,
            "即将按顺序执行 4 项自动安全验真：\n" +
            "1. 防护与运行配置\n" +
            "2. 来宾出口\n" +
            "3. DNS 与旁路\n" +
            "4. 断线阻断与恢复（将短暂停止来宾代理几秒后自动恢复）\n\n" +
            "是否允许短暂断网以一次完成全部 4 项？\n" +
            "· 点击「是」：完整顺序运行全部 4 项\n" +
            "· 点击「否」：先运行前 3 项，第 4 项记为「未测试」可稍后补测\n" +
            "· 点击「取消」：暂不开始",
            "开始安全验真", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        await RunOneClickVerificationAsync(includeOutage: choice == MessageBoxResult.Yes);
    }

    private async Task RunOneClickVerificationAsync(bool includeOutage)
    {
        if (busy || verificationRunning) return;
        verificationRunning = true;
        if (section == 5) Navigate(5);
        try
        {
            await RunValidation(restart: false, outageTest: includeOutage);
        }
        finally
        {
            verificationRunning = false;
            if (section == 5 && !closeAfterPause) Navigate(5);
        }
    }

    private Border BuildProtectionCard()
    {
        var p = state.Protection;
        var connected = BoundaryRuntime.BackendFor(state) is not null;
        var hostNet = NodeEndpointResolver.InspectHostNetworkContext();
        var netPending = p.PendingNetworkChecks(state);
        var summaryText = !connected
            ? $"当前受控连接未激活 · 宿主网络：{hostNet.Summary}"
            : netPending.Length == 0
                ? $"4 项自动网络验真已全部通过 · 宿主网络：{hostNet.Summary}"
                : $"待完成自动验真：{string.Join("、", netPending)} · 宿主网络：{hostNet.Summary}";

        var startBtn = Action(verificationRunning ? "检查中…" : "开始验真", async (_, _) => await StartOneClickVerificationAsync(), true);
        startBtn.Name = "StartVerificationButton";
        startBtn.IsEnabled = !busy && !verificationRunning;

        var topDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(startBtn, Dock.Right);
        topDock.Children.Add(startBtn);
        var headStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        headStack.Children.Add(Text("一键四维安全验真", 16, false));
        headStack.Children.Add(new TextBlock { Text = summaryText, FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        topDock.Children.Add(headStack);

        Button? boundaryRecovery = p.Boundary.Status == CheckStatus.Passed ? null :
            Action("重新配置本任务网络", async (_, _) => await ReconfigureGuestNetworkWithConfirmation());
        Button? exitRecovery = p.Exit.Status == CheckStatus.Passed ? null :
            Action("重试失败项", async (_, _) => await RunOneClickVerificationAsync(includeOutage: false));
        Button? dnsRecovery = p.Dns.Status == CheckStatus.Passed ? null :
            (p.Dns.Evidence.Contains("阻断审核")
                ? Action("启用来宾阻断审核并重测", async (_, _) => await EnableBlockAuditAndRetest())
                : Action("重试失败项", async (_, _) => await RunOneClickVerificationAsync(includeOutage: false)));
        Button? outageRecovery = p.Outage.Status == CheckStatus.Passed ? null :
            Action("补测断线阻断", async (_, _) =>
            {
                if (MessageBox.Show(this, "断线阻断与恢复检查会短暂停止来宾代理几秒，再自动恢复（不修改任务配置）。继续吗？",
                    "补测断线阻断", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    await RunOneClickVerificationAsync(includeOutage: true);
            });

        var autoRows = new StackPanel();
        autoRows.Children.Add(BuildCheckRow("1. 防护与运行配置", p.Boundary, verificationRunning, boundaryRecovery, "核对外层网关、总线隔离与来宾 Clash/防火墙生效策略。"));
        autoRows.Children.Add(BuildCheckRow("2. 来宾出口", p.Exit, verificationRunning, exitRecovery, "核对虚拟机实际公网出口与任务地区/IP 设置是否一致。"));
        autoRows.Children.Add(BuildCheckRow("3. DNS 与旁路", p.Dns, verificationRunning, dnsRecovery, "核对 DoH 加密解析与物理上行直连 DNS 拒绝证据。"));
        autoRows.Children.Add(BuildCheckRow("4. 断线阻断与恢复", p.Outage, verificationRunning, outageRecovery, "停核期间阻断直连泄露，并验证重启后完整恢复。"));

        var browserConfirmBtn = Action("确认浏览器出口", (_, _) => ConfirmBrowserExit(), p.Browser.Status != CheckStatus.Passed);
        var finishBtn = Action("完成设置", (_, _) => FinishWorkspace(), IsProtectionReadyForFinish());
        finishBtn.IsEnabled = !busy && !verificationRunning;
        var browserRow = BuildCheckRow(
            "手动浏览器出口确认（由本人核对 · 非自动验证）",
            p.Browser,
            false,
            browserConfirmBtn,
            "请在虚拟机内的 AdsPower 打开 https://ip.net.coffee/claude/ 核对出口一致后点击确认。");

        var advancedContent = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        advancedContent.Children.Add(Text("以下为高级排障与验收工具；重启复验作为独立验收记录保存。", 11));
        advancedContent.Children.Add(Row(
            Action("启用来宾阻断审核并重测", async (_, _) => await EnableBlockAuditAndRetest()),
            Action("查看脱敏报告", (_, _) =>
            {
                try
                {
                    var path = Paths.Under(state.Options.Root, "protection-report.json");
                    if (!File.Exists(path)) throw new SetupException("请先运行「开始验真」。");
                    Notice(File.ReadAllText(path));
                }
                catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
            }),
            Action("重启后持久复核", async (_, _) =>
            {
                if (MessageBox.Show(this, "请先保存虚拟机中的工作。将请求正常重启这台任务虚拟机；重新登录 Windows 后检查持久配置和实际网络，检查前不会修复配置。保持助手开启。继续？",
                    "重启验收", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    await RunValidation(true, false);
            }),
            Action("导出客户使用手册", (_, _) => ExportCustomerGuideFile())));
        if (p.BlockAuditNote.Length > 0)
            advancedContent.Children.Add(Text("Windows 阻断审核：" + p.BlockAuditNote, 11));

        var advancedExpander = new Expander
        {
            Name = "AdvancedChecksExpander",
            Header = "高级检查（脱敏报告 · 阻断审核 · 重启复验）",
            IsExpanded = false,
            Foreground = Ink,
            Margin = new Thickness(0, 6, 0, 0),
            Content = advancedContent
        };

        return Card(topDock, autoRows, browserRow, Row(finishBtn, Action("进入桌面", (_, _) => Navigate(7))), advancedExpander);
    }

    private void ExportCustomerGuideFile()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出客户使用手册",
                Filter = "Markdown 文档|*.md|文本文档|*.txt",
                FileName = $"Claudome-客户使用手册-{state.Options.VmName}.md"
            };
            if (dialog.ShowDialog() != true) return;
            var lines = new List<string>
            {
                $"# {ProductProfile.WindowTitle} 客户使用手册（{state.Options.VmName}）",
                "",
                $"- 任务地区 / 时区：{CustomerGuide.Country(state.Options.Country)} / {state.Options.TimeZoneId}",
                $"- 当前验收摘要：{(state.Protection.FourDimensionsPassed(state) ? "四维受控验收已通过" : "尚有待核验项：" + string.Join("、", state.Protection.PendingChecks(state)))}",
                "",
                "## 日常操作与排查步骤"
            };
            foreach (var step in CustomerGuide.Software(state.Options))
            {
                lines.Add($"### {step.Title}");
                lines.Add(step.Instructions);
                lines.Add("");
            }
            lines.Add("## 重要提示：conhost / 命令行终端运行 Claude Code / CLI");
            lines.Add("若通过 conhost、PowerShell 或 cmd 等命令行窗口运行 Claude Code / CLI 工具，需要由本人在对应终端窗口手动配置代理环境变量（不会由 AdsPower 指纹浏览器自动接管）。");
            File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, lines), new System.Text.UTF8Encoding(false));
            Notice("已导出客户使用手册：\n" + dialog.FileName);
        }
        catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
    }

    private async Task EnableBlockAuditAndRetest()
    {
        if (busy) return;
        if (BoundaryRuntime.BackendFor(state) is null)
        {
            Notice("请先在「虚拟机桌面 → 检查与维护」点击「一键连接网络」。");
            return;
        }
        if (MessageBox.Show(this, "在此任务的虚拟机内开启「筛选平台连接」的失败审核，然后重测 DNS。Windows 将记录被拒绝的连接；如来宾弹出管理员提示，请确认。",
            "启用来宾阻断审核", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var checkedNow = false;
        await Perform(async (_, token) =>
        {
            await Engine().ReadProtection(state, AppliedLogin(), token, guestOnly: true, enableBlockAudit: true);
            checkedNow = true;
        }, useSavedOptions: true);
        if (closeAfterPause) return;
        Navigate(5);
        if (checkedNow) Notice(ProtectionSummary());
    }

    private void SaveProtectionChange(Action mutation)
    {
        using var taskLock = WorkspaceLock.Acquire(state);
        RequireCurrentCheckpoint(state, JsonFile.Read<SessionState>(state.Options.StatePath));
        var snapshot = Clone(state);
        try
        {
            mutation();
            Deployment.WriteProtectionReport(state);
            if (!state.DeferCheckpoint) Deployment.WriteAcceptanceReport(state);
            state.Save();
        }
        catch
        {
            state = snapshot;
            throw;
        }
    }

    private static string FormatCompletionHint(SessionState task, bool connected)
    {
        if (!connected) return "先连上原任务的受控网络，再确认出口和完成设置。";
        var pending = task.Protection.PendingChecks(task);
        return pending.Length == 0 ? "核验已通过，可以完成设置。" : "待确认：" + string.Join("、", pending) + "。可在「安全验真」查看。";
    }

    private void ConfirmBrowserExit()
    {
        if (busy) return;
        try
        {
            if (BoundaryRuntime.BackendFor(state) is null || !state.NetworkPassed ||
                state.Protection.Exit.Status != CheckStatus.Passed || state.Protection.ConfigHash != state.CoreConfigSha256)
                throw new SetupException("请先运行「核验当前防护」，确认当前来宾出口通过。");
            var fixedIp = AppliedLogin().ExpectedExitIp;
            if (MessageBox.Show(this, $"请在实际使用的 AdsPower 窗口核对出口地区 {state.Options.Country}" +
                (fixedIp.Length > 0 ? $" 与固定 IPv4 {fixedIp}" : "") +
                "。只有已打开检测页并确认一致时才继续。", "确认浏览器出口", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;
            SaveProtectionChange(() =>
            {
                state.Protection.WorkspaceId = state.Options.WorkspaceId;
                state.Protection.VmId = state.VmId;
                state.Protection.Browser = new(CheckStatus.Passed, DateTime.UtcNow, "用户确认实际 AdsPower 窗口出口");
                state.PendingActions.RemoveAll(action => action.Id == CustomerGuide.ManualAdsPowerId);
            });
            if (section == 7) UpdateDesktopGuide();
            else Navigate(5);
        }
        catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
    }

    private void FinishWorkspace()
    {
        if (busy) return;
        try
        {
            var connected = BoundaryRuntime.BackendFor(state) is not null;
            if (!connected || state.Protection.PendingChecks(state).Length > 0)
                throw new SetupException(FormatCompletionHint(state, connected));
            SaveProtectionChange(() =>
            {
                state.SetupFinished = true;
                state.Stage = "已完成设置，可进入虚拟机";
                state.Status = "设置已完成";
            });
            Navigate(2);
        }
        catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
    }
}
