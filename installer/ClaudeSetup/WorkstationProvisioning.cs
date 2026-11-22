using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed class WorkstationCheckpoint
{
    public int Schema { get; set; } = 1;
    public string VmId { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string DiskPath { get; set; } = "";
    public string Phase { get; set; } = "creating";
    public List<string> PreservedDisks { get; set; } = [];
}

public static class WorkstationProvisioning
{
    private static string DiskPath(SessionState state, string value)
    {
        var path = Paths.Under(state.Options.VmDirectory, value);
        if (!Regex.IsMatch(Path.GetFileName(path), "^Windows11(?:-retry-[a-f0-9]{32})?\\.vmdk$"))
            throw new SetupException("VMware 安装检查点中的磁盘路径无效，未修改原文件。");
        return path;
    }
    public static void ValidateExisting(SessionState state, Dictionary<string, string> settings, string expectedDisk)
    {
        WorkstationBoundary.CheckOffline(settings);
        var o = state.Options;
        var required = new Dictionary<string, string> { ["displayName"] = o.VmName, ["memsize"] = o.MemoryMb.ToString(), ["numvcpus"] = o.CpuCount.ToString(), ["guestOS"] = "windows11-64", ["firmware"] = "efi", ["uefi.secureBoot.enabled"] = "TRUE", ["scsi0.present"] = "TRUE", ["scsi0:0.present"] = "TRUE", ["scsi0.virtualDev"] = "lsisas1068", ["vmci0.present"] = "TRUE", ["vmci0.unrestricted"] = "FALSE" };
        foreach (var pair in required)
            if (!settings.GetValueOrDefault(pair.Key, "").Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new SetupException("已有 VMware 配置与本任务的资源或隔离设置不符，保留原文件并停止续装。");
        foreach (var pair in new[] { ("guestinfo.claudome.vmId", state.VmId), ("guestinfo.claudome.workspaceId", o.WorkspaceId) })
            if (settings.TryGetValue(pair.Item1, out var value) && value != pair.Item2) throw new SetupException("VMware 配置属于其他任务。");
        var actual = settings.GetValueOrDefault("scsi0:0.fileName", "");
        if (string.IsNullOrEmpty(actual) || !DiskPath(state, actual).Equals(expectedDisk, StringComparison.OrdinalIgnoreCase) || !File.Exists(expectedDisk) || new FileInfo(expectedDisk).Length == 0)
            throw new SetupException("VMware 原磁盘缺失或身份不匹配，未创建替代磁盘或覆盖配置。");
        foreach (var key in settings.Keys.Where(k => Regex.IsMatch(k, "^(scsi|sata|ide|nvme)[0-9]+:[0-9]+\\.present$", RegexOptions.IgnoreCase))) {
            if (!settings[key].Equals("TRUE", StringComparison.OrdinalIgnoreCase) || key.Equals("scsi0:0.present", StringComparison.OrdinalIgnoreCase)) continue;
            var device = key[..^".present".Length];
            if (!settings.GetValueOrDefault(device + ".deviceType", "").Equals("cdrom-image", StringComparison.OrdinalIgnoreCase))
                throw new SetupException("VMware 存在未归属本任务的额外磁盘，停止自动续装。");
        }
    }
    public static async Task Configure(SessionState state, string directory, ICommandRunner runner, CancellationToken token)
    {
        var o = state.Options;
        var marker = JsonFile.Read<Dictionary<string, string>>(Paths.Under(o.VmDirectory, ".claude-workspace.json"));
        if (!Guid.TryParseExact(state.VmId, "D", out _) || marker.GetValueOrDefault("vmId") != state.VmId || marker.GetValueOrDefault("workspaceId") != o.WorkspaceId)
            throw new SetupException("VMware 任务身份不匹配，未恢复配置。");
        var vmx = Paths.Under(o.VmDirectory, o.VmName + ".vmx");
        var checkpointPath = Paths.Under(o.VmDirectory, ".vmware-install.json");
        var saved = File.Exists(checkpointPath) ? JsonFile.Read<WorkstationCheckpoint>(checkpointPath) : null;
        if (saved is not null && (saved.Schema != 1 || saved.VmId != state.VmId || saved.WorkspaceId != o.WorkspaceId || saved.Phase is not ("creating" or "disk-ready" or "ready") || saved.PreservedDisks.Count > 128))
            throw new SetupException("VMware 安装检查点与本任务不符，未恢复配置。");
        var disk = DiskPath(state, saved?.DiskPath ?? "Windows11.vmdk");
        if (File.Exists(vmx)) {
            ValidateExisting(state, WorkstationBoundary.Parse(File.ReadAllText(vmx)), disk);
            // Leave the VMX byte-for-byte intact, including user encryption/TPM state.
            saved ??= new() { VmId = state.VmId, WorkspaceId = o.WorkspaceId, DiskPath = disk };
            saved.Phase = "ready"; JsonFile.Write(checkpointPath, saved); return;
        }
        if (saved?.Phase == "ready") throw new SetupException("VMware 原配置文件已丢失，请恢复原 VMX；不会重建并丢失原加密或 TPM 设置。");
        saved ??= new() { VmId = state.VmId, WorkspaceId = o.WorkspaceId, DiskPath = disk };
        if (saved.Phase != "creating" && !File.Exists(disk)) throw new SetupException("VMware 检查点记录的原磁盘已丢失，未重新创建。");
        if (saved.Phase == "creating" && File.Exists(disk)) {
            if (saved.PreservedDisks.Count >= 128) throw new SetupException("已保留较多中断磁盘，请先检查任务目录的磁盘空间。");
            saved.PreservedDisks.Add(disk); disk = DiskPath(state, "Windows11-retry-" + Guid.NewGuid().ToString("N") + ".vmdk"); saved.DiskPath = disk;
        }
        if (saved.Phase == "creating") {
            JsonFile.Write(checkpointPath, saved); // durable intent before invoking the vendor utility
            await runner.Run(Path.Combine(directory, "vmware-vdiskmanager.exe"), ["-c", "-s", o.DiskGb + "GB", "-a", "lsilogic", "-t", "0", disk], token, 300);
            if (!File.Exists(disk) || new FileInfo(disk).Length == 0) throw new SetupException("VMware 尚未生成完整磁盘文件，已保留进度。");
            saved.Phase = "disk-ready"; JsonFile.Write(checkpointPath, saved);
        }
        var settings = new Dictionary<string, string> {
            [".encoding"]="UTF-8",["config.version"]="8",["virtualHW.version"]="21",["displayName"]=o.VmName,["guestOS"]="windows11-64",["firmware"]="efi",["uefi.secureBoot.enabled"]="TRUE",
            ["memsize"]=o.MemoryMb.ToString(),["numvcpus"]=o.CpuCount.ToString(),["scsi0.present"]="TRUE",["scsi0.virtualDev"]="lsisas1068",["scsi0:0.present"]="TRUE",["scsi0:0.fileName"]=disk,
            ["sata0.present"]="TRUE",["sata0:0.present"]="TRUE",["sata0:0.deviceType"]="cdrom-image",["sata0:0.fileName"]=o.IsoPath,
            ["sata0:1.present"]="TRUE",["sata0:1.deviceType"]="cdrom-image",["sata0:1.fileName"]=Paths.Under(o.Private,"windows-seed.iso"),
            ["sata0:2.present"]="TRUE",["sata0:2.deviceType"]="cdrom-image",["sata0:2.fileName"]=Path.Combine(directory,"windows.iso"),
            ["ethernet0.present"]="FALSE",["vmci0.present"]="TRUE",["vmci0.unrestricted"]="FALSE",["floppy0.present"]="FALSE",["sound.present"]="FALSE",
            ["guestinfo.claudome.vmId"]=state.VmId,["guestinfo.claudome.workspaceId"]=o.WorkspaceId
        };
        foreach (var pair in WorkstationBoundary.SharingDisabled()) settings[pair.Key] = pair.Value;
        ValidateExisting(state, settings, disk);
        WorkstationBoundary.Write(vmx, settings); // atomic replacement of a new task-owned file
        saved.Phase = "ready"; JsonFile.Write(checkpointPath, saved);
    }
}
