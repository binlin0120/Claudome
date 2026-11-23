using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace ClaudeSetup;

public sealed partial class MainWindow
{
    private string storageArchivePreviewDir = "";
    private string storageLastResultNote = "";
    private readonly HashSet<string> storageSelectedIds = new(StringComparer.OrdinalIgnoreCase);

    internal StorageCleanupContext BuildStorageCleanupContext(string? archiveDirOverride = null)
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.Options.Root)) roots.Add(state.Options.Root);
        if (!string.IsNullOrWhiteSpace(rootBox.Text)) roots.Add(rootBox.Text.Trim());
        return new StorageCleanupContext(
            PlatformId: "mac",
            AppDirectory: appDir,
            TempDirectory: Path.GetTempPath(),
            WorkspaceRoots: roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ArchiveDirectory: archiveDirOverride ?? (string.IsNullOrWhiteSpace(storageArchivePreviewDir) ? null : storageArchivePreviewDir));
    }

    private void RenderStorageCleanupSection()
    {
        HeaderSubtitle.Text = "按来源功能分别查看与清理备份/暂存；成功校验后始终保留当前有效数据与上一份成功备份。";
        var settings = CleanupSettings.Load();
        var context = BuildStorageCleanupContext();
        var report = StorageCleanupService.Inspect(context);

        storageSelectedIds.IntersectWith(report.Items.Where(i => i.Status == CleanupItemStatus.Cleanable && i.CanSelect).Select(i => i.Id));

        // 1. 存储总览
        var summaryPanel = new StackPanel { Spacing = 8 };
        summaryPanel.Children.Add(NoteText(
            $"最近扫描：{report.ScannedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 合计 {report.Items.Count} 项（可清理 {report.Items.Count(i => i.Status == CleanupItemStatus.Cleanable)} 项）",
            muted: false));
        foreach (var cat in report.Categories)
        {
            var note = string.IsNullOrEmpty(cat.PlatformNote)
                ? $"可清理 {cat.CleanableCount} 项（{StorageCleanupService.FormatBytes(cat.CleanableBytes)}）"
                : cat.PlatformNote;
            summaryPanel.Children.Add(NoteText($"• {cat.CategoryLabel}：{cat.FileCount} 项 / {StorageCleanupService.FormatBytes(cat.TotalBytes)} — {note}"));
        }
        var summaryActions = new WrapPanel { Orientation = Orientation.Horizontal };
        var refreshBtn = MakeButton("刷新清单", () => Navigate(8));
        var pickArchiveBtn = MakeButton("选择历史交付 ZIP 目录预览…", async () => await PickMacArchiveDirAsync());
        var selectAllBtn = MakeButton("全选可清理项", () =>
        {
            foreach (var item in report.Items.Where(i => i.Status == CleanupItemStatus.Cleanable && i.CanSelect))
                storageSelectedIds.Add(item.Id);
            Navigate(8);
        });
        var clearSelBtn = MakeButton("取消全选", () => { storageSelectedIds.Clear(); Navigate(8); });
        foreach (var b in new[] { refreshBtn, pickArchiveBtn, selectAllBtn, clearSelBtn })
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            summaryActions.Children.Add(b);
        }
        ContentHost.Children.Add(MakeCard("存储总览", summaryPanel, summaryActions));

        // 2. 自动清理策略
        var autoUpdate = new CheckBox
        {
            Content = "程序更新备份与暂存（如本机存在对应记录，校验通过后保留上一份可用版本）",
            IsChecked = settings.AutoCleanProgramUpdate,
            Foreground = inkBrush
        };
        autoUpdate.IsCheckedChanged += (_, _) => SaveMacAutoSetting(BackupCategory.ProgramUpdate, autoUpdate.IsChecked == true);

        var autoCheckpoint = new CheckBox
        {
            Content = "任务检查点备份（新状态原子写入并语义核验后保留上一份有效自动存档）",
            IsChecked = settings.AutoCleanTaskCheckpoint,
            Foreground = inkBrush
        };
        autoCheckpoint.IsCheckedChanged += (_, _) => SaveMacAutoSetting(BackupCategory.TaskCheckpoint, autoCheckpoint.IsChecked == true);

        var autoRoute = new CheckBox
        {
            Content = "线路与配置变更备份（新线路通过后保留上一份确认可用凭据版本）",
            IsChecked = settings.AutoCleanRouteConfig,
            Foreground = inkBrush
        };
        autoRoute.IsCheckedChanged += (_, _) => SaveMacAutoSetting(BackupCategory.RouteConfig, autoRoute.IsChecked == true);

        var lastAutoText = settings.LastAutoCleanup is { } lastAuto
            ? $"最近一次自动清理（{lastAuto.ExecutedUtc.ToLocalTime():MM-dd HH:mm:ss} · {lastAuto.ScopeSummary}）：清理 {lastAuto.DeletedCount} 项，释放 {StorageCleanupService.FormatBytes(lastAuto.FreedBytes)}，保留 {lastAuto.RetainedCount} 项，跳过 {lastAuto.SkippedCount} 项。"
            : "最近一次自动清理：暂无记录（保留优先于数量上限；用户交付 ZIP 与虚拟机/磁盘/ISO 从不自动清理）。";

        ContentHost.Children.Add(MakeCard(
            "自动清理策略",
            NoteText("Mac 与 Windows 共用同一套清理与保留规则；Mac 预览版暂未接入 Windows 原地 EXE 替换，故该分类如实展示本机登记状态。"),
            autoUpdate,
            autoCheckpoint,
            autoRoute,
            NoteText(lastAutoText, muted: false)));

        // 3. 手动清理明细
        var detailsPanel = new StackPanel { Spacing = 6 };
        if (!string.IsNullOrEmpty(storageArchivePreviewDir))
            detailsPanel.Children.Add(NoteText($"已选定历史包目录：{storageArchivePreviewDir}", muted: false));

        foreach (var cat in report.Categories)
        {
            var catItems = report.Items.Where(i => i.Category == cat.Category).ToList();
            if (catItems.Count == 0) continue;

            detailsPanel.Children.Add(new TextBlock
            {
                Text = $"{cat.CategoryLabel}（{catItems.Count} 项）",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = inkBrush,
                Margin = new Thickness(0, 6, 0, 2)
            });

            var catCleanable = catItems.Where(i => i.Status == CleanupItemStatus.Cleanable && i.CanSelect).ToList();
            if (catCleanable.Count > 0)
            {
                var catCopy = catCleanable;
                detailsPanel.Children.Add(MakeButton($"全选「{cat.CategoryLabel}」可清理项 ({catCleanable.Count})", () =>
                {
                    foreach (var i in catCopy) storageSelectedIds.Add(i.Id);
                    Navigate(8);
                }));
            }

            foreach (var item in catItems)
            {
                var cb = new CheckBox
                {
                    Content = $"[{item.StatusLabel}] {item.DisplayName} · {StorageCleanupService.FormatBytes(item.SizeBytes)} · {item.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {item.SourceFeature}（{item.ScopeLabel}）",
                    IsEnabled = item.CanSelect && item.Status == CleanupItemStatus.Cleanable,
                    IsChecked = item.CanSelect && storageSelectedIds.Contains(item.Id),
                    Foreground = inkBrush
                };
                var itemCopy = item;
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (cb.IsChecked == true) storageSelectedIds.Add(itemCopy.Id);
                    else storageSelectedIds.Remove(itemCopy.Id);
                };
                detailsPanel.Children.Add(cb);
                detailsPanel.Children.Add(NoteText($"    状态说明：{item.Reason}  ·  路径：{item.Path}"));
            }
        }

        if (report.Items.Count == 0)
            detailsPanel.Children.Add(NoteText("当前范围内暂无可展示的备份或暂存文件。"));

        var selectedBytes = report.Items
            .Where(i => storageSelectedIds.Contains(i.Id) && i.Status == CleanupItemStatus.Cleanable)
            .Sum(i => i.SizeBytes);

        var runBtn = MakeButton(
            $"预览确认并清理已选 ({storageSelectedIds.Count} 项 / {StorageCleanupService.FormatBytes(selectedBytes)})",
            async () => await ConfirmAndExecuteMacCleanupAsync(report, context),
            primary: true);

        ContentHost.Children.Add(MakeCard("手动清理与保留明细", detailsPanel, runBtn));

        if (!string.IsNullOrEmpty(storageLastResultNote))
            ContentHost.Children.Add(MakeCard("最近一次手动清理结果", NoteText(storageLastResultNote, muted: false)));
    }

    private void SaveMacAutoSetting(BackupCategory category, bool enabled)
    {
        var settings = CleanupSettings.Load();
        settings.SetAutoCleanEnabled(category, enabled);
        settings.Save();
        BannerText.Text = $"已保存「{StorageCleanupService.CategoryTitle(category)}」自动清理设置：{(enabled ? "开启" : "关闭")}（重启后生效）。";
    }

    private async Task PickMacArchiveDirAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择历史 Claudome 交付包目录",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        storageArchivePreviewDir = folders[0].Path.LocalPath;
        Navigate(8);
    }

    private async Task ConfirmAndExecuteMacCleanupAsync(CleanupInspectionReport report, StorageCleanupContext context)
    {
        var selected = report.Items
            .Where(i => storageSelectedIds.Contains(i.Id) && i.Status == CleanupItemStatus.Cleanable && i.CanSelect)
            .ToList();
        if (selected.Count == 0)
        {
            BannerText.Text = "请先勾选至少一项状态为「可清理」的条目。";
            return;
        }

        var retained = report.Items.Where(i => i.Status != CleanupItemStatus.Cleanable).Take(6).ToList();
        var confirm = new Window
        {
            Title = "确认手动清理",
            Width = 560,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = surfaceBrush,
            Foreground = inkBrush
        };
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = $"拟删除 {selected.Count} 项，预计释放 {StorageCleanupService.FormatBytes(selected.Sum(x => x.SizeBytes))}",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = inkBrush
        });
        foreach (var item in selected.Take(10))
            body.Children.Add(NoteText($"• [{item.CategoryLabel}] {item.DisplayName} ({StorageCleanupService.FormatBytes(item.SizeBytes)})", muted: false));
        if (retained.Count > 0)
        {
            body.Children.Add(NoteText("继续保留项：", muted: false));
            foreach (var r in retained)
                body.Children.Add(NoteText($"• [{r.StatusLabel}] {r.DisplayName}：{r.Reason}"));
        }
        var proceed = false;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(MakeButton("确认清理", () => { proceed = true; confirm.Close(); }, primary: true));
        row.Children.Add(MakeButton("取消", () => confirm.Close()));
        body.Children.Add(row);
        confirm.Content = new ScrollViewer { Content = body };
        await confirm.ShowDialog(this);
        if (!proceed) return;

        var result = StorageCleanupService.ExecuteManualCleanup(selected, context);
        storageSelectedIds.Clear();
        storageLastResultNote = $"已清理 {result.DeletedCount} 项，实际释放 {StorageCleanupService.FormatBytes(result.FreedBytes)}，保留 {result.RetainedCount} 项，跳过 {result.SkippedCount} 项。";
        if (result.SkippedReasons.Count > 0)
            storageLastResultNote += "\n跳过原因：" + string.Join("；", result.SkippedReasons);
        BannerText.Text = storageLastResultNote;
        Navigate(8);
    }
}
