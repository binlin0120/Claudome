using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ClaudeSetup;

internal enum TaskSaveOutcome { NotInitialized, Saved, KeptNewer, LockedByAnotherInstance }

public sealed partial class SetupWindow
{
    private readonly DispatcherTimer taskSaveTimer=new(){Interval=TimeSpan.FromSeconds(15)};
    private bool closeAfterPause,closingPrompt,taskSavingEnabled;
    internal static TaskSaveOutcome TrySaveTaskState(SessionState state)
    {
        if (!File.Exists(Paths.Under(state.Options.Root, ".claude-setup-workspace.json"))) return TaskSaveOutcome.NotInitialized;
        var ownsLock = WorkspaceLock.IsHeldBy(state);
        WorkspaceLock? idleLock = null;
        if (!ownsLock)
        {
            try { idleLock = WorkspaceLock.Acquire(state); }
            catch (IOException) { return TaskSaveOutcome.LockedByAnotherInstance; }
        }
        using (idleLock)
        {
            if (!ownsLock && File.Exists(state.Options.StatePath))
            {
                var saved = JsonFile.Read<SessionState>(state.Options.StatePath);
                if (saved.UpdatedUtc > state.UpdatedUtc) return TaskSaveOutcome.KeptNewer;
            }
            state.Save();
            return TaskSaveOutcome.Saved;
        }
    }
    private bool SaveCurrentTask(bool showFailure=false)
    {
        if(!taskSavingEnabled||string.IsNullOrEmpty(state.VmId))return true;
        try {
            var outcome = TrySaveTaskState(state);
            return true;
        }catch(Exception error) {
            if(showFailure)Notice("当前进度尚未保存，原任务与上一份保存保留。\n"+Redaction.SafeError(error));
            return false;
        }
    }
    private void InitializeTaskSaving(bool enabled)
    {
        taskSavingEnabled=enabled;taskSaveTimer.Tick+=(_,_)=>SaveCurrentTask();if(enabled)taskSaveTimer.Start();
        Closing+=OnTaskClosing;
        Closed+=(_,_)=>{StopHyperVDirectClipboardSync();taskSaveTimer.Stop();desktopConnectionCancellation?.Cancel();mediaCancellation?.Cancel();desktopTimer.Stop();};
    }
    private void PauseCurrentTask()
    {
        StopHyperVDirectClipboardSync();
        CancelPendingHyperVDisplayFit(resetViewport: true);
        desktopConnectionPolicy.Suspend();autoConnectDesktop=false;
        BoundaryRuntime.RevokeWindowsSignInHold(state);
        if (consoleHost?.IsAttached != true) desktopConnectionCancellation?.Cancel();
        if(!busy)state.Status="已暂停，可继续原任务";
        SaveCurrentTask();cancellation?.Cancel();mediaCancellation?.Cancel();
        SetDesktopConnectionStatus(busy?"正在保存并暂停，完成当前可中断步骤后保留原任务。":"任务已保存并暂停。");
    }
    private void CompleteCloseAfterPause()
    {
        if (closeAfterPause && !busy && !mediaBusy) _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private bool pendingConsoleClose;
    private async Task FinishPendingConsoleClose()
    {
        try { await HidePendingConsole(); }
        finally { pendingConsoleClose = false; _ = Dispatcher.BeginInvoke(new Action(Close)); }
    }

    private void OnTaskClosing(object? sender,CancelEventArgs e)
    {
        if (!busy && !mediaBusy) {
            if (closingDesktop) { e.Cancel = pendingConsoleClose; return; }
            if (taskSavingEnabled && !closingPrompt && !closeAfterPause && BoundaryRuntime.BackendFor(state) is not null &&
                MessageBox.Show(this, "退出会保存进度并结束受控连接。需要保持虚拟机联网时，可以取消并最小化。确认退出？",
                    "退出 Claudome", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }
            if (!SaveCurrentTask(true)) { e.Cancel = true; return; }
            CloseDesktopConsole();
            if (pendingConsole is null) return;
            e.Cancel = pendingConsoleClose = true;
            _ = FinishPendingConsoleClose();
            return;
        }
        e.Cancel=true;if(closingPrompt||closeAfterPause)return;
        closingPrompt=true;
        try {
            SaveCurrentTask();
            var dialog=new Window{Title="任务正在进行",Owner=this,Width=440,Height=235,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Surface};
            var promptText = mediaBusy && mediaInElevatedInspect
                ? "正在执行管理员镜像检查，需等待检查子进程（DISM）安全结束后才能退出。可选择等待完成后继续，或请求在子进程结束后自动退出。"
                : mediaBusy && !busy
                    ? "Windows 镜像正在后台下载或核验。选择「保存并暂停后退出」会保留断点续传进度并安全退出。"
                    : "当前任务正在进行。可以继续等待完成，也可以保存并暂停后退出。已完成的步骤和原磁盘会保留。";
            var body=new StackPanel{Margin=new Thickness(22)};body.Children.Add(Text(promptText,14));
            body.Children.Add(Row(Action("继续任务",(_,_)=>{dialog.DialogResult=false;}),Action("保存并暂停后退出",(_,_)=>{dialog.DialogResult=true;},true)));dialog.Content=body;
            if(dialog.ShowDialog()==true){closeAfterPause=true;PauseCurrentTask();if(!busy && !mediaBusy)_ = Dispatcher.BeginInvoke(new Action(Close));}
        }finally{closingPrompt=false;}
    }
}
