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

    private Border? routeActionsCard;

    private Button? restoreRouteDraftButton;

    private Border BuildRouteActions()
    {
        restoreRouteDraftButton = Action("恢复原已保存线路", (_, _) => RestoreSavedRouteDraft());
        routeActionsCard = Card(Text("当前线路与配置草稿", 16, false),
            routeSummary,
            Text("编辑、刷新和测速不会切换连接；仅在需要更新已创建环境时点击「应用线路」。", 11),
            Row(Action("应用线路", async (_, _) => await ApplyRouteFromPage(), true),
                restoreRouteDraftButton,
                Action("多选线路 / 测速", (_, _) => SelectBackupRoutes()),
                Action("已保存的备用线路", async (_, _) => await PickSavedRoute()),
                Action("测速当前来宾", async (_, _) => await MeasureCurrentGuest())));
        UpdateRouteActionsVisibility();
        return routeActionsCard;
    }

    private void UpdateRouteActionsVisibility()
    {
        if (routeActionsCard is null) return;
        var hasSaved = HasSavedRouteSnapshot();
        if (restoreRouteDraftButton is not null)
            restoreRouteDraftButton.Visibility = hasSaved ? Visibility.Visible : Visibility.Collapsed;
        var hasContext = hasSaved || !string.IsNullOrEmpty(state.VmId) || nodes.Count > 0 || !string.IsNullOrEmpty(appliedSecrets?.SelectedNodeJson);
        routeActionsCard.Visibility = hasContext ? Visibility.Visible : Visibility.Collapsed;
    }

    internal bool HasSavedRouteSnapshot() =>
        state.Completed.Contains("software") ||
        (!string.IsNullOrEmpty(state.VmId) && (appliedSecrets is not null || state.Options.NetworkSource == "稍后配置")) ||
        (File.Exists(state.Options.StatePath) && (!string.IsNullOrEmpty(appliedSecrets?.SelectedNodeJson) || state.Options.NetworkSource == "稍后配置"));

    private Secrets ResolveAppliedSnapshotSecrets(bool throwOnSecretError = false)
    {
        if (appliedSecrets is { SelectedNodeJson.Length: > 0 } || state.Options.NetworkSource == "稍后配置")
            return AppliedLogin();
        if (state.Options.Remember && File.Exists(state.Options.StatePath))
        {
            try
            {
                var loaded = SecretStore.Load(state);
                if (loaded is not null)
                {
                    RememberAppliedSecrets(loaded);
                    return AppliedLogin();
                }
            }
            catch (SetupException)
            {
                if (throwOnSecretError) throw;
            }
        }
        return AppliedLogin();
    }

    internal static void ValidateSavedRouteSnapshot(SetupOptions savedOptions, Secrets saved)
    {
        if (savedOptions.NetworkSource == "稍后配置") return;
        if (string.IsNullOrWhiteSpace(saved.SelectedNodeJson))
            throw new SetupException("已保存的线路快照缺失或无效。请前往「4 网络节点」重新读取、选择并点击「应用线路」。");
        try
        {
            var parsed = NetworkImport.Parse("{\"proxies\":[" + saved.SelectedNodeJson + "]}").Nodes;
            if (parsed.Count != 1) throw new SetupException("已保存的主用节点无效。");
            var raw = JsonSerializer.Deserialize<Dictionary<string, object?>>(saved.SelectedNodeJson)!
                .ToDictionary(p => p.Key, p => p.Value is JsonElement e ? UnwrapRouteJson(e) : p.Value);
            _ = NetworkImport.Validate(raw);
            if (savedOptions.ChainEnabled)
            {
                if (string.IsNullOrWhiteSpace(saved.UpstreamNodeJson))
                    throw new SetupException("已保存的链式前置节点缺失。");
                var upstreamParsed = NetworkImport.Parse("{\"proxies\":[" + saved.UpstreamNodeJson + "]}").Nodes;
                if (upstreamParsed.Count != 1 || upstreamParsed[0].Upstream is not null)
                    throw new SetupException("已保存的前置节点无效。");
                var rawUp = JsonSerializer.Deserialize<Dictionary<string, object?>>(saved.UpstreamNodeJson)!
                    .ToDictionary(p => p.Key, p => p.Value is JsonElement e ? UnwrapRouteJson(e) : p.Value);
                _ = NetworkImport.Validate(rawUp);
            }
            if (!string.IsNullOrEmpty(saved.ExpectedExitIp) &&
                (!IPAddress.TryParse(saved.ExpectedExitIp, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
                throw new SetupException("已保存的预期固定出口 IPv4 无效。");
        }
        catch (SetupException ex)
        {
            throw new SetupException("已保存的线路快照无效（" + ex.Message + "）。请前往「4 网络节点」重新读取、选择并点击「应用线路」。");
        }
        catch
        {
            throw new SetupException("已保存的线路快照无效或已损坏。请前往「4 网络节点」重新读取、选择并点击「应用线路」。");
        }
    }

    private static object? UnwrapRouteJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(UnwrapRouteJson).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => UnwrapRouteJson(p.Value)),
        _ => null
    };

    private bool HasUnappliedNetworkDraft(SetupOptions uiOptions, Secrets uiSecrets, SetupOptions savedOptions, Secrets savedSecrets)
    {
        if (uiOptions.NetworkSource != savedOptions.NetworkSource) return true;
        if (savedOptions.NetworkSource == "稍后配置") return false;
        var currentInput = uiOptions.NetworkSource == "已有订阅链接"
            ? subscription.Text.Trim()
            : uiOptions.NetworkSource == "手动 HTTP / SOCKS5 节点"
                ? ManualFingerprint()
                : networkText.Text;
        if (string.IsNullOrWhiteSpace(currentInput) || node.SelectedItem is not ProxyNode || importedInput != currentInput)
            return true;
        if (!NetworkImport.SameSavedDefinition(uiSecrets.SelectedNodeJson, savedSecrets.SelectedNodeJson)) return true;
        if (uiOptions.ChainEnabled != savedOptions.ChainEnabled) return true;
        if (uiOptions.ChainEnabled && !NetworkImport.SameSavedDefinition(uiSecrets.UpstreamNodeJson, savedSecrets.UpstreamNodeJson)) return true;
        if (uiSecrets.ExpectedExitIp != savedSecrets.ExpectedExitIp) return true;
        if (uiOptions.Country != savedOptions.Country || uiOptions.TimeZoneId != savedOptions.TimeZoneId) return true;
        return false;
    }

    private static Secrets MergeRuntimeCredentials(Secrets saved, Secrets currentUi) => new()
    {
        VmPassword = string.IsNullOrEmpty(currentUi.VmPassword) ? saved.VmPassword : currentUi.VmPassword,
        VmMicrosoftAddress = string.IsNullOrEmpty(currentUi.VmMicrosoftAddress) ? saved.VmMicrosoftAddress : currentUi.VmMicrosoftAddress,
        AdsPowerApiKey = string.IsNullOrEmpty(currentUi.AdsPowerApiKey) ? saved.AdsPowerApiKey : currentUi.AdsPowerApiKey,
        SubscriptionUrl = saved.SubscriptionUrl,
        NetworkText = saved.NetworkText,
        SelectedNodeJson = saved.SelectedNodeJson,
        UpstreamNodeJson = saved.UpstreamNodeJson,
        UpstreamSubscriptionUrl = saved.UpstreamSubscriptionUrl,
        UpstreamNetworkText = saved.UpstreamNetworkText,
        ExpectedExitIp = saved.ExpectedExitIp,
        BackupRoutes = [.. (currentUi.BackupRoutes.Count > 0 ? currentUi.BackupRoutes : saved.BackupRoutes)]
    };

    private void PopulateSavedManualNodeFields(ProxyNode parsedNode, string nodeJson)
    {
        using var json = JsonDocument.Parse(nodeJson);
        var n = json.RootElement;
        var typeValue = n.GetProperty("type").GetString();
        server.Text = parsedNode.Server;
        port.Text = parsedNode.Port.ToString();
        protocol.SelectedItem = typeValue == "http" && n.TryGetProperty("tls", out var tls) && tls.ValueKind == JsonValueKind.True
            ? "https"
            : typeValue;
        proxyUser.Text = n.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
        proxyPassword.Password = n.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
    }

    private void PopulateSavedFinalNodeUi(Secrets saved, SetupOptions o)
    {
        var parsed = NetworkImport.Parse("{\"proxies\":[" + saved.SelectedNodeJson + "]}");
        if (parsed.Nodes.Count == 0)
            throw new SetupException("已保存的节点快照无法解析，请重新读取或填写后再点击「应用线路」。");
        nodes = parsed.Nodes;
        node.ItemsSource = nodes;
        node.SelectedIndex = 0;
        if (o.NetworkSource == "手动 HTTP / SOCKS5 节点")
            PopulateSavedManualNodeFields(parsed.Nodes[0], saved.SelectedNodeJson);
        RestoreChainRelationship(saved, o);
        importedInput = o.NetworkSource == "已有订阅链接"
            ? subscription.Text.Trim()
            : o.NetworkSource == "手动 HTTP / SOCKS5 节点"
                ? ManualFingerprint()
                : networkText.Text;
        currentFinalSubscription = false;
    }

    private void RestoreSavedRouteDraft()
    {
        if (busy) return;
        try
        {
            var saved = ResolveAppliedSnapshotSecrets(throwOnSecretError: true);
            var o = state.Options;
            ValidateSavedRouteSnapshot(o, saved);
            updatingCpuRecommendation = true;
            try
            {
                source.SelectedItem = o.NetworkSource;
                country.SelectedValue = o.Country;
                timeZone.SelectedValue = o.TimeZoneId;
                chain.IsChecked = o.ChainEnabled && !o.ChainFromImport;
                upstreamSource.SelectedItem = o.UpstreamSource;
            }
            finally { updatingCpuRecommendation = false; }
            subscription.Text = saved.SubscriptionUrl;
            networkText.Text = saved.NetworkText;
            expectedIp.Text = saved.ExpectedExitIp;
            LoadChainSecrets(saved);
            if (o.NetworkSource != "稍后配置")
            {
                PopulateSavedFinalNodeUi(saved, o);
            }
            else
            {
                nodes = [];
                node.ItemsSource = nodes;
                importedInput = "";
            }
            UpdateRouteActionsVisibility();
            Notice("已恢复为当前任务已保存的线路配置。");
        }
        catch (Exception ex)
        {
            Notice("未能恢复原已保存线路：" + Redaction.SafeError(ex) + "\n\n请确认是否已解锁本机保存的凭据，或在「4 网络节点」重新读取、选择并点击「应用线路」。");
        }
    }

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
        if (!state.Completed.Contains("software"))
        {
            if (!HasSavedRouteSnapshot()) { Go(5); return; }
            await Perform(async (o, token) =>
            {
                if (o.NetworkSource != "稍后配置" && (country.SelectedValue is null || timeZone.SelectedValue is null))
                    throw new SetupException("请按节点实际地区选择国家和城市时区。");
                var currentNetworkInput = o.NetworkSource == "已有订阅链接" ? subscription.Text.Trim() : o.NetworkSource == "手动 HTTP / SOCKS5 节点" ? ManualFingerprint() : networkText.Text;
                if (o.NetworkSource != "稍后配置" && (string.IsNullOrWhiteSpace(currentNetworkInput) || node.SelectedItem is not ProxyNode || importedInput != currentNetworkInput))
                    throw new SetupException("请先读取并选择一个节点；网络资料已变化或尚未载入时需重新读取。");
                var candidate = ReadSecrets();
                candidate.BackupRoutes = [.. (pendingBackupRoutes ?? appliedSecrets?.BackupRoutes ?? [])];
                await RefreshSelectedRoutes(o, candidate, token);
                ValidateSavedRouteSnapshot(o, candidate);
                var updated = Clone(state);
                updated.Options.NetworkSource = o.NetworkSource;
                updated.Options.NodeName = o.NodeName;
                updated.Options.ChainEnabled = o.ChainEnabled;
                updated.Options.ChainFromImport = o.ChainFromImport;
                updated.Options.UpstreamSource = o.UpstreamSource;
                updated.Options.UpstreamName = o.UpstreamName;
                updated.Options.Country = o.Country;
                updated.Options.TimeZoneId = o.TimeZoneId;
                updated.Options.Remember = o.Remember;
                using var taskLock = WorkspaceLock.Acquire(updated);
                if (updated.Options.Remember)
                    SecretStore.SaveMerged(updated.Options.Private, candidate, updated.Options.NetworkSource == "稍后配置", !secretsLoaded, updated.CredentialsRevision);
                updated.Save();
                state = updated;
                RememberAppliedSecrets(candidate);
                Notice("已保存本任务的新线路配置；可点击「继续部署」按新线路继续。");
                Go(5);
            });
            return;
        }
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
                            string.IsNullOrEmpty(route.UpstreamJson) ? null : StoredNode(route.UpstreamJson), probeCancel.Token,
                            ProductProfile.IsHyperVStandalone ? EndpointResolutionStrategy.HyperVStandalone : EndpointResolutionStrategy.Default);
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
        var probeStrategy = ProductProfile.IsHyperVStandalone ? EndpointResolutionStrategy.HyperVStandalone : EndpointResolutionStrategy.Default;
        var result = await NodeExitProbe.ProbeAsync(options, selected, upstream, token, strategy: probeStrategy);
        if (result.CountryCode != options.Country || (candidate.ExpectedExitIp.Length > 0 && result.ExitIp != candidate.ExpectedExitIp))
            throw new SetupException("候选线路的实测出口与预期不符，未切换当前线路。");
        var pinnedEndpoints = result.VerifiedFirstHopAddresses is { Length: > 0 }
            ? new Deployment.PinnedRouteEndpoints(
                result.VerifiedFirstHopAddresses,
                result.VerifiedExitAddresses is { Length: > 0 } ? result.VerifiedExitAddresses : result.VerifiedFirstHopAddresses,
                string.IsNullOrWhiteSpace(result.ResolutionSummary) ? "候选测速已核验" : result.ResolutionSummary,
                result.VerifiedConnectedEndpoint)
            : null;
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
            await Engine().Run(staged, candidate, token, pinnedEndpoints: pinnedEndpoints,
                preferredDesktopWidth: staged.Options.PlatformId == "hyperv" && consoleHost?.IsAttached == true ? consoleHost.GetVmConnectContentViewport().Width : 0,
                preferredDesktopHeight: staged.Options.PlatformId == "hyperv" && consoleHost?.IsAttached == true ? consoleHost.GetVmConnectContentViewport().Height : 0);
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
