using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    private Secrets? appliedSecrets;
    private readonly Dictionary<ProxyNode, string> routeMeasurements = new();
    private readonly TextBlock routeSummary = Text("", 12);
    private bool enteringWorkspace;

    private void RememberAppliedSecrets(Secrets secrets)
    {
        appliedSecrets = secrets;
        UpdateRouteSummary();
    }

    private Secrets AppliedLogin()
    {
        var saved = appliedSecrets ?? new Secrets();
        // Credentials may be entered again when the user declines local storage.
        return new Secrets
        {
            VmPassword = string.IsNullOrEmpty(CurrentGuestPassword()) ? saved.VmPassword : CurrentGuestPassword(),
            VmMicrosoftAddress = string.IsNullOrEmpty(guestMicrosoftAddress) ? saved.VmMicrosoftAddress : guestMicrosoftAddress,
            AdsPowerApiKey = string.IsNullOrEmpty(adsApiKey.Password) ? saved.AdsPowerApiKey : adsApiKey.Password,
            ExpectedExitIp = saved.ExpectedExitIp,
            SelectedNodeJson = saved.SelectedNodeJson,
            UpstreamNodeJson = saved.UpstreamNodeJson,
            SubscriptionUrl = saved.SubscriptionUrl,
            NetworkText = saved.NetworkText,
            UpstreamSubscriptionUrl = saved.UpstreamSubscriptionUrl,
            UpstreamNetworkText = saved.UpstreamNetworkText,
            BackupRoutes = [.. saved.BackupRoutes]
        };
    }

    private Border BuildRouteActions() => Card(Text("当前线路与配置草稿", 16, false),
        routeSummary,
        Text("编辑、刷新和测速不会切换连接。现有环境在点击「应用线路」后才更新。", 11),
        Row(Action("应用线路", async (_, _) => await ApplyRouteFromPage(), true),
            Action("多选线路 / 测速", (_, _) => SelectBackupRoutes()),
            Action("已保存的备用线路", async (_, _) => await PickSavedRoute()),
            Action("测速当前来宾", async (_, _) => await MeasureCurrentGuest())));

    private Task MeasureCurrentGuest() => Perform(async (_, token) =>
    {
        var result = await Engine().MeasureCurrentGuest(state, AppliedLogin(), token);
        Notice(result);
    }, useSavedOptions: true);

    private void UpdateRouteSummary()
    {
        routeSummary.Text = string.IsNullOrEmpty(state.Options.NodeName)
            ? "当前未保存主用线路"
            : $"当前线路：{state.Options.NodeName} · 备用 {appliedSecrets?.BackupRoutes.Count ?? 0} 条";
    }

    private async Task ApplyRouteFromPage()
    {
        if (busy) return;
        if (!state.Completed.Contains("software")) { Go(5); return; }
        if (MessageBox.Show(this, "应用时虚拟机网络可能短暂中断。准备好后继续？",
            "应用线路", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        consent.IsChecked = true;
        await StartInstall(applyRoute: true);
    }

    private static ProxyNode StoredNode(string json) => NetworkImport.Parse("{\"proxies\":[" + json + "]}").Nodes.Single();
    private static bool SameRoute(SavedRoute a, SavedRoute b) => a.NodeJson == b.NodeJson && a.UpstreamJson == b.UpstreamJson;

    private SavedRoute CandidateRoute(ProxyNode selected)
    {
        var first = chain.IsChecked == true ? upstreamNode.SelectedItem as ProxyNode : selected.Upstream;
        if (chain.IsChecked == true && (first is null || upstreamImported != UpstreamFingerprint()))
            throw new SetupException("请先重新读取并选择前置节点。");
        if (first?.Upstream is not null) throw new SetupException("只支持一个前置节点。");
        return new(selected.Json, first?.Json ?? "", country.SelectedValue?.ToString() ?? "", timeZone.SelectedValue?.ToString() ?? "");
    }

    private void SelectBackupRoutes()
    {
        if (busy) return;
        if (nodes.Count == 0) { Notice("请先读取节点列表。"); return; }
        var items = nodes.ToArray();
        var measured = routeMeasurements;
        var list = new ListBox { SelectionMode = SelectionMode.Extended, MinHeight = 240, MaxHeight = 410 };
        var updated = false;
        void Populate(IEnumerable<ProxyNode> reselect)
        {
            var previous = reselect.ToHashSet();
            list.Items.Clear();
            foreach (var item in items)
            {
                var row = new ListBoxItem()
                {
                    Content = $"{item.Name} · {item.Type} · {item.Server}:{item.Port}" +
                        (measured.TryGetValue(item, out var value) ? Environment.NewLine + value : ""),
                    Tag = item
                };
                list.Items.Add(row);
                row.IsSelected = previous.Contains(item);
            }
        }
        ProxyNode[] Checked() => list.SelectedItems.Cast<ListBoxItem>().Select(row => (ProxyNode)row.Tag).ToArray();
        var savedRoutes = pendingBackupRoutes ?? appliedSecrets?.BackupRoutes ?? [];
        Populate(items.Where(item => savedRoutes.Any(r => r.NodeJson == item.Json)));
        var dialog = new Window { Owner = this, Title = "多选线路", Width = 730, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(16) };
        var hint = Text("按住 Ctrl 多选，最多 8 条。测速通过主机上的临时代理，当前虚拟机连接会保留。", 12);
        panel.Children.Add(hint);
        panel.Children.Add(list);
        using var probeCancel = new CancellationTokenSource();
        var measuring = false;
        dialog.Closing += (_, e) =>
        {
            if (!measuring) return;
            e.Cancel = true;
            probeCancel.Cancel();
        };
        var measure = Action("测速选中线路", async (_, _) =>
        {
            if (measuring) return;
            var chosen = Checked();
            if (chosen.Length is < 1 or > 8) { hint.Text = "请选 1–8 条线路。"; return; }
            measuring = true;
            panel.IsEnabled = false;
            try
            {
                foreach (var item in chosen)
                {
                    var route = CandidateRoute(item);
                    hint.Text = $"正在测量 {item.Name}…";
                    try
                    {
                        var speed = await NodeExitProbe.MeasureAsync(state.Options, item,
                            string.IsNullOrEmpty(route.UpstreamJson) ? null : StoredNode(route.UpstreamJson), probeCancel.Token);
                        measured[item] = $"{DateTime.Now:HH:mm} · {speed}";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { measured[item] = "未能验证 · " + Redaction.SafeError(ex); }
                }
                hint.Text = "测量完成。来宾测速可在应用线路后执行。";
            }
            catch (OperationCanceledException) { hint.Text = "已停止测速。"; }
            catch (Exception ex) { hint.Text = Redaction.SafeError(ex); }
            finally { measuring = false; panel.IsEnabled = true; Populate(chosen); }
        });
        panel.Children.Add(Row(measure, Action("保存为备用", (_, _) =>
        {
            var chosen = Checked();
            if (chosen.Length > 8) { hint.Text = "最多保存 8 条备用线路。"; return; }
            try
            {
                var candidates = chosen.Where(item => !ReferenceEquals(item, node.SelectedItem)).Select(CandidateRoute);
                var absent = savedRoutes.Where(route => !items.Any(item => route.NodeJson == item.Json));
                var routes = candidates.Concat(absent).Distinct().ToList();
                if (routes.Count > 8) throw new SetupException("加上已保存的线路后超过 8 条，请先删减备用线路。");
                if (state.Completed.Contains("software") && appliedSecrets is { } saved)
                {
                    routes.RemoveAll(item => item.NodeJson == saved.SelectedNodeJson && item.UpstreamJson == saved.UpstreamNodeJson);
                    SaveBackupRoutes(routes);
                }
                else pendingBackupRoutes = routes;
                updated = true;
                dialog.DialogResult = true;
            }
            catch (Exception ex) { hint.Text = Redaction.SafeError(ex); }
        }), Action("返回", (_, _) => dialog.Close())));
        dialog.Content = panel;
        if (dialog.ShowDialog() == true && updated)
            Notice("备用线路列表已更新。实际切换前会重新核验出口。");
    }

    private List<SavedRoute>? pendingBackupRoutes;

    private void SaveBackupRoutes(List<SavedRoute> routes)
    {
        using var taskLock = WorkspaceLock.Acquire(state);
        var disk = JsonFile.Read<SessionState>(state.Options.StatePath);
        RequireCurrentCheckpoint(state, disk);
        var saved = AppliedLogin();
        saved.BackupRoutes = routes;
        if (state.Options.Remember)
        {
            if (string.IsNullOrEmpty(saved.SelectedNodeJson)) throw new SetupException("请先载入当前线路或应用主用线路。");
            SecretStore.Save(state.Options.Private, saved, state.CredentialsRevision);
        }
        RememberAppliedSecrets(saved);
    }

    private async Task PickSavedRoute()
    {
        if (busy) return;
        var saved = AppliedLogin();
        var routes = saved.BackupRoutes;
        if (routes.Count == 0) { Notice("还没有备用线路。请从已读取的节点列表里多选保存。"); return; }
        var choices = new ListBox { MinHeight = 155 };
        foreach (var route in routes)
        {
            var entry = StoredNode(route.NodeJson);
            choices.Items.Add(new ListBoxItem { Content = $"{entry.Name} · {entry.Type} · {entry.Server}:{entry.Port}", Tag = route });
        }
        var dialog = new Window { Owner = this, Title = "切换备用线路", Width = 600, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Text("请选择备用线路。应用前会检查当前任务的出口要求，切换期间连接可能短暂中断。", 12));
        panel.Children.Add(choices);
        panel.Children.Add(Row(Action("核验并切换", (_, _) => { if (choices.SelectedItem is not null) dialog.DialogResult = true; }, true),
            Action("移除所选备用", (_, _) =>
            {
                if (choices.SelectedItem is not ListBoxItem { Tag: SavedRoute selectedRoute } row) return;
                try
                {
                    SaveBackupRoutes(routes.Where(r => !SameRoute(r, selectedRoute)).ToList());
                    routes = appliedSecrets!.BackupRoutes;
                    choices.Items.Remove(row);
                }
                catch (Exception error) { Notice(Redaction.SafeError(error)); }
            })));
        dialog.Content = panel;
        if (dialog.ShowDialog() != true || choices.SelectedItem is not ListBoxItem { Tag: SavedRoute routeToApply }) return;
        await Perform(async (_, token) =>
        {
            var options = Clone(state.Options);
            options.AcceptChanges = true;
            options.NetworkSource = "节点链接 / 本地配置";
            options.NodeName = StoredNode(routeToApply.NodeJson).Name;
            options.ChainEnabled = !string.IsNullOrEmpty(routeToApply.UpstreamJson);
            options.ChainFromImport = options.ChainEnabled;
            options.UpstreamName = options.ChainEnabled ? StoredNode(routeToApply.UpstreamJson).Name : "";
            options.UpstreamSource = "节点链接 / 本地配置";
            // A backup never relaxes the task's expected country or fixed exit.
            if (!string.IsNullOrEmpty(routeToApply.TimeZone) && routeToApply.Country == options.Country)
                options.TimeZoneId = routeToApply.TimeZone;
            var candidate = AppliedLogin();
            if (!string.IsNullOrEmpty(candidate.SelectedNodeJson))
                candidate.BackupRoutes.Add(new(candidate.SelectedNodeJson, candidate.UpstreamNodeJson, state.Options.Country, state.Options.TimeZoneId));
            candidate.BackupRoutes.RemoveAll(route => SameRoute(route, routeToApply));
            candidate.SelectedNodeJson = routeToApply.NodeJson;
            candidate.UpstreamNodeJson = routeToApply.UpstreamJson;
            candidate.NetworkText = "{\"proxies\":[" + routeToApply.NodeJson + "]}";
            await ApplyConfiguredRoute(options, candidate, token, restoreDraft: true);
        }, useSavedOptions: true);
    }

    private async Task ApplyConfiguredRoute(SetupOptions requested, Secrets candidate, CancellationToken token, bool restoreDraft = false)
    {
        var original = state;
        if (requested.NetworkSource == "稍后配置") throw new SetupException("请选择要应用的线路。");
        original.RequireResumeIdentity(requested);
        var previous = AppliedLogin();
        // Only network fields and the explicit save preference belong to this operation.
        var options = Clone(original.Options);
        options.NetworkSource = requested.NetworkSource;
        options.NodeName = requested.NodeName;
        options.Country = requested.Country;
        options.TimeZoneId = requested.TimeZoneId;
        options.ChainEnabled = requested.ChainEnabled;
        options.ChainFromImport = requested.ChainFromImport;
        options.UpstreamSource = requested.UpstreamSource;
        options.UpstreamName = requested.UpstreamName;
        options.AcceptChanges = requested.AcceptChanges;
        options.Remember = requested.Remember;
        options.TestOutage = false;
        if (!options.AcceptChanges) throw new SetupException("请先确认应用范围。");
        if (original.Options.NetworkSource != "稍后配置" && string.IsNullOrEmpty(previous.SelectedNodeJson) &&
            BoundaryRuntime.BackendFor(original) is not null)
            throw new SetupException("当前连接缺少可用于回退的原线路资料。请重新载入原任务。");
        var selected = StoredNode(candidate.SelectedNodeJson);
        var upstream = string.IsNullOrEmpty(candidate.UpstreamNodeJson) ? null : StoredNode(candidate.UpstreamNodeJson);
        if (!options.HasUsableGuestPassword(candidate.VmPassword))
            throw new SetupException("请先填写已配置虚拟机的登录密码。");
        if (candidate.ExpectedExitIp.Length > 0 && (!IPAddress.TryParse(candidate.ExpectedExitIp, out var expected) ||
            expected.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            throw new SetupException("预期固定出口请输入有效的 IPv4 地址。");

        RecordTaskActivity("正在核验候选线路，原连接仍可使用。");
        var result = await NodeExitProbe.ProbeAsync(options, selected, upstream, token);
        if (result.CountryCode != options.Country || (candidate.ExpectedExitIp.Length > 0 && result.ExitIp != candidate.ExpectedExitIp))
            throw new SetupException("候选线路的实测出口与预期不符，未切换当前线路。");
        candidate.BackupRoutes = [.. (pendingBackupRoutes ??
            (restoreDraft ? candidate.BackupRoutes : previous.BackupRoutes))];
        candidate.BackupRoutes.RemoveAll(r => r.NodeJson == candidate.SelectedNodeJson && r.UpstreamJson == candidate.UpstreamNodeJson);
        if (candidate.BackupRoutes.Count > 8) candidate.BackupRoutes = candidate.BackupRoutes.Take(8).ToList();

        var staged = Clone(original);
        staged.Options = options;
        staged.DeferCheckpoint = true;
        staged.Protection = new();
        staged.SetupFinished = original.SetupFinished;
        staged.OutagePassed = false;
        staged.RestartPassed = false;
        using var taskLock = WorkspaceLock.Acquire(staged);
        RequireCurrentCheckpoint(original, JsonFile.Read<SessionState>(original.Options.StatePath));
        string? writtenRevision = null;
        try
        {
            await Engine().Run(staged, candidate, token);
            token.ThrowIfCancellationRequested();
            if (!staged.NetworkPassed) throw new SetupException("来宾未通过新线路出口核验。");
            if (staged.Protection.Boundary.Status != CheckStatus.Passed || staged.Protection.Exit.Status != CheckStatus.Passed ||
                staged.Protection.Dns.Status == CheckStatus.Failed)
                throw new SetupException("新线路的外层防护、出口或旁路检查需要处理。");

            // Commit credentials under a new name. The atomic state write selects the version.
            staged.CredentialsRevision = "";
            if (options.Remember)
            {
                writtenRevision = Guid.NewGuid().ToString("N");
                SecretStore.Save(options.Private, candidate, writtenRevision);
                staged.CredentialsRevision = writtenRevision;
            }
            staged.DeferCheckpoint = false;
            staged.Stage = "当前线路已应用";
            staged.Status = staged.SetupFinished ? "设置已完成" : "待完成设置";
            staged.Save();
        }
        catch (Exception cause)
        {
            if (writtenRevision is not null)
                try { SecretStore.Forget(options.Private, writtenRevision); } catch (Exception ex)
                    when (ex is IOException or UnauthorizedAccessException or SetupException) { }
            // An interrupted process leaves the original checkpoint authoritative.
            await BoundaryRuntime.Replace(null, "");
            original.NetworkPassed = false;
            using var restoreDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            if (previous.SelectedNodeJson.Length > 0)
            {
                try
                {
                    var recovery = Clone(original);
                    recovery.Options.AcceptChanges = true;
                    recovery.Options.TestOutage = false;
                    recovery.DeferCheckpoint = true;
                    // Run uses the transaction's lock, including during rollback.
                    taskLock.Dispose();
                    using var recoveryLock = WorkspaceLock.Acquire(recovery);
                    RequireCurrentCheckpoint(original, JsonFile.Read<SessionState>(original.Options.StatePath));
                    await Engine().Run(recovery, previous, restoreDeadline.Token);
                    if (!recovery.NetworkPassed) throw new SetupException("上一条线路复测未通过。");
                    await BoundaryRuntime.VerifyCurrent(recovery, restoreDeadline.Token);
                    original.NetworkPassed = true;
                    original.Protection = recovery.Protection;
                }
                catch { await BoundaryRuntime.Replace(null, ""); }
            }
            throw new SetupException(Redaction.SafeError(cause) + (original.NetworkPassed
                ? "\n已恢复并复测原线路。" : "\n受控连接已暂停，请在网络页恢复原线路。"));
        }

        state = staged;
        pendingBackupRoutes = null;
        RememberAppliedSecrets(candidate);
        try
        {
            SecretStore.PruneRevisions(staged, original);
            Deployment.WriteProtectionReport(staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SetupException)
        {
            Notice("线路已应用，报告写入或旧资料清理需要处理。\n" + Redaction.SafeError(ex));
        }
        if (restoreDraft)
        {
            try { RestoreNetworkDraft(staged.Options, candidate); }
            catch (SetupException) { }
        }
        RecordTaskActivity("已保存并应用线路。");
        if (!closeAfterPause) Notice("新线路已通过来宾出口核对并应用。请核对当前浏览器出口。");
    }

    private void RestoreNetworkDraft(SetupOptions options, Secrets saved)
    {
        source.SelectedItem = options.NetworkSource;
        subscription.Text = saved.SubscriptionUrl;
        networkText.Text = saved.NetworkText;
        expectedIp.Text = saved.ExpectedExitIp;
        country.SelectedValue = options.Country;
        timeZone.SelectedValue = options.TimeZoneId;
        chain.IsChecked = false;
        if (options.NetworkSource != "节点链接 / 本地配置") return;
        var selected = StoredNode(saved.SelectedNodeJson);
        if (!string.IsNullOrEmpty(saved.UpstreamNodeJson)) selected = selected with { Upstream = StoredNode(saved.UpstreamNodeJson) };
        nodes = [selected];
        node.ItemsSource = nodes;
        node.SelectedIndex = 0;
        importedInput = networkText.Text;
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonFile.Format), JsonFile.Format)!;
    private static void RequireCurrentCheckpoint(SessionState current, SessionState disk)
    {
        if (disk.VmId != current.VmId || disk.Options.WorkspaceId != current.Options.WorkspaceId || disk.UpdatedUtc > current.UpdatedUtc)
            throw new SetupException("任务已被另一窗口更新，请重新载入原任务。");
    }
}
