using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    private string storageArchivePreviewDir = "";
    private string storageLastResultNote = "";
    private readonly HashSet<string> storageSelectedIds = new(StringComparer.OrdinalIgnoreCase);

    internal StorageCleanupContext BuildStorageCleanupContext(string? archiveDirOverride = null)
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
        var expectedPlatform = ProductProfile.IsHyperVStandalone ? "hyperv" : null;
        var expectedPrefix = ProductProfile.IsHyperVStandalone ? ProductProfile.PackagePrefix : null;
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.Options.Root) &&
            StorageCleanupService.IsWorkspaceOwnedByEdition(state.Options.Root, expectedPlatform))
            roots.Add(state.Options.Root);
        if (!ProductProfile.IsHyperVStandalone &&
            !string.IsNullOrWhiteSpace(root.Text) &&
            StorageCleanupService.IsWorkspaceOwnedByEdition(root.Text, null))
            roots.Add(root.Text);
        return new StorageCleanupContext(
            PlatformId: "windows",
            AppDirectory: appDir,
            TempDirectory: Path.GetTempPath(),
            WorkspaceRoots: roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ArchiveDirectory: archiveDirOverride ?? (string.IsNullOrWhiteSpace(storageArchivePreviewDir) ? null : storageArchivePreviewDir),
            ExpectedPlatformId: expectedPlatform,
            ExpectedPackagePrefix: expectedPrefix);
    }

    private void DrawStorageCleanup()
    {
        subtitle.Text = "按来源功能分别管理备份与暂存；每项操作通过校验后保留当前有效数据与上一份成功备份。";
        var settings = CleanupSettings.Load();
        var context = BuildStorageCleanupContext();
        var report = StorageCleanupService.Inspect(context);

        // Clean stale selection IDs while keeping valid selections
        storageSelectedIds.IntersectWith(report.Items.Where(i => i.Status == CleanupItemStatus.Cleanable && i.CanSelect).Select(i => i.Id));

        // 1. 存储总览卡片
        var overviewGrid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var cat in report.Categories)
        {
            var note = string.IsNullOrEmpty(cat.PlatformNote)
                ? $"可清理 {cat.CleanableCount} 项（{StorageCleanupService.FormatBytes(cat.CleanableBytes)}）"
                : cat.PlatformNote;
            overviewGrid.Children.Add(Card(
                Text(cat.CategoryLabel, 13, false),
                Text($"{cat.FileCount} 项  ·  {StorageCleanupService.FormatBytes(cat.TotalBytes)}", 16, false),
                Text(note, 11)));
        }
        main.Children.Add(Card(
            Text("存储总览", 16, false),
            Text($"最近扫描：{report.ScannedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  合计 {report.Items.Count} 项（可清理 {report.Items.Count(i => i.Status == CleanupItemStatus.Cleanable)} 项）", 12),
            overviewGrid,
            Row(
                Action("刷新清单", (_, _) => Navigate(8)),
                Action("选择历史交付 ZIP 目录预览…", (_, _) => PickArchiveDirectoryForPreview()),
                Action("全选当前可清理项", (_, _) =>
                {
                    foreach (var item in report.Items.Where(i => i.Status == CleanupItemStatus.Cleanable && i.CanSelect))
                        storageSelectedIds.Add(item.Id);
                    Navigate(8);
                }),
                Action("取消全选", (_, _) => { storageSelectedIds.Clear(); Navigate(8); }))));

        // 2. 自动清理策略卡片
        var autoUpdateCheck = Check($"程序更新备份与暂存（成功启动并通过核验后保留上一份回退备份）");
        autoUpdateCheck.IsChecked = settings.AutoCleanProgramUpdate;
        autoUpdateCheck.Checked += (_, _) => UpdateAutoSetting(BackupCategory.ProgramUpdate, true);
        autoUpdateCheck.Unchecked += (_, _) => UpdateAutoSetting(BackupCategory.ProgramUpdate, false);

        var autoCheckpointCheck = Check("任务检查点备份（新状态原子写入通过语义校验后保留上一份有效自动存档）");
        autoCheckpointCheck.IsChecked = settings.AutoCleanTaskCheckpoint;
        autoCheckpointCheck.Checked += (_, _) => UpdateAutoSetting(BackupCategory.TaskCheckpoint, true);
        autoCheckpointCheck.Unchecked += (_, _) => UpdateAutoSetting(BackupCategory.TaskCheckpoint, false);

        var autoRouteCheck = Check("线路与配置变更备份（新线路核验与回读通过后保留上一份确认可用凭据版本）");
        autoRouteCheck.IsChecked = settings.AutoCleanRouteConfig;
        autoRouteCheck.Checked += (_, _) => UpdateAutoSetting(BackupCategory.RouteConfig, true);
        autoRouteCheck.Unchecked += (_, _) => UpdateAutoSetting(BackupCategory.RouteConfig, false);

        var lastAutoText = settings.LastAutoCleanup is { } lastAuto
            ? $"最近一次自动清理（{lastAuto.ExecutedUtc.ToLocalTime():MM-dd HH:mm:ss} · {lastAuto.ScopeSummary}）：清理 {lastAuto.DeletedCount} 项，释放 {StorageCleanupService.FormatBytes(lastAuto.FreedBytes)}，保留 {lastAuto.RetainedCount} 项，跳过 {lastAuto.SkippedCount} 项。"
            : "最近一次自动清理：暂无记录（将在操作成功并核验通过后按上述开关执行，保留项优先于数量上限）。";

        main.Children.Add(Card(
            Text("自动清理策略", 16, false),
            Text("说明：更新、保存任务和切换线路仅轮换各自更早且未被恢复/待补流程引用的旧备份，绝不互相清理；用户历史 ZIP 与虚拟机/磁盘/ISO 从不自动删除。", 11),
            autoUpdateCheck,
            autoCheckpointCheck,
            autoRouteCheck,
            Text(lastAutoText, 11)));

        // 3. 手动清理明细卡片
        var detailsPanel = new StackPanel();
        if (!string.IsNullOrEmpty(storageArchivePreviewDir))
        {
            detailsPanel.Children.Add(Text($"已选定历史交付包目录：{storageArchivePreviewDir}", 11));
        }

        foreach (var catGroup in report.Categories)
        {
            var catItems = report.Items.Where(i => i.Category == catGroup.Category).ToList();
            if (catItems.Count == 0) continue;

            detailsPanel.Children.Add(new TextBlock
            {
                Text = $"{catGroup.CategoryLabel}（{catItems.Count} 项）",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink,
                Margin = new Thickness(0, 10, 0, 4)
            });

            var catCleanable = catItems.Where(i => i.Status == CleanupItemStatus.Cleanable && i.CanSelect).ToList();
            if (catCleanable.Count > 0)
            {
                var catCopy = catCleanable;
                detailsPanel.Children.Add(Row(
                    Action($"全选「{catGroup.CategoryLabel}」可清理项 ({catCleanable.Count})", (_, _) =>
                    {
                        foreach (var item in catCopy) storageSelectedIds.Add(item.Id);
                        Navigate(8);
                    })));
            }

            foreach (var item in catItems)
            {
                var cb = new CheckBox
                {
                    Content = $"[{item.StatusLabel}] {item.DisplayName} · {StorageCleanupService.FormatBytes(item.SizeBytes)} · {item.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {item.SourceFeature}（归属：{item.ScopeLabel}）",
                    IsEnabled = item.CanSelect && item.Status == CleanupItemStatus.Cleanable,
                    IsChecked = item.CanSelect && storageSelectedIds.Contains(item.Id),
                    Foreground = Ink,
                    Margin = new Thickness(4, 4, 0, 1)
                };
                var itemCopy = item;
                cb.Checked += (_, _) => storageSelectedIds.Add(itemCopy.Id);
                cb.Unchecked += (_, _) => storageSelectedIds.Remove(itemCopy.Id);
                detailsPanel.Children.Add(cb);
                detailsPanel.Children.Add(new TextBlock
                {
                    Text = $"    状态说明：{item.Reason}  ·  路径：{item.Path}",
                    FontSize = 11,
                    Foreground = Muted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(20, 0, 0, 4)
                });
            }
        }

        if (report.Items.Count == 0)
            detailsPanel.Children.Add(Text("当前范围内暂无已登记的备份或暂存文件。", 12));

        var selectedTotalBytes = report.Items
            .Where(i => storageSelectedIds.Contains(i.Id) && i.Status == CleanupItemStatus.Cleanable)
            .Sum(i => i.SizeBytes);

        main.Children.Add(Card(
            Text("手动清理与保留明细", 16, false),
            detailsPanel,
            Row(Action($"清理已选项目 ({storageSelectedIds.Count} 项 / {StorageCleanupService.FormatBytes(selectedTotalBytes)})",
                (_, _) => ConfirmAndExecuteManualCleanup(report, context), primary: true))));

        if (!string.IsNullOrEmpty(storageLastResultNote))
        {
            main.Children.Add(Card(Text("最近一次手动清理结果", 15, false), Text(storageLastResultNote, 12)));
        }
    }

    private void UpdateAutoSetting(BackupCategory category, bool enabled)
    {
        var settings = CleanupSettings.Load();
        settings.SetAutoCleanEnabled(category, enabled);
        settings.Save();
        banner.Text = $"已更新「{StorageCleanupService.CategoryTitle(category)}」自动清理开关：{(enabled ? "开启（保留上一份成功备份）" : "关闭（仅手动清理）")}";
    }

    private void PickArchiveDirectoryForPreview()
    {
        var dialog = new OpenFolderDialog { Title = $"选择历史 {ProductProfile.WindowTitle} 交付包目录以预览可清理项" };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        storageArchivePreviewDir = dialog.FolderName;
        Navigate(8);
    }

    private void ConfirmAndExecuteManualCleanup(CleanupInspectionReport report, StorageCleanupContext context)
    {
        var selectedItems = report.Items
            .Where(i => storageSelectedIds.Contains(i.Id) && i.Status == CleanupItemStatus.Cleanable && i.CanSelect)
            .ToList();
        if (selectedItems.Count == 0)
        {
            Notice("请先勾选至少一项状态为「可清理」的文件或暂存目录。");
            return;
        }
        var totalBytes = selectedItems.Sum(x => x.SizeBytes);
        var retainedItems = report.Items.Where(i => i.Status != CleanupItemStatus.Cleanable).Take(6).ToList();
        var previewLines = new List<string>
        {
            $"拟删除 {selectedItems.Count} 项，预计释放 {StorageCleanupService.FormatBytes(totalBytes)}：",
            ""
        };
        previewLines.AddRange(selectedItems.Take(10).Select(x => $"• [{x.CategoryLabel}] {x.DisplayName} ({StorageCleanupService.FormatBytes(x.SizeBytes)})"));
        if (selectedItems.Count > 10) previewLines.Add($"• ……另有 {selectedItems.Count - 10} 项");
        if (retainedItems.Count > 0)
        {
            previewLines.Add("");
            previewLines.Add("以下受保护/必需恢复项将继续保留：");
            previewLines.AddRange(retainedItems.Select(x => $"• [{x.StatusLabel}] {x.DisplayName}：{x.Reason}"));
        }

        if (MessageBox.Show(this, string.Join("\n", previewLines), "确认手动清理", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var result = StorageCleanupService.ExecuteManualCleanup(selectedItems, context);
        storageSelectedIds.Clear();
        var summary = new List<string>
        {
            $"执行完成：已清理 {result.DeletedCount} 项，实际释放 {StorageCleanupService.FormatBytes(result.FreedBytes)}，保留 {result.RetainedCount} 项，跳过 {result.SkippedCount} 项。"
        };
        if (result.DeletedNames.Count > 0)
            summary.Add("已删除：" + string.Join("、", result.DeletedNames));
        if (result.SkippedReasons.Count > 0)
            summary.Add("跳过项：\n" + string.Join("\n", result.SkippedReasons));
        if (result.RetainedReasons.Count > 0)
            summary.Add("继续保留（示例）：\n" + string.Join("\n", result.RetainedReasons.Take(6)));
        storageLastResultNote = string.Join("\n\n", summary);
        Navigate(8);
        Notice(storageLastResultNote);
    }
}
