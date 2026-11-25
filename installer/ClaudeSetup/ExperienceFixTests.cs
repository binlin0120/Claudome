using System.IO;
using System.Text.Json;

namespace ClaudeSetup;

public static class ExperienceFixTests
{
    private sealed class Runner(Func<string[],string> reply):ICommandRunner
    {
        public readonly List<string[]> Calls=[];
        public Task<string> Run(string exe,IEnumerable<string> args,CancellationToken token,int seconds=120){token.ThrowIfCancellationRequested();var a=args.ToArray();Calls.Add(a);return Task.FromResult(reply(a));}
    }
    private sealed class ConsoleFixture:IVmConsole
    {
        public int Shows,Attaches;
        public bool TryShow(string id){Shows++;return true;}
        public void Attach(string directory,string id){Attaches++;}
    }
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static async Task Rejected(Func<Task> action){try{await action();}catch(SetupException){return;}throw new Exception("Expected a scoped rejection");}
    public static async Task Run(Func<string,Func<Task>,Task> check,string root)
    {
        Task Sync(Action action){action();return Task.CompletedTask;}
        SessionState State(string name)=>new(){VmId="11111111-1111-1111-1111-111111111111",Options=new(){Root=Path.Combine(root,name),AcceptChanges=true}};
        string Info(SessionState s,string mode,string clip="disabled")=>$"UUID=\"{s.VmId}\"\nVMState=\"{mode}\"\nCfgFile=\"{Path.Combine(s.Options.VmDirectory,s.Options.VmName+".vbox").Replace("\\","\\\\")}\"\nclipboard=\"{clip}\"\ndraganddrop=\"disabled\"\nclipboard_file_transfers=\"off\"\nusb=\"off\"\nehci=\"off\"\nxhci=\"off\"\nvrde=\"off\"\ncpus=\"4\"\nmemory=\"4096\"\n";
        await check("general-subscription-106-nodes-keeps-70-insecure-36-verified-options",()=>Sync(()=> {
            var links=Enumerable.Range(0,106).Select(i=>$"trojan://fictional-password@node.example:443?allowInsecure={(i<70?1:0)}&sni=cert.example#Node-{i}");
            var parsed=NetworkImport.Parse(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",links))));
            Assert(parsed.Nodes.Count==106&&parsed.Unsupported==0,"whole subscription blocked");var insecure=0;
            foreach(var n in parsed.Nodes){using var doc=JsonDocument.Parse(n.Json);if(doc.RootElement.GetProperty("skip-cert-verify").GetBoolean())insecure++;}
            Assert(insecure==70,"original per-node certificate choices changed");
        }));
        await check("malformed-node-does-not-hide-neighbors",()=>Sync(()=> {
            var list=NetworkImport.Parse("socks5://first.example:1080#First\ntrojan://password@bad.example:443?allowInsecure=invalid#Bad\nsocks5://last.example:1080#Last");
            Assert(list.Nodes.Count==2&&list.Unsupported==1&&list.Issues.Count==1,"single malformed entry interrupted parsing");
        }));
        await check("yaml-original-tls-choice-reaches-core",()=>Sync(()=> {
            var node=NetworkImport.Parse("proxies:\n- {name: Choice, type: trojan, server: node.example, port: 443, password: fixture, skip-cert-verify: true}").Nodes.Single();
            using var doc=JsonDocument.Parse(NetworkImport.CoreConfig(node,["1.1.1.1"]));Assert(doc.RootElement.GetProperty("proxies")[0].GetProperty("skip-cert-verify").GetBoolean(),"TLS choice lost downstream");
        }));
        foreach(var flag in new[]{"true","1","false","0"})await check("URI-certificate-value-"+flag,()=>Sync(()=> {
            var n=NetworkImport.Parse("trojan://fixture@node.example:443?peer=peer.example&allowInsecure="+flag+"#Choice").Nodes.Single();using var json=JsonDocument.Parse(n.Json);
            Assert(json.RootElement.GetProperty("skip-cert-verify").GetBoolean()==(flag is "true" or "1"),"wrong certificate conversion");Assert(json.RootElement.GetProperty("sni").GetString()=="peer.example","SNI peer alias lost");
        }));
        await check("real-VBox-numeric-fields-read-back",()=>Sync(()=> {
            Assert(VmInventory.VBoxValue("memory=4096\ncpus=2\nGuestAdditionsRunLevel=0\n","memory")=="4096","numeric memory field lost");
            Assert(VmInventory.VBoxValue("memory=4096\ncpus=2\nGuestAdditionsRunLevel=0\n","cpus")=="2","numeric CPU field lost");
        }));
        await check("Windows10-x64-accepted-and-host-label-correct",()=>Sync(()=> {
            HostCompatibility.Require("Windows","x64",19045);Assert(!HostCompatibility.IsSupported("Windows","arm64",19045),"ARM accepted by x64 package");
            Assert(new HostInfo("Professional",1,1,4,true,19045,null).Summary.StartsWith("Windows 10 专业版"),"Windows 10 shown as 11");
            var choices=Platforms.Recommend(new("Windows","x64","Core",true,true,16,false,false,Build:19045));
            if (ProductProfile.IsHyperVStandalone)
                Assert(choices.Count == 1 && choices[0].Id == "hyperv" && !choices[0].Compatible, "Windows 10 Home must be rejected in Hyper-V standalone");
            else
                Assert(choices.First().Id=="virtualbox"&&choices.First().Compatible,"Windows 10 Home route wrong");
        }));
        await check("resume-keeps-created-and-started-milestones",()=>Sync(()=> {
            var s=State("resume");s.Completed=["machine","windows-started"];var plan=ResumePlan.For(s);
            Assert(!plan.CreateMachine&&!plan.InstallWindows&&plan.ResumeTitle.Contains("等待原 Windows"),"resume repeated new installation");
            s.Completed.Add("guest-ready");Assert(ResumePlan.For(s).ResumeTitle.Contains("来宾配置"),"resume did not advance");
        }));
        await check("browser-handoff-reuses-the-completed-checkpoint", () => Sync(() =>
        {
            var s = State("browser-resume");
            s.Options.NetworkSource = "已有订阅链接";
            s.Completed = ["machine", "windows-started", "guest-ready", "media-cleanup", "recovery-cleanup", "guest-files", "software"];
            s.ClashConfigured = s.NetworkPassed = true;
            s.GuestSettingsStamp = ResumePlan.SettingsStamp(s.Options, "current-network-config");
            s.PendingActions = [new("AdsPower", [], "adspower"), new("验收", [], "acceptance")];
            Assert(ResumePlan.CanResumeBrowser(s), "existing customer checkpoint did not continue AdsPower");
            s.PendingActions.Add(new("补传资源", [], "guest-files"));
            Assert(!ResumePlan.CanResumeBrowser(s), "browser handoff skipped a pending file repair");
            s.PendingActions.RemoveAt(2);
            s.NetworkPassed = false;
            Assert(ResumePlan.CanResumeBrowser(s), "an unreadable report lost the browser continuation checkpoint");
            s.PendingActions.Add(new("复查网络", [], "resume-network"));
            Assert(!ResumePlan.CanResumeBrowser(s), "browser handoff skipped an explicit network repair");
            s.PendingActions.RemoveAt(2);
            s.Options.ManualAdsPower = true;
            s.PendingActions = [CustomerGuide.ManualAdsPower()];
            Assert(ResumePlan.CanResumeBrowser(s), "manual mode lost the installed-software checkpoint");
            s.PendingActions = [new("复查来宾的代理联网", [], "guest-step")];
            Assert(ResumePlan.CanResumeBrowser(s), "legacy TLS failure repeated installed-software setup");
            s.PendingActions = [new("复查来宾的代理联网", [], "guest-network-probe")];
            Assert(ResumePlan.CanResumeBrowser(s), "TLS retry lost the installed-software checkpoint");
            s.Completed.Remove("software");
            Assert(!ResumePlan.CanResumeBrowser(s), "browser handoff skipped unfinished software installation");
        }));
        await check("network-reconnect-uses-only-the-existing-task-and-does-not-upload", async () =>
        {
            var task = State("fileless-network");
            task.Options.NetworkSource = "已有订阅链接";
            task.CoreConfigSha256 = new string('c', 64);
            Assert(!Deployment.HasConfiguredWorkspaceNetwork(task), "unfinished guest should not start deployment from a reconnect");
            task.Completed.Add("software");
            Assert(Deployment.HasConfiguredWorkspaceNetwork(task) && !task.SetupFinished,
                "configured guest must reconnect even before the user finishes setup");
            task.Options.Country = "KR";
            var runner = new Runner(args =>
            {
                Assert(args[0] == "guestcontrol" && args[1] == task.VmId && args[2] == "run" && args[^2] == "-EncodedCommand",
                    "network test changed the VM or requested a file transfer");
                var text = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
                var configuration = text.Split("'")[1];
                using var input = JsonDocument.Parse(Convert.FromBase64String(configuration));
                var root = input.RootElement;
                Assert(root.GetProperty("country").GetString() == "KR" && root.GetProperty("expectedExitIp").GetString() == "",
                    "the verifier did not preserve the dynamic exit policy");
                Assert(!text.Contains("fictional-ads-key"), "network-only check forwarded an API key");
                return "CLAUDOME_NET_CHECK:" + JsonSerializer.Serialize(new
                {
                    schema = 2,
                    runId = root.GetProperty("runId").GetString(),
                    vmId = task.VmId,
                    workspaceId = task.Options.WorkspaceId,
                    networkPassed = true
                });
            });
            using var report = await Deployment.ReadQuickNetwork(new VirtualBox(runner, "fixture-vbox"), task,
                new() { VmPassword = "FictionalReconnect!1", AdsPowerApiKey = "fictional-ads-key" }, CancellationToken.None);
            Assert(report.RootElement.GetProperty("networkPassed").GetBoolean() && runner.Calls.Count == 1,
                "network result did not come from exactly one current guest command");
            await Rejected(() => Sync(() => Deployment.ParseQuickNetworkOutput("CLAUDOME_NET_CHECK:{\"schema\":2,\"runId\":\"old\"}", task, "current").Dispose()));
        });
        await check("deployment-checks-live-runtime-and-only-copies-missing-guest-packages", async () =>
        {
            var task = State("guest-package-inventory");
            var powerShell = Catalog.Resources.Single(r => r.Id == "pwsh");
            var currentVersion = powerShell.FileName.Split('-')[1];
            var pwshPresent = true;
            var installed = new[] { "clash", "adspower" };
            var cached = new[] { "vcredist" };
            var fetches = new List<string>();
            var runner = new Runner(args =>
            {
                Assert(args[0] == "guestcontrol" && args[1] == task.VmId, "probe changed task target");
                if (args[2] == "stat")
                {
                    if (!pwshPresent && args[^1].EndsWith("pwsh.exe", StringComparison.Ordinal))
                        throw new SetupException("VERR_FILE_NOT_FOUND");
                    return "";
                }
                if (args[2] is "mkdir" or "copyto") return "";
                if (args[^1] == "-Version") return "PowerShell " + currentVersion;
                if (args.Contains("-hashfile")) return powerShell.Sha256;
                if (args.Contains("-xf")) { pwshPresent = true; return ""; }
                Assert(args[^2] == "-EncodedCommand", "resource inspection did not use the installed runtime");
                var text = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
                using var input = JsonDocument.Parse(Convert.FromBase64String(text.Split("'")[1]));
                return "CLAUDOME_INSTALLER_INVENTORY:" + JsonSerializer.Serialize(new
                {
                    schema = 2, runId = input.RootElement.GetProperty("runId").GetString(),
                    vmId = task.VmId, workspaceId = task.Options.WorkspaceId, status = "ready", installed, cached
                });
            });
            await using var api = new VirtualBox(runner, "fixture-vbox");
            var deployment = new Deployment(new Progress<string>(), new Progress<DownloadProgress>());
            Task<string> Fetch(Resource resource, CancellationToken _) { fetches.Add(resource.Id); return Task.FromResult(Path.Combine(task.Options.Root, resource.FileName)); }
            var guest = new Secrets { VmPassword = "FictionalPackageCheck!1" };
            var needed = await deployment.PrepareGuestPackages(api, task, guest, task.Options.GuestSetupDirectory, false,
                CancellationToken.None, Fetch);
            Assert(fetches.Count == 0 && needed.Select(r => r.Id).SequenceEqual(new[] { "vcredist" }), "ready apps should not be downloaded");
            Assert(runner.Calls.All(args => args[2] != "copyto" && !args.Contains("-xf")), "ready runtime and apps were recopied");

            runner.Calls.Clear();
            installed = ["clash"];
            pwshPresent = false;
            await deployment.PrepareGuestPackages(api, task, guest, task.Options.GuestSetupDirectory, false,
                CancellationToken.None, Fetch);
            Assert(fetches.SequenceEqual(new[] { "adspower" }) && runner.Calls.Count(args => args[2] == "copyto") == 1 &&
                runner.Calls.Count(args => args.Contains("-xf")) == 1, "did not reuse the verified PowerShell ZIP or copied an unrelated installer");

            // Hyper-V reuses C:\ProgramData\ClaudeBootstrap\pwsh locally inside guest without transferring pwsh ZIP a second time
            var hvTask = State("guest-package-hyperv-local-pwsh");
            hvTask.Options.PlatformId = "hyperv";
            Directory.CreateDirectory(hvTask.Options.Private);
            var hvCopiedPwsh = false;
            var hvRunner = new Runner(args =>
            {
                if (args[2] == "stat")
                {
                    var p = args[^1];
                    if (p.EndsWith(@"ClaudeBootstrap\pwsh\pwsh.exe", StringComparison.OrdinalIgnoreCase) || hvCopiedPwsh)
                        return "";
                    throw new SetupException("VERR_FILE_NOT_FOUND");
                }
                if (args[2] == "mkdir") return "";
                if (args[2] == "copyto") throw new Exception("unexpected host->VM pwsh upload when bootstrap pwsh exists");
                if (args[^1] == "-Version") return "PowerShell " + currentVersion;
                if (args[^2] == "-EncodedCommand")
                {
                    var cmd = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
                    if (cmd.StartsWith("Copy-Item", StringComparison.Ordinal)) { hvCopiedPwsh = true; return ""; }
                    using var inp = JsonDocument.Parse(Convert.FromBase64String(cmd.Split("'")[1]));
                    return "CLAUDOME_INSTALLER_INVENTORY:" + JsonSerializer.Serialize(new
                    {
                        schema = 2, runId = inp.RootElement.GetProperty("runId").GetString(),
                        vmId = hvTask.VmId, workspaceId = hvTask.Options.WorkspaceId, status = "ready",
                        installed = new[] { "clash", "adspower" }, cached = new[] { "vcredist" }
                    });
                }
                return "";
            });
            var hvNeeded = await deployment.PrepareGuestPackages(new VirtualBox(hvRunner, "fixture-hv"), hvTask, guest,
                hvTask.Options.GuestSetupDirectory, false, CancellationToken.None, Fetch);
            Assert(hvCopiedPwsh && hvNeeded.Select(r => r.Id).SequenceEqual(new[] { "vcredist" }), "Hyper-V did not reuse bootstrap pwsh locally inside guest");
        });
        await check("missing-VM-identity-cannot-silently-recreate",async()=> {
            var s=new SessionState{Completed=["machine"]};await Rejected(()=>Sync(()=>ResumePlan.For(s)));
        });
        await check("already-running-original-console-is-reused",async()=> {
            var s=State("running");var ui=new ConsoleFixture();var r=new Runner(a=>a[0]=="list"?$"\"{s.Options.VmName}\" {{{s.VmId}}}":Info(s,"running"));
            await new VirtualBox(r,"fixture-vbox",ui).Open(s,CancellationToken.None);
            Assert(r.Calls.All(c=>c[0] is "list" or "showvminfo")&&ui.Attaches==0,"running VM or visible console restarted");
        });
        await check("slow-start-waits-for-running-without-duplicate-start",async()=> {
            var s=State("slow");var started=false;var polls=0;
            var r=new Runner(a=> {if(a[0]=="list")return $"\"{s.Options.VmName}\" {{{s.VmId}}}";if(a[0]=="startvm"){started=true;return "";}return Info(s,!started?"poweroff":++polls<4?"starting":"running");});
            await new VirtualBox(r,"fixture-vbox",startCheck:_=>{}).EnsureRunning(s,CancellationToken.None,10,1);
            Assert(r.Calls.Count(c=>c[0]=="startvm")==1&&polls>=4,"early running-state rejection");
        });
        await check("starting-state-is-polled-without-another-launch",async()=> {
            var s=State("transition");var polls=0;var r=new Runner(a=>a[0]=="list"?$"\"{s.Options.VmName}\" {{{s.VmId}}}":Info(s,++polls<5?"starting":"running"));
            await new VirtualBox(r,"fixture-vbox").EnsureRunning(s,CancellationToken.None,10,1);Assert(r.Calls.All(c=>c[0]!="startvm"),"transition launched duplicate VM");
        });
        await check("sharing-recovery-only-modifies-powered-off-authorized-task",async()=> {
            var s=State("sharing-off");var repaired=false;var r=new Runner(a=> {if(a[0]=="list")return $"\"{s.Options.VmName}\" {{{s.VmId}}}";if(a[0]=="modifyvm"){
                // The modifyvm input uses enabled/disabled; its readback uses on/off.
                var position=Array.IndexOf(a,"--clipboard-file-transfers");
                if(position<0||position+1>=a.Length||a[position+1]!="disabled")throw new SetupException("Fixture CLI rejected clipboard file transfer value");
                repaired=true;return "";}return Info(s,"poweroff",repaired?"disabled":"hosttoguest");});
            await new VirtualBox(r,"fixture-vbox").EnsureSharing(s,CancellationToken.None);
            Assert(repaired&&r.Calls.Single(c=>c[0]=="modifyvm")[1]==s.VmId,"sharing recovery scope wrong");
        });
        await check("powered-off-sharing-recovery-still-requires-task-consent",async()=> {
            var s=State("sharing-no-consent");s.Options.AcceptChanges=false;var r=new Runner(_=>Info(s,"poweroff","hosttoguest"));
            await Rejected(()=>new VirtualBox(r,"fixture-vbox").EnsureSharing(s,CancellationToken.None));
            Assert(r.Calls.All(c=>c[0]!="modifyvm"),"unauthorized sharing repair");
        });
        await check("sharing-recovery-rejects-same-UUID-with-foreign-directory",async()=> {
            var s=State("sharing-foreign");var foreign=Info(s,"poweroff","hosttoguest").Replace(s.Options.VmDirectory.Replace("\\","\\\\"),Path.Combine(root,"another-task").Replace("\\","\\\\"));
            var r=new Runner(a=>a[0]=="list"?$"\"{s.Options.VmName}\" {{{s.VmId}}}":foreign);
            await Rejected(()=>new VirtualBox(r,"fixture-vbox").EnsureSharing(s,CancellationToken.None));Assert(r.Calls.All(c=>c[0]!="modifyvm"),"foreign task repaired");
        });
        await check("running-sharing-change-gives-specific-handoff-without-mutation",async()=> {
            var s=State("sharing-running");var r=new Runner(a=>Info(s,"running","hosttoguest"));
            try{await new VirtualBox(r,"fixture-vbox").EnsureSharing(s,CancellationToken.None);throw new Exception("Expected handoff");}
            catch(HandoffException ex){Assert(ex.Message.Contains("共享剪贴板")&&ex.Action.Steps[0].Contains("hosttoguest"),"unhelpful generic sharing error");}
            Assert(r.Calls.All(c=>c[0]=="showvminfo"),"running VM was reconfigured");
        });
        await check("console-window-matching-requires-exact-UUID-token",()=>Sync(()=> {
            const string id="11111111-1111-1111-1111-111111111111";
            Assert(ConsoleWindowLocator.MatchesCommand("VirtualBoxVM.exe --startvm "+id+" --separate",id),"owned window not found");
            Assert(!ConsoleWindowLocator.MatchesCommand("--startvm "+id+"0",id),"another UUID prefix matched");
        }));
        await check("black-screen-diagnostic-is-not-a-false-recovery-pass",()=>Sync(()=> {
            var result=VmDiagnostics.Parse("VMState=\"running\"\ncpus=\"4\"\nmemory=\"4096\"\nGuestAdditionsRunLevel=\"0\"", "NEMR3Init: Snail execution mode is active!\nChanging the VM state to 'RESETTING'");
            Assert(result.NemMode&&!result.GuestToolsReady&&result.Actions.Any(x=>x.Contains("待验证")),"diagnosis reported black screen fixed");
        }));
    }
}
