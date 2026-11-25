using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed record ConsoleWindow(IntPtr Handle,int ProcessId,string Executable);
internal sealed record ConsoleProcess(int Id,string Image,string Command);
public static class ConsoleWindowLocator
{
    private delegate bool EnumWindow(IntPtr handle,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumWindow callback,IntPtr parameter);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr handle,out uint id);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr handle,StringBuilder text,int maximum);
    [DllImport("user32.dll")]private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")]private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]private static extern bool ShowWindowAsync(IntPtr handle,int command);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]private static extern IntPtr GetWindow(IntPtr handle,uint command);
    [DllImport("user32.dll")]private static extern bool GetClientRect(IntPtr handle,out NativeRect rect);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr OpenProcess(uint access,bool inherit,int id);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder value,ref uint size);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr process);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CommandLineToArgvW(string command,out int count);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    internal static bool IsConsoleRoot(IntPtr handle)=>IsWindow(handle)&&GetWindow(handle,4)==IntPtr.Zero&&GetWindowTextLength(handle)>0;
    private static readonly ConcurrentDictionary<string,ConsoleWindow> Embedded=new(StringComparer.OrdinalIgnoreCase);
    private static string[] Arguments(string command)
    {
        var memory=CommandLineToArgvW(command,out var count);if(memory==IntPtr.Zero)return [];
        try{return Enumerable.Range(0,count).Select(i=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory,i*IntPtr.Size))??"").ToArray();}
        finally{LocalFree(memory);}
    }
    public static bool MatchesCommand(string command,string vmId)
    {
        if(!Guid.TryParse(vmId,out var expected))return false;var args=Arguments(command);
        for(var i=0;i<args.Length;i++) {
            var arg=args[i];string? selected=null;
            if((arg.Equals("--startvm",StringComparison.OrdinalIgnoreCase)||arg.Equals("-startvm",StringComparison.OrdinalIgnoreCase)||arg.Equals("-G",StringComparison.OrdinalIgnoreCase))&&i+1<args.Length)selected=args[i+1];
            else if(arg.StartsWith("--startvm=",StringComparison.OrdinalIgnoreCase))selected=arg[10..];
            else if(arg.StartsWith("-G",StringComparison.OrdinalIgnoreCase)&&arg.Length>2)selected=arg[2..];
            if(Guid.TryParse(selected,out var actual)&&actual==expected)return true;
        }
        return false;
    }
    internal static bool MatchesPathArgument(string command,string path)
    {
        if(!Path.IsPathFullyQualified(path))return false;
        var argv=CommandLineToArgvW(command,out var count);
        if(argv==IntPtr.Zero)return false;
        try {
            for(var i=1;i<count;i++) {
                var argument=Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv,i*IntPtr.Size));
                if(argument is not null&&Path.IsPathFullyQualified(argument)&&Path.GetFullPath(argument).Equals(Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase))return true;
            }
            return false;
        }finally{LocalFree(argv);}
    }
    internal static bool MatchesIdentity(ConsoleProcess process,string vmId,string executable,string? exactVmName=null,string? vmConfigPath=null)
    {
        if(!Path.IsPathFullyQualified(process.Image)||!Path.GetFullPath(process.Image).Equals(Path.GetFullPath(executable),StringComparison.OrdinalIgnoreCase))return false;
        if(Path.GetFileName(executable).Equals("vmware.exe",StringComparison.OrdinalIgnoreCase))return vmConfigPath is not null&&MatchesPathArgument(process.Command,vmConfigPath);
        if(MatchesCommand(process.Command,vmId))return true;
        return exactVmName is not null&&Path.GetFileName(executable).Equals("vmconnect.exe",StringComparison.OrdinalIgnoreCase)&&Arguments(process.Command).Skip(1).Any(arg=>arg.Equals(exactVmName,StringComparison.OrdinalIgnoreCase));
    }
    private static string ProcessImage(int id)
    {
        var process=OpenProcess(0x1000,false,id);if(process==IntPtr.Zero)return "";
        try{uint size=32768;var image=new StringBuilder((int)size);return QueryFullProcessImageName(process,0,image,ref size)?image.ToString():"";}
        finally{CloseHandle(process);}
    }
    internal static IReadOnlyList<ConsoleProcess> Processes(string executable)
    {
        var file=Path.GetFileName(executable);
        if(file is not ("VirtualBoxVM.exe" or "vmconnect.exe" or "vmware.exe"))throw new SetupException("控制台程序类型未识别。");
        try {
            using var query=new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"),new ObjectQuery("SELECT ProcessId,CommandLine,ExecutablePath FROM Win32_Process WHERE Name='"+file+"'"),new System.Management.EnumerationOptions{ReturnImmediately=true,Rewindable=false,Timeout=TimeSpan.FromSeconds(5)});
            using var rows=query.Get();var result=new List<ConsoleProcess>();
            foreach(ManagementObject row in rows)using(row) {
                var id=Convert.ToInt32(row["ProcessId"]);
                var image=row["ExecutablePath"] as string;
                if(string.IsNullOrEmpty(image))image=ProcessImage(id);
                var command=row["CommandLine"] as string??"";
                if(string.IsNullOrEmpty(image)||string.IsNullOrEmpty(command))throw new SetupException("控制台进程身份暂时无法读取，未重复打开窗口。请确认程序与虚拟机组件在同一 Windows 用户下运行。");
                result.Add(new(id,image,command));
            }
            return result;
        }catch(SetupException){throw;}
        catch(ManagementException error){throw new SetupException($"控制台进程查询未完成（WMI {unchecked((uint)error.ErrorCode):X8}），未重复打开窗口。");}
        catch(UnauthorizedAccessException){throw new SetupException("控制台进程查询被拒绝，未重复打开窗口。请核对当前用户与运行权限。");}
        catch(COMException error){throw new SetupException($"控制台进程查询未完成（HRESULT 0x{unchecked((uint)error.HResult):X8}），未重复打开窗口。");}
    }
    internal static bool MatchesVmwareCaption(string caption,string expected)=>caption.Equals(expected,StringComparison.OrdinalIgnoreCase)||caption.StartsWith(expected+" - ",StringComparison.OrdinalIgnoreCase);
    internal static bool IsAlive(ConsoleWindow window,string? expectedName=null){if(!IsWindow(window.Handle))return false;GetWindowThreadProcessId(window.Handle,out var id);if(id!=window.ProcessId)return false;if(expectedName is not null&&Path.GetFileName(window.Executable).Equals("vmware.exe",StringComparison.OrdinalIgnoreCase)){var title=new StringBuilder(1024);GetWindowText(window.Handle,title,title.Capacity);return MatchesVmwareCaption(title.ToString(),expectedName);}return true;}
    internal static void Hide(ConsoleWindow? window, string vmName, string? vmId = null)
    {
        // Keep VirtualBoxVM --separate alive (disabled + hidden at Z-bottom) rather than killing it!
        // Force-killing VirtualBoxVM --separate leaves a dead IFramebuffer stub in VBoxHeadless slot 0 ("Framebuffer already attached to 0" black screen).
        if (window is null || !IsAlive(window, vmName)) return;
        NativeConsoleHost.EnableWindow(window.Handle, false);
        if (IsWindowVisible(window.Handle))
            ShowWindowAsync(window.Handle, 0);
    }

    internal static void RequestClose(ConsoleWindow window, string? expectedName)
    {
        if (!IsAlive(window, expectedName)) return;
        NativeConsoleHost.EnableWindow(window.Handle, true);
        if (!PostMessage(window.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero) && IsAlive(window, expectedName))
            throw new SetupException("虚拟机已关机，但系统拒绝关闭控制台窗口。");
    }

    public static void Track(string vmId,ConsoleWindow window)=>Embedded[vmId]=window;
    public static void Forget(string vmId)=>Embedded.TryRemove(vmId,out _);
    public static ConsoleWindow? Find(string vmId,string executable,string? exactVmName=null,string? vmConfigPath=null)=>Find(vmId,executable,out _,exactVmName,vmConfigPath);
    public static ConsoleWindow? Find(string vmId,string executable,out bool frontendExists,string? exactVmName=null,string? vmConfigPath=null)
    {
        frontendExists=false;if(!Guid.TryParse(vmId,out _))return null;
        var ids=Processes(executable).Where(p=>MatchesIdentity(p,vmId,executable,exactVmName,vmConfigPath)).Select(p=>p.Id).ToHashSet();
        frontendExists=ids.Count>0;
        if(Embedded.TryGetValue(vmId,out var cached)&&IsAlive(cached,exactVmName)&&cached.Executable.Equals(executable,StringComparison.OrdinalIgnoreCase)) {
            GetWindowThreadProcessId(cached.Handle,out var cachedPid);
            if(cachedPid==cached.ProcessId&&ids.Contains(cached.ProcessId))return cached;
            Embedded.TryRemove(vmId,out _);
        }
        ConsoleWindow? found=null;long best=-1;
        EnumWindows((handle,_)=> {
            GetWindowThreadProcessId(handle,out var pid);
            if(!ids.Contains((int)pid)||!IsConsoleRoot(handle))return true;
            var title=new StringBuilder(1024);GetWindowText(handle,title,title.Capacity);
            if(exactVmName is not null) {
                var caption=title.ToString();
                if(Path.GetFileName(executable).Equals("vmware.exe",StringComparison.OrdinalIgnoreCase)) {
                    if(!MatchesVmwareCaption(caption,exactVmName))return true;
                }else if(!caption.Contains(exactVmName,StringComparison.OrdinalIgnoreCase))return true;
            }
            GetClientRect(handle,out var rect);var area=Math.Max(1L,(long)(rect.Right-rect.Left)*(rect.Bottom-rect.Top));
            var score=(IsWindowVisible(handle)?1L<<48:0)+area;
            if(score>best){best=score;found=new(handle,(int)pid,executable);}
            return true;
        },IntPtr.Zero);
        return found;
    }
    public static bool ShowExisting(string vmId,string executable)
    {
        var window=Find(vmId,executable);if(window is null)return false;
        RestoreVisible(window);
        return true;
    }

    internal static void RestoreVisible(ConsoleWindow window, bool foreground = false)
    {
        if (!IsAlive(window)) return;
        NativeConsoleHost.EnableWindow(window.Handle, true);
        if (!IsWindowVisible(window.Handle) || IsIconic(window.Handle)) ShowWindowAsync(window.Handle, 9);
        if (foreground) SetForegroundWindow(window.Handle);
    }
}
