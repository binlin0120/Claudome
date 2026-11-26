using System.Windows;
using System.Windows.Controls;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    private void RefreshPlatforms()
    {
        var previous = platform.SelectedValue?.ToString();
        refreshingPlatforms=true;
        try {
            platform.ItemsSource = Platforms.Recommend(Platforms.Detect());
            if (previous is not null) platform.SelectedValue = previous;
            if (platform.SelectedIndex < 0) platform.SelectedIndex = 0;
        } finally { refreshingPlatforms=false; }
    }
    private Border PendingCard(UserAction action)
    {
        if (action.Id == CustomerGuide.ManualAdsPowerId)
            action = CustomerGuide.ManualAdsPower();
        var stepList = new StackPanel();
        foreach (var (text, index) in action.Steps.Select((t, i) => (t, i)))
            stepList.Children.Add(Text($"{index + 1}. {text}", 12));
        UIElement stepsBlock = action.Steps.Length > 4
            ? new ScrollViewer { Content = stepList, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 8) }
            : stepList;
        var instructions = new List<UIElement> { Text(action.Title, 16, false), stepsBlock };
        if (action.Id == CustomerGuide.ManualAdsPowerId)
        {
            instructions.Add(Row(Action("打开程序内虚拟机桌面", (_, _) => Navigate(7), true)));
            return Card(instructions.ToArray());
        }
        if (action.Id == "resume-network")
        {
            instructions.Add(Row(Action("重试检查原网络", async (_, _) => await StartInstall(checkSavedNetwork: true), true),
                Action("重新配置本任务网络", async (_, _) =>
                {
                    if (MessageBox.Show(this, "将按当前任务已保存的线路重新配置来宾网络，然后重新核对生效设置和出口。是否继续？",
                        "重新配置本任务网络", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        await StartInstall(reconfigureGuestNetwork: true);
                })));
            return Card(instructions.ToArray());
        }
        if(state.RestartBootBefore.Length>0 || new[]{"runtime-validation","browser-runtime","browser-reopen","outage-validation"}.Contains(action.Id)) {
            instructions.Add(Row(Action("处理后检查现有配置",async(_,_)=>await RunValidation(false,false),true),Action("重新配置后再验收",async(_,_)=>await StartInstall())));
            return Card(instructions.ToArray());
        }
        if(new[]{"guest-wait","guest-status-unreadable","vm-start-wait","vm-sharing","console-window","gateway-firewall-profile"}.Contains(action.Id)) {
            instructions.Add(Row(Action("打开程序内虚拟机桌面",(_,_)=>Navigate(7),true),Action("处理后继续原任务",async(_,_)=>await StartInstall())));
            return Card(instructions.ToArray());
        }
        if(action.Id=="hyperv-disk-recovery") { instructions.Add(Row(Action("处理后重新检查并恢复",async(_,_)=>await StartInstall(),true))); return Card(instructions.ToArray()); }
        if(action.Id=="vm-stop-before-install") { instructions.Add(Row(Action("去「专属桌面」关机", (_, _) => Navigate(7)), Action("已关机，继续安装", async (_, _) => await StartInstall(), true))); return Card(instructions.ToArray()); }
        if(action.Id=="hyperv-restart") { instructions.Add(Row(Action("我已重启，重新检查并继续", async (_, _) => await StartInstall(), true))); return Card(instructions.ToArray()); }
        if(action.Id=="windows-media"){instructions.Add(Row(Action("去读取镜像版本",(_,_)=>Go(2),true)));return Card(instructions.ToArray());}
        if(action.Id=="hyperv-console") instructions.Add(Row(Action("允许关闭主机增强会话并继续",async(_,_)=>{
            if(MessageBox.Show(this,"这会关闭整台主机的 Hyper-V 增强会话策略，其他 Hyper-V 虚拟机也会受影响。原值会保存，待所有受保护虚拟机正常关机后可恢复。是否继续？","确认 Hyper-V 主机设置",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            state.Options.HyperVBasicConsoleConfirmed=true;state.Save();await StartInstall();
        },true)));
        if (action.Id == "workstation-security") instructions.Add(Row(Action("已完成安全设置，继续", async (_, _) => {
            if (MessageBox.Show(this, "确认你已在 VMware 中配置加密和虚拟 TPM，并自行保存加密密码？安装器不会替你恢复这个密码。", "继续安装", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            state.Options.VmwareSecurityConfirmed = true; state.Save(); await StartInstall();
        }, true)));
        if (action.Id == "isolation-boundary") { instructions.Add(Text("这是旧构建保存的记录。当前构建已改为实际边界检查，请重新核对方案和镜像后继续。", 12)); instructions.Add(Row(Action("重新检查安装方案",(_,_)=>Go(0),true))); return Card(instructions.ToArray()); }
        if (action.Id == "adspower")
            instructions.Add(Row(Action("不用 API，手动使用 AdsPower", (_, _) => { SelectManualAdsPower(); Navigate(7); })));
        var openAction = ResumePlan.HasVerifiedSystemDiskProof(state)
            ? Action("打开虚拟机", async (_, _) => await Perform(async (_, token) => await Backends.Open(state, token)))
            : Action("前往「6 开始安装」", (_, _) => Go(5));
        instructions.Add(Row(openAction, Action("处理后继续部署", async (_, _) => await StartInstall(), true)));
        return Card(instructions.ToArray());
    }
    private void RefreshPending()
    {
        pendingPanel.Children.Clear();
        foreach (var action in state.PendingActions) pendingPanel.Children.Add(PendingCard(action));
    }
}
