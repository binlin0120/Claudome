using System.IO;
using System.Text.Json;

namespace ClaudeSetup;

public static class CleanupProgressTests
{
    private static void Assert(bool value,string message) { if(!value)throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch(SetupException) { return; } throw new Exception("unsafe cleanup/progress accepted"); }
    public static async Task Run(Func<string,Func<Task>,Task> check,string root)
    {
        async Task<(SessionState State,string Old,string Current)> Fixture(string label) {
            var s=new SessionState {VmId=Guid.NewGuid().ToString(),Options=new(){Root=Path.Combine(root,label),PlatformId="workstation",IsoPath=@"D:\Fixture\Windows.iso"}};
            Directory.CreateDirectory(s.Options.VmDirectory);JsonFile.Write(Path.Combine(s.Options.VmDirectory,".claude-workspace.json"),new {vmId=s.VmId,workspaceId=s.Options.WorkspaceId});
            var old=Path.Combine(s.Options.VmDirectory,"Windows11.vmdk");File.WriteAllText(old,"fictional failed disk");
            await new Workstation(new Runner(),"fixture-only").Configure(s,CancellationToken.None);
            return(s,old,Path.Combine(s.Options.VmDirectory,s.Options.VmName+".vmx"));
        }
        await check("cleanup-removes-only-replaced-task-disk-after-ready",async()=>{
            var(s,old,vmx)=await Fixture("cleanup-success");var point=JsonFile.Read<WorkstationCheckpoint>(Path.Combine(s.Options.VmDirectory,".vmware-install.json"));var active=File.ReadAllBytes(point.DiskPath);
            var result=RecoveryCleanup.Workstation(s,[vmx]);Assert(result.Deleted==1&&result.Remaining==0&&!File.Exists(old)&&File.ReadAllBytes(point.DiskPath).SequenceEqual(active),"old disk remained or active disk changed");
            result=RecoveryCleanup.Workstation(s,[vmx]);Assert(result.Deleted==0&&result.Remaining==0,"cleanup not idempotent");
        });
        foreach(var mode in new[]{"active","outside","referenced","not-ready","snapshot"})await check("cleanup-rejects-"+mode+"-before-any-delete",async()=>{
            var(s,old,vmx)=await Fixture("cleanup-"+mode);var file=Path.Combine(s.Options.VmDirectory,".vmware-install.json");var point=JsonFile.Read<WorkstationCheckpoint>(file);var configs=new List<string>{vmx};
            switch(mode){
                case "active":point.PreservedDisks.Add(point.DiskPath);break;
                case "outside":point.PreservedDisks.Add(Path.Combine(root,"other-project","Windows11.vmdk"));break;
                case "not-ready":point.Phase="disk-ready";break;
                case "snapshot":WorkstationBoundary.Write(Path.Combine(s.Options.VmDirectory,"snapshot.vmsd"),new(){["snapshot.numSnapshots"]="1"});break;
                case "referenced":var other=Path.Combine(root,"another.vmx");WorkstationBoundary.Write(other,new(){["scsi0:0.fileName"]=old});configs.Add(other);break;
            }
            JsonFile.Write(file,point);Reject(()=>RecoveryCleanup.Workstation(s,configs));Assert(File.Exists(old)&&File.Exists(point.DiskPath),"a rejected cleanup deleted files");
        });
        await check("cleanup-defers-locked-file-and-keeps-pending-list",async()=>{
            var(s,old,vmx)=await Fixture("cleanup-locked");using var held=File.Open(old,FileMode.Open,FileAccess.Read,FileShare.Read);
            var result=RecoveryCleanup.Workstation(s,[vmx]);Assert(result.Deleted==0&&result.Remaining==1&&File.Exists(old),"locked disk was forced or lost from pending list");
        });
        await check("worker-progress-uses-actual-percentage-and-whitelisted-caption",()=>{
            using var json=JsonDocument.Parse("{\"step\":\"apply-image\",\"percent\":43.5}");var value=InstallationProgress.FromWorker(json.RootElement);
            Assert(value.Percent==43.5&&value.Message.Contains("写入 Windows")&&value.Display.Contains("44%"),"real percentage lost");return Task.CompletedTask;
        });
        await check("unknown-install-progress-remains-indeterminate",()=>{
            using var json=JsonDocument.Parse("{\"step\":\"boot\",\"percent\":null}");var value=InstallationProgress.FromWorker(json.RootElement);Assert(value.Percent is null&&!value.Display.Contains('%'),"invented percentage");return Task.CompletedTask;
        });
        await check("worker-progress-rejects-invalid-number-or-raw-output",()=>{
            foreach(var source in new[]{"{\"step\":\"apply-image\",\"percent\":101}","{\"step\":\"raw-secret-output\",\"percent\":1}"}){using var json=JsonDocument.Parse(source);Reject(()=>InstallationProgress.FromWorker(json.RootElement));}return Task.CompletedTask;
        });
    }
    private sealed class Runner:ICommandRunner
    {
        public Task<string> Run(string exe,IEnumerable<string> args,CancellationToken token,int seconds=120){var file=args.Last();Assert(!File.Exists(file),"test would overwrite disk");File.WriteAllText(file,"fictional successful disk");return Task.FromResult("");}
    }
}
