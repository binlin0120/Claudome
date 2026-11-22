using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeSetup;

public static class PlatformConsole
{
    private delegate bool EnumWindow(IntPtr window,IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback,IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int maximum);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint id);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint access,bool inherit,uint id);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private static bool Visible(string executable,string name)
    {
        var found=false;
        EnumWindows((window,_)=>{
            if(!IsWindowVisible(window))return true;var title=new StringBuilder(1024);GetWindowText(window,title,title.Capacity);
            if(!title.ToString().Contains(name,StringComparison.OrdinalIgnoreCase))return true;
            GetWindowThreadProcessId(window,out var id);var process=OpenProcess(0x1000,false,id);if(process==IntPtr.Zero)return true;
            try{uint size=32768;var image=new StringBuilder((int)size);if(QueryFullProcessImageName(process,0,image,ref size)&&Path.GetFullPath(image.ToString()).Equals(Path.GetFullPath(executable),StringComparison.OrdinalIgnoreCase)){found=true;return false;}}
            finally{CloseHandle(process);}return true;
        },IntPtr.Zero);return found;
    }
    public static async Task Confirm(string executable,string name,Func<Task<bool>> running,CancellationToken token,Func<string,string,bool>? fixtureVisibility=null)
    {
        var visible=fixtureVisibility??Visible;
        for(var i=0;i<40;i++){
            token.ThrowIfCancellationRequested();if(visible(executable,name)&&await running())return;await Task.Delay(500,token);
        }
        throw new HandoffException(new("虚拟机窗口尚未确认打开",["请查看任务栏中的虚拟机窗口，处理仍在等待的许可、首次设置或加密密码提示。","程序会保留原虚拟机，不强制重启；完成提示后可重新点击打开。"],"console-window"));
    }
}
