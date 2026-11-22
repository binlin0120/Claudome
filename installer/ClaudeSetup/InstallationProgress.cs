using System.Text.Json;

namespace ClaudeSetup;

public sealed record InstallationProgress(string Message, double? Percent = null)
{
    public string Display => Percent is double p ? $"{Message} · {p:F0}%" : Message;
    public static InstallationProgress FromWorker(JsonElement item)
    {
        var code = item.GetProperty("step").GetString();
        var caption = code switch {
            "recovery" => "4 / 7  正在恢复上次安装，复用已核验的镜像",
            "disk" => "4 / 7  正在准备 Windows 虚拟硬盘",
            "apply-image" => "4 / 7  正在写入 Windows 系统映像",
            "boot" => "4 / 7  正在配置 Windows 引导并启动",
            "cleanup" => "4 / 7  已确认新系统，正在清理旧半成品磁盘",
            _ => throw new SetupException("安装组件返回了无法识别的进度状态。")
        };
        double? percent = null;
        if (item.TryGetProperty("percent", out var value) && value.ValueKind != JsonValueKind.Null) {
            if (!value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0 || number > 100) throw new SetupException("安装进度数值无效。");
            percent = number;
        }
        return new(caption, percent);
    }
}
public sealed record RecoveryCleanupResult(int Deleted, int Remaining);
