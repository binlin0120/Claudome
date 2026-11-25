using System.IO;

namespace ClaudeSetup;

public static class StorageCleanupTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static Task Run(Func<string, Func<Task>, Task> check, string root)
    {
        var baseDir = Path.Combine(root, "storage-cleanup-suite");
        if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
        Directory.CreateDirectory(baseDir);

        return RunAllAsync();

        async Task RunAllAsync()
        {
            // C01: Preview two categories, manually delete ONLY one selected item; retain rest with explicit reasons.
            await check("cleanup-c01-preview-and-selective-manual-delete", () =>
            {
                var caseDir = Path.Combine(baseDir, "c01");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                var wsDir = Path.Combine(caseDir, "ws");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                Directory.CreateDirectory(wsDir);
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(tmpDir);

                var s = new SessionState
                {
                    VmId = "11111111-1111-1111-1111-111111111111",
                    Options = new() { Root = wsDir, VmName = "C01Box", WorkspaceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Remember = true },
                    CredentialsRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                };
                new CleanupSettings { AutoCleanTaskCheckpoint = false, AutoCleanRouteConfig = false }.Save(settingsPath);
                TaskCheckpointStore.Write(s, settingsPath, regPath);
                s.Completed.Add("step1");
                TaskCheckpointStore.Write(s, settingsPath, regPath);
                s.Completed.Add("step2");
                TaskCheckpointStore.Write(s, settingsPath, regPath); // Produces 1 cleanable history checkpoint + preserves .previous.json

                Directory.CreateDirectory(s.Options.Private);
                var oldCred = Path.Combine(s.Options.Private, "credentials-11111111111111111111111111111111.dpapi");
                var activeCred = Path.Combine(s.Options.Private, "credentials-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.dpapi");
                File.WriteAllText(oldCred, "old-route-secret");
                File.WriteAllText(activeCred, "active-route-secret");

                var ctx = new StorageCleanupContext("windows", appDir, tmpDir, [wsDir], settingsPath, regPath);
                var report = StorageCleanupService.Inspect(ctx);
                var cleanableHist = report.Items.Where(i => i.Category == BackupCategory.TaskCheckpoint && i.Status == CleanupItemStatus.Cleanable).ToList();
                var cleanableRoute = report.Items.Where(i => i.Category == BackupCategory.RouteConfig && i.Status == CleanupItemStatus.Cleanable).ToList();
                Assert(cleanableHist.Count == 1 && cleanableRoute.Count == 1, "C01 expected 1 cleanable item in each of two categories");
                Assert(report.Items.Any(i => i.Category == BackupCategory.TaskCheckpoint && i.Status == CleanupItemStatus.Retained && !string.IsNullOrEmpty(i.Reason)),
                    "C01 missing retained backup reason");

                var manual = StorageCleanupService.ExecuteManualCleanup([cleanableHist[0]], ctx);
                Assert(manual.DeletedCount == 1 &&
                       !File.Exists(cleanableHist[0].Path) &&
                       File.Exists(oldCred) &&
                       File.Exists(TaskCheckpointStore.BackupPath(s.Options.StatePath)),
                    "C01 manual delete did not remove only selected file");
                return Task.CompletedTask;
            });

            // C02: 3 consecutive verified successes rotate older backups per-task/per-install without affecting other task or category.
            await check("cleanup-c02-three-successes-rotate-same-scope-without-cross-cleanup", () =>
            {
                var caseDir = Path.Combine(baseDir, "c02");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                var wsA = Path.Combine(caseDir, "wsA");
                var wsB = Path.Combine(caseDir, "wsB");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                Directory.CreateDirectory(wsA);
                Directory.CreateDirectory(wsB);
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(tmpDir);
                new CleanupSettings().Save(settingsPath);

                var taskA = new SessionState { VmId = "11111111-1111-1111-1111-111111111111", Options = new() { Root = wsA, VmName = "TaskA", WorkspaceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Remember = true } };
                var taskB = new SessionState { VmId = "22222222-2222-2222-2222-222222222222", Options = new() { Root = wsB, VmName = "TaskB", WorkspaceId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Remember = true } };
                TaskCheckpointStore.Write(taskB, settingsPath, regPath);
                taskB.Completed.Add("b-prev");
                TaskCheckpointStore.Write(taskB, settingsPath, regPath);
                var bBackupFile = TaskCheckpointStore.BackupPath(taskB.Options.StatePath);

                taskA.Completed.Add("v1"); TaskCheckpointStore.Write(taskA, settingsPath, regPath);
                taskA.Completed.Add("v2"); TaskCheckpointStore.Write(taskA, settingsPath, regPath);
                taskA.Completed.Add("v3"); TaskCheckpointStore.Write(taskA, settingsPath, regPath);
                var histDirA = Path.Combine(wsA, "checkpoints-history");
                var histRemainingA = Directory.Exists(histDirA) ? Directory.GetFiles(histDirA).Length : 0;
                var prevA = TaskCheckpointStore.Read(TaskCheckpointStore.BackupPath(taskA.Options.StatePath));
                Assert(histRemainingA == 0 && prevA.Completed.Contains("v2") && !prevA.Completed.Contains("v3"),
                    "C02 did not rotate older checkpoint v1 while retaining v2");
                Assert(File.Exists(bBackupFile), "C02 rotation touched TaskB backup");

                // 3 consecutive route transitions on TaskA
                var rev1 = Guid.NewGuid().ToString("N");
                var rev2 = Guid.NewGuid().ToString("N");
                var rev3 = Guid.NewGuid().ToString("N");
                SecretStore.Save(taskA.Options.Private, new Secrets { ExpectedExitIp = "1.1.1.1" }, rev1);
                SecretStore.Save(taskA.Options.Private, new Secrets { ExpectedExitIp = "2.2.2.2" }, rev2);
                SecretStore.Save(taskA.Options.Private, new Secrets { ExpectedExitIp = "3.3.3.3" }, rev3);
                var prevState = Clone(taskA); prevState.CredentialsRevision = rev2;
                taskA.CredentialsRevision = rev3;
                TaskCheckpointStore.Write(prevState, settingsPath, regPath); // puts rev2 in .previous.json
                TaskCheckpointStore.Write(taskA, settingsPath, regPath);     // puts rev3 in primary, rev2 in .previous.json
                SecretStore.PruneRevisions(taskA, prevState, settingsPath, regPath);
                Assert(!File.Exists(Path.Combine(taskA.Options.Private, $"credentials-{rev1}.dpapi")) &&
                       File.Exists(Path.Combine(taskA.Options.Private, $"credentials-{rev2}.dpapi")) &&
                       File.Exists(Path.Combine(taskA.Options.Private, $"credentials-{rev3}.dpapi")),
                    "C02 route rotation did not clean rev1 while keeping rev2 and rev3");
                return Task.CompletedTask;
            });

            // C03: Failed/pending updates or corrupt primary hold recovery materials as Pending/Retained; auto-clean never deletes them.
            await check("cleanup-c03-failed-or-pending-ops-protect-recovery-backups", () =>
            {
                var caseDir = Path.Combine(baseDir, "c03");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var wsDir = Path.Combine(caseDir, "ws");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(tmpDir);
                Directory.CreateDirectory(wsDir);
                new CleanupSettings().Save(settingsPath);

                var exeBak = Path.Combine(appDir, "Claudome.exe.777.bak");
                File.WriteAllText(exeBak, "old-exe");
                File.WriteAllText(Path.Combine(appDir, SelfUpdater.UpdateErrorMarkerName), "Required file locked");
                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath);
                Assert(File.Exists(exeBak), "C03 CleanupStaleUpdateFiles deleted rollback backup while error marker existed");

                var ctx = new StorageCleanupContext("windows", appDir, tmpDir, [wsDir], settingsPath, regPath);
                var report = StorageCleanupService.Inspect(ctx);
                Assert(report.Items.Any(i => i.Path.Equals(exeBak, StringComparison.OrdinalIgnoreCase) && i.Status == CleanupItemStatus.Pending),
                    "C03 failed update backup not marked Pending");
                return Task.CompletedTask;
            });

            // C06 & C07: Foreign staging, similar filenames, ZIPs, links skipped automatically; user ZIP preview + locked/mutated item skipped cleanly on delete.
            await check("cleanup-c06-c07-isolation-lookalikes-zip-preview-and-locked-retry", () =>
            {
                var caseDir = Path.Combine(baseDir, "c06-07");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                var appDir = Path.Combine(caseDir, "app");
                var otherAppDir = Path.Combine(caseDir, "other-app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var wsDir = Path.Combine(caseDir, "ws");
                var archiveDir = Path.Combine(caseDir, "archives");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(otherAppDir);
                Directory.CreateDirectory(tmpDir);
                Directory.CreateDirectory(wsDir);
                Directory.CreateDirectory(archiveDir);
                new CleanupSettings().Save(settingsPath);

                var similarFile = Path.Combine(appDir, "Claudome.exe.similar-backup.bak");
                File.WriteAllText(similarFile, "keep-me");
                var foreignStage = Path.Combine(tmpDir, "claudome-stage-other-install");
                Directory.CreateDirectory(foreignStage);
                File.WriteAllText(Path.Combine(foreignStage, "data.bin"), "foreign");
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord
                {
                    Category = BackupCategory.ProgramUpdate,
                    ScopeId = Path.GetFullPath(otherAppDir),
                    ScopeLabel = "OtherInstall",
                    Path = foreignStage,
                    StageRoot = foreignStage,
                    OpState = BackupOpState.VerifiedSuccess
                }, regPath);

                var unownedStage = Path.Combine(tmpDir, "claudome-stage-unregistered");
                Directory.CreateDirectory(unownedStage);
                File.WriteAllText(Path.Combine(unownedStage, "unknown.bin"), "keep");

                var oldZip = Path.Combine(archiveDir, "Claudome_0.4.6_win-x64.zip");
                var lockedZip = Path.Combine(archiveDir, "Claudome_0.4.7_win-x64.zip");
                var latestZip = Path.Combine(archiveDir, "Claudome_0.4.8_win-x64.zip");
                File.WriteAllText(oldZip, "z1"); File.SetLastWriteTimeUtc(oldZip, DateTime.UtcNow.AddDays(-3));
                File.WriteAllText(lockedZip, "z2"); File.SetLastWriteTimeUtc(lockedZip, DateTime.UtcNow.AddDays(-2));
                File.WriteAllText(latestZip, "z3"); File.SetLastWriteTimeUtc(latestZip, DateTime.UtcNow.AddDays(-1));

                var ctx = new StorageCleanupContext("windows", appDir, tmpDir, [wsDir], settingsPath, regPath, ArchiveDirectory: archiveDir);
                Assert(!StorageCleanupService.IsWithinAuthorizedDirectory(Path.Combine(caseDir, "outside.txt"), appDir),
                    "C06 path outside authorized root was accepted");
                try
                {
                    var linkDir = Path.Combine(tmpDir, "claudome-stage-link");
                    Directory.CreateSymbolicLink(linkDir, otherAppDir);
                    Assert(StorageCleanupService.IsReparsePointPath(linkDir), "C06 symlink detection failed");
                }
                catch (IOException) { /* Non-elevated Windows without Developer Mode cannot create symlinks; boundary guard already verified above. */ }
                // Auto cleanup must NEVER touch archiveDir, foreignStage, unownedStage, or similarFile.
                StorageCleanupService.RotateAfterVerifiedSuccess(BackupCategory.ProgramUpdate, appDir, ctx);
                Assert(File.Exists(oldZip) && File.Exists(lockedZip) && File.Exists(latestZip) &&
                       Directory.Exists(foreignStage) && Directory.Exists(unownedStage) && File.Exists(similarFile),
                    "C06 auto cleanup deleted foreign/unowned/archive/lookalike item");

                var report = StorageCleanupService.Inspect(ctx);
                Assert(report.Items.Any(i => i.Path.Equals(latestZip, StringComparison.OrdinalIgnoreCase) && i.Status == CleanupItemStatus.Retained),
                    "C06 latest user archive was not defaulted to Retained");

                var cleanZips = report.Items.Where(i => i.Category == BackupCategory.UserArchives && i.Status == CleanupItemStatus.Cleanable).ToList();
                Assert(cleanZips.Count == 2, $"C06 expected 2 cleanable older ZIPs, got {cleanZips.Count}");

                // Lock one ZIP while executing manual cleanup -> partial delete + exact freed byte count + skip reason (C07).
                CleanupExecutionResult execRes;
                using (var zipLock = new FileStream(lockedZip, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    execRes = StorageCleanupService.ExecuteManualCleanup(cleanZips, ctx);
                }
                Assert(execRes.DeletedCount == 1 && execRes.SkippedCount == 1 &&
                       execRes.FreedBytes == 2 && !File.Exists(oldZip) && File.Exists(lockedZip) && File.Exists(latestZip),
                    $"C07 partial cleanup mismatch (deleted={execRes.DeletedCount}, skipped={execRes.SkippedCount}, freed={execRes.FreedBytes})");
                return Task.CompletedTask;
            });

            // C08: Disabling category auto-clean persists across reload and retains older backups for manual preview/cleanup.
            await check("cleanup-c08-disabled-auto-clean-persists-and-retains-until-manual", () =>
            {
                var caseDir = Path.Combine(baseDir, "c08");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                var wsDir = Path.Combine(caseDir, "ws");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                Directory.CreateDirectory(wsDir);
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(tmpDir);

                var s = new CleanupSettings { AutoCleanTaskCheckpoint = false, AutoCleanProgramUpdate = false };
                s.Save(settingsPath);
                var reloaded = CleanupSettings.Load(settingsPath);
                Assert(!reloaded.AutoCleanTaskCheckpoint && !reloaded.AutoCleanProgramUpdate && reloaded.AutoCleanRouteConfig,
                    "C08 settings did not persist across reload");

                var task = new SessionState { VmId = "33333333-3333-3333-3333-333333333333", Options = new() { Root = wsDir, VmName = "C08Box", WorkspaceId = "cccccccccccccccccccccccccccccccc" } };
                task.Completed.Add("1"); TaskCheckpointStore.Write(task, settingsPath, regPath);
                task.Completed.Add("2"); TaskCheckpointStore.Write(task, settingsPath, regPath);
                task.Completed.Add("3"); TaskCheckpointStore.Write(task, settingsPath, regPath);

                var histFiles = Directory.GetFiles(Path.Combine(wsDir, "checkpoints-history"), "setup-state.prev-*.json");
                Assert(histFiles.Length == 1, "C08 disabled auto-clean deleted history checkpoint instead of keeping it for manual cleanup");

                var ctx = new StorageCleanupContext("windows", appDir, tmpDir, [wsDir], settingsPath, regPath);
                var cleanables = StorageCleanupService.Inspect(ctx).Items.Where(i => i.Status == CleanupItemStatus.Cleanable).ToList();
                Assert(cleanables.Count == 1, "C08 disabled category should still expose manual cleanable candidate");
                var manualRes = StorageCleanupService.ExecuteManualCleanup(cleanables, ctx);
                Assert(manualRes.DeletedCount == 1 && Directory.GetFiles(Path.Combine(wsDir, "checkpoints-history")).Length == 0,
                    "C08 manual cleanup failed after auto-clean was disabled");
                return Task.CompletedTask;
            });

            // B01: Files modified/replaced (even with IDENTICAL size & timestamp) AND directories whose members changed with identical size & timestamp are skipped.
            await check("cleanup-b01-post-preview-file-and-dir-changes-are-skipped", () =>
            {
                var caseDir = Path.Combine(baseDir, "b01");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var archiveDir = Path.Combine(caseDir, "archives");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(tmpDir);
                Directory.CreateDirectory(archiveDir);
                new CleanupSettings().Save(settingsPath);

                var fixedTime = DateTime.UtcNow.AddDays(-6);
                var zipA = Path.Combine(archiveDir, "Claudome_0.4.4_win-x64.zip");
                var zipB = Path.Combine(archiveDir, "Claudome_0.4.5_win-x64.zip");
                var zipSameMeta = Path.Combine(archiveDir, "Claudome_0.4.6_win-x64.zip");
                var zipKeep = Path.Combine(archiveDir, "Claudome_0.4.8_win-x64.zip");
                File.WriteAllText(zipA, new string('a', 24)); File.SetLastWriteTimeUtc(zipA, DateTime.UtcNow.AddDays(-7));
                File.WriteAllText(zipB, new string('b', 24)); File.SetLastWriteTimeUtc(zipB, DateTime.UtcNow.AddDays(-5));
                File.WriteAllText(zipSameMeta, new string('s', 24)); File.SetLastWriteTimeUtc(zipSameMeta, fixedTime);
                File.WriteAllText(zipKeep, new string('k', 24)); File.SetLastWriteTimeUtc(zipKeep, DateTime.UtcNow.AddDays(-1));

                var stageDir = Path.Combine(tmpDir, "claudome-stage-b01");
                Directory.CreateDirectory(stageDir);
                var stageMember = Path.Combine(stageDir, "Claudome.exe");
                var memberTime = DateTime.UtcNow.AddDays(-2);
                File.WriteAllText(stageMember, "payload");
                File.SetLastWriteTimeUtc(stageMember, memberTime);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord
                {
                    Category = BackupCategory.ProgramUpdate,
                    ScopeId = Path.GetFullPath(appDir),
                    ScopeLabel = "B01App",
                    Path = stageDir,
                    StageRoot = stageDir,
                    Role = "staging",
                    OpState = BackupOpState.VerifiedSuccess
                }, regPath);

                var ctx = new StorageCleanupContext("windows", appDir, tmpDir, [], settingsPath, regPath, ArchiveDirectory: archiveDir);
                var preview = StorageCleanupService.Inspect(ctx);
                var candA = preview.Items.Single(i => i.Path.Equals(zipA, StringComparison.OrdinalIgnoreCase));
                var candB = preview.Items.Single(i => i.Path.Equals(zipB, StringComparison.OrdinalIgnoreCase));
                var candSameMeta = preview.Items.Single(i => i.Path.Equals(zipSameMeta, StringComparison.OrdinalIgnoreCase));
                var candDir = preview.Items.Single(i => i.Path.Equals(stageDir, StringComparison.OrdinalIgnoreCase));

                // Mutate after preview:
                // (1) Grow zipA with different mtime; (2) Rename away and recreate zipB;
                // (3) Replace zipSameMeta keeping EXACT byte length (24) and EXACT LastWriteTimeUtc;
                // (4) Overwrite stageDir member keeping EXACT byte length (7) and EXACT LastWriteTimeUtc.
                File.WriteAllText(zipA, new string('x', 96));
                File.SetLastWriteTimeUtc(zipA, DateTime.UtcNow.AddDays(-3));
                File.Move(zipB, zipB + ".old");
                File.WriteAllText(zipB, new string('y', 48));
                File.SetLastWriteTimeUtc(zipB, DateTime.UtcNow.AddDays(-2));
                File.WriteAllText(zipSameMeta, new string('z', 24));
                File.SetLastWriteTimeUtc(zipSameMeta, fixedTime);
                File.WriteAllText(stageMember, "tamper!");
                File.SetLastWriteTimeUtc(stageMember, memberTime);

                var res = StorageCleanupService.ExecuteManualCleanup([candA, candB, candSameMeta, candDir], ctx);
                Assert(res.DeletedCount == 0 && res.SkippedCount == 4 && res.FreedBytes == 0 &&
                       File.Exists(zipA) && File.Exists(zipB) &&
                       File.ReadAllText(zipSameMeta) == new string('z', 24) &&
                       File.ReadAllText(stageMember) == "tamper!",
                    $"B01 deleted modified/replaced items after preview (deleted={res.DeletedCount}, skipped={res.SkippedCount}, freed={res.FreedBytes})");
                return Task.CompletedTask;
            });

            // B02: Startup cleanup never deletes unowned items OR InProgress updates when second instance / cancelled / crashed / unverified binary starts.
            await check("cleanup-b02-startup-cleanup-matches-preview-retentions", () =>
            {
                var caseDir = Path.Combine(baseDir, "b02");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                var appDir = Path.Combine(caseDir, "app");
                var otherAppDir = Path.Combine(caseDir, "other-app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(otherAppDir);
                Directory.CreateDirectory(tmpDir);
                new CleanupSettings().Save(settingsPath);

                var unownedBak = Path.Combine(appDir, "Claudome.exe.999.bak");
                File.WriteAllText(unownedBak, "unknown-bak");
                var unownedStage = Path.Combine(tmpDir, "claudome-stage-unowned-b02");
                Directory.CreateDirectory(unownedStage);
                File.WriteAllText(Path.Combine(unownedStage, "doc.txt"), "keep");
                var unownedCmd = Path.Combine(tmpDir, "claudome-update-unowned.cmd");
                File.WriteAllText(unownedCmd, "echo hi");
                var otherStage = Path.Combine(tmpDir, "claudome-stage-other-b02");
                Directory.CreateDirectory(otherStage);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord
                {
                    Category = BackupCategory.ProgramUpdate,
                    ScopeId = Path.GetFullPath(otherAppDir),
                    ScopeLabel = "Other",
                    Path = otherStage,
                    StageRoot = otherStage,
                    Role = "staging",
                    OpState = BackupOpState.InProgress
                }, regPath);

                // Register an active InProgress update in the SAME appDir (second instance, crash/cancel, wrong EXE).
                var targetExe = Path.Combine(appDir, "Claudome.exe");
                var newExePayload = Path.Combine(tmpDir, "new-binary.bin");
                File.WriteAllText(targetExe, "old-running-exe");
                File.WriteAllText(Path.Combine(appDir, "PROJECT-LICENSE.txt"), "b02-lic");
                File.WriteAllText(Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt"), "b02-notices");
                File.WriteAllText(newExePayload, "verified-new-exe-bytes");
                var newExeSha = StorageCleanupService.ComputeFileSha256Safe(newExePayload);

                var activeStage = Path.Combine(tmpDir, "claudome-stage-active-b02");
                var activeScript = Path.Combine(tmpDir, "claudome-update-active-b02.cmd");
                var activeBak = Path.Combine(appDir, "Claudome.exe.555.bak");
                Directory.CreateDirectory(activeStage);
                File.WriteAllText(activeScript, "@echo off");
                File.WriteAllText(activeBak, "bak-555");
                const string activeTx = "txb02active";
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), ScopeLabel = "B02", Path = activeStage, StageRoot = activeStage, TransactionId = activeTx, Role = "staging", OpState = BackupOpState.InProgress }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), ScopeLabel = "B02", Path = activeScript, StageRoot = activeStage, TransactionId = activeTx, Role = "swap-script", OpState = BackupOpState.InProgress }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), ScopeLabel = "B02", Path = activeBak, StageRoot = activeStage, TransactionId = activeTx, Role = "backup", OpState = BackupOpState.InProgress }, regPath);
                JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName), new SelfUpdater.PendingUpdateTransaction(activeTx, newExeSha, targetExe, activeStage, activeScript, DateTime.UtcNow));

                var ctx = new StorageCleanupContext("windows", appDir, tmpDir, [], settingsPath, regPath);
                var preview = StorageCleanupService.Inspect(ctx);
                Assert(preview.Items.All(i => i.Status != CleanupItemStatus.Cleanable),
                    "B02 preview should retain/pend all unowned, foreign, and InProgress artifacts");

                // 1. Second instance in SAME appDir with old EXE and NO completed receipt -> deletes NOTHING!
                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: targetExe);
                Assert(Directory.Exists(activeStage) && File.Exists(activeScript) && File.Exists(activeBak),
                    "B02 second instance deleted InProgress update before completion");

                // 2. Even after swap receipt written, launching ANOTHER binary (wrong EXE SHA/path) -> deletes NOTHING!
                File.WriteAllText(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName), $"{{\"transactionId\":\"{activeTx}\",\"swapSucceeded\":true}}");
                var foreignCaller = Path.Combine(appDir, "OtherTool.exe");
                File.WriteAllText(foreignCaller, "other-tool");
                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: foreignCaller);
                Assert(Directory.Exists(activeStage) && File.Exists(activeScript),
                    "B02 unverified caller EXE cleared InProgress update artifacts");

                // 3. Genuine swapped new EXE starts first time -> cleans activeTx script+stage, KEEPS rollback activeBak + all unowned/other items!
                File.Copy(newExePayload, targetExe, overwrite: true);
                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: targetExe);
                Assert(!Directory.Exists(activeStage) && !File.Exists(activeScript) && File.Exists(activeBak) &&
                       File.Exists(unownedBak) && Directory.Exists(unownedStage) &&
                       File.Exists(unownedCmd) && Directory.Exists(otherStage),
                    "B02 genuine first startup did not preserve rollback EXE/unowned items or clean verified staging/script");
                return Task.CompletedTask;
            });

            // B03: Real cmd.exe swap preserves complete EXE + required sidecar rollback group, and rotates older groups across 3 consecutive updates.
            await check("cleanup-b03-deleted-staging-never-evicts-rollback-exe-or-group", () =>
            {
                var caseDir = Path.Combine(baseDir, "b03");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(tmpDir);
                new CleanupSettings().Save(settingsPath);

                // Seed v1 installation in appDir
                var exePath = Path.Combine(appDir, "Claudome.exe");
                var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                File.WriteAllText(exePath, "exe-v1");
                File.WriteAllText(licPath, "lic-v1");
                File.WriteAllText(notPath, "not-v1");

                // Run 3 real consecutive cmd.exe swap updates (v1 -> v2 -> v3 -> v4)
                foreach (var (ver, fakePid) in new[] { (2, 999991), (3, 999993), (4, 999995) })
                {
                    var stage = Path.Combine(tmpDir, $"claudome-stage-v{ver}");
                    Directory.CreateDirectory(stage);
                    File.WriteAllText(Path.Combine(stage, "Claudome.exe"), $"exe-v{ver}");
                    File.WriteAllText(Path.Combine(stage, "PROJECT-LICENSE.txt"), $"lic-v{ver}");
                    File.WriteAllText(Path.Combine(stage, "Gateway-THIRD-PARTY-NOTICES.txt"), $"not-v{ver}");
                    var stagedSha = StorageCleanupService.ComputeFileSha256Safe(Path.Combine(stage, "Claudome.exe"));
                    var txId = $"tx-v{ver}";
                    var cmdPath = Path.Combine(tmpDir, $"claudome-update-v{ver}.cmd");

                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = Path.Combine(appDir, $"Claudome.exe.{fakePid}.bak"), StageRoot = stage, TransactionId = txId, Role = "backup", OpState = BackupOpState.InProgress }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = Path.Combine(appDir, $"PROJECT-LICENSE.txt.{fakePid}.bak"), StageRoot = stage, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = Path.Combine(appDir, $"Gateway-THIRD-PARTY-NOTICES.txt.{fakePid}.bak"), StageRoot = stage, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = stage, StageRoot = stage, TransactionId = txId, Role = "staging", OpState = BackupOpState.InProgress }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = cmdPath, StageRoot = stage, TransactionId = txId, Role = "swap-script", OpState = BackupOpState.InProgress }, regPath);

                    JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName), new SelfUpdater.PendingUpdateTransaction(txId, stagedSha, exePath, stage, cmdPath, DateTime.UtcNow));
                    var scriptText = SelfUpdater.BuildSwapScript(appDir, stage, stage, "Claudome.exe", $"Claudome.exe.{fakePid}.bak", fakePid, relaunch: false, transactionId: txId);
                    File.WriteAllText(cmdPath, scriptText, new System.Text.UTF8Encoding(false));
                    using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", SelfUpdater.BuildCmdArguments(cmdPath))
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetTempPath()
                    })!;
                    Assert(proc.WaitForExit(15000) && proc.ExitCode == 0, $"B03 real swap v{ver} failed (exit={proc.ExitCode})");

                    // Simulate new version startup verification & rotation
                    SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                }

                // After v1 -> v2 -> v3 -> v4: ONLY v3 rollback group (pid=999995: exe-v3, lic-v3, not-v3) stays on disk; v1 and v2 groups rotated cleanly!
                Assert(File.ReadAllText(exePath) == "exe-v4" &&
                       File.ReadAllText(Path.Combine(appDir, "Claudome.exe.999995.bak")) == "exe-v3" &&
                       File.ReadAllText(Path.Combine(appDir, "PROJECT-LICENSE.txt.999995.bak")) == "lic-v3" &&
                       File.ReadAllText(Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.999995.bak")) == "not-v3" &&
                       !File.Exists(Path.Combine(appDir, "Claudome.exe.999991.bak")) &&
                       !File.Exists(Path.Combine(appDir, "PROJECT-LICENSE.txt.999991.bak")) &&
                       !File.Exists(Path.Combine(appDir, "Claudome.exe.999993.bak")) &&
                       !File.Exists(Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.999993.bak")),
                    "B03 real 3-update rotation did not keep full v3 EXE+required-sidecars rollback group or clean older v1/v2 groups");
                return Task.CompletedTask;
            });

            // B04: Route backup materials must remain Pending/unselectable when primary/backup states are corrupted or route change failed.
            await check("cleanup-b04-corrupt-or-recovering-task-protects-route-revisions", () =>
            {
                var caseDir = Path.Combine(baseDir, "b04");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                var wsDir = Path.Combine(caseDir, "ws");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                Directory.CreateDirectory(wsDir);
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(tmpDir);
                new CleanupSettings().Save(settingsPath);

                var task = new SessionState
                {
                    VmId = "44444444-4444-4444-4444-444444444444",
                    Options = new() { Root = wsDir, VmName = "B04Box", WorkspaceId = "dddddddddddddddddddddddddddddddd", Remember = true }
                };
                var rPrev = Guid.NewGuid().ToString("N");
                var rActive = Guid.NewGuid().ToString("N");
                var rOld = Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(task.Options.Private);
                File.WriteAllText(Path.Combine(task.Options.Private, $"credentials-{rOld}.dpapi"), "old");
                File.WriteAllText(Path.Combine(task.Options.Private, $"credentials-{rPrev}.dpapi"), "prev");
                File.WriteAllText(Path.Combine(task.Options.Private, $"credentials-{rActive}.dpapi"), "active");

                task.CredentialsRevision = rPrev; TaskCheckpointStore.Write(task, settingsPath, regPath);
                task.CredentialsRevision = rActive; TaskCheckpointStore.Write(task, settingsPath, regPath);

                var ctx = new StorageCleanupContext("windows", appDir, tmpDir, [wsDir], settingsPath, regPath);

                // (1) 主坏备好 -> both rPrev (recovery ref) and rOld MUST be Pending & CanSelect=false; auto-rotate deletes nothing
                File.WriteAllText(task.Options.StatePath, "{broken");
                var repPrimaryBad = StorageCleanupService.Inspect(ctx);
                Assert(repPrimaryBad.Items.Where(i => i.Path.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase))
                    .All(i => !i.CanSelect && i.Status == CleanupItemStatus.Pending),
                    "B04 primary-corrupt state allowed deleting route credentials");
                StorageCleanupService.RotateAfterVerifiedSuccess(BackupCategory.RouteConfig, task.Options.WorkspaceId, ctx);
                Assert(File.Exists(Path.Combine(task.Options.Private, $"credentials-{rOld}.dpapi")),
                    "B04 primary-corrupt auto-clean deleted credential");

                // (2) 主备双坏 -> ALL credentials remain Pending & CanSelect=false
                File.WriteAllText(TaskCheckpointStore.BackupPath(task.Options.StatePath), "{\"schema\":1,\"options\":null}");
                var repBothBad = StorageCleanupService.Inspect(ctx);
                var dpapiItems = repBothBad.Items.Where(i => i.Path.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase)).ToList();
                Assert(dpapiItems.Count == 3 && dpapiItems.All(i => i.Status == CleanupItemStatus.Pending && !i.CanSelect),
                    "B04 dual-corrupt state left route credential Cleanable");

                // (3) Valid states restored BUT registry marks failed/in-progress RouteConfig -> older credential stays Pending
                task.CredentialsRevision = rPrev; File.Delete(TaskCheckpointStore.BackupPath(task.Options.StatePath)); File.Delete(task.Options.StatePath);
                TaskCheckpointStore.Write(task, settingsPath, regPath);
                task.CredentialsRevision = rActive; TaskCheckpointStore.Write(task, settingsPath, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord
                {
                    Category = BackupCategory.RouteConfig,
                    ScopeId = task.Options.WorkspaceId,
                    Path = Path.Combine(task.Options.Private, $"credentials-{rOld}.dpapi"),
                    Role = "route-revision",
                    OpState = BackupOpState.FailedOrCancelled
                }, regPath);
                var repFailedRoute = StorageCleanupService.Inspect(ctx);
                var oldItem = repFailedRoute.Items.Single(i => i.DisplayName.Contains(rOld));
                Assert(oldItem.Status == CleanupItemStatus.Pending && !oldItem.CanSelect,
                    "B04 failed route registry record did not guard credentials");
                return Task.CompletedTask;
            });

            // B05: Single historical ZIP in explicit directory MUST remain Retained by default, even after deleting older ZIPs.
            await check("cleanup-b05-single-remaining-archive-stays-retained", () =>
            {
                var caseDir = Path.Combine(baseDir, "b05");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var archiveDir = Path.Combine(caseDir, "archives");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(tmpDir);
                Directory.CreateDirectory(archiveDir);
                new CleanupSettings().Save(settingsPath);

                var singleZip = Path.Combine(archiveDir, "Claudome_0.4.8_win-x64.zip");
                File.WriteAllText(singleZip, "only-archive");
                var ctx = new StorageCleanupContext("windows", appDir, tmpDir, [], settingsPath, regPath, ArchiveDirectory: archiveDir);
                var singleRep = StorageCleanupService.Inspect(ctx);
                var soleItem = singleRep.Items.Single(i => i.Category == BackupCategory.UserArchives);
                Assert(soleItem.Status == CleanupItemStatus.Retained && !soleItem.CanSelect,
                    "B05 sole historical ZIP was marked Cleanable instead of Retained");

                // Add older ZIP -> only older ZIP becomes cleanable -> after deleting it, remaining 1 ZIP STILL Retained!
                var olderZip = Path.Combine(archiveDir, "Claudome_0.4.7_win-x64.zip");
                File.WriteAllText(olderZip, "older-archive");
                File.SetLastWriteTimeUtc(olderZip, DateTime.UtcNow.AddDays(-2));
                var pairRep = StorageCleanupService.Inspect(ctx);
                var cleanables = pairRep.Items.Where(i => i.Category == BackupCategory.UserArchives && i.Status == CleanupItemStatus.Cleanable).ToList();
                Assert(cleanables.Count == 1 && cleanables[0].Path.Equals(olderZip, StringComparison.OrdinalIgnoreCase),
                    "B05 expected only older ZIP to be cleanable");
                StorageCleanupService.ExecuteManualCleanup(cleanables, ctx);

                var afterRep = StorageCleanupService.Inspect(ctx);
                var remaining = afterRep.Items.Single(i => i.Category == BackupCategory.UserArchives);
                Assert(remaining.Path.Equals(singleZip, StringComparison.OrdinalIgnoreCase) &&
                       remaining.Status == CleanupItemStatus.Retained && !remaining.CanSelect,
                    "B05 last remaining ZIP flipped to Cleanable after older ZIP deleted");
                return Task.CompletedTask;
            });

            // Blocker 2-a: Locked required sidecar during rollback MUST preserve .bak, record incomplete rollback state,
            // prevent auto/manual cleanup, and resume cleanly without ZIP archive once unlocked.
            await check("cleanup-b06-partial-rollback-required-sidecar-preserved-and-resumed", () =>
            {
                var caseDir = Path.Combine(baseDir, "b06-sidecar");
                var appDir = Path.Combine(caseDir, "验收 目录 (中文)");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var stageDir = Path.Combine(tmpDir, "claudome-stage-b06");
                var payloadDir = Path.Combine(stageDir, "payload");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(payloadDir);
                new CleanupSettings().Save(settingsPath);

                var exePath = Path.Combine(appDir, "Claudome.exe");
                var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                File.WriteAllText(exePath, "old-exe");
                File.WriteAllText(licPath, "old-lic");
                File.WriteAllText(notPath, "old-notices");
                var oldExeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);
                var oldLicSha = StorageCleanupService.ComputeFileSha256Safe(licPath);
                var oldNotSha = StorageCleanupService.ComputeFileSha256Safe(notPath);

                const int pid = 999996;
                var txId = "tx-b06-sidecar";
                SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", pid, txId);

                // Simulate mid-swap state right when ROLLBACK enters after PROJECT-LICENSE.txt was already replaced and backed up.
                var exeBak = Path.Combine(appDir, $"Claudome.exe.{pid}.bak");
                var licBak = Path.Combine(appDir, $"PROJECT-LICENSE.txt.{pid}.bak");
                var notBak = Path.Combine(appDir, $"Gateway-THIRD-PARTY-NOTICES.txt.{pid}.bak");
                File.Copy(exePath, exeBak, overwrite: true);
                File.Copy(licPath, licBak, overwrite: true);
                File.Copy(notPath, notBak, overwrite: true);
                File.WriteAllText(exePath, "new-exe");
                File.WriteAllText(licPath, "new-lic");
                File.WriteAllText(Path.Combine(payloadDir, "Claudome.exe"), "new-exe");
                File.WriteAllText(Path.Combine(payloadDir, "PROJECT-LICENSE.txt"), "new-lic");
                File.WriteAllText(Path.Combine(payloadDir, "Gateway-THIRD-PARTY-NOTICES.txt"), "new-notices");

                var cmdPath = Path.Combine(tmpDir, "claudome-update-b06.cmd");
                JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                    new SelfUpdater.PendingUpdateTransaction(txId, StorageCleanupService.ComputeFileSha256Safe(Path.Combine(payloadDir, "Claudome.exe")), exePath, stageDir, cmdPath, DateTime.UtcNow));
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord
                {
                    Category = BackupCategory.ProgramUpdate,
                    ScopeId = Path.GetFullPath(appDir),
                    ScopeLabel = appDir,
                    Path = licBak,
                    StageRoot = stageDir,
                    TransactionId = txId,
                    Role = "backup-sidecar",
                    OpState = BackupOpState.InProgress
                }, regPath);

                var scriptText = SelfUpdater.BuildSwapScript(appDir, payloadDir, stageDir, "Claudome.exe", $"Claudome.exe.{pid}.bak", pid, relaunch: false, transactionId: txId)
                    .Replace("if errorlevel 1 goto DO_SWAP", "set \"FAILED_REASON=Required file locked or unwritable: Gateway-THIRD-PARTY-NOTICES.txt\"\r\ngoto ROLLBACK");
                File.WriteAllText(cmdPath, scriptText, new System.Text.UTF8Encoding(false));

                int exitCode;
                using (var licLock = new FileStream(licPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", SelfUpdater.BuildCmdArguments(cmdPath))
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetTempPath()
                    })!;
                    Assert(proc.WaitForExit(15000), "B06 rollback script timed out");
                    exitCode = proc.ExitCode;
                }

                Assert(exitCode != 0 &&
                       File.ReadAllText(exePath) == "old-exe" &&
                       File.ReadAllText(licPath) == "new-lic" &&
                       File.Exists(licBak) &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)) &&
                       File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                       File.Exists(Path.Combine(appDir, SelfUpdater.UpdateUnrecoveredListName)) &&
                       File.Exists(Path.Combine(appDir, SelfUpdater.UpdateErrorMarkerName)),
                    $"B06 incomplete rollback did not preserve .bak or state markers (exit={exitCode})");

                var notice = SelfUpdater.ReadPendingUpdateNotice(appDir);
                var uiDisplay = SetupWindow.DescribePendingUpdateNotice(notice);
                Assert(notice.IsRollbackIncomplete &&
                       notice.UnrecoveredFiles is not null &&
                       notice.UnrecoveredFiles.Contains("PROJECT-LICENSE.txt", StringComparer.OrdinalIgnoreCase) &&
                       uiDisplay is { ClearErrorMarker: false, ActionLabel: "继续恢复原版" } d &&
                       !d.Message.Contains("已恢复原版", StringComparison.Ordinal) &&
                       d.Message.Contains("PROJECT-LICENSE.txt", StringComparison.Ordinal),
                    "B06 ReadPendingUpdateNotice or UI banner misrepresented incomplete rollback");

                var ctx = new StorageCleanupContext("windows", appDir, tmpDir, [], settingsPath, regPath);
                var inspection = StorageCleanupService.Inspect(ctx);
                var updateItems = inspection.Items.Where(i => i.Category == BackupCategory.ProgramUpdate).ToList();
                var licBakItem = updateItems.Single(i => i.Path.Equals(licBak, StringComparison.OrdinalIgnoreCase));
                Assert(updateItems.Count > 0 &&
                       updateItems.All(i => !i.CanSelect && i.Status != CleanupItemStatus.Cleanable) &&
                       licBakItem.Status == CleanupItemStatus.Pending,
                    "B06 StorageCleanup marked incomplete rollback artifacts selectable/cleanable");

                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath);
                Assert(File.Exists(licBak) &&
                       File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                       File.Exists(Path.Combine(appDir, SelfUpdater.UpdateErrorMarkerName)),
                    "B06 CleanupStaleUpdateFiles deleted incomplete rollback artifacts");

                // While still locked, ResumePendingRollback must fail closed and preserve .bak + markers.
                using (var stillLocked = new FileStream(licPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var failResume = SelfUpdater.ResumePendingRollback(appDir, regPath);
                    Assert(!failResume.Succeeded &&
                           failResume.RemainingFiles.Contains("PROJECT-LICENSE.txt", StringComparer.OrdinalIgnoreCase) &&
                           File.Exists(licBak) &&
                           File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)),
                        "B06 ResumePendingRollback claimed success while target remained locked");
                }

                // Remove staged payload/ZIP completely, then resume offline from local .bak only!
                Directory.Delete(stageDir, recursive: true);
                var okResume = SelfUpdater.ResumePendingRollback(appDir, regPath);
                var afterNotice = SelfUpdater.ReadPendingUpdateNotice(appDir);
                Assert(okResume.Succeeded &&
                       okResume.RemainingFiles.Length == 0 &&
                       StorageCleanupService.ComputeFileSha256Safe(exePath) == oldExeSha &&
                       StorageCleanupService.ComputeFileSha256Safe(licPath) == oldLicSha &&
                       StorageCleanupService.ComputeFileSha256Safe(notPath) == oldNotSha &&
                       !File.Exists(licBak) &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateUnrecoveredListName)) &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateErrorMarkerName)) &&
                       !afterNotice.IsRollbackIncomplete && afterNotice.ErrorReason is null,
                    "B06 ResumePendingRollback did not verify SHA-256 and clean rollback group");
                return Task.CompletedTask;
            });

            // Blocker 2-b: Locked Claudome.exe during rollback MUST preserve .exe.{pid}.bak, detect via manifest even without unrecovered.txt, and resume cleanly.
            await check("cleanup-b07-partial-rollback-locked-exe-preserved-and-resumed", () =>
            {
                var caseDir = Path.Combine(baseDir, "b07-exe");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var stageDir = Path.Combine(tmpDir, "claudome-stage-b07");
                var payloadDir = Path.Combine(stageDir, "payload");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(payloadDir);
                new CleanupSettings().Save(settingsPath);

                var exePath = Path.Combine(appDir, "Claudome.exe");
                var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                File.WriteAllText(exePath, "old-exe-v1");
                File.WriteAllText(licPath, "old-lic-v1");
                File.WriteAllText(notPath, "old-not-v1");
                var oldExeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);

                const int pid = 999995;
                var txId = "tx-b07-exe";
                SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", pid, txId);

                var exeBak = Path.Combine(appDir, $"Claudome.exe.{pid}.bak");
                File.Copy(exePath, exeBak, overwrite: true);
                File.WriteAllText(exePath, "new-exe-v2");
                File.WriteAllText(Path.Combine(payloadDir, "Claudome.exe"), "new-exe-v2");
                File.WriteAllText(Path.Combine(payloadDir, "PROJECT-LICENSE.txt"), "new-lic-v2");
                File.WriteAllText(Path.Combine(payloadDir, "Gateway-THIRD-PARTY-NOTICES.txt"), "new-not-v2");

                var cmdPath = Path.Combine(tmpDir, "claudome-update-b07.cmd");
                var scriptText = SelfUpdater.BuildSwapScript(appDir, payloadDir, stageDir, "Claudome.exe", $"Claudome.exe.{pid}.bak", pid, relaunch: false, transactionId: txId)
                    .Replace("if errorlevel 1 goto DO_SWAP", "set \"FAILED_REASON=Failed after replacing executable\"\r\ngoto ROLLBACK");
                File.WriteAllText(cmdPath, scriptText, new System.Text.UTF8Encoding(false));

                int exitCode;
                using (var exeLock = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", SelfUpdater.BuildCmdArguments(cmdPath))
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetTempPath()
                    })!;
                    Assert(proc.WaitForExit(15000), "B07 exe rollback script timed out");
                    exitCode = proc.ExitCode;
                }

                Assert(exitCode != 0 &&
                       File.Exists(exeBak) &&
                       File.ReadAllText(exePath) == "new-exe-v2" &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)),
                    $"B07 locked Claudome.exe rollback did not preserve {exeBak} (exit={exitCode})");

                // Even if unrecovered list file was lost/deleted, RollbackManifest SHA-256 mismatch still reports IsRollbackIncomplete!
                File.Delete(Path.Combine(appDir, SelfUpdater.UpdateUnrecoveredListName));
                var noticeFromManifestOnly = SelfUpdater.ReadPendingUpdateNotice(appDir);
                Assert(noticeFromManifestOnly.IsRollbackIncomplete &&
                       noticeFromManifestOnly.UnrecoveredFiles is not null &&
                       noticeFromManifestOnly.UnrecoveredFiles.Contains("Claudome.exe", StringComparer.OrdinalIgnoreCase),
                    "B07 manifest SHA-256 fallback did not detect unrecovered Claudome.exe");

                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath);
                Assert(File.Exists(exeBak) && File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)),
                    "B07 CleanupStaleUpdateFiles removed pending EXE rollback backup");

                var resumed = SelfUpdater.ResumePendingRollback(appDir, regPath);
                Assert(resumed.Succeeded &&
                       StorageCleanupService.ComputeFileSha256Safe(exePath) == oldExeSha &&
                       !File.Exists(exeBak) &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                       !SelfUpdater.ReadPendingUpdateNotice(appDir).IsRollbackIncomplete,
                    "B07 ResumePendingRollback failed to restore original Claudome.exe");
                return Task.CompletedTask;
            });

            // Round-4 Blocker 1: Locked original files BEFORE swap must abort before mutating state;
            // legacy/empty-SHA rollback manifests must never fake success or delete .bak prematurely.
            await check("cleanup-b08-unreadable-original-aborts-swap-and-empty-sha-never-fakes-recovery", () =>
            {
                var caseDir = Path.Combine(baseDir, "b08-empty-sha");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var stageDir = Path.Combine(tmpDir, "claudome-stage-b08");
                var payloadDir = Path.Combine(stageDir, "payload");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(payloadDir);

                var exePath = Path.Combine(appDir, "Claudome.exe");
                var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                File.WriteAllText(exePath, "old-exe-v1");
                File.WriteAllText(licPath, "old-lic-v1");
                File.WriteAllText(notPath, "old-not-v1");
                var oldLicSha = StorageCleanupService.ComputeFileSha256Safe(licPath);

                var stagedExe = Path.Combine(payloadDir, "Claudome.exe");
                File.WriteAllText(stagedExe, "new-exe-v2");
                File.WriteAllText(Path.Combine(payloadDir, "PROJECT-LICENSE.txt"), "new-lic-v2");
                File.WriteAllText(Path.Combine(payloadDir, "Gateway-THIRD-PARTY-NOTICES.txt"), "new-not-v2");
                var staged = new StagedUpdate("0.4.9", stageDir, payloadDir, stagedExe, StorageCleanupService.ComputeFileSha256Safe(stagedExe));

                // (A) Locked original sidecar before StartSwapAndRestart must throw SetupException and create zero tx/rollback files.
                using (var lockOld = new FileStream(licPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var aborted = false;
                    try { SelfUpdater.StartSwapAndRestart(staged, exePath, 999993, relaunch: false, registryPath: regPath); }
                    catch (SetupException ex) when (ex.Message.Contains("PROJECT-LICENSE.txt", StringComparison.Ordinal)) { aborted = true; }
                    Assert(aborted &&
                           !File.Exists(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName)) &&
                           !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                           !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)),
                        "B08 StartSwapAndRestart did not abort cleanly when original sidecar was locked");
                }

                // (B) Existing manifest with empty OriginalSha256 must fail-closed if locked or .bak missing, and restore from .bak when unlocked.
                var b08CaseB = Path.Combine(caseDir, "legacy-empty-sha");
                Directory.CreateDirectory(b08CaseB);
                var bExe = Path.Combine(b08CaseB, "Claudome.exe");
                var bLic = Path.Combine(b08CaseB, "PROJECT-LICENSE.txt");
                var bNot = Path.Combine(b08CaseB, "Gateway-THIRD-PARTY-NOTICES.txt");
                var bLicBak = Path.Combine(b08CaseB, "PROJECT-LICENSE.txt.999992.bak");
                File.WriteAllText(bExe, "old-exe-v1");
                File.WriteAllText(bLic, "new-lic-v2");
                File.WriteAllText(bNot, "old-not-v1");
                File.WriteAllText(Path.Combine(b08CaseB, SelfUpdater.UpdateErrorMarkerName), "Simulated partial rollback");
                File.WriteAllText(Path.Combine(b08CaseB, SelfUpdater.UpdateUnrecoveredListName), "PROJECT-LICENSE.txt");
                JsonFile.Write(Path.Combine(b08CaseB, SelfUpdater.UpdateRollbackManifestName), new SelfUpdater.RollbackManifest(
                    "tx-b08-empty",
                    Path.GetFullPath(b08CaseB),
                    999992,
                    DateTime.UtcNow,
                    [
                        new SelfUpdater.RollbackFileEntry("Claudome.exe", true, StorageCleanupService.ComputeFileSha256Safe(bExe), "Claudome.exe.999992.bak"),
                        new SelfUpdater.RollbackFileEntry("PROJECT-LICENSE.txt", true, "", "PROJECT-LICENSE.txt.999992.bak"),
                        new SelfUpdater.RollbackFileEntry("Gateway-THIRD-PARTY-NOTICES.txt", true, StorageCleanupService.ComputeFileSha256Safe(bNot), "Gateway-THIRD-PARTY-NOTICES.txt.999992.bak")
                    ]));

                // Without .bak AND with empty SHA, ResumePendingRollback must fail-closed and preserve manifest + error marker.
                var noBakNotice = SelfUpdater.ReadPendingUpdateNotice(b08CaseB);
                var noBakResume = SelfUpdater.ResumePendingRollback(b08CaseB, regPath);
                Assert(noBakNotice.IsRollbackIncomplete &&
                       !noBakResume.Succeeded &&
                       noBakResume.RemainingFiles.Contains("PROJECT-LICENSE.txt", StringComparer.OrdinalIgnoreCase) &&
                       File.Exists(Path.Combine(b08CaseB, SelfUpdater.UpdateRollbackManifestName)) &&
                       File.Exists(Path.Combine(b08CaseB, SelfUpdater.UpdateErrorMarkerName)),
                    "B08 empty-SHA manifest with missing .bak falsely succeeded");

                // With valid .bak ("old-lic-v1"), live "new-lic-v2" differs -> locked fails, unlocked actually restores "old-lic-v1".
                File.WriteAllText(bLicBak, "old-lic-v1");
                using (var lockNew = new FileStream(bLic, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var lockedRes = SelfUpdater.ResumePendingRollback(b08CaseB, regPath);
                    Assert(!lockedRes.Succeeded && File.Exists(bLicBak),
                        "B08 empty-SHA recovery deleted backup while locked");
                }
                Assert(File.ReadAllText(bLic) == "new-lic-v2", "B08 empty-SHA file content changed while locked");
                var restoredRes = SelfUpdater.ResumePendingRollback(b08CaseB, regPath);
                Assert(restoredRes.Succeeded &&
                       StorageCleanupService.ComputeFileSha256Safe(bLic) == oldLicSha &&
                       !File.Exists(bLicBak) &&
                       !File.Exists(Path.Combine(b08CaseB, SelfUpdater.UpdateRollbackManifestName)),
                    "B08 empty-SHA manifest failed to restore actual old content from .bak");
                return Task.CompletedTask;
            });

            // Round-4 Blocker 2: Active swap script holds kernel authority lock across pause points;
            // second instance keeps InProgress untouched, and crashed swap recovers cleanly without ZIP.
            await check("cleanup-b09-active-swap-authority-blocks-second-instance-and-crash-recovers", () =>
            {
                var caseDir = Path.Combine(baseDir, "b09-active-swap");
                var appDir = Path.Combine(caseDir, "验收 目录 (中文)");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var stageDir = Path.Combine(tmpDir, "claudome-stage-b09");
                var payloadDir = Path.Combine(stageDir, "payload");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(payloadDir);
                new CleanupSettings().Save(settingsPath);

                var exePath = Path.Combine(appDir, "Claudome.exe");
                var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                File.WriteAllText(exePath, "old-exe-v1");
                File.WriteAllText(licPath, "old-lic-v1");
                File.WriteAllText(notPath, "old-not-v1");
                var oldExeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);
                var oldLicSha = StorageCleanupService.ComputeFileSha256Safe(licPath);
                File.WriteAllText(Path.Combine(payloadDir, "Claudome.exe"), "new-exe-v2");
                File.WriteAllText(Path.Combine(payloadDir, "PROJECT-LICENSE.txt"), "new-lic-v2");
                File.WriteAllText(Path.Combine(payloadDir, "Gateway-THIRD-PARTY-NOTICES.txt"), "new-not-v2");
                var newExeSha = StorageCleanupService.ComputeFileSha256Safe(Path.Combine(payloadDir, "Claudome.exe"));

                const int pid = 999991;
                var txId = "tx-b09-active";
                var cmdPath = Path.Combine(tmpDir, "claudome-update-b09.cmd");
                SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", pid, txId);
                JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                    new SelfUpdater.PendingUpdateTransaction(txId, newExeSha, exePath, stageDir, cmdPath, DateTime.UtcNow));
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord
                {
                    Category = BackupCategory.ProgramUpdate,
                    ScopeId = Path.GetFullPath(appDir),
                    ScopeLabel = appDir,
                    Path = Path.Combine(appDir, $"Claudome.exe.{pid}.bak"),
                    StageRoot = stageDir,
                    TransactionId = txId,
                    Role = "backup",
                    OpState = BackupOpState.InProgress
                }, regPath);

                var p1Ready = Path.Combine(caseDir, "p1.ready");
                var p1Go = Path.Combine(caseDir, "p1.go");
                var p2Ready = Path.Combine(caseDir, "p2.ready");
                var p2Go = Path.Combine(caseDir, "p2.go");

                var rawScript = SelfUpdater.BuildSwapScript(appDir, payloadDir, stageDir, "Claudome.exe", $"Claudome.exe.{pid}.bak", pid, relaunch: false, transactionId: txId);
                const string exeCopyAnchor = "copy /y \"%PAYLOAD_DIR%\\%EXE_NAME%\" \"%APP_DIR%\\%EXE_NAME%\" >nul\r\nif errorlevel 1 (\r\n  set \"FAILED_REASON=Failed to replace executable\"\r\n  goto ROLLBACK\r\n)";
                var doneAnchor = $"> \"%DONE_FILE%.tmp\" echo {{\"transactionId\":\"{txId}\",\"swapSucceeded\":true}}";
                Assert(rawScript.Contains(exeCopyAnchor, StringComparison.Ordinal) && rawScript.Contains(doneAnchor, StringComparison.Ordinal),
                    "B09 script anchors not found");

                var injected = rawScript
                    .Replace(exeCopyAnchor, exeCopyAnchor + $"\r\n> \"{p1Ready}\" echo p1\r\n:B09_W1\r\nif not exist \"{p1Go}\" (\r\n  ping -n 1 127.0.0.1 >nul 2>nul\r\n  goto B09_W1\r\n)")
                    .Replace(doneAnchor, $"> \"{p2Ready}\" echo p2\r\n:B09_W2\r\nif not exist \"{p2Go}\" (\r\n  ping -n 1 127.0.0.1 >nul 2>nul\r\n  goto B09_W2\r\n)\r\n" + doneAnchor);
                File.WriteAllText(cmdPath, injected, new System.Text.UTF8Encoding(false));

                using (var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", SelfUpdater.BuildCmdArguments(cmdPath))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                })!)
                {
                    try
                    {
                        var deadline1 = DateTime.UtcNow.AddSeconds(15);
                        while (!File.Exists(p1Ready) && DateTime.UtcNow < deadline1) Thread.Sleep(30);
                        Assert(File.Exists(p1Ready), "B09 did not reach Pause 1");

                        static void AssertActiveSwapPreserved(string dir, string tmp, string settings, string reg, string tx)
                        {
                            Assert(SelfUpdater.IsSwapLockActive(dir), "Expected swap lock active while paused");
                            var n = SelfUpdater.ReadPendingUpdateNotice(dir);
                            var ui = SetupWindow.DescribePendingUpdateNotice(n);
                            Assert(n.IsUpdateInProgress && !n.IsRollbackIncomplete && (n.UnrecoveredFiles?.Length ?? 0) == 0 &&
                                   ui is { ClearErrorMarker: false } u && !u.Message.Contains("已恢复原版", StringComparison.Ordinal),
                                "Active swap was misclassified as incomplete/recovered");
                            SelfUpdater.CleanupStaleUpdateFiles(dir, tmp, settings, reg);
                            var res = SelfUpdater.ResumePendingRollback(dir, reg);
                            var txRec = StorageCleanupService.LoadRegistry(reg).Single(r => r.TransactionId == tx && r.Role == "backup");
                            Assert(!res.Succeeded &&
                                   txRec.OpState == BackupOpState.InProgress &&
                                   File.Exists(Path.Combine(dir, SelfUpdater.UpdatePendingTxName)) &&
                                   File.Exists(Path.Combine(dir, SelfUpdater.UpdateRollbackManifestName)),
                                "Second instance mutated active swap state");
                        }

                        // Pause 1: right after EXE replaced
                        AssertActiveSwapPreserved(appDir, tmpDir, settingsPath, regPath, txId);
                        Assert(File.ReadAllText(exePath) == "new-exe-v2", "B09 Pause 1 EXE modified unexpectedly");
                        File.WriteAllText(p1Go, "go");

                        // Pause 2: all 3 files written, before completion receipt
                        var deadline2 = DateTime.UtcNow.AddSeconds(15);
                        while (!File.Exists(p2Ready) && DateTime.UtcNow < deadline2) Thread.Sleep(30);
                        Assert(File.Exists(p2Ready), "B09 did not reach Pause 2");
                        AssertActiveSwapPreserved(appDir, tmpDir, settingsPath, regPath, txId);
                        Assert(File.ReadAllText(exePath) == "new-exe-v2" &&
                               File.ReadAllText(licPath) == "new-lic-v2" &&
                               File.ReadAllText(notPath) == "new-not-v2",
                            "B09 Pause 2 rolled back files while updater still running");
                        File.WriteAllText(p2Go, "go");

                        Assert(proc.WaitForExit(15000) && proc.ExitCode == 0, $"B09 swap failed after resume (exit={proc.ExitCode})");
                    }
                    finally
                    {
                        if (!proc.HasExited)
                        {
                            try { proc.Kill(entireProcessTree: true); } catch { }
                        }
                    }
                }

                Assert(!SelfUpdater.IsSwapLockActive(appDir) &&
                       File.ReadAllText(exePath) == "new-exe-v2" &&
                       File.ReadAllText(licPath) == "new-lic-v2" &&
                       File.ReadAllText(notPath) == "new-not-v2" &&
                       File.Exists(Path.Combine(appDir, $"Claudome.exe.{pid}.bak")) &&
                       File.Exists(Path.Combine(appDir, $"PROJECT-LICENSE.txt.{pid}.bak")) &&
                       File.Exists(Path.Combine(appDir, $"Gateway-THIRD-PARTY-NOTICES.txt.{pid}.bak")),
                    "B09 completed swap lost files or v1 rollback group");

                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                var promotedRec = StorageCleanupService.LoadRegistry(regPath).Single(r => r.TransactionId == txId && r.Role == "backup");
                Assert(promotedRec.OpState == BackupOpState.VerifiedSuccess &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)),
                    "B09 completed swap was not verified on subsequent startup");

                // Crash recovery sub-case: kill active swap mid-flight, prove dead lock + missing receipt triggers recovery from local backups without ZIP!
                var crashDir = Path.Combine(caseDir, "crash");
                var crashApp = Path.Combine(crashDir, "app");
                var crashTmp = Path.Combine(crashDir, "tmp");
                var crashStage = Path.Combine(crashTmp, "claudome-stage-crash");
                var crashPayload = Path.Combine(crashStage, "payload");
                var crashSettings = Path.Combine(crashDir, "settings.json");
                var crashReg = Path.Combine(crashDir, "registry.json");
                Directory.CreateDirectory(crashApp);
                Directory.CreateDirectory(crashPayload);
                new CleanupSettings().Save(crashSettings);

                var cExe = Path.Combine(crashApp, "Claudome.exe");
                var cLic = Path.Combine(crashApp, "PROJECT-LICENSE.txt");
                var cNot = Path.Combine(crashApp, "Gateway-THIRD-PARTY-NOTICES.txt");
                File.WriteAllText(cExe, "old-exe-v1");
                File.WriteAllText(cLic, "old-lic-v1");
                File.WriteAllText(cNot, "old-not-v1");
                File.WriteAllText(Path.Combine(crashPayload, "Claudome.exe"), "new-exe-v2");
                File.WriteAllText(Path.Combine(crashPayload, "PROJECT-LICENSE.txt"), "new-lic-v2");
                File.WriteAllText(Path.Combine(crashPayload, "Gateway-THIRD-PARTY-NOTICES.txt"), "new-not-v2");

                const int cPid = 999990;
                var cTxId = "tx-b09-crash";
                var cCmd = Path.Combine(crashTmp, "claudome-update-crash.cmd");
                var cWaitReady = Path.Combine(crashDir, "crash.ready");
                var cWaitGo = Path.Combine(crashDir, "crash.never");
                SelfUpdater.CaptureRollbackManifest(crashApp, "Claudome.exe", cPid, cTxId);
                JsonFile.Write(Path.Combine(crashApp, SelfUpdater.UpdatePendingTxName),
                    new SelfUpdater.PendingUpdateTransaction(cTxId, newExeSha, cExe, crashStage, cCmd, DateTime.UtcNow));
                var crashScript = SelfUpdater.BuildSwapScript(crashApp, crashPayload, crashStage, "Claudome.exe", $"Claudome.exe.{cPid}.bak", cPid, relaunch: false, transactionId: cTxId)
                    .Replace(exeCopyAnchor, exeCopyAnchor + $"\r\n> \"{cWaitReady}\" echo mid\r\n:B09_CW\r\nif not exist \"{cWaitGo}\" (\r\n  ping -n 1 127.0.0.1 >nul 2>nul\r\n  goto B09_CW\r\n)");
                File.WriteAllText(cCmd, crashScript, new System.Text.UTF8Encoding(false));

                using (var cProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", SelfUpdater.BuildCmdArguments(cCmd))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                })!)
                {
                    var deadlineC = DateTime.UtcNow.AddSeconds(15);
                    while (!File.Exists(cWaitReady) && DateTime.UtcNow < deadlineC) Thread.Sleep(30);
                    Assert(File.Exists(cWaitReady) && SelfUpdater.IsSwapLockActive(crashApp), "B09 crash case did not acquire lock");
                    cProc.Kill(entireProcessTree: true);
                    cProc.WaitForExit(10000);
                }

                // Wait briefly for kernel handle teardown after Kill(entireProcessTree: true)
                for (var w = 0; w < 20 && SelfUpdater.IsSwapLockActive(crashApp); w++) Thread.Sleep(50);
                var crashNotice = SelfUpdater.ReadPendingUpdateNotice(crashApp);
                SelfUpdater.CleanupStaleUpdateFiles(crashApp, crashTmp, crashSettings, crashReg);
                Assert(!SelfUpdater.IsSwapLockActive(crashApp) &&
                       !crashNotice.IsUpdateInProgress &&
                       crashNotice.IsRollbackIncomplete &&
                       crashNotice.UnrecoveredFiles is not null &&
                       crashNotice.UnrecoveredFiles.Contains("Claudome.exe", StringComparer.OrdinalIgnoreCase) &&
                       File.Exists(Path.Combine(crashApp, $"Claudome.exe.{cPid}.bak")),
                    "B09 crashed updater was not detected as recoverable incomplete swap");

                Directory.Delete(crashStage, recursive: true);
                var crashResume = SelfUpdater.ResumePendingRollback(crashApp, crashReg);
                Assert(crashResume.Succeeded &&
                       StorageCleanupService.ComputeFileSha256Safe(cExe) == oldExeSha &&
                       StorageCleanupService.ComputeFileSha256Safe(cLic) == oldLicSha &&
                       !File.Exists(Path.Combine(crashApp, SelfUpdater.UpdateRollbackManifestName)) &&
                       !File.Exists(Path.Combine(crashApp, SelfUpdater.UpdateSwapLockName)) &&
                       !SelfUpdater.ReadPendingUpdateNotice(crashApp).IsRollbackIncomplete,
                    "B09 ResumePendingRollback failed to recover crashed swap without staging/ZIP");
                return Task.CompletedTask;
            });

            // Round-5 Blocker 1: Second update (StartSwapAndRestart) MUST reject cleanly while first holds lock;
            // concurrent parallel StartSwapAndRestart callers MUST serialize with zero clobbering of active tx/manifest/receipt.
            await check("cleanup-b10-concurrent-start-swap-blocked-and-first-promotes-verified", async () =>
            {
                var caseDir = Path.Combine(baseDir, "b10-concurrent");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var stage1 = Path.Combine(tmpDir, "claudome-stage-tx1");
                var payload1 = Path.Combine(stage1, "payload");
                var stage2 = Path.Combine(tmpDir, "claudome-stage-tx2");
                var payload2 = Path.Combine(stage2, "payload");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(payload1);
                Directory.CreateDirectory(payload2);
                new CleanupSettings().Save(settingsPath);

                var exePath = Path.Combine(appDir, "Claudome.exe");
                var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                File.WriteAllText(exePath, "old-exe-v1");
                File.WriteAllText(licPath, "old-lic-v1");
                File.WriteAllText(notPath, "old-not-v1");

                File.WriteAllText(Path.Combine(payload1, "Claudome.exe"), "new-exe-v2");
                File.WriteAllText(Path.Combine(payload1, "PROJECT-LICENSE.txt"), "new-lic-v2");
                File.WriteAllText(Path.Combine(payload1, "Gateway-THIRD-PARTY-NOTICES.txt"), "new-not-v2");
                var newExeSha1 = StorageCleanupService.ComputeFileSha256Safe(Path.Combine(payload1, "Claudome.exe"));

                var stagedExe2 = Path.Combine(payload2, "Claudome.exe");
                File.WriteAllText(stagedExe2, "other-exe-v3");
                File.WriteAllText(Path.Combine(payload2, "PROJECT-LICENSE.txt"), "other-lic-v3");
                File.WriteAllText(Path.Combine(payload2, "Gateway-THIRD-PARTY-NOTICES.txt"), "other-not-v3");
                var staged2 = new StagedUpdate("0.9.9", stage2, payload2, stagedExe2, StorageCleanupService.ComputeFileSha256Safe(stagedExe2));

                const int pid1 = 999989;
                var tx1 = "tx-b10-first";
                var cmd1 = Path.Combine(tmpDir, "claudome-update-b10.cmd");
                SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", pid1, tx1);
                JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                    new SelfUpdater.PendingUpdateTransaction(tx1, newExeSha1, exePath, stage1, cmd1, DateTime.UtcNow));
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord
                {
                    Category = BackupCategory.ProgramUpdate,
                    ScopeId = Path.GetFullPath(appDir),
                    ScopeLabel = appDir,
                    Path = Path.Combine(appDir, $"Claudome.exe.{pid1}.bak"),
                    StageRoot = stage1,
                    TransactionId = tx1,
                    Role = "backup",
                    OpState = BackupOpState.InProgress
                }, regPath);

                var pauseReady = Path.Combine(caseDir, "b10.ready");
                var pauseGo = Path.Combine(caseDir, "b10.go");
                var doneAnchor = $"> \"%DONE_FILE%.tmp\" echo {{\"transactionId\":\"{tx1}\",\"swapSucceeded\":true}}";
                var script1 = SelfUpdater.BuildSwapScript(appDir, payload1, stage1, "Claudome.exe", $"Claudome.exe.{pid1}.bak", pid1, relaunch: false, transactionId: tx1)
                    .Replace(doneAnchor, $"> \"{pauseReady}\" echo paused\r\n:B10_W\r\nif not exist \"{pauseGo}\" (\r\n  ping -n 1 127.0.0.1 >nul 2>nul\r\n  goto B10_W\r\n)\r\n" + doneAnchor);
                File.WriteAllText(cmd1, script1, new System.Text.UTF8Encoding(false));

                using (var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", SelfUpdater.BuildCmdArguments(cmd1))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                })!)
                {
                    try
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(15);
                        while (!File.Exists(pauseReady) && DateTime.UtcNow < deadline) Thread.Sleep(30);
                        Assert(File.Exists(pauseReady) && SelfUpdater.IsSwapLockActive(appDir), "B10 first swap did not hold active lock");

                        var pendingBefore = File.ReadAllText(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName));
                        var rollbackBefore = File.ReadAllText(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName));

                        var rejected = false;
                        try
                        {
                            SelfUpdater.StartSwapAndRestart(staged2, exePath, 999988, relaunch: false, registryPath: regPath);
                        }
                        catch (SetupException) { rejected = true; }

                        Assert(rejected &&
                               File.ReadAllText(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName)) == pendingBefore &&
                               File.ReadAllText(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) == rollbackBefore &&
                               StorageCleanupService.LoadRegistry(regPath).All(r => r.TransactionId == tx1) &&
                               File.ReadAllText(exePath) == "new-exe-v2",
                            "B10 second StartSwapAndRestart overwrote active first update metadata");

                        File.WriteAllText(pauseGo, "go");
                        Assert(proc.WaitForExit(15000) && proc.ExitCode == 0, $"B10 first swap did not exit 0 (exit={proc.ExitCode})");
                    }
                    finally
                    {
                        if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } }
                    }
                }

                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                var rec = StorageCleanupService.LoadRegistry(regPath).Single(r => r.TransactionId == tx1 && r.Role == "backup");
                Assert(rec.OpState == BackupOpState.VerifiedSuccess &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)) &&
                       File.Exists(Path.Combine(appDir, $"Claudome.exe.{pid1}.bak")),
                    "B10 first swap failed to promote VerifiedSuccess after blocking concurrent second swap");

                // Also test two concurrent StartSwapAndRestart callers racing directly on a fresh appDir (only 1 wins, 1 blocked).
                var raceDir = Path.Combine(caseDir, "race");
                var raceApp = Path.Combine(raceDir, "app");
                var raceStageA = Path.Combine(raceDir, "tmp", "claudome-stage-ra");
                var raceStageB = Path.Combine(raceDir, "tmp", "claudome-stage-rb");
                var raceReg = Path.Combine(raceDir, "reg.json");
                Directory.CreateDirectory(raceApp);
                Directory.CreateDirectory(Path.Combine(raceStageA, "payload"));
                Directory.CreateDirectory(Path.Combine(raceStageB, "payload"));
                var rExe = Path.Combine(raceApp, "Claudome.exe");
                File.WriteAllText(rExe, "old");
                File.WriteAllText(Path.Combine(raceApp, "PROJECT-LICENSE.txt"), "old");
                File.WriteAllText(Path.Combine(raceApp, "Gateway-THIRD-PARTY-NOTICES.txt"), "old");
                var rPayA = Path.Combine(raceStageA, "payload", "Claudome.exe");
                var rPayB = Path.Combine(raceStageB, "payload", "Claudome.exe");
                File.WriteAllText(rPayA, "new-a");
                File.WriteAllText(Path.Combine(raceStageA, "payload", "PROJECT-LICENSE.txt"), "new-a");
                File.WriteAllText(Path.Combine(raceStageA, "payload", "Gateway-THIRD-PARTY-NOTICES.txt"), "new-a");
                File.WriteAllText(rPayB, "new-b");
                File.WriteAllText(Path.Combine(raceStageB, "payload", "PROJECT-LICENSE.txt"), "new-b");
                File.WriteAllText(Path.Combine(raceStageB, "payload", "Gateway-THIRD-PARTY-NOTICES.txt"), "new-b");
                var stA = new StagedUpdate("0.9.1", raceStageA, Path.GetDirectoryName(rPayA)!, rPayA, StorageCleanupService.ComputeFileSha256Safe(rPayA));
                var stB = new StagedUpdate("0.9.2", raceStageB, Path.GetDirectoryName(rPayB)!, rPayB, StorageCleanupService.ComputeFileSha256Safe(rPayB));

                // Wait on real current process PID briefly via Process? No, use a sleeping child PowerShell PID so both scripts stay alive briefly while racing!
                using var sleeper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
                try
                {
                    using var gate = new ManualResetEventSlim(false);
                    var t1 = Task.Run(() => { gate.Wait(); SelfUpdater.StartSwapAndRestart(stA, rExe, sleeper.Id, relaunch: false, registryPath: raceReg); });
                    var t2 = Task.Run(() => { gate.Wait(); SelfUpdater.StartSwapAndRestart(stB, rExe, sleeper.Id, relaunch: false, registryPath: raceReg); });
                    gate.Set();
                    var outcomes = await Task.WhenAll(t1.ContinueWith(t => t.Exception is null), t2.ContinueWith(t => t.Exception is null));
                    Assert(outcomes.Count(x => x) == 1 && outcomes.Count(x => !x) == 1,
                        $"B10 parallel race expected exactly 1 winner and 1 rejected caller (wins={outcomes.Count(x => x)})");
                }
                finally
                {
                    if (!sleeper.HasExited) { try { sleeper.Kill(); } catch { } }
                }
                // Wait briefly for the winning swap script in raceDir to settle before cleanup finishes
                for (var w = 0; w < 100 && SelfUpdater.IsSwapLockActive(raceApp); w++) Thread.Sleep(50);
            });

            // Round-5 Blocker 2 Case B: Valid completion receipt written, updater killed BEFORE deleting rollback manifest;
            // first valid startup must promote VerifiedSuccess and clean residual manifest, keeping full rollback group & rotating on newer update.
            await check("cleanup-b11-post-receipt-crash-with-residual-manifest-verifies-and-rotates", () =>
            {
                var caseDir = Path.Combine(baseDir, "b11-post-receipt-crash");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var stageDir = Path.Combine(tmpDir, "claudome-stage-b11");
                var payloadDir = Path.Combine(stageDir, "payload");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(payloadDir);
                new CleanupSettings().Save(settingsPath);

                var exePath = Path.Combine(appDir, "Claudome.exe");
                var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                File.WriteAllText(exePath, "old-exe-v1");
                File.WriteAllText(licPath, "old-lic-v1");
                File.WriteAllText(notPath, "old-not-v1");
                File.WriteAllText(Path.Combine(payloadDir, "Claudome.exe"), "new-exe-v2");
                File.WriteAllText(Path.Combine(payloadDir, "PROJECT-LICENSE.txt"), "new-lic-v2");
                File.WriteAllText(Path.Combine(payloadDir, "Gateway-THIRD-PARTY-NOTICES.txt"), "new-not-v2");
                var newExeSha = StorageCleanupService.ComputeFileSha256Safe(Path.Combine(payloadDir, "Claudome.exe"));

                const int pid = 999987;
                var txId = "tx-b11-post-receipt";
                var cmdPath = Path.Combine(tmpDir, "claudome-update-b11.cmd");
                SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", pid, txId);
                JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                    new SelfUpdater.PendingUpdateTransaction(txId, newExeSha, exePath, stageDir, cmdPath, DateTime.UtcNow));
                var exeBak = Path.Combine(appDir, $"Claudome.exe.{pid}.bak");
                var licBak = Path.Combine(appDir, $"PROJECT-LICENSE.txt.{pid}.bak");
                var notBak = Path.Combine(appDir, $"Gateway-THIRD-PARTY-NOTICES.txt.{pid}.bak");
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), ScopeLabel = appDir, Path = exeBak, StageRoot = stageDir, TransactionId = txId, Role = "backup", OpState = BackupOpState.InProgress }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), ScopeLabel = appDir, Path = licBak, StageRoot = stageDir, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), ScopeLabel = appDir, Path = notBak, StageRoot = stageDir, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress }, regPath);

                var postReady = Path.Combine(caseDir, "b11.ready");
                var postNever = Path.Combine(caseDir, "b11.never");
                const string postReceiptAnchor = "if not defined DEFERRED_FILES goto CLEAN_STAGE";
                var script = SelfUpdater.BuildSwapScript(appDir, payloadDir, stageDir, "Claudome.exe", $"Claudome.exe.{pid}.bak", pid, relaunch: false, transactionId: txId)
                    .Replace(postReceiptAnchor, $"> \"{postReady}\" echo written\r\n:B11_WAIT\r\nif not exist \"{postNever}\" (\r\n  ping -n 1 127.0.0.1 >nul 2>nul\r\n  goto B11_WAIT\r\n)\r\n" + postReceiptAnchor);
                File.WriteAllText(cmdPath, script, new System.Text.UTF8Encoding(false));

                using (var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", SelfUpdater.BuildCmdArguments(cmdPath))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                })!)
                {
                    var dl = DateTime.UtcNow.AddSeconds(15);
                    while (!File.Exists(postReady) && DateTime.UtcNow < dl) Thread.Sleep(30);
                    Assert(File.Exists(postReady) &&
                           File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)) &&
                           File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)),
                        "B11 did not pause with both completed receipt and residual rollback manifest");
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(10000);
                }

                for (var w = 0; w < 20 && SelfUpdater.IsSwapLockActive(appDir); w++) Thread.Sleep(50);
                var notice = SelfUpdater.ReadPendingUpdateNotice(appDir);
                Assert(!notice.IsUpdateInProgress && !notice.IsRollbackIncomplete && notice.ErrorReason is null,
                    "B11 misclassified post-receipt residual manifest as failed/incomplete rollback");

                // First verified boot promotes VerifiedSuccess & clears residual manifest/pending/receipt while preserving complete v1 backup group
                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                var regAfter1 = StorageCleanupService.LoadRegistry(regPath).Where(r => r.TransactionId == txId).ToList();
                Assert(regAfter1.All(r => r.OpState == BackupOpState.VerifiedSuccess) &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)) &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName)) &&
                       File.Exists(exeBak) && File.Exists(licBak) && File.Exists(notBak) &&
                       File.ReadAllText(exePath) == "new-exe-v2",
                    "B11 first startup failed to verify transaction or clean residual rollback metadata");

                // Second boot remains verified and retains v1 rollback group until a newer verified update replaces it
                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                Assert(File.Exists(exeBak) && File.Exists(licBak) && File.Exists(notBak), "B11 second startup dropped v1 rollback group");

                // Simulate newer COMPLETE verified v3 transaction -> old v1 group rotates out while v2/v3 complete group stays retained
                var exeBak2 = Path.Combine(appDir, "Claudome.exe.999986.bak");
                var licBak2 = Path.Combine(appDir, "PROJECT-LICENSE.txt.999986.bak");
                var notBak2 = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.999986.bak");
                File.WriteAllText(exeBak2, "new-exe-v2");
                File.WriteAllText(licBak2, "new-lic-v2");
                File.WriteAllText(notBak2, "new-not-v2");
                var t3 = DateTime.UtcNow.AddSeconds(5);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), ScopeLabel = appDir, Path = exeBak2, TransactionId = "tx-b11-v3", Role = "backup", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t3 }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), ScopeLabel = appDir, Path = licBak2, TransactionId = "tx-b11-v3", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t3 }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), ScopeLabel = appDir, Path = notBak2, TransactionId = "tx-b11-v3", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t3 }, regPath);
                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                Assert(!File.Exists(exeBak) && !File.Exists(licBak) && !File.Exists(notBak) &&
                       File.Exists(exeBak2) && File.Exists(licBak2) && File.Exists(notBak2),
                    "B11 old v1 rollback group failed to rotate after newer verified v3 transaction");
                return Task.CompletedTask;
            });

            // Round-5 Blocker 2 Case A: Locked completion receipt MUST fail swap, preserve rollback manifest & .bak & stage, and recover cleanly once unlocked.
            await check("cleanup-b12-locked-completion-receipt-preserves-rollback-and-recovers", () =>
            {
                var caseDir = Path.Combine(baseDir, "b12-locked-receipt");
                var appDir = Path.Combine(caseDir, "验收 目录 (中文)");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var stageDir = Path.Combine(tmpDir, "claudome-stage-b12");
                var payloadDir = Path.Combine(stageDir, "payload");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(payloadDir);
                new CleanupSettings().Save(settingsPath);

                var exePath = Path.Combine(appDir, "Claudome.exe");
                var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                File.WriteAllText(exePath, "old-exe-v1");
                File.WriteAllText(licPath, "old-lic-v1");
                File.WriteAllText(notPath, "old-not-v1");
                var oldExeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);
                var oldLicSha = StorageCleanupService.ComputeFileSha256Safe(licPath);
                var oldNotSha = StorageCleanupService.ComputeFileSha256Safe(notPath);

                File.WriteAllText(Path.Combine(payloadDir, "Claudome.exe"), "new-exe-v2");
                File.WriteAllText(Path.Combine(payloadDir, "PROJECT-LICENSE.txt"), "new-lic-v2");
                File.WriteAllText(Path.Combine(payloadDir, "Gateway-THIRD-PARTY-NOTICES.txt"), "new-not-v2");
                var newExeSha = StorageCleanupService.ComputeFileSha256Safe(Path.Combine(payloadDir, "Claudome.exe"));

                const int pid = 999985;
                var txId = "tx-b12-receipt-lock";
                var cmdPath = Path.Combine(tmpDir, "claudome-update-b12.cmd");
                var donePath = Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName);
                SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", pid, txId);
                JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                    new SelfUpdater.PendingUpdateTransaction(txId, newExeSha, exePath, stageDir, cmdPath, DateTime.UtcNow));
                var exeBak = Path.Combine(appDir, $"Claudome.exe.{pid}.bak");
                var licBak = Path.Combine(appDir, $"PROJECT-LICENSE.txt.{pid}.bak");
                var notBak = Path.Combine(appDir, $"Gateway-THIRD-PARTY-NOTICES.txt.{pid}.bak");
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), ScopeLabel = appDir, Path = exeBak, StageRoot = stageDir, TransactionId = txId, Role = "backup", OpState = BackupOpState.InProgress }, regPath);

                var script = SelfUpdater.BuildSwapScript(appDir, payloadDir, stageDir, "Claudome.exe", $"Claudome.exe.{pid}.bak", pid, relaunch: false, transactionId: txId);
                File.WriteAllText(cmdPath, script, new System.Text.UTF8Encoding(false));

                int exitCode;
                using (var receiptLock = new FileStream(donePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", SelfUpdater.BuildCmdArguments(cmdPath))
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetTempPath()
                    })!;
                    Assert(proc.WaitForExit(15000), "B12 swap script timed out");
                    exitCode = proc.ExitCode;
                }

                Assert(exitCode != 0 &&
                       File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                       File.Exists(Path.Combine(appDir, SelfUpdater.UpdateErrorMarkerName)) &&
                       File.Exists(exeBak) && File.Exists(licBak) && File.Exists(notBak) &&
                       Directory.Exists(stageDir),
                    $"B12 locked completion receipt deleted rollback material or succeeded silently (exit={exitCode})");

                var notice = SelfUpdater.ReadPendingUpdateNotice(appDir);
                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                var regRow = StorageCleanupService.LoadRegistry(regPath).Single(r => r.TransactionId == txId && r.Role == "backup");
                Assert(notice.IsRollbackIncomplete &&
                       regRow.OpState == BackupOpState.FailedOrCancelled &&
                       File.Exists(exeBak) &&
                       File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)),
                    "B12 startup check did not transition failed receipt tx safely or deleted backups");

                Directory.Delete(stageDir, recursive: true);
                var resumed = SelfUpdater.ResumePendingRollback(appDir, regPath);
                Assert(resumed.Succeeded &&
                       StorageCleanupService.ComputeFileSha256Safe(exePath) == oldExeSha &&
                       StorageCleanupService.ComputeFileSha256Safe(licPath) == oldLicSha &&
                       StorageCleanupService.ComputeFileSha256Safe(notPath) == oldNotSha &&
                       !File.Exists(donePath) &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                       !File.Exists(exeBak) && !File.Exists(licBak) && !File.Exists(notBak),
                    "B12 ResumePendingRollback failed to recover full v1 group after locked receipt");
                return Task.CompletedTask;
            });

            // Consolidated Blocker 1: Pending/unrecovered/unfinalized tx MUST block a subsequent update (even a partial ZIP missing sidecar) with ZERO overwrites.
            await check("cleanup-b13-pending-or-unverified-tx-blocks-second-update", () =>
            {
                var caseDir = Path.Combine(baseDir, "b13-pending-blocks-new");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var stage1 = Path.Combine(tmpDir, "claudome-stage-tx1");
                var stage2 = Path.Combine(tmpDir, "claudome-stage-tx2");
                var payload2 = Path.Combine(stage2, "payload");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(stage1);
                Directory.CreateDirectory(payload2);

                var exePath = Path.Combine(appDir, "Claudome.exe");
                var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                File.WriteAllText(exePath, "old-exe-v1");
                File.WriteAllText(licPath, "old-lic-v1");
                File.WriteAllText(notPath, "old-not-v1");
                var oldExeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);
                var oldLicSha = StorageCleanupService.ComputeFileSha256Safe(licPath);
                var oldNotSha = StorageCleanupService.ComputeFileSha256Safe(notPath);

                // First tx wrote v2 files, failed receipt, left complete .bak + rollback manifest + unrecovered list + error marker.
                const int pid1 = 999971;
                var tx1 = "tx-b13-first-pending";
                SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", pid1, tx1);
                var exeBak1 = Path.Combine(appDir, $"Claudome.exe.{pid1}.bak");
                var licBak1 = Path.Combine(appDir, $"PROJECT-LICENSE.txt.{pid1}.bak");
                var notBak1 = Path.Combine(appDir, $"Gateway-THIRD-PARTY-NOTICES.txt.{pid1}.bak");
                File.Copy(exePath, exeBak1, true);
                File.Copy(licPath, licBak1, true);
                File.Copy(notPath, notBak1, true);
                File.WriteAllText(exePath, "unverified-v2-exe");
                File.WriteAllText(licPath, "unverified-v2-lic");
                File.WriteAllText(notPath, "unverified-v2-not");
                JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                    new SelfUpdater.PendingUpdateTransaction(tx1, "deadbeef", exePath, stage1, Path.Combine(tmpDir, "u1.cmd"), DateTime.UtcNow));
                File.WriteAllText(Path.Combine(appDir, SelfUpdater.UpdateErrorMarkerName), "Failed to write completion receipt");
                File.WriteAllText(Path.Combine(appDir, SelfUpdater.UpdateUnrecoveredListName), "Claudome.exe\r\nPROJECT-LICENSE.txt\r\nGateway-THIRD-PARTY-NOTICES.txt\r\n");
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = exeBak1, TransactionId = tx1, Role = "backup", OpState = BackupOpState.FailedOrCancelled }, regPath);

                var pSnap = File.ReadAllText(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName));
                var rSnap = File.ReadAllText(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName));
                var uSnap = File.ReadAllText(Path.Combine(appDir, SelfUpdater.UpdateUnrecoveredListName));
                var eSnap = File.ReadAllText(Path.Combine(appDir, SelfUpdater.UpdateErrorMarkerName));

                // Prepare a second staged update (both complete and missing-sidecar)
                var stagedExe2 = Path.Combine(payload2, "Claudome.exe");
                File.WriteAllText(stagedExe2, "new-v3-exe");
                File.WriteAllText(Path.Combine(payload2, "PROJECT-LICENSE.txt"), "new-v3-lic");
                var stagedMissing = new StagedUpdate("0.9.9", stage2, payload2, stagedExe2, StorageCleanupService.ComputeFileSha256Safe(stagedExe2));
                File.WriteAllText(Path.Combine(payload2, "Gateway-THIRD-PARTY-NOTICES.txt"), "new-v3-not");
                var stagedComplete = new StagedUpdate("0.9.9", stage2, payload2, stagedExe2, StorageCleanupService.ComputeFileSha256Safe(stagedExe2));

                foreach (var second in new[] { stagedMissing, stagedComplete })
                {
                    Assert(SelfUpdater.TryGetPendingUpdateBlocker(appDir, out var uiBlock, regPath) && !string.IsNullOrEmpty(uiBlock),
                        "B13 UI blocker check missed pending recovery state");
                    var blocked = false;
                    try { SelfUpdater.StartSwapAndRestart(second, exePath, 999972, relaunch: false, registryPath: regPath); }
                    catch (SetupException) { blocked = true; }
                    Assert(blocked &&
                           File.ReadAllText(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName)) == pSnap &&
                           File.ReadAllText(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) == rSnap &&
                           File.ReadAllText(Path.Combine(appDir, SelfUpdater.UpdateUnrecoveredListName)) == uSnap &&
                           File.ReadAllText(Path.Combine(appDir, SelfUpdater.UpdateErrorMarkerName)) == eSnap &&
                           StorageCleanupService.LoadRegistry(regPath).All(r => r.TransactionId == tx1) &&
                           File.Exists(exeBak1) && File.Exists(licBak1) && File.Exists(notBak1),
                        "B13 second StartSwapAndRestart mutated tx1 recovery artifacts");
                }

                // Recover tx1 cleanly -> now all tx1 recovery artifacts clear and v1 original hashes match
                var resumed = SelfUpdater.ResumePendingRollback(appDir, regPath);
                Assert(resumed.Succeeded &&
                       StorageCleanupService.ComputeFileSha256Safe(exePath) == oldExeSha &&
                       StorageCleanupService.ComputeFileSha256Safe(licPath) == oldLicSha &&
                       StorageCleanupService.ComputeFileSha256Safe(notPath) == oldNotSha &&
                       !SelfUpdater.TryGetPendingUpdateBlocker(appDir, out _, regPath),
                    "B13 ResumePendingRollback failed after rejecting concurrent tx2");
                return Task.CompletedTask;
            });

            // Consolidated Blocker 2: Verified tx whose rollback manifest was locked on boot #1 stays VerifiedSuccess across reboots;
            // ResumePendingRollback strictly refuses to revert a VerifiedSuccess update.
            await check("cleanup-b14-verified-tx-with-locked-residual-manifest-stays-verified", () =>
            {
                var caseDir = Path.Combine(baseDir, "b14-locked-residual-manifest");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(tmpDir);
                new CleanupSettings().Save(settingsPath);

                var exePath = Path.Combine(appDir, "Claudome.exe");
                var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                File.WriteAllText(exePath, "old-exe-v1");
                File.WriteAllText(licPath, "old-lic-v1");
                File.WriteAllText(notPath, "old-not-v1");

                const int pid = 999970;
                var txId = "tx-b14-verified";
                SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", pid, txId);
                var exeBak = Path.Combine(appDir, $"Claudome.exe.{pid}.bak");
                var licBak = Path.Combine(appDir, $"PROJECT-LICENSE.txt.{pid}.bak");
                var notBak = Path.Combine(appDir, $"Gateway-THIRD-PARTY-NOTICES.txt.{pid}.bak");
                File.Copy(exePath, exeBak, true);
                File.Copy(licPath, licBak, true);
                File.Copy(notPath, notBak, true);
                File.WriteAllText(exePath, "new-exe-v2");
                File.WriteAllText(licPath, "new-lic-v2");
                File.WriteAllText(notPath, "new-not-v2");
                var newExeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);
                var sideHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PROJECT-LICENSE.txt"] = StorageCleanupService.ComputeFileSha256Safe(licPath),
                    ["Gateway-THIRD-PARTY-NOTICES.txt"] = StorageCleanupService.ComputeFileSha256Safe(notPath)
                };
                JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                    new SelfUpdater.PendingUpdateTransaction(txId, newExeSha, exePath, Path.Combine(tmpDir, "s14"), Path.Combine(tmpDir, "s14.cmd"), DateTime.UtcNow, ExpectedRequiredSideFileSha256: sideHashes));
                File.WriteAllText(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName), $"{{\"transactionId\":\"{txId}\",\"swapSucceeded\":true}}");
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = exeBak, TransactionId = txId, Role = "backup", OpState = BackupOpState.InProgress }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = licBak, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = notBak, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress }, regPath);

                var rbPath = Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName);
                // Lock rollback manifest exclusively during Boot #1 verification
                using (var rbLock = new FileStream(rbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                }
                Assert(StorageCleanupService.LoadRegistry(regPath).All(r => r.OpState == BackupOpState.VerifiedSuccess) &&
                       File.Exists(rbPath) &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName)) &&
                       File.Exists(Path.Combine(appDir, SelfUpdater.UpdateVerifiedMarkerName)),
                    "B14 Boot #1 failed to promote VerifiedSuccess while rollback manifest was locked");

                // Boot #2 with unlocked residual manifest: MUST NOT report rollback incomplete, MUST refuse ResumePendingRollback, and cleans residual manifest cleanly
                var noticeBoot2 = SelfUpdater.ReadPendingUpdateNotice(appDir, regPath);
                var resumeAttempt = SelfUpdater.ResumePendingRollback(appDir, regPath);
                Assert(!noticeBoot2.IsRollbackIncomplete && noticeBoot2.ErrorReason is null &&
                       !resumeAttempt.Succeeded &&
                       File.ReadAllText(exePath) == "new-exe-v2" &&
                       StorageCleanupService.LoadRegistry(regPath).All(r => r.OpState == BackupOpState.VerifiedSuccess) &&
                       File.Exists(exeBak) && File.Exists(licBak) && File.Exists(notBak),
                    "B14 Boot #2 triggered spurious rollback or demoted VerifiedSuccess");

                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                Assert(!File.Exists(rbPath) &&
                       File.Exists(exeBak) && File.Exists(licBak) && File.Exists(notBak) &&
                       StorageCleanupService.LoadRegistry(regPath).All(r => r.OpState == BackupOpState.VerifiedSuccess),
                    "B14 Boot #2 cleanup failed to clear residual manifest cleanly");
                return Task.CompletedTask;
            });

            // Consolidated Blocker 3: Startup verification MUST bind and verify BOTH EXE and all RequiredSideFiles;
            // missing/tampered required sidecar rejects promotion and preserves earlier complete rollback group, while deferrable doc stays independent.
            await check("cleanup-b15-startup-rejects-missing-or-mismatched-required-sidecar", () =>
            {
                foreach (var mode in new[] { "missing", "tampered" })
                {
                    var caseDir = Path.Combine(baseDir, $"b15-{mode}");
                    var appDir = Path.Combine(caseDir, "app");
                    var tmpDir = Path.Combine(caseDir, "tmp");
                    var settingsPath = Path.Combine(caseDir, "settings.json");
                    var regPath = Path.Combine(caseDir, "registry.json");
                    Directory.CreateDirectory(appDir);
                    Directory.CreateDirectory(tmpDir);
                    new CleanupSettings().Save(settingsPath);

                    // Earlier complete VerifiedSuccess rollback group (tx-v1)
                    var v1Exe = Path.Combine(appDir, "Claudome.exe.101.bak");
                    var v1Lic = Path.Combine(appDir, "PROJECT-LICENSE.txt.101.bak");
                    var v1Not = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.101.bak");
                    File.WriteAllText(v1Exe, "v0-exe");
                    File.WriteAllText(v1Lic, "v0-lic");
                    File.WriteAllText(v1Not, "v0-not");
                    var tOld = DateTime.UtcNow.AddMinutes(-10);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = v1Exe, TransactionId = "tx-v1", Role = "backup", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = tOld }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = v1Lic, TransactionId = "tx-v1", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = tOld }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = v1Not, TransactionId = "tx-v1", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = tOld }, regPath);

                    // Newer tx-v2 writes correct EXE + receipt, plus v1->v2 backups, EXCEPT one required sidecar is missing or tampered!
                    var exePath = Path.Combine(appDir, "Claudome.exe");
                    var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                    var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                    File.WriteAllText(exePath, "v2-exe");
                    File.WriteAllText(licPath, "v2-lic-expected");
                    File.WriteAllText(notPath, "v2-not-expected");
                    var newExeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);
                    var expectedSides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["PROJECT-LICENSE.txt"] = StorageCleanupService.ComputeFileSha256Safe(licPath),
                        ["Gateway-THIRD-PARTY-NOTICES.txt"] = StorageCleanupService.ComputeFileSha256Safe(notPath)
                    };
                    var v2ExeBak = Path.Combine(appDir, "Claudome.exe.202.bak");
                    var v2LicBak = Path.Combine(appDir, "PROJECT-LICENSE.txt.202.bak");
                    var v2NotBak = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.202.bak");
                    File.WriteAllText(v2ExeBak, "v1-exe");
                    File.WriteAllText(v2LicBak, "v1-lic");
                    File.WriteAllText(v2NotBak, "v1-not");

                    const string tx2 = "tx-v2";
                    JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                        new SelfUpdater.PendingUpdateTransaction(tx2, newExeSha, exePath, Path.Combine(tmpDir, "s2"), Path.Combine(tmpDir, "s2.cmd"), DateTime.UtcNow, ExpectedRequiredSideFileSha256: expectedSides));
                    File.WriteAllText(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName), $"{{\"transactionId\":\"{tx2}\",\"swapSucceeded\":true}}");
                    var tNew = DateTime.UtcNow;
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = v2ExeBak, TransactionId = tx2, Role = "backup", OpState = BackupOpState.InProgress, CreatedUtc = tNew }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = v2LicBak, TransactionId = tx2, Role = "backup-sidecar", OpState = BackupOpState.InProgress, CreatedUtc = tNew }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = v2NotBak, TransactionId = tx2, Role = "backup-sidecar", OpState = BackupOpState.InProgress, CreatedUtc = tNew }, regPath);

                    if (mode == "missing")
                        File.Delete(licPath);
                    else
                        File.WriteAllText(notPath, "v2-not-TAMPERED");

                    SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                    var v2Rows = StorageCleanupService.LoadRegistry(regPath).Where(r => r.TransactionId == tx2).ToList();
                    var notice = SelfUpdater.ReadPendingUpdateNotice(appDir, regPath);
                    var expectedMissingName = mode == "missing" ? "PROJECT-LICENSE.txt" : "Gateway-THIRD-PARTY-NOTICES.txt";
                    Assert(v2Rows.All(r => r.OpState == BackupOpState.FailedOrCancelled) &&
                           !string.IsNullOrWhiteSpace(notice.ErrorReason) &&
                           notice.ErrorReason.Contains(expectedMissingName, StringComparison.Ordinal) &&
                           File.Exists(v1Exe) && File.Exists(v1Lic) && File.Exists(v1Not) &&
                           File.Exists(v2ExeBak) && File.Exists(v2LicBak) && File.Exists(v2NotBak),
                        $"B15 ({mode}) promoted invalid required sidecar or evicted earlier complete group");
                }
                return Task.CompletedTask;
            });

            // Consolidated Blocker 4: ResumePendingRollback deletes ONLY current transaction's owned backups;
            // prior transactions' backups and ambiguous historical candidates are strictly preserved.
            await check("cleanup-b16-resume-scoped-strictly-never-deletes-other-tx-or-ambiguous", () =>
            {
                var caseDir = Path.Combine(baseDir, "b16-scoped-resume");
                var appDir = Path.Combine(caseDir, "app");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);

                var exePath = Path.Combine(appDir, "Claudome.exe");
                var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                // Older VerifiedSuccess tx1 backups
                var exe1 = Path.Combine(appDir, "Claudome.exe.100.bak");
                var lic1 = Path.Combine(appDir, "PROJECT-LICENSE.txt.100.bak");
                var not1 = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.100.bak");
                File.WriteAllText(exe1, "v0-exe");
                File.WriteAllText(lic1, "v0-lic");
                File.WriteAllText(not1, "v0-not");
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = exe1, TransactionId = "tx1", Role = "backup", OpState = BackupOpState.VerifiedSuccess }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = lic1, TransactionId = "tx1", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = not1, TransactionId = "tx1", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess }, regPath);

                // Current tx2 (pid=200): EXE was already renamed back to v1-exe (so Claudome.exe.200.bak is gone), Notices was unchanged,
                // ONLY PROJECT-LICENSE.txt.200.bak awaits recovery!
                File.WriteAllText(exePath, "v1-exe");
                File.WriteAllText(notPath, "v1-not");
                File.WriteAllText(licPath, "v2-lic-bad");
                var lic2 = Path.Combine(appDir, "PROJECT-LICENSE.txt.200.bak");
                File.WriteAllText(lic2, "v1-lic-good");
                var v1ExeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);
                var v1LicSha = StorageCleanupService.ComputeFileSha256Safe(lic2);
                var v1NotSha = StorageCleanupService.ComputeFileSha256Safe(notPath);
                JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName), new SelfUpdater.RollbackManifest(
                    "tx2", Path.GetFullPath(appDir), 200, DateTime.UtcNow,
                    [
                        new SelfUpdater.RollbackFileEntry("Claudome.exe", true, v1ExeSha, "Claudome.exe.200.bak"),
                        new SelfUpdater.RollbackFileEntry("PROJECT-LICENSE.txt", true, v1LicSha, "PROJECT-LICENSE.txt.200.bak"),
                        new SelfUpdater.RollbackFileEntry("Gateway-THIRD-PARTY-NOTICES.txt", true, v1NotSha, "Gateway-THIRD-PARTY-NOTICES.txt.200.bak")
                    ]));
                File.WriteAllText(Path.Combine(appDir, SelfUpdater.UpdateUnrecoveredListName), "PROJECT-LICENSE.txt\r\n");

                var res = SelfUpdater.ResumePendingRollback(appDir, regPath);
                Assert(res.Succeeded &&
                       StorageCleanupService.ComputeFileSha256Safe(licPath) == v1LicSha &&
                       !File.Exists(lic2) &&
                       File.Exists(exe1) && File.Exists(lic1) && File.Exists(not1) &&
                       File.ReadAllText(exe1) == "v0-exe" && File.ReadAllText(not1) == "v0-not",
                    "B16 ResumePendingRollback deleted prior tx1 backups while recovering tx2");

                // Sub-case B: Legacy manifest missing BackupFileName + two ambiguous candidate .bak files -> fail-closed!
                var ambDir = Path.Combine(caseDir, "ambiguous");
                Directory.CreateDirectory(ambDir);
                var ambLic = Path.Combine(ambDir, "PROJECT-LICENSE.txt");
                var candA = Path.Combine(ambDir, "PROJECT-LICENSE.txt.301.bak");
                var candB = Path.Combine(ambDir, "PROJECT-LICENSE.txt.302.bak");
                File.WriteAllText(ambLic, "corrupt-lic");
                File.WriteAllText(candA, "same-content");
                File.WriteAllText(candB, "same-content");
                var sameSha = StorageCleanupService.ComputeFileSha256Safe(candA);
                JsonFile.Write(Path.Combine(ambDir, SelfUpdater.UpdateRollbackManifestName), new SelfUpdater.RollbackManifest(
                    "tx-amb", Path.GetFullPath(ambDir), 0, DateTime.UtcNow,
                    [new SelfUpdater.RollbackFileEntry("PROJECT-LICENSE.txt", true, sameSha, "")]));
                File.WriteAllText(Path.Combine(ambDir, SelfUpdater.UpdateUnrecoveredListName), "PROJECT-LICENSE.txt\r\n");

                var ambRes = SelfUpdater.ResumePendingRollback(ambDir, regPath);
                Assert(!ambRes.Succeeded &&
                       ambRes.RemainingFiles.Contains("PROJECT-LICENSE.txt", StringComparer.OrdinalIgnoreCase) &&
                       File.Exists(candA) && File.Exists(candB) &&
                       File.Exists(Path.Combine(ambDir, SelfUpdater.UpdateRollbackManifestName)),
                    "B16 ambiguous legacy backup candidates were deleted or falsely accepted");
                return Task.CompletedTask;
            });

            // Consolidated Blocker 5: Incomplete newer rollback group NEVER triggers eviction of the earlier complete rollback group.
            await check("cleanup-b17-incomplete-latest-group-preserves-earlier-complete-group", () =>
            {
                var caseDir = Path.Combine(baseDir, "b17-complete-group");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(tmpDir);
                new CleanupSettings().Save(settingsPath);

                // Earlier complete VerifiedSuccess group (g1, tx-g1)
                var g1Exe = Path.Combine(appDir, "Claudome.exe.10.bak");
                var g1Lic = Path.Combine(appDir, "PROJECT-LICENSE.txt.10.bak");
                var g1Not = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.10.bak");
                File.WriteAllText(g1Exe, "v1-exe");
                File.WriteAllText(g1Lic, "v1-lic");
                File.WriteAllText(g1Not, "v1-not");
                var t1 = DateTime.UtcNow.AddMinutes(-5);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g1Exe, TransactionId = "tx-g1", Role = "backup", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t1, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(g1Exe) }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g1Lic, TransactionId = "tx-g1", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t1, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(g1Lic) }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g1Not, TransactionId = "tx-g1", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t1, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(g1Not) }, regPath);

                // Newer VerifiedSuccess group (g2, tx-g2) MISSING Gateway-THIRD-PARTY-NOTICES.txt!
                var g2Exe = Path.Combine(appDir, "Claudome.exe.20.bak");
                var g2Lic = Path.Combine(appDir, "PROJECT-LICENSE.txt.20.bak");
                File.WriteAllText(g2Exe, "v2-exe");
                File.WriteAllText(g2Lic, "v2-lic");
                var t2 = DateTime.UtcNow;
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g2Exe, TransactionId = "tx-g2", Role = "backup", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t2 }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g2Lic, TransactionId = "tx-g2", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t2 }, regPath);

                var ctx = new StorageCleanupContext("windows", appDir, tmpDir, [], settingsPath, regPath);
                var preview = StorageCleanupService.Inspect(ctx);
                var g1Items = preview.Items.Where(i => i.Path.EndsWith(".10.bak", StringComparison.OrdinalIgnoreCase)).ToList();
                Assert(g1Items.Count == 3 && g1Items.All(i => i.Status == CleanupItemStatus.Retained && !i.CanSelect) &&
                       preview.Items.All(i => i.Status != CleanupItemStatus.Cleanable),
                    "B17 preview marked earlier complete group Cleanable when latest group missed a required file");

                StorageCleanupService.RotateAfterVerifiedSuccess(BackupCategory.ProgramUpdate, appDir, ctx);
                Assert(File.Exists(g1Exe) && File.Exists(g1Lic) && File.Exists(g1Not) &&
                       File.Exists(g2Exe) && File.Exists(g2Lic),
                    "B17 auto-rotate evicted earlier complete rollback group");

                // Complete g2 -> NOW g1 cleanly becomes Cleanable (manual preview & auto-rotate match) while locked member in g1 skips honestly!
                var g2Not = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.20.bak");
                File.WriteAllText(g2Not, "v2-not");
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g2Not, TransactionId = "tx-g2", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t2 }, regPath);
                var previewReady = StorageCleanupService.Inspect(ctx);
                var cleanG1 = previewReady.Items.Where(i => i.Status == CleanupItemStatus.Cleanable).ToList();
                Assert(cleanG1.Count == 3 && cleanG1.All(i => i.Path.EndsWith(".10.bak", StringComparison.OrdinalIgnoreCase)),
                    "B17 g1 did not become Cleanable once complete g2 existed");

                using (var lockOldMember = new FileStream(g1Lic, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var manRes = StorageCleanupService.ExecuteManualCleanup(cleanG1, ctx);
                    Assert(manRes.DeletedCount == 2 && manRes.SkippedCount == 1 &&
                           File.Exists(g1Lic) && !File.Exists(g1Exe) && !File.Exists(g1Not) &&
                           File.Exists(g2Exe) && File.Exists(g2Lic) && File.Exists(g2Not),
                        "B17 partial locked cleanup did not report skipped count or touched complete g2");
                }
                return Task.CompletedTask;
            });

            // Codex Consolidated Blocker 1: Staged missing/tampered required sidecars abort precheck & allow clean retry; forced mismatched receipt fails closed & recovers v1.
            await check("cleanup-b18-post-verify-missing-or-tampered-staged-sidecar-recovers-and-allows-retry", () =>
            {
                var caseDir = Path.Combine(baseDir, "b18-failed-tx-retry-中文");
                var appDir = Path.Combine(caseDir, "app dir");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(tmpDir);
                new CleanupSettings().Save(settingsPath);

                int RunCmdSync(string cmdFile)
                {
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", SelfUpdater.BuildCmdArguments(cmdFile))
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetTempPath()
                    })!;
                    Assert(p.WaitForExit(15000), "B18 swap cmd timed out");
                    return p.ExitCode;
                }

                // Case 1A: Valid ZIP staged -> delete required sidecar from staged dir -> precheck aborts -> error cleared -> retry unblocked
                var stageA = Path.Combine(tmpDir, "claudome-stage-b18a");
                var payloadA = Path.Combine(stageA, "payload");
                Directory.CreateDirectory(payloadA);
                var exePath = Path.Combine(appDir, "Claudome.exe");
                var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                File.WriteAllText(exePath, "old-exe-v1");
                File.WriteAllText(licPath, "old-lic-v1");
                File.WriteAllText(notPath, "old-not-v1");
                File.WriteAllText(Path.Combine(payloadA, "Claudome.exe"), "new-exe-v2");
                File.WriteAllText(Path.Combine(payloadA, "PROJECT-LICENSE.txt"), "new-lic-v2");
                File.WriteAllText(Path.Combine(payloadA, "Gateway-THIRD-PARTY-NOTICES.txt"), "new-not-v2");
                var stagedHashesA = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Claudome.exe"] = StorageCleanupService.ComputeFileSha256Safe(Path.Combine(payloadA, "Claudome.exe")),
                    ["PROJECT-LICENSE.txt"] = StorageCleanupService.ComputeFileSha256Safe(Path.Combine(payloadA, "PROJECT-LICENSE.txt")),
                    ["Gateway-THIRD-PARTY-NOTICES.txt"] = StorageCleanupService.ComputeFileSha256Safe(Path.Combine(payloadA, "Gateway-THIRD-PARTY-NOTICES.txt"))
                };
                File.Delete(Path.Combine(payloadA, "Gateway-THIRD-PARTY-NOTICES.txt"));

                const int pidA = 999970;
                const string txA = "tx-b18-missing-side";
                var cmdA = Path.Combine(tmpDir, "claudome-update-b18a.cmd");
                SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", pidA, txA);
                JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                    new SelfUpdater.PendingUpdateTransaction(txA, stagedHashesA["Claudome.exe"], exePath, stageA, cmdA, DateTime.UtcNow, ExpectedRequiredSideFileSha256: stagedHashesA));
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = Path.Combine(appDir, $"Claudome.exe.{pidA}.bak"), TransactionId = txA, Role = "backup", OpState = BackupOpState.InProgress }, regPath);
                File.WriteAllText(cmdA, SelfUpdater.BuildSwapScript(appDir, payloadA, stageA, "Claudome.exe", $"Claudome.exe.{pidA}.bak", pidA, relaunch: false, transactionId: txA, expectedTargetHashes: stagedHashesA), new System.Text.UTF8Encoding(false));

                var exitA = RunCmdSync(cmdA);
                Assert(exitA != 0 &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)) &&
                       File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                       File.ReadAllText(exePath) == "old-exe-v1" &&
                       File.ReadAllText(licPath) == "old-lic-v1" &&
                       File.ReadAllText(notPath) == "old-not-v1",
                    $"B18-1A missing staged sidecar did not abort cleanly before swap (exit={exitA})");

                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                var noticeA = SelfUpdater.ReadPendingUpdateNotice(appDir, regPath);
                var uiA = SetupWindow.DescribePendingUpdateNotice(noticeA);
                var txARec = StorageCleanupService.LoadRegistry(regPath).Single(r => r.TransactionId == txA);
                Assert(txARec.OpState == BackupOpState.FailedOrCancelled &&
                       uiA is { ClearErrorMarker: true } &&
                       SelfUpdater.TryGetPendingUpdateBlocker(appDir, out _, regPath),
                    "B18-1A failed to settle original group or prematurely unblocked before error cleared");

                SelfUpdater.ClearUpdateErrorMarker(appDir, regPath);
                Assert(!SelfUpdater.TryGetPendingUpdateBlocker(appDir, out var blockerAfterA, regPath),
                    $"B18-1A ClearUpdateErrorMarker still left blocker: {blockerAfterA}");

                // Case 1B-precheck: Tampered staged sidecar content (same filename) is rejected by certutil SHA-256 precheck before touching live files!
                var stageB1 = Path.Combine(tmpDir, "claudome-stage-b18b1");
                var payloadB1 = Path.Combine(stageB1, "payload");
                Directory.CreateDirectory(payloadB1);
                File.WriteAllText(Path.Combine(payloadB1, "Claudome.exe"), "new-exe-v2");
                File.WriteAllText(Path.Combine(payloadB1, "PROJECT-LICENSE.txt"), "new-lic-v2");
                File.WriteAllText(Path.Combine(payloadB1, "Gateway-THIRD-PARTY-NOTICES.txt"), "tampered-not-v2");
                var expectedB1 = new Dictionary<string, string>(stagedHashesA, StringComparer.OrdinalIgnoreCase);
                const int pidB1 = 999971;
                const string txB1 = "tx-b18-tamper-pre";
                var cmdB1 = Path.Combine(tmpDir, "claudome-update-b18b1.cmd");
                SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", pidB1, txB1);
                JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                    new SelfUpdater.PendingUpdateTransaction(txB1, expectedB1["Claudome.exe"], exePath, stageB1, cmdB1, DateTime.UtcNow, ExpectedRequiredSideFileSha256: expectedB1));
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = Path.Combine(appDir, $"Claudome.exe.{pidB1}.bak"), TransactionId = txB1, Role = "backup", OpState = BackupOpState.InProgress }, regPath);
                File.WriteAllText(cmdB1, SelfUpdater.BuildSwapScript(appDir, payloadB1, stageB1, "Claudome.exe", $"Claudome.exe.{pidB1}.bak", pidB1, relaunch: false, transactionId: txB1, expectedTargetHashes: expectedB1), new System.Text.UTF8Encoding(false));

                var exitB1 = RunCmdSync(cmdB1);
                Assert(exitB1 != 0 &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)) &&
                       File.ReadAllText(notPath) == "old-not-v1",
                    $"B18-1B precheck failed to reject tampered staged sidecar before commit (exit={exitB1})");
                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                SelfUpdater.ClearUpdateErrorMarker(appDir, regPath);
                Assert(!SelfUpdater.TryGetPendingUpdateBlocker(appDir, out _, regPath), "B18-1B precheck abort did not unblock retry");

                // Case 1B-startup: Simulate receipt somehow present while installed sidecar was tampered -> startup marks FailedOrCancelled,
                // writes unrecovered list, preserves rollback.json + .bak group, UI shows recovery action (NOT verified original), ResumePendingRollback restores v1 & unblocks retry!
                const int pidB2 = 999972;
                const string txB2 = "tx-b18-tamper-post";
                SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", pidB2, txB2);
                var exeBak2 = Path.Combine(appDir, $"Claudome.exe.{pidB2}.bak");
                var licBak2 = Path.Combine(appDir, $"PROJECT-LICENSE.txt.{pidB2}.bak");
                var notBak2 = Path.Combine(appDir, $"Gateway-THIRD-PARTY-NOTICES.txt.{pidB2}.bak");
                File.Copy(exePath, exeBak2, overwrite: true);
                File.Copy(licPath, licBak2, overwrite: true);
                File.Copy(notPath, notBak2, overwrite: true);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = exeBak2, TransactionId = txB2, Role = "backup", OpState = BackupOpState.InProgress, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(exeBak2) }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = licBak2, TransactionId = txB2, Role = "backup-sidecar", OpState = BackupOpState.InProgress, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(licBak2) }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = notBak2, TransactionId = txB2, Role = "backup-sidecar", OpState = BackupOpState.InProgress, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(notBak2) }, regPath);
                File.WriteAllText(exePath, "new-exe-v2");
                File.WriteAllText(licPath, "new-lic-v2");
                File.WriteAllText(notPath, "tampered-not-v2");
                JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                    new SelfUpdater.PendingUpdateTransaction(txB2, stagedHashesA["Claudome.exe"], exePath, stageA, cmdA, DateTime.UtcNow, ExpectedRequiredSideFileSha256: stagedHashesA));
                File.WriteAllText(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName), $"{{\"transactionId\":\"{txB2}\",\"swapSucceeded\":true}}");

                SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                var noticeB2 = SelfUpdater.ReadPendingUpdateNotice(appDir, regPath);
                var uiB2 = SetupWindow.DescribePendingUpdateNotice(noticeB2);
                var txB2Recs = StorageCleanupService.LoadRegistry(regPath).Where(r => r.TransactionId == txB2).ToList();
                Assert(txB2Recs.All(r => r.OpState == BackupOpState.FailedOrCancelled) &&
                       !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)) &&
                       File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                       noticeB2.IsRollbackIncomplete &&
                       !noticeB2.IsOriginalGroupVerifiedRestored &&
                       uiB2 is { ClearErrorMarker: false, ActionLabel: "继续恢复原版" } dB2 &&
                       !dB2.Message.Contains("已核验原版", StringComparison.Ordinal),
                    "B18-1B startup did not fail-closed or UI misrepresented unrecovered tampered install as verified original");

                var resumeB2 = SelfUpdater.ResumePendingRollback(appDir, regPath);
                var hasBlockerAfterB2 = SelfUpdater.TryGetPendingUpdateBlocker(appDir, out var blockerAfterB2, regPath);
                Assert(resumeB2.Succeeded &&
                       File.ReadAllText(exePath) == "old-exe-v1" &&
                       File.ReadAllText(licPath) == "old-lic-v1" &&
                       File.ReadAllText(notPath) == "old-not-v1" &&
                       !File.Exists(exeBak2) && !File.Exists(licBak2) && !File.Exists(notBak2) &&
                       !hasBlockerAfterB2,
                    $"B18-1B ResumePendingRollback failed to recover v1 or left blocker: {resumeB2.Message} / {blockerAfterB2}");
                return Task.CompletedTask;
            });

            // Codex Consolidated Blocker 2: Partial finalization (locked completed.json or rollback.json, OR crash after verified.json before MarkTransactionVerified) converges cleanly on later boots.
            await check("cleanup-b19-partial-finalization-locked-receipt-and-verified-marker-converge", () =>
            {
                // Sub-case A: claudome-update-completed.json is readable (FileShare.Read) & locked against Delete during Boot #1 -> Boot #2 unlocks and deletes it so future updates work.
                {
                    var subDir = Path.Combine(baseDir, "b19-locked-receipt");
                    var appDir = Path.Combine(subDir, "app");
                    var tmpDir = Path.Combine(subDir, "tmp");
                    var settingsPath = Path.Combine(subDir, "settings.json");
                    var regPath = Path.Combine(subDir, "registry.json");
                    Directory.CreateDirectory(appDir);
                    Directory.CreateDirectory(tmpDir);
                    new CleanupSettings().Save(settingsPath);

                    var exePath = Path.Combine(appDir, "Claudome.exe");
                    var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                    var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                    File.WriteAllText(exePath, "new-exe-v2");
                    File.WriteAllText(licPath, "new-lic-v2");
                    File.WriteAllText(notPath, "new-not-v2");
                    var exeBak = Path.Combine(appDir, "Claudome.exe.7001.bak");
                    var licBak = Path.Combine(appDir, "PROJECT-LICENSE.txt.7001.bak");
                    var notBak = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.7001.bak");
                    File.WriteAllText(exeBak, "old-exe-v1");
                    File.WriteAllText(licBak, "old-lic-v1");
                    File.WriteAllText(notBak, "old-not-v1");

                    const string txId = "tx-b19-locked-receipt";
                    SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", 7001, txId);
                    var sideHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["PROJECT-LICENSE.txt"] = StorageCleanupService.ComputeFileSha256Safe(licPath),
                        ["Gateway-THIRD-PARTY-NOTICES.txt"] = StorageCleanupService.ComputeFileSha256Safe(notPath)
                    };
                    JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                        new SelfUpdater.PendingUpdateTransaction(txId, StorageCleanupService.ComputeFileSha256Safe(exePath), exePath, tmpDir, "noop.cmd", DateTime.UtcNow, ExpectedRequiredSideFileSha256: sideHashes));
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = exeBak, TransactionId = txId, Role = "backup", OpState = BackupOpState.InProgress }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = licBak, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = notBak, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress }, regPath);
                    var completedPath = Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName);
                    File.WriteAllText(completedPath, $"{{\"transactionId\":\"{txId}\",\"swapSucceeded\":true}}");

                    using (var lockCompleted = new FileStream(completedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                        Assert(File.Exists(completedPath) &&
                               File.Exists(Path.Combine(appDir, SelfUpdater.UpdateVerifiedMarkerName)) &&
                               !File.Exists(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName)) &&
                               !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                               StorageCleanupService.LoadRegistry(regPath).All(r => r.OpState == BackupOpState.VerifiedSuccess),
                            "B19-A Boot #1 did not promote VerifiedSuccess when completed.json was locked against Delete");
                    }

                    // Boot #2 after completed.json unlocked
                    SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                    var hasBlockerA = SelfUpdater.TryGetPendingUpdateBlocker(appDir, out var blockerA, regPath);
                    Assert(!File.Exists(completedPath) &&
                           File.Exists(exeBak) && File.Exists(licBak) && File.Exists(notBak) &&
                           !hasBlockerA,
                        $"B19-A Boot #2 failed to clean unlocked receipt or unblocked future updates: {blockerA}");
                }

                // Sub-case B: Process crashed right after writing claudome-update-verified.json BEFORE MarkTransactionVerified ran
                {
                    var subDir = Path.Combine(baseDir, "b19-verified-before-reg");
                    var appDir = Path.Combine(subDir, "app");
                    var tmpDir = Path.Combine(subDir, "tmp");
                    var settingsPath = Path.Combine(subDir, "settings.json");
                    var regPath = Path.Combine(subDir, "registry.json");
                    Directory.CreateDirectory(appDir);
                    Directory.CreateDirectory(tmpDir);
                    new CleanupSettings().Save(settingsPath);

                    var exePath = Path.Combine(appDir, "Claudome.exe");
                    var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                    var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                    File.WriteAllText(exePath, "new-exe-v2");
                    File.WriteAllText(licPath, "new-lic-v2");
                    File.WriteAllText(notPath, "new-not-v2");
                    var exeBak = Path.Combine(appDir, "Claudome.exe.7002.bak");
                    var licBak = Path.Combine(appDir, "PROJECT-LICENSE.txt.7002.bak");
                    var notBak = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.7002.bak");
                    File.WriteAllText(exeBak, "old-exe-v1");
                    File.WriteAllText(licBak, "old-lic-v1");
                    File.WriteAllText(notBak, "old-not-v1");

                    const string txId = "tx-b19-verified-crash";
                    SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", 7002, txId);
                    var exeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);
                    JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                        new SelfUpdater.PendingUpdateTransaction(txId, exeSha, exePath, tmpDir, "noop.cmd", DateTime.UtcNow));
                    File.WriteAllText(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName), $"{{\"transactionId\":\"{txId}\",\"swapSucceeded\":true}}");
                    JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdateVerifiedMarkerName),
                        new SelfUpdater.VerifiedUpdateReceipt(txId, exeSha, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), DateTime.UtcNow));
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = exeBak, TransactionId = txId, Role = "backup", OpState = BackupOpState.InProgress }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = licBak, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = notBak, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress }, regPath);

                    SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                    var reg = StorageCleanupService.LoadRegistry(regPath).Where(r => r.TransactionId == txId).ToList();
                    var hasBlockerB = SelfUpdater.TryGetPendingUpdateBlocker(appDir, out var blockerB, regPath);
                    Assert(reg.Count == 3 && reg.All(r => r.OpState == BackupOpState.VerifiedSuccess) &&
                           !File.Exists(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName)) &&
                           !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                           !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)) &&
                           File.Exists(exeBak) && File.Exists(licBak) && File.Exists(notBak) &&
                           !hasBlockerB,
                        $"B19-B left registry stuck in InProgress or blocked next update: {blockerB}");
                }
                return Task.CompletedTask;
            });

            // Codex Consolidated Blocker 3: Missing or hash-mismatched EXE backup NEVER gets satisfied by a sidecar's backup-absent record.
            await check("cleanup-b20-exe-missing-or-corrupt-with-sidecar-absent-keeps-older-complete-group", () =>
            {
                var caseDir = Path.Combine(baseDir, "b20-exe-vs-sidecar-absent");
                var appDir = Path.Combine(caseDir, "app");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var settingsPath = Path.Combine(caseDir, "settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(appDir);
                Directory.CreateDirectory(tmpDir);
                new CleanupSettings().Save(settingsPath);

                // Earlier complete VerifiedSuccess group (g1, pid 10)
                var g1Exe = Path.Combine(appDir, "Claudome.exe.10.bak");
                var g1Lic = Path.Combine(appDir, "PROJECT-LICENSE.txt.10.bak");
                var g1Not = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.10.bak");
                File.WriteAllText(g1Exe, "v1-exe");
                File.WriteAllText(g1Lic, "v1-lic");
                File.WriteAllText(g1Not, "v1-not");
                var t1 = DateTime.UtcNow.AddMinutes(-10);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g1Exe, TransactionId = "tx-g1", Role = "backup", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t1, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(g1Exe) }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g1Lic, TransactionId = "tx-g1", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t1, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(g1Lic) }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g1Not, TransactionId = "tx-g1", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t1, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(g1Not) }, regPath);

                // Case 3A: Newer VerifiedSuccess group (g2, pid 20) has sidecar license .bak + Gateway-THIRD-PARTY-NOTICES.txt.20.absent (backup-absent) while EXE backup is MISSING!
                var g2Lic = Path.Combine(appDir, "PROJECT-LICENSE.txt.20.bak");
                var g2NotAbsent = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.20.absent");
                File.WriteAllText(g2Lic, "v2-lic");
                File.WriteAllText(g2NotAbsent, "absent");
                var t2 = DateTime.UtcNow.AddMinutes(-5);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g2Lic, TransactionId = "tx-g2", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t2, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(g2Lic) }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g2NotAbsent, TransactionId = "tx-g2", Role = "backup-absent", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t2 }, regPath);

                var ctx = new StorageCleanupContext("windows", appDir, tmpDir, [], settingsPath, regPath);
                var previewA = StorageCleanupService.Inspect(ctx);
                var g1ItemsA = previewA.Items.Where(i => i.Path.EndsWith(".10.bak", StringComparison.OrdinalIgnoreCase)).ToList();
                Assert(g1ItemsA.Count == 3 && g1ItemsA.All(i => i.Status == CleanupItemStatus.Retained && !i.CanSelect) &&
                       previewA.Items.All(i => i.Status != CleanupItemStatus.Cleanable),
                    "B20-A sidecar backup-absent falsely satisfied EXE check and marked g1 Cleanable");
                StorageCleanupService.RotateAfterVerifiedSuccess(BackupCategory.ProgramUpdate, appDir, ctx);
                Assert(File.Exists(g1Exe) && File.Exists(g1Lic) && File.Exists(g1Not),
                    "B20-A RotateAfterVerifiedSuccess deleted g1 despite missing EXE in g2");

                // Case 3B: Newer group (g2, pid 20) adds EXE backup, EXCEPT EXE backup file content mismatches registered ContentSha256!
                var g2Exe = Path.Combine(appDir, "Claudome.exe.20.bak");
                File.WriteAllText(g2Exe, "corrupted-v2-exe");
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g2Exe, TransactionId = "tx-g2", Role = "backup", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t2, ContentSha256 = new string('f', 64) }, regPath);
                var previewB = StorageCleanupService.Inspect(ctx);
                var g1ItemsB = previewB.Items.Where(i => i.Path.EndsWith(".10.bak", StringComparison.OrdinalIgnoreCase)).ToList();
                Assert(g1ItemsB.Count == 3 && g1ItemsB.All(i => i.Status == CleanupItemStatus.Retained) &&
                       previewB.Items.All(i => i.Status != CleanupItemStatus.Cleanable),
                    "B20-B hash-corrupted EXE in g2 falsely satisfied EXE check and evicted g1");
                StorageCleanupService.RotateAfterVerifiedSuccess(BackupCategory.ProgramUpdate, appDir, ctx);
                Assert(File.Exists(g1Exe) && File.Exists(g1Lic) && File.Exists(g1Not),
                    "B20-B RotateAfterVerifiedSuccess evicted g1 despite corrupt g2 EXE backup");

                // Finally register a truly COMPLETE group g3 -> g1 now becomes Cleanable & rotates cleanly
                var g3Exe = Path.Combine(appDir, "Claudome.exe.30.bak");
                var g3Lic = Path.Combine(appDir, "PROJECT-LICENSE.txt.30.bak");
                var g3Not = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.30.bak");
                File.WriteAllText(g3Exe, "v3-exe");
                File.WriteAllText(g3Lic, "v3-lic");
                File.WriteAllText(g3Not, "v3-not");
                var t3 = DateTime.UtcNow;
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g3Exe, TransactionId = "tx-g3", Role = "backup", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t3, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(g3Exe) }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g3Lic, TransactionId = "tx-g3", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t3, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(g3Lic) }, regPath);
                StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = g3Not, TransactionId = "tx-g3", Role = "backup-sidecar", OpState = BackupOpState.VerifiedSuccess, CreatedUtc = t3, ContentSha256 = StorageCleanupService.ComputeFileSha256Safe(g3Not) }, regPath);

                StorageCleanupService.RotateAfterVerifiedSuccess(BackupCategory.ProgramUpdate, appDir, ctx);
                Assert(!File.Exists(g1Exe) && !File.Exists(g1Lic) && !File.Exists(g1Not) &&
                       File.Exists(g3Exe) && File.Exists(g3Lic) && File.Exists(g3Not),
                    "B20 complete g3 did not rotate older g1 out while preserving complete g3");
                return Task.CompletedTask;
            });

            // Codex Final Acceptance Blocker: First-boot EXE hash mismatch or unreadable EXE at target path fails closed and recovers v1; foreign launch keeps waiting.
            await check("cleanup-b21-first-boot-target-exe-mismatch-unreadable-and-foreign-path", () =>
            {
                // Sub-case A: Foreign-path launch preserves waiting InProgress state (receipt + manifest + backups untouched)
                // -> followed by target-path EXE hash mismatch -> marks FailedOrCancelled, names Claudome.exe, preserves all 3 .bak, ResumePendingRollback restores v1 & unblocks!
                {
                    var subDir = Path.Combine(baseDir, "b21-mismatch-and-foreign (中文)");
                    var appDir = Path.Combine(subDir, "app");
                    var foreignDir = Path.Combine(subDir, "other-copy");
                    var tmpDir = Path.Combine(subDir, "tmp");
                    var settingsPath = Path.Combine(subDir, "settings.json");
                    var regPath = Path.Combine(subDir, "registry.json");
                    Directory.CreateDirectory(appDir);
                    Directory.CreateDirectory(foreignDir);
                    Directory.CreateDirectory(tmpDir);
                    new CleanupSettings().Save(settingsPath);

                    var exePath = Path.Combine(appDir, "Claudome.exe");
                    var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                    var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                    var exeBak = Path.Combine(appDir, "Claudome.exe.8001.bak");
                    var licBak = Path.Combine(appDir, "PROJECT-LICENSE.txt.8001.bak");
                    var notBak = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.8001.bak");
                    File.WriteAllText(exePath, "old-exe-v1");
                    File.WriteAllText(licPath, "old-lic-v1");
                    File.WriteAllText(notPath, "old-not-v1");
                    var v1ExeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);
                    var v1LicSha = StorageCleanupService.ComputeFileSha256Safe(licPath);
                    var v1NotSha = StorageCleanupService.ComputeFileSha256Safe(notPath);

                    const string txId = "tx-b21-exe-mismatch";
                    SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", 8001, txId);
                    File.Copy(exePath, exeBak, overwrite: true);
                    File.Copy(licPath, licBak, overwrite: true);
                    File.Copy(notPath, notBak, overwrite: true);

                    File.WriteAllText(exePath, "expected-exe-v2");
                    var expectedExeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);
                    File.WriteAllText(licPath, "new-lic-v2");
                    File.WriteAllText(notPath, "new-not-v2");
                    var sideHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["PROJECT-LICENSE.txt"] = StorageCleanupService.ComputeFileSha256Safe(licPath),
                        ["Gateway-THIRD-PARTY-NOTICES.txt"] = StorageCleanupService.ComputeFileSha256Safe(notPath)
                    };

                    // Tamper installed EXE on disk AFTER swap wrote expected v2 + receipt!
                    File.WriteAllText(exePath, "tampered-exe-v2");
                    var foreignExe = Path.Combine(foreignDir, "Claudome.exe");
                    File.WriteAllText(foreignExe, "foreign-copy");

                    JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                        new SelfUpdater.PendingUpdateTransaction(txId, expectedExeSha, exePath, tmpDir, "noop.cmd", DateTime.UtcNow, ExpectedRequiredSideFileSha256: sideHashes));
                    File.WriteAllText(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName), $"{{\"transactionId\":\"{txId}\",\"swapSucceeded\":true}}");
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = exeBak, TransactionId = txId, Role = "backup", OpState = BackupOpState.InProgress, ContentSha256 = v1ExeSha }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = licBak, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress, ContentSha256 = v1LicSha }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = notBak, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress, ContentSha256 = v1NotSha }, regPath);

                    // 1) Launch from foreign path -> still waiting for TargetExePath!
                    SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: foreignExe);
                    var regForeign = StorageCleanupService.LoadRegistry(regPath).Where(r => r.TransactionId == txId).ToList();
                    var noticeForeign = SelfUpdater.ReadPendingUpdateNotice(appDir, regPath);
                    var blockedForeign = SelfUpdater.TryGetPendingUpdateBlocker(appDir, out _, regPath);
                    Assert(regForeign.Count == 3 && regForeign.All(r => r.OpState == BackupOpState.InProgress) &&
                           noticeForeign.ErrorReason is null && !noticeForeign.IsRollbackIncomplete &&
                           File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)) &&
                           File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                           File.Exists(exeBak) && File.Exists(licBak) && File.Exists(notBak) &&
                           blockedForeign,
                        "B21 foreign-path launch mutated waiting InProgress state");

                    // 2) Launch from TargetExePath itself with mismatched EXE hash -> fail-closed recovery settlement!
                    SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                    var regMismatch = StorageCleanupService.LoadRegistry(regPath).Where(r => r.TransactionId == txId).ToList();
                    var noticeMismatch = SelfUpdater.ReadPendingUpdateNotice(appDir, regPath);
                    var uiMismatch = SetupWindow.DescribePendingUpdateNotice(noticeMismatch);
                    var blockedBeforeResume = SelfUpdater.TryGetPendingUpdateBlocker(appDir, out _, regPath);
                    Assert(regMismatch.All(r => r.OpState == BackupOpState.FailedOrCancelled) &&
                           !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateVerifiedMarkerName)) &&
                           !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName)) &&
                           File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                           File.Exists(exeBak) && File.Exists(licBak) && File.Exists(notBak) &&
                           noticeMismatch.IsRollbackIncomplete &&
                           !noticeMismatch.IsOriginalGroupVerifiedRestored &&
                           !string.IsNullOrWhiteSpace(noticeMismatch.ErrorReason) &&
                           noticeMismatch.ErrorReason.Contains("Claudome.exe", StringComparison.OrdinalIgnoreCase) &&
                           noticeMismatch.UnrecoveredFiles is not null &&
                           noticeMismatch.UnrecoveredFiles.Contains("Claudome.exe", StringComparer.OrdinalIgnoreCase) &&
                           uiMismatch is { ClearErrorMarker: false, ActionLabel: "继续恢复原版" } &&
                           blockedBeforeResume,
                        "B21 target EXE hash mismatch did not fail closed or failed to identify Claudome.exe");

                    // 3) User runs ResumePendingRollback -> all 3 originals restored & verified against OriginalSha256, next update unblocked!
                    var resumeRes = SelfUpdater.ResumePendingRollback(appDir, regPath);
                    var blockedAfterResume = SelfUpdater.TryGetPendingUpdateBlocker(appDir, out var afterMsg, regPath);
                    Assert(resumeRes.Succeeded &&
                           StorageCleanupService.ComputeFileSha256Safe(exePath) == v1ExeSha &&
                           StorageCleanupService.ComputeFileSha256Safe(licPath) == v1LicSha &&
                           StorageCleanupService.ComputeFileSha256Safe(notPath) == v1NotSha &&
                           !File.Exists(exeBak) && !File.Exists(licBak) && !File.Exists(notBak) &&
                           !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)) &&
                           !File.Exists(Path.Combine(appDir, SelfUpdater.UpdateErrorMarkerName)) &&
                           !blockedAfterResume,
                        $"B21 ResumePendingRollback failed or left blocker: {resumeRes.Message} / {afterMsg}");
                }

                // Sub-case B: Target EXE temporarily unreadable during first boot -> actionable retryable state with backups preserved; recovers once unlocked!
                {
                    var subDir = Path.Combine(baseDir, "b21-unreadable-exe");
                    var appDir = Path.Combine(subDir, "app");
                    var tmpDir = Path.Combine(subDir, "tmp");
                    var settingsPath = Path.Combine(subDir, "settings.json");
                    var regPath = Path.Combine(subDir, "registry.json");
                    Directory.CreateDirectory(appDir);
                    Directory.CreateDirectory(tmpDir);
                    new CleanupSettings().Save(settingsPath);

                    var exePath = Path.Combine(appDir, "Claudome.exe");
                    var licPath = Path.Combine(appDir, "PROJECT-LICENSE.txt");
                    var notPath = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt");
                    var exeBak = Path.Combine(appDir, "Claudome.exe.8002.bak");
                    var licBak = Path.Combine(appDir, "PROJECT-LICENSE.txt.8002.bak");
                    var notBak = Path.Combine(appDir, "Gateway-THIRD-PARTY-NOTICES.txt.8002.bak");
                    File.WriteAllText(exePath, "old-exe-v1");
                    File.WriteAllText(licPath, "old-lic-v1");
                    File.WriteAllText(notPath, "old-not-v1");
                    var v1ExeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);

                    const string txId = "tx-b21-unreadable";
                    SelfUpdater.CaptureRollbackManifest(appDir, "Claudome.exe", 8002, txId);
                    File.Copy(exePath, exeBak, overwrite: true);
                    File.Copy(licPath, licBak, overwrite: true);
                    File.Copy(notPath, notBak, overwrite: true);
                    File.WriteAllText(exePath, "new-exe-v2");
                    var newExeSha = StorageCleanupService.ComputeFileSha256Safe(exePath);
                    File.WriteAllText(licPath, "new-lic-v2");
                    File.WriteAllText(notPath, "new-not-v2");

                    JsonFile.Write(Path.Combine(appDir, SelfUpdater.UpdatePendingTxName),
                        new SelfUpdater.PendingUpdateTransaction(txId, newExeSha, exePath, tmpDir, "noop.cmd", DateTime.UtcNow));
                    File.WriteAllText(Path.Combine(appDir, SelfUpdater.UpdateCompletedReceiptName), $"{{\"transactionId\":\"{txId}\",\"swapSucceeded\":true}}");
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = exeBak, TransactionId = txId, Role = "backup", OpState = BackupOpState.InProgress, ContentSha256 = v1ExeSha }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = licBak, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress }, regPath);
                    StorageCleanupService.RegisterRecord(new BackupRegistryRecord { Category = BackupCategory.ProgramUpdate, ScopeId = Path.GetFullPath(appDir), Path = notBak, TransactionId = txId, Role = "backup-sidecar", OpState = BackupOpState.InProgress }, regPath);

                    using (var lockExe = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        SelfUpdater.CleanupStaleUpdateFiles(appDir, tmpDir, settingsPath, regPath, runningExePath: exePath);
                        var noticeLocked = SelfUpdater.ReadPendingUpdateNotice(appDir, regPath);
                        var lockedResume = SelfUpdater.ResumePendingRollback(appDir, regPath);
                        Assert(noticeLocked.IsRollbackIncomplete &&
                               noticeLocked.ErrorReason is not null &&
                               noticeLocked.ErrorReason.Contains("Claudome.exe", StringComparison.OrdinalIgnoreCase) &&
                               !lockedResume.Succeeded &&
                               lockedResume.RemainingFiles.Contains("Claudome.exe", StringComparer.OrdinalIgnoreCase) &&
                               File.Exists(exeBak) && File.Exists(licBak) && File.Exists(notBak) &&
                               File.Exists(Path.Combine(appDir, SelfUpdater.UpdateRollbackManifestName)),
                            "B21 unreadable EXE did not preserve retryable recovery state while locked");
                    }

                    // Once unlocked, retrying ResumePendingRollback completes restoration cleanly and unblocks retry!
                    var unlockedResume = SelfUpdater.ResumePendingRollback(appDir, regPath);
                    var blockedAfterUnlock = SelfUpdater.TryGetPendingUpdateBlocker(appDir, out var unlockMsg, regPath);
                    Assert(unlockedResume.Succeeded &&
                           StorageCleanupService.ComputeFileSha256Safe(exePath) == v1ExeSha &&
                           !File.Exists(exeBak) && !File.Exists(licBak) && !File.Exists(notBak) &&
                           !blockedAfterUnlock,
                        $"B21 retry after EXE unlock failed: {unlockedResume.Message} / {unlockMsg}");
                }
                return Task.CompletedTask;
            });

            await check("cleanup-b22-hyperv-edition-isolates-archives-and-legacy-workspaces", () =>
            {
                var caseDir = Path.Combine(baseDir, "b22");
                var archiveDir = Path.Combine(caseDir, "archives");
                var tmpDir = Path.Combine(caseDir, "tmp");
                var vboxWorkspace = Path.Combine(caseDir, "vbox-task");
                var runningHyperVWorkspace = Path.Combine(caseDir, "hyperv-running");
                var settingsPath = Path.Combine(caseDir, "cleanup-settings.json");
                var regPath = Path.Combine(caseDir, "registry.json");
                Directory.CreateDirectory(archiveDir);
                Directory.CreateDirectory(tmpDir);
                Paths.EnsureWorkspace(vboxWorkspace);
                Paths.EnsureWorkspace(runningHyperVWorkspace);
                new CleanupSettings().Save(settingsPath);

                // Mixed standard + Hyper-V standalone ZIPs in the same user folder
                var standardOldZip = Path.Combine(archiveDir, "Claudome_0.4.9_win-x64.zip");
                var standardNewZip = Path.Combine(archiveDir, "Claudome_0.4.10_win-x64.zip");
                var hypervOldZip = Path.Combine(archiveDir, "Claudome-HyperV_0.4.8_win-x64.zip");
                var hypervNewZip = Path.Combine(archiveDir, "Claudome-HyperV_0.5.0_win-x64.zip");
                void CreateDummyPackageZip(string zipPath, string entryPayload)
                {
                    using var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
                    var entry = archive.CreateEntry("Claudome.exe");
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(entryPayload);
                }
                CreateDummyPackageZip(standardOldZip, "std-049");
                CreateDummyPackageZip(standardNewZip, "std-0410");
                CreateDummyPackageZip(hypervOldZip, "hv-048");
                CreateDummyPackageZip(hypervNewZip, "hv-050");
                File.SetLastWriteTimeUtc(hypervOldZip, DateTime.UtcNow.AddHours(-2));
                File.SetLastWriteTimeUtc(hypervNewZip, DateTime.UtcNow.AddHours(-1));

                // Legacy VirtualBox workspace containing a previous checkpoint & protected disk
                var vboxState = new SessionState
                {
                    VmId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    Options = new() { Root = vboxWorkspace, PlatformId = "virtualbox", VmName = "LegacyVBox", WorkspaceId = "cccccccccccccccccccccccccccccccc" }
                };
                new CleanupSettings { AutoCleanTaskCheckpoint = false }.Save(settingsPath);
                TaskCheckpointStore.Write(vboxState, settingsPath, regPath);
                vboxState.Completed.Add("step1");
                TaskCheckpointStore.Write(vboxState, settingsPath, regPath);
                vboxState.Completed.Add("step2");
                TaskCheckpointStore.Write(vboxState, settingsPath, regPath);

                // Running Hyper-V standalone workspace (must retain its active files)
                var hvRunningState = new SessionState
                {
                    VmId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                    Options = new() { Root = runningHyperVWorkspace, PlatformId = "hyperv", VmName = "LiveHyperV", WorkspaceId = "dddddddddddddddddddddddddddddddd" }
                };
                TaskCheckpointStore.Write(hvRunningState, settingsPath, regPath);

                var ctx = new StorageCleanupContext(
                    PlatformId: "windows",
                    AppDirectory: caseDir,
                    TempDirectory: tmpDir,
                    WorkspaceRoots: [vboxWorkspace, runningHyperVWorkspace],
                    SettingsPath: settingsPath,
                    RegistryPath: regPath,
                    ArchiveDirectory: archiveDir,
                    ActiveWorkspaceId: hvRunningState.Options.WorkspaceId,
                    ExpectedPlatformId: "hyperv",
                    ExpectedPackagePrefix: "Claudome-HyperV");

                var report = StorageCleanupService.Inspect(ctx);
                var cleanablePaths = report.Items.Where(c => c.CanSelect && c.Status == CleanupItemStatus.Cleanable).Select(c => Path.GetFullPath(c.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var retainedPaths = report.Items.Where(c => c.Status == CleanupItemStatus.Retained && !c.CanSelect).Select(c => Path.GetFullPath(c.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

                Assert(cleanablePaths.Contains(Path.GetFullPath(hypervOldZip)), "B22 old same-edition Hyper-V ZIP should be cleanable");
                Assert(!cleanablePaths.Contains(Path.GetFullPath(hypervNewZip)), "B22 newest same-edition Hyper-V ZIP must not be cleanable");
                Assert(!cleanablePaths.Contains(Path.GetFullPath(standardOldZip)) && !cleanablePaths.Contains(Path.GetFullPath(standardNewZip)),
                    "B22 standard Claudome ZIPs must NEVER be offered as cleanable in Hyper-V context");
                Assert(retainedPaths.Contains(Path.GetFullPath(standardOldZip)) && retainedPaths.Contains(Path.GetFullPath(standardNewZip)),
                    "B22 standard Claudome ZIPs must be Retained");
                Assert(!cleanablePaths.Any(p => p.StartsWith(Path.GetFullPath(vboxWorkspace), StringComparison.OrdinalIgnoreCase)),
                    "B22 legacy VirtualBox workspace files must NOT be marked cleanable in Hyper-V standalone context");
                Assert(report.Items.Any(c => Path.GetFullPath(c.Path) == Path.GetFullPath(hvRunningState.Options.StatePath) && c.Status == CleanupItemStatus.InUse && !c.CanSelect),
                    "B22 active Hyper-V task state must be marked InUse");
                return Task.CompletedTask;
            });
        }

        static SessionState Clone(SessionState s) =>
            System.Text.Json.JsonSerializer.Deserialize<SessionState>(System.Text.Json.JsonSerializer.Serialize(s, JsonFile.Format), JsonFile.Format)!;
    }
}
