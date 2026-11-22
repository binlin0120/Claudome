using System.IO;

namespace ClaudeSetup;

public static class CommandFailureTests
{
    private static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    public static async Task Run(Func<string,Func<Task>,Task> check,string root)
    {
        Task Sync(Action action){action();return Task.CompletedTask;}
        const string secret="FICTIONAL_PRIVATE_VALUE_237";
        await check("VirtualBox-invalid-parameter-shows-operation-without-raw-value",()=>Sync(()=> {
            var text=CommandFailure.Describe(@"C:\components\VBoxManage.exe",["modifyvm",secret,"--password",secret],2,"VBoxManage.exe: error: Invalid clipboard argument '"+secret+"'\nhttps://example.invalid/?token="+secret);
            Assert(text.Contains("VirtualBox")&&text.Contains("更新原虚拟机设置")&&text.Contains("退出码 2")&&text.Contains("命令参数"),"missing command context");
            Assert(!text.Contains(secret)&&!text.Contains("example.invalid")&&!text.Contains("--password"),"private arguments escaped");
        }));
        await check("VirtualBox-state-error-is-not-misdiagnosed-as-password",()=>Sync(()=> {
            var text=CommandFailure.Describe("VBoxManage.exe",["startvm",secret],1,"VBOX_E_INVALID_VM_STATE\ncredential="+secret);
            Assert(text.Contains("启动原虚拟机")&&text.Contains("VBOX_E_INVALID_VM_STATE")&&text.Contains("状态")&&!text.Contains(secret),"state cause lost");
        }));
        await check("VirtualBox-guest-exit-identifies-extraction-and-keeps-raw-arguments-private", () => Sync(() => {
            var run = CommandFailure.Describe("VBoxManage.exe",
                ["guestcontrol", secret, "run", "--exe", @"C:\Windows\System32\tar.exe", "--", secret], 33, secret);
            var directory = CommandFailure.Describe("VBoxManage.exe", ["guestcontrol", secret, "mkdir", "--parents", secret], 1, secret);
            Assert(run.Contains("解压来宾安装资源") && run.Contains("来宾程序退出码 1") && !run.Contains(secret),
                "Mapped guest exit lost operation context or exposed a private value");
            Assert(directory.Contains("创建来宾目录") && !directory.Contains(secret), "Directory failure lacked a safe operation label");
        }));
        await check("VMware-command-context-does-not-reuse-VirtualBox-settings",()=>Sync(()=> {
            var text=CommandFailure.Describe("vmrun.exe",["list"],1,"Error: "+secret);
            Assert(text.Contains("VMware")&&text.Contains("读取虚拟机列表")&&!text.Contains("VirtualBox")&&!text.Contains(secret),"wrong backend context");
        }));
        await check("HyperV-failure-keeps-structured-operation-and-known-code",()=>Sync(()=> {
            var text=CommandFailure.HyperV("windows","WINDOWS_IMAGE_APPLY_FAILED","系统映像写入未完成。");
            Assert(text.Contains("安装 Windows")&&text.Contains("WINDOWS_IMAGE_APPLY_FAILED"),"Hyper-V operation code lost");
            var unknown=CommandFailure.HyperV(secret,secret,"原任务保留。");Assert(!unknown.Contains(secret)&&unknown.Contains("HYPERV_STEP_FAILED"),"unknown worker text escaped");
        }));
        await check("hyperv-windows-errors-report-only-whitelisted-stage-and-numeric-status", () => Sync(() =>
        {
            using var known = System.Text.Json.JsonDocument.Parse("""
                {"windowsStage":"apply-image","windowsNativeCode":-2147024784,"message":"fixture-private-value"}
                """);
            var detail = CommandFailure.HyperVWindowsStage(known.RootElement);
            Assert(detail.Contains("写入 Windows 系统映像") && detail.Contains("0x80070070") &&
                !detail.Contains("fixture-private-value"), "Missing or unsafe Windows installation detail");
            using var unknown = System.Text.Json.JsonDocument.Parse("""{"windowsStage":"fixture-private-value","windowsNativeCode":"private"}""");
            Assert(CommandFailure.HyperVWindowsStage(unknown.RootElement).Length == 0, "Unexpected worker data reached the UI");
        }));
        await check("VMware-VIX-errors-keep-numeric-code-and-scoped-operation",()=>Sync(()=> {
            var text=CommandFailure.Vix("login",3015);Assert(text.Contains("登录来宾系统")&&text.Contains("3015"),"VIX details lost");
            Assert(!CommandFailure.Vix(secret,1).Contains(secret)&&CommandFailure.GuestExit(23).Contains("退出码 23"),"VIX exposed argument or lost guest exit");
        }));
        await check("real-command-runner-nonzero-exit-omits-private-argument",async()=> {
            // Supported Windows hosts already provide this read-only tool; no SDK or pwsh install is needed.
            var runtime=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"whoami.exe");
            try{await new CommandRunner().Run(runtime,["/NOT_A_REAL_OPTION_"+secret],CancellationToken.None,30);throw new Exception("Expected command failure");}
            catch(SetupException error){Assert(error.Message.Contains("外部组件")&&error.Message.Contains("退出码 ")&&!error.Message.Contains(secret)&&!error.Message.Contains(runtime),"runner lost safe failure evidence");}
        });
    }
}
