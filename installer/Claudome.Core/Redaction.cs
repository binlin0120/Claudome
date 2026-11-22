using System.Security.Cryptography;
using System.Text;

namespace ClaudeSetup;

public sealed class PrivateFile : IDisposable
{
    public string FileName { get; }
    public PrivateFile(string directory, string value, string extension = ".txt")
    {
        HostPlatform.Environment.RestrictPrivateDirectory(directory);
        FileName = Paths.Under(directory, Guid.NewGuid().ToString("N") + extension);
        using var stream = new FileStream(FileName, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        CryptographicOperations.ZeroMemory(bytes);
    }
    public void Dispose() { if (File.Exists(FileName)) File.Delete(FileName); }
}

public static class Redaction
{
    internal static SetupException SocketStartupError(string step, System.Net.Sockets.SocketException error) =>
        SocketStartupError(step, error.SocketErrorCode, error.NativeErrorCode);

    internal static SetupException SocketStartupError(string step, System.Net.Sockets.SocketError error, int nativeCode)
    {
        var detail = error switch
        {
            System.Net.Sockets.SocketError.AddressAlreadyInUse => "地址已被占用。请确认旧版 Claudome 已退出，再继续原任务。",
            System.Net.Sockets.SocketError.AccessDenied => "系统拒绝访问套接字。请在客户主机核对通道权限、端口保留或占用情况。",
            System.Net.Sockets.SocketError.AddressFamilyNotSupported or
                System.Net.Sockets.SocketError.ProtocolNotSupported => "宿主机当前未提供所需的套接字协议。请核对所选虚拟化平台状态。",
            _ => "请提供此错误码定位原任务的外层通道。"
        };
        return new SetupException($"{step}失败（SOCKET_{nativeCode}）。{detail} 当前未建立受控连接。");
    }

    public static string SafeError(Exception error)
    {
        if (error is SetupException) return error.Message;
        if (error is OperationCanceledException) return "操作已暂停，已完成的下载与阶段会保留。";
        var code = error switch
        {
            System.Net.Sockets.SocketException socket => "SOCKET_" + socket.NativeErrorCode,
            System.ComponentModel.Win32Exception native => "NATIVE_" + native.NativeErrorCode,
            FileNotFoundException => "FILE_NOT_FOUND",
            DirectoryNotFoundException => "DIRECTORY_NOT_FOUND",
            UnauthorizedAccessException => "ACCESS_DENIED",
            System.Text.Json.JsonException => "JSON_INVALID",
            System.Net.Http.HttpRequestException => "HTTP_REQUEST_FAILED",
            InvalidOperationException => "INVALID_OPERATION",
            ArgumentException => "INVALID_ARGUMENT",
            IOException => "IO_FAILED",
            _ => "UNCLASSIFIED"
        };
        return "此步骤未完成（诊断码 " + code + "）。原任务已保留，请根据当前步骤定位；未输出原始参数或敏感资料。";
    }
}
