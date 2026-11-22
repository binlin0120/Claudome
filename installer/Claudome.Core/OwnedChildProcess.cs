using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeSetup;

internal sealed class OwnedChildProcess : IDisposable
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref Limits value,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits{public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWorkingSet,MaxWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct Limits{public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    private readonly SafeFileHandle? job;
    public Process Process { get; }
    private OwnedChildProcess(Process process,SafeFileHandle? handle){Process=process;job=handle;}
    public static OwnedChildProcess Start(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unixProcess = System.Diagnostics.Process.Start(start) ?? throw new SetupException("安装网络组件未启动。");
            return new(unixProcess, null);
        }
        var job=CreateJobObject(IntPtr.Zero,null);
        if(job.IsInvalid)throw new SetupException("无法建立安装网络组件的退出保护。");
        Process? process=null;
        try {
            var limits=new Limits{Basic=new BasicLimits{Flags=0x2000}};
            if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<Limits>()))throw new Win32Exception(Marshal.GetLastWin32Error());
            process=System.Diagnostics.Process.Start(start)??throw new SetupException("安装网络组件未启动。");
            if(!AssignProcessToJobObject(job,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(process,job);
        }catch{try{if(process is not null&&!process.HasExited)process.Kill(true);}catch{}process?.Dispose();job.Dispose();throw;}
    }
    public void Dispose(){job?.Dispose();try{if(!Process.HasExited)Process.Kill(true);Process.WaitForExit(5000);}catch{}Process.Dispose();}
}
