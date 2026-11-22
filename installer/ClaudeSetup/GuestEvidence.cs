using System.IO;
using System.Text.Json;
namespace ClaudeSetup;
public static class GuestEvidence
{
    private static bool MatchesRun(JsonElement report, string runId) =>
        report.ValueKind == JsonValueKind.Object &&
        report.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Number &&
        schema.TryGetInt32(out var version) && version == 2 && Matches(report, "runId", runId);

    public static void RequireCurrent(JsonElement report, SessionState state, string runId)
    {
        if (!MatchesRun(report, runId) || !Matches(report, "vmId", state.VmId) ||
            !Matches(report, "workspaceId", state.Options.WorkspaceId))
            throw new SetupException("未收到本次操作的新报告，旧结果不能作为成功依据。请查看虚拟机提示后继续原任务。");
    }

    private static bool Matches(JsonElement report, string property, string expected) =>
        report.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    internal static async Task<JsonDocument> CollectConfiguration(SessionState state, string runId, SetupException? commandFailure,
        Func<string, CancellationToken, Task> copy, TimeSpan? retryDelay = null)
    {
        var acceptedPath = Paths.Under(state.Options.Private, "guest-result.json");
        var incoming = Paths.Under(state.Options.Private, $"guest-result-{runId}.incoming");
        Exception? transferFailure = null;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (File.Exists(incoming)) File.Delete(incoming);
                using var transfer = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try
                {
                    await copy(incoming, transfer.Token);
                    transferFailure = null;
                    break;
                }
                catch (SetupException ex) when (ex is not HandoffException) { transferFailure = ex; }
                catch (OperationCanceledException ex) when (transfer.IsCancellationRequested) { transferFailure = ex; }
                if (attempt < 2) await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(1));
            }
            if (transferFailure is not null || !File.Exists(incoming))
            {
                var detail = commandFailure?.Message ?? (transferFailure is SetupException ? transferFailure.Message : "请检查来宾工具与虚拟机当前状态。");
                throw new SetupException($"本轮来宾配置报告未传回（GUEST_REPORT_UNAVAILABLE）。{detail}");
            }
            if (new FileInfo(incoming).Length > 128 * 1024)
                throw new SetupException("来宾配置报告超过大小限制（GUEST_REPORT_INVALID）。");
            JsonDocument document;
            try { document = JsonDocument.Parse(File.ReadAllBytes(incoming)); }
            catch (JsonException) { throw new SetupException("本轮来宾配置报告内容不完整（GUEST_REPORT_INVALID）。"); }
            try
            {
                var report = document.RootElement;
                // Before reading the payload, the guest knows the run ID from the file name only.
                // This report carries a diagnostic and cannot replace task-bound evidence.
                if (MatchesRun(report, runId) && Matches(report, "code", "GUEST_INPUT_UNREADABLE") &&
                    Matches(report, "vmId", "") && Matches(report, "workspaceId", ""))
                    throw new SetupException("来宾未能读取本轮部署参数（GUEST_INPUT_UNREADABLE）。请继续原任务重传参数。");
                RequireCurrent(report, state, runId);
                File.Move(incoming, acceptedPath, true);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        finally
        {
            if (File.Exists(incoming)) File.Delete(incoming);
        }
    }
}
