using System.Runtime.InteropServices;
using System.IO;

namespace ClaudeSetup;

// Function declarations follow VMware's published open-vm-tools vix.h.
// Windows x64 uses one calling convention, including the integer varargs accessors.
public sealed class Vix : IDisposable
{
    private readonly IntPtr library;
    private int host, vm;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ConnectFn(int version, int provider, IntPtr name, int port, IntPtr user, IntPtr password, int options, int properties, IntPtr callback, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OpenFn(int host, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int options, int properties, IntPtr callback, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong PropsFn(int handle, int property, out long result, int end);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong CompleteFn(int job, out int complete);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong ErrorFn(int job);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleaseFn(int handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoginFn(int vm, [MarshalAs(UnmanagedType.LPUTF8Str)] string user, [MarshalAs(UnmanagedType.LPUTF8Str)] string password, int options, IntPtr callback, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProgramFn(int vm, [MarshalAs(UnmanagedType.LPUTF8Str)] string program, [MarshalAs(UnmanagedType.LPUTF8Str)] string args, int options, int properties, IntPtr callback, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CopyFn(int vm, [MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string target, int options, int properties, IntPtr callback, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FileExistsFn(int vm, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr callback, IntPtr data);
    private T Api<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    public Vix(string directory)
    {
        var path = new[] { Path.Combine(directory, "vix.dll"), Path.Combine(directory, "x64", "vix.dll") }.FirstOrDefault(p => File.Exists(p) && IsX64(p));
        if (path is null) throw new SetupException("此 Workstation 安装缺少 x64 VIX 来宾控制组件。请使用官方完整安装并修复组件，随后继续；程序不会把密码放入 vmrun 命令行。");
        library = NativeLibrary.Load(path, typeof(Vix).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
    }
    private static bool IsX64(string path)
    {
        using var file = File.OpenRead(path); using var reader = new BinaryReader(file);
        if (file.Length < 64 || reader.ReadUInt16() != 0x5a4d) return false;
        file.Position = 60; var offset = reader.ReadInt32(); if (offset < 0 || offset + 6 > file.Length) return false;
        file.Position = offset; return reader.ReadUInt32() == 0x4550 && reader.ReadUInt16() == 0x8664;
    }
    private async Task<long> Wait(int job, CancellationToken token, int seconds = 300, int property = 0,string operation="wait")
    {
        try {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (true) {
                token.ThrowIfCancellationRequested();
                if (Api<CompleteFn>("VixJob_CheckCompletion")(job, out var complete) != 0) throw new SetupException("VMware 操作状态读取失败，请检查软件和来宾工具。");
                if (complete != 0) break;
                if (DateTime.UtcNow > until) throw new SetupException("VMware 操作等待超时，原任务已保留；请查看虚拟机提示后继续。");
                await Task.Delay(250, token);
            }
            var code=Api<ErrorFn>("VixJob_GetError")(job);
            if (code != 0) throw new SetupException(CommandFailure.Vix(operation,code));
            if (property == 0) return 0;
            if (Api<PropsFn>("Vix_GetProperties")(job, property, out var value, 0) != 0) throw new SetupException("VMware 未返回预期结果。");
            return value;
        } finally { Api<ReleaseFn>("Vix_ReleaseHandle")(job); }
    }
    public async Task Open(string path, CancellationToken token)
    {
        if (host == 0) host = (int)await Wait(Api<ConnectFn>("VixHost_Connect")(-1, 3, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, 0, 0, IntPtr.Zero, IntPtr.Zero), token, property: 3010,operation:"connect");
        if (vm == 0) vm = (int)await Wait(Api<OpenFn>("VixHost_OpenVM")(host, path, 0, 0, IntPtr.Zero, IntPtr.Zero), token, property: 3010,operation:"open");
    }
    public bool Running()
    {
        if (Api<PropsFn>("Vix_GetProperties")(vm, 129, out var power, 0) != 0) throw new SetupException("无法核对 VMware 电源状态。");
        return (power & 8) != 0;
    }
    public Task Login(string user, string password, CancellationToken token) => Wait(Api<LoginFn>("VixVM_LoginInGuest")(vm, user, password, 8, IntPtr.Zero, IntPtr.Zero), token, 30,operation:"login");
    public async Task Run(string program, IEnumerable<string> arguments, CancellationToken token, int seconds)
    {
        var job = Api<ProgramFn>("VixVM_RunProgramInGuest")(vm, program, WindowsArguments.Join(arguments), 0, 0, IntPtr.Zero, IntPtr.Zero);
        var exit=await Wait(job,token,seconds,3018,"run");
        if(exit!=0)throw new SetupException(CommandFailure.GuestExit(exit));
    }
    public async Task<bool> FileExists(string path, CancellationToken token)
    {
        var job = Api<FileExistsFn>("VixVM_FileExistsInGuest")(vm, path, IntPtr.Zero, IntPtr.Zero);
        return ((await Wait(job, token, property: 3011, operation: "stat")) & 0xff) != 0;
    }
    public Task Copy(string source, string target, bool toGuest, CancellationToken token) => Wait(Api<CopyFn>(toGuest ? "VixVM_CopyFileFromHostToGuest" : "VixVM_CopyFileFromGuestToHost")(vm, source, target, 0, 0, IntPtr.Zero, IntPtr.Zero), token,operation:"copy");
    public void Dispose()
    {
        if (vm != 0) Api<ReleaseFn>("Vix_ReleaseHandle")(vm);
        if (host != 0) Api<ReleaseFn>("VixHost_Disconnect")(host);
        // Keep the module loaded until process exit: vendor jobs may retain native callbacks.
    }
}
public static class WindowsArguments
{
    public static string Join(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));
    public static string Quote(string value)
    {
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new SetupException("参数含有不支持的控制字符。");
        var result = new System.Text.StringBuilder("\""); var slashes = 0;
        foreach (var c in value) {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); }
            else { result.Append('\\', slashes); result.Append(c); }
            slashes = 0;
        }
        result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
    }
}
