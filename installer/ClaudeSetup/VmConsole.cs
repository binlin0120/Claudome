using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeSetup;

public interface IVmConsole
{
    bool TryShow(string machineId);
    void Attach(string virtualBoxDirectory, string machineId);
}
public sealed class VmConsole : IVmConsole
{
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    public bool TryShow(string machineId)
    {
        var tool=Machine.VBoxPath();
        if(tool is not null && ConsoleWindowLocator.ShowExisting(machineId,System.IO.Path.Combine(System.IO.Path.GetDirectoryName(tool)!,"VirtualBoxVM.exe")))return true;
        object? api = null, machine = null;
        try
        {
            var type = Type.GetTypeFromProgID("VirtualBox.VirtualBox"); if (type is null) return false;
            api = Activator.CreateInstance(type); if (api is null) return false;
            machine = ((dynamic)api).FindMachine(machineId);
            if (!(bool)((dynamic)machine).CanShowConsoleWindow()) return false;
            long value = (long)((dynamic)machine).ShowConsoleWindow();
            if (value == 0) return true; // The front end performed activation itself.
            var window = new IntPtr(value); if (!IsWindow(window)) return false;
            ShowWindowAsync(window, 9); SetForegroundWindow(window); return true;
        }
        catch { return false; }
        finally
        {
            if (machine is not null && Marshal.IsComObject(machine)) Marshal.ReleaseComObject(machine);
            if (api is not null && Marshal.IsComObject(api)) Marshal.ReleaseComObject(api);
        }
    }
    public void Attach(string virtualBoxDirectory, string machineId)
    {
        var executable = System.IO.Path.Combine(virtualBoxDirectory, "VirtualBoxVM.exe");
        if (!System.IO.File.Exists(executable)) throw new SetupException("缺少虚拟机窗口组件，请检查 VirtualBox 安装。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Normal };
        start.ArgumentList.Add("--startvm"); start.ArgumentList.Add(machineId); start.ArgumentList.Add("--separate");
        _ = Process.Start(start) ?? throw new SetupException("没有启动虚拟机窗口。");
    }
}
