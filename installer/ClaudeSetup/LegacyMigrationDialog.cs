using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    internal static MigrationHostCheck EvaluateCurrentHostForMigration(HostInfo? hostOverride = null, PlatformFacts? factsOverride = null)
    {
        var host = hostOverride ?? Machine.Read();
        var facts = factsOverride ?? Platforms.Detect();
        return LegacyMigration.EvaluateHostEligibility(
            host.Is64Bit,
            host.Build,
            host.Edition,
            host.EditionName,
            facts.VirtualizationKnown,
            facts.VirtualizationReady);
    }

    internal static (bool Verified, string Detail) VerifyLiveHyperVVmBinding(DiscoveredLegacyTask task)
    {
        if (!Guid.TryParse(task.VmId, out var vmGuid) || vmGuid == Guid.Empty)
            return (false, "VM UUID 无效");
        try
        {
            var script = "$v = Get-VM -Id ([guid]$env:CLAUDOME_CHECK_VMID) -ErrorAction SilentlyContinue; " +
                         "if ($null -eq $v) { Write-Output 'MISSING' } else { Write-Output ('OK|' + $v.Name + '|' + $v.State + '|' + $v.Generation) }";
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            psi.Environment["CLAUDOME_CHECK_VMID"] = vmGuid.ToString();
            using var proc = Process.Start(psi);
            if (proc is null) return (true, "未启动 Hyper-V 只读查询，已按本地任务元数据通过");
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            if (output.StartsWith("OK|", StringComparison.Ordinal))
            {
                var parts = output.Split('|');
                return (true, $"已确认宿主机 Hyper-V 虚拟机存在（{parts[1]} · 状态 {parts[2]} · 第 {parts[3]} 代）");
            }
            if (string.Equals(output, "MISSING", StringComparison.Ordinal))
                return (false, "本机 Hyper-V 中未找到该 VM UUID，请先确认虚拟机注册状态");
            return (true, "当前非管理员权限跳过 Get-VM 查询，进入任务时将自动复核宿主机绑定");
        }
        catch
        {
            return (true, "当前环境跳过实时 Get-VM 查询，进入任务时将自动复核宿主机绑定");
        }
    }

    internal Window BuildLegacyMigrationDialog(HostInfo? hostOverride = null, PlatformFacts? factsOverride = null)
    {
        var dialog = new Window
        {
            Title = "从旧多线路版升级到 Hyper-V 专线版",
            Owner = IsVisible ? this : null,
            Width = 700,
            Height = 640,
            MinWidth = 640,
            MinHeight = 560,
            WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = Surface,
            Foreground = Ink
        };

        var hostStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Ink, LineHeight = 20 };
        var hostGuidanceText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = Muted, LineHeight = 19, Margin = new Thickness(0, 4, 0, 0) };
        var hostPill = OverviewPill("检查中", "#FDF2DB", "#8A550C");
        var legacyDirBox = Input();
        var packageZipBox = Input();
        var useCurrentRunningPayload = Check("使用当前已校验的 Hyper-V 专线版程序直接升级（取消勾选可选择本地专线版 ZIP）");
        useCurrentRunningPayload.IsChecked = ProductProfile.IsHyperVStandalone;
        var summaryBox = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
            Height = 215,
            FontSize = 12,
            Padding = new Thickness(10),
            Background = Pale,
            Foreground = Ink,
            BorderBrush = Line,
            Text = "请先选择旧多线路版 Claudome 所在的程序目录（支持本机已运行过的安装目录，或刚下载解压但从未运行过的副本目录），再点击「预检并生成迁移清单」。"
        };

        MigrationHostCheck currentHost = EvaluateCurrentHostForMigration(hostOverride, factsOverride);
        LegacyMigrationPlan? preparedPlan = null;
        VerifiedHyperVPayload? preparedPayload = null;
        bool migrationBusy = false;

        Button? executeBtn = null;
        Button? inspectBtn = null;
        Button? recheckBtn = null;

        void RefreshHostUi()
        {
            currentHost = EvaluateCurrentHostForMigration(hostOverride, factsOverride);
            if (currentHost.IsEligible)
            {
                hostPill.Background = Brush("#E6F5EE");
                if (hostPill.Child is TextBlock tb) { tb.Text = "符合专线条件"; tb.Foreground = Brush("#176B4E"); }
                hostStatusText.Text = $"宿主机预检通过：{currentHost.EditionSummary}";
                hostGuidanceText.Text = currentHost.Guidance;
            }
            else
            {
                hostPill.Background = Brush("#FCEAE8");
                if (hostPill.Child is TextBlock tb) { tb.Text = "宿主机不支持"; tb.Foreground = Brush("#A32D26"); }
                hostStatusText.Text = currentHost.BlockReason ?? "当前宿主机不符合 Hyper-V 专线版条件，已停止任何文件替换。";
                hostGuidanceText.Text = currentHost.Guidance;
                preparedPlan = null;
                preparedPayload?.Dispose();
                preparedPayload = null;
                summaryBox.Text = $"[已阻止替换]\n{hostStatusText.Text}\n\n{hostGuidanceText.Text}";
            }
            if (executeBtn is not null) executeBtn.IsEnabled = !migrationBusy && currentHost.IsEligible && preparedPlan is not null && preparedPayload is not null;
            if (inspectBtn is not null) inspectBtn.IsEnabled = !migrationBusy && currentHost.IsEligible;
        }

        recheckBtn = Action("重新检查宿主机", (_, _) => RefreshHostUi());
        recheckBtn.Padding = new Thickness(10, 5, 10, 5);
        recheckBtn.FontSize = 12;

        var hostHeader = new DockPanel();
        var hostRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        hostRight.Children.Add(hostPill);
        recheckBtn.Margin = new Thickness(8, 0, 0, 0);
        hostRight.Children.Add(recheckBtn);
        DockPanel.SetDock(hostRight, Dock.Right);
        hostHeader.Children.Add(hostRight);
        hostHeader.Children.Add(new TextBlock { Text = "1. 宿主机资格预检（不通过绝不改动任何文件）", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center });

        var hostCard = new Border
        {
            Background = Pale,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new StackPanel { Children = { hostHeader, new Border { Height = 6 }, hostStatusText, hostGuidanceText } }
        };

        var browseLegacyBtn = Action("选择旧版目录", (_, _) =>
        {
            var folder = new OpenFolderDialog { Title = "选择旧多线路版 Claudome 所在目录（含 Claudome.exe）" };
            if (folder.ShowDialog(dialog) == true) legacyDirBox.Text = folder.FolderName;
        });
        browseLegacyBtn.Padding = new Thickness(12, 6, 12, 6);

        var browseZipBtn = Action("选择专线版 ZIP", (_, _) =>
        {
            var open = new OpenFileDialog { Filter = "Hyper-V 专线版包|Claudome-HyperV_*_win-x64.zip|ZIP 压缩包|*.zip", Title = "选择已校验的 Claudome Hyper-V 专线版 ZIP" };
            if (open.ShowDialog(dialog) == true)
            {
                packageZipBox.Text = open.FileName;
                useCurrentRunningPayload.IsChecked = false;
            }
        });
        browseZipBtn.Padding = new Thickness(12, 6, 12, 6);

        var zipRow = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        DockPanel.SetDock(browseZipBtn, Dock.Right);
        browseZipBtn.Margin = new Thickness(8, 0, 0, 0);
        zipRow.Children.Add(browseZipBtn);
        zipRow.Children.Add(packageZipBox);
        zipRow.Visibility = useCurrentRunningPayload.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        useCurrentRunningPayload.Checked += (_, _) => zipRow.Visibility = Visibility.Collapsed;
        useCurrentRunningPayload.Unchecked += (_, _) => zipRow.Visibility = Visibility.Visible;

        var legacyRow = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        DockPanel.SetDock(browseLegacyBtn, Dock.Right);
        browseLegacyBtn.Margin = new Thickness(8, 0, 0, 0);
        legacyRow.Children.Add(browseLegacyBtn);
        legacyRow.Children.Add(legacyDirBox);

        inspectBtn = Action("预检并生成迁移清单", async (_, _) =>
        {
            RefreshHostUi();
            if (!currentHost.IsEligible) return;
            migrationBusy = true;
            inspectBtn!.IsEnabled = false;
            executeBtn!.IsEnabled = false;
            summaryBox.Text = "正在校验旧版目录、扫描已有任务并核验专线版安装包…";
            try
            {
                preparedPayload?.Dispose();
                preparedPayload = null;
                preparedPlan = null;

                var legacyInfo = LegacyMigration.InspectLegacyDirectory(legacyDirBox.Text);
                var tasks = LegacyMigration.DiscoverLegacyTasks();
                VerifiedHyperVPayload payload;
                if (useCurrentRunningPayload.IsChecked == true)
                {
                    var currentDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
                    payload = await LegacyMigration.StageHyperVPayloadFromDirectoryAsync(currentDir);
                }
                else
                {
                    payload = await LegacyMigration.StageHyperVPackageFromZipAsync(packageZipBox.Text, Path.GetTempPath());
                }
                preparedPayload = payload;
                preparedPlan = LegacyMigration.BuildPlan(currentHost, legacyInfo, payload, tasks);
                summaryBox.Text = preparedPlan.FormatSummary();
            }
            catch (Exception ex)
            {
                preparedPayload?.Dispose();
                preparedPayload = null;
                preparedPlan = null;
                summaryBox.Text = "预检未通过（未改动任何文件）：\n" + Redaction.SafeError(ex);
            }
            finally
            {
                migrationBusy = false;
                inspectBtn.IsEnabled = currentHost.IsEligible;
                executeBtn.IsEnabled = currentHost.IsEligible && preparedPlan is not null && preparedPayload is not null;
            }
        });

        executeBtn = Action("确认备份并升级到专线版", async (_, _) =>
        {
            if (preparedPlan is null || preparedPayload is null || !currentHost.IsEligible) return;
            var confirmMsg =
                $"即将把以下旧多线路版原地升级为 Claudome Hyper-V v{preparedPlan.TargetVersion}：\n\n" +
                $"• 旧版目录：{preparedPlan.LegacyInstall.InstallDirectory}\n" +
                $"• 完整备份目录：{preparedPlan.BackupDirectory}\n" +
                $"• 已有 Hyper-V 任务：{preparedPlan.HyperVTasks.Count} 个（升级后自动核对身份并续用）\n" +
                $"• 已有 VirtualBox / VMware 任务：{preparedPlan.LegacyVmTasks.Count} 个（磁盘与快照保留，可通过备份目录独立打开）\n\n" +
                "若升级中发生任何异常，程序将自动完整回退到原多线路版。是否立即执行？";
            if (MessageBox.Show(dialog, confirmMsg, "确认一键迁移到专线版", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            migrationBusy = true;
            inspectBtn.IsEnabled = false;
            executeBtn!.IsEnabled = false;
            recheckBtn.IsEnabled = false;
            summaryBox.Text = "正在完整备份旧多线路版并执行原子替换，请稍候…";
            try
            {
                var result = await LegacyMigration.ExecuteMigrationAsync(
                    preparedPlan,
                    preparedPayload,
                    liveVmVerifier: VerifyLiveHyperVVmBinding);
                summaryBox.Text = result.Summary + "\n\n" +
                    (result.VerifiedHyperVTasks.Count > 0
                        ? "Hyper-V 任务续用核对：\n" + string.Join("\n", result.VerifiedHyperVTasks.Select(v => $"  • {v.VmName}: {v.Detail}")) + "\n\n"
                        : "") +
                    (result.PreservedLegacyVmTasks.Count > 0
                        ? $"旧 VirtualBox / VMware 任务独立入口：\n  • {result.LegacyLauncherExePath}\n  • {result.LegacyLauncherCmdPath}\n"
                        : "");
                MessageBox.Show(dialog,
                    $"升级完成！\n\n{result.Summary}\n\n现在可以从目标目录启动新版 Claudome Hyper-V，或在备份目录打开旧多线路版任务。",
                    "迁移成功", MessageBoxButton.OK, MessageBoxImage.Information);
                preparedPlan = null;
            }
            catch (Exception ex)
            {
                summaryBox.Text = "迁移未完成：\n" + Redaction.SafeError(ex);
                MessageBox.Show(dialog, Redaction.SafeError(ex), "迁移已中止或回退", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                migrationBusy = false;
                recheckBtn.IsEnabled = true;
                RefreshHostUi();
            }
        }, true);
        executeBtn.Name = "ExecuteLegacyMigrationButton";

        var closeBtn = Action("取消 / 关闭", (_, _) => dialog.Close());
        dialog.Closed += (_, _) =>
        {
            preparedPayload?.Dispose();
            preparedPayload = null;
        };

        var rootPanel = new StackPanel { Margin = new Thickness(20) };
        rootPanel.Children.Add(new TextBlock { Text = "一键迁移：旧多线路版 → Hyper-V 专线版", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Ink, Margin = new Thickness(0, 0, 0, 4) });
        rootPanel.Children.Add(new TextBlock { Text = "先预检宿主机与扫描任务，再完整备份旧版并原地升级。旧 VirtualBox / VMware 任务磁盘、快照与记录全程保留。", FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        rootPanel.Children.Add(hostCard);
        rootPanel.Children.Add(new TextBlock { Text = "2. 选择旧多线路版程序目录（含 Claudome.exe，未运行过的解压副本也可选择）", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ink });
        rootPanel.Children.Add(legacyRow);
        rootPanel.Children.Add(useCurrentRunningPayload);
        rootPanel.Children.Add(zipRow);
        rootPanel.Children.Add(new TextBlock { Text = "3. 迁移前核对清单与任务处理说明", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ink, Margin = new Thickness(0, 4, 0, 4) });
        rootPanel.Children.Add(summaryBox);
        var footerRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        closeBtn.Margin = new Thickness(0, 0, 8, 0);
        inspectBtn.Margin = new Thickness(0, 0, 8, 0);
        footerRow.Children.Add(closeBtn);
        footerRow.Children.Add(inspectBtn);
        footerRow.Children.Add(executeBtn);
        rootPanel.Children.Add(footerRow);

        dialog.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rootPanel };
        RefreshHostUi();
        return dialog;
    }

    internal void OpenLegacyMigrationDialog()
    {
        var dialog = BuildLegacyMigrationDialog();
        dialog.ShowDialog();
    }
}
