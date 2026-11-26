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
        return error switch
        {
            System.Net.Sockets.SocketException socket =>
                socket.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.AddressAlreadyInUse =>
                        $"本地通道端口被占用（SOCKET_{socket.NativeErrorCode} · {socket.SocketErrorCode}）。请在任务管理器关闭重复打开的 Claudome 或冲突代理程序后重试。",
                    System.Net.Sockets.SocketError.AccessDenied =>
                        $"系统拒绝监听或访问本机网络端口（SOCKET_{socket.NativeErrorCode} · {socket.SocketErrorCode}）。请检查 Windows 防火墙拦截规则，或在终端执行 netsh interface ipv4 show excludedportrange 排查保留端口冲突。",
                    _ =>
                        $"宿主机网络连接失败（SOCKET_{socket.NativeErrorCode} · {socket.SocketErrorCode}）。请确认本机网络畅通、代理客户端正在运行，且目标地址与端口未被本机防火墙阻断。"
                },
            System.ComponentModel.Win32Exception native =>
                $"系统组件调用未通过（Windows 0x{unchecked((uint)native.NativeErrorCode):X8}）。请在本机确认：1) 是否已以管理员身份运行；2) 对应系统功能或依赖工具未被安全软件拦截；3) 相关占用进程已退出。",
            FileNotFoundException =>
                "在本机未找到所需的安装包、镜像或运行组件（FILE_NOT_FOUND）。请在客户主机检查：1) 任务存放目录和原 ISO 文件是否被移动或误删；2) Windows Defender 或安全软件隔离区是否误拦了组件；3) 重新核对目录文件后继续原任务。",
            DirectoryNotFoundException =>
                "找不到指定的任务目录或磁盘分区。请在客户主机检查：1) 目标非系统盘（如 D: 盘）是否已接入并可读写；2) 在“安装 → 任务目录”重新选定可用 NTFS 分区后再试。",
            UnauthorizedAccessException =>
                "目标目录或系统配置权限不足。请在客户主机检查：1) 是否右键“以管理员身份运行”；2) 安全软件是否启用了文件夹写入保护或锁定目标目录；3) 占用该目录或虚拟磁盘的窗口/进程是否已关闭。",
            System.Text.Json.JsonException =>
                "读取本机任务配置或检查点时遇到损坏的 JSON 记录（JSON_INVALID）。请关闭占用程序，清理临时缓存文件后重新载入原任务或检查目录备份。",
            System.Net.Http.HttpRequestException http =>
                $"本机下载或联网请求未成功（{(http.StatusCode is { } status ? $"HTTP {(int)status}" : http.HttpRequestError.ToString())}）。请在客户主机排查：1) 宿主机当前代理或网络能否正常访问目标站点；2) 如在下载微软 Windows ISO，请返回官网重新生成新的直链；3) 系统时间是否准确。",
            TimeoutException =>
                "本机等待系统服务或网络响应超时。请检查宿主机 CPU/磁盘占用是否过高、Windows Hyper-V 服务（vmms）是否正在运行，以及代理链路是否可用后重试。",
            IOException io => unchecked((uint)io.HResult) switch
            {
                0x80070070 or 0x80070027 =>
                    "目标磁盘剩余空间不足（0x80070070）。请在客户主机清理目标分区并确保保留充足可用空间后，回到原任务继续。",
                0x80070020 or 0x80070021 =>
                    "目标文件或虚拟磁盘正被其它程序占用（0x80070020）。请关闭正在访问该目录的虚拟机窗口、资源管理器或安全软件实时扫描后重试。",
                _ =>
                    $"本机磁盘文件读写未完成（IO 0x{unchecked((uint)io.HResult):X8}）。请在客户主机确认目标盘为正常可读写的本地 NTFS 分区、剩余空间充足，并关闭冲突进程后继续。"
            },
            InvalidOperationException =>
                "当前操作前置条件未满足（INVALID_OPERATION）。请在客户主机确认虚拟机关机/待命状态与前置步骤已完成，原任务数据已完整保留。",
            ArgumentException =>
                "填写的参数或本机路径格式不符合要求。请检查目录路径（需完整本地 NTFS 路径）、端口号和账号格式后再试。",
            _ =>
                $"当前步骤执行中断（{error.GetType().Name}）。请在本机核对管理员权限、目标盘可用空间与本机网络/代理状态；原任务进度已保留，处理后可直接继续。"
        };
    }
}
