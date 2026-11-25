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
        p.Boundary = Flag("clashRuntimePassed") ? new(CheckStatus.Passed, now, "外层与来宾有效策略已回读")
            : new(runtimeCode == "MISMATCH" ? CheckStatus.Failed : CheckStatus.Unverified, now, "来宾有效策略待核对");
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
        p.Dns = Code("dnsCode") switch
        {
            "PASSED" => new(CheckStatus.Passed, now, "DoH 查询成功，物理上行直连 DNS 被拒绝"),
            "BYPASS" => new(CheckStatus.Failed, now, "发现可从上行尝试绕过受控通道的配置或流量"),
            "DOH_UNVERIFIED" => new(CheckStatus.Unverified, now, "加密 DNS 测试未返回有效响应"),
            "BLOCK_AUDIT_DISABLED" => new(CheckStatus.Unverified, now, "加密 DNS 已响应；来宾未开启阻断审核，可点击「启用来宾阻断审核并重测」"),
            "NO_DENIAL_EVIDENCE" => new(CheckStatus.Unverified, now, "加密 DNS 已响应，未取得直连 DNS 的阻断证据"),
            "PHYSICAL_PROBE_UNVERIFIED" => new(CheckStatus.Unverified, now, "加密 DNS 已响应，无法完成物理上行探测"),
            _ => new(CheckStatus.Unverified, now, "缺少 DNS 实测或 Windows 拒绝记录")
        };
        if (Flag("outageTested"))
        {
            p.Outage = Flag("outagePassed")
                ? new(CheckStatus.Passed, now, state.Options.ManualAdsPower
                    ? "来宾停核阻断及恢复通过 · AdsPower 停核待手动验收"
                    : "来宾和 AdsPower 停核阻断及恢复通过")
                : new(Code("outageCode") == "FAILED" ? CheckStatus.Failed : CheckStatus.Unverified, now,
                    "未取得足够的阻断或恢复证据");
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
            boundary = SafeCheck(state.Protection.Boundary),
            guestExit = SafeCheck(state.Protection.Exit),
            browserExit = SafeCheck(state.Protection.Browser),
            dns = SafeCheck(state.Protection.Dns),
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
    private string ProtectionSummary()
    {
        static string Show(ProtectionCheck item) => item.Status switch
        {
            CheckStatus.Passed => "通过",
            CheckStatus.Failed => "失败",
            CheckStatus.Unverified => "未能验证",
            _ => "未测试"
        } + (item.CheckedUtc is { } time ? $" · {time.ToLocalTime():MM-dd HH:mm} · {item.Evidence}" : "");
        var p = state.Protection;
        var network = BoundaryRuntime.BackendFor(state) is not null && state.NetworkPassed ? "当前受控连接已核对" : "当前连接待核对";
        return $"{network}\n外层网关与来宾防火墙　{Show(p.Boundary)}\n来宾出口　{Show(p.Exit)}\n" +
            $"AdsPower 窗口出口　{Show(p.Browser)}\nDNS 与直连旁路　{Show(p.Dns)}" +
            (p.BlockAuditNote.Length == 0 ? "" : $"\nWindows 阻断审核　{p.BlockAuditNote}") +
            $"\n断线阻断与恢复　{Show(p.Outage)}";
    }

    private Border BuildProtectionCard() => Card(Text("防护验收", 16, false),
        Text(ProtectionSummary(), 12),
        Row(Action("核验当前防护", async (_, _) => { await RunValidation(false, false); Navigate(5); }, true),
            Action("确认浏览器出口", (_, _) => ConfirmBrowserExit()),
            Action("完成设置", (_, _) => FinishWorkspace())),
        Row(Action("启用来宾阻断审核并重测", async (_, _) => await EnableBlockAuditAndRetest()),
            Action("查看脱敏报告", (_, _) =>
        {
            try
            {
                var path = Paths.Under(state.Options.Root, "protection-report.json");
                if (!File.Exists(path)) throw new SetupException("请先核验当前防护。");
                Notice(File.ReadAllText(path));
            }
            catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
        }),
            Action("导出客户使用手册", (_, _) => ExportCustomerGuideFile())));

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
        return pending.Length == 0 ? "核验已通过，可以完成设置。" : "待确认：" + string.Join("、", pending) + "。可在「防护结果」查看。";
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
