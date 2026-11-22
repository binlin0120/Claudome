using System.Text;
using System.Text.Json;

namespace ClaudeSetup;

public sealed partial class Deployment
{
    private const string QuickNetworkPrefix = "CLAUDOME_NET_CHECK:";

    // VirtualBox guestcontrol supplies stdout. Reuse the installed PowerShell
    // without copying scripts, payloads or packages to the guest.
    internal static async Task<JsonDocument> ReadQuickNetwork(IVmBackend backend, SessionState state, Secrets secrets, CancellationToken token)
    {
        var runId = Guid.NewGuid().ToString("N");
        var input = JsonSerializer.SerializeToUtf8Bytes(new
        {
            runId,
            vmId = state.VmId,
            workspaceId = state.Options.WorkspaceId,
            configHash = state.CoreConfigSha256,
            country = state.Options.Country,
            expectedExitIp = secrets.ExpectedExitIp
        });
        var input64 = Convert.ToBase64String(input);
        var script = "$input64='" + input64 + "'\n" + Catalog.EmbeddedText("QuickNetworkCheck.ps1");
        var command = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        try
        {
            var output = await backend.GuestRun(state, secrets, state.Options.GuestSetupDirectory + @"\pwsh\pwsh.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", command], token, 55);
            return ParseQuickNetworkOutput(output, state, runId);
        }
        catch (HandoffException) { throw; }
        catch (Exception error) when (error is SetupException or JsonException)
        {
            throw new GuestCheckPendingException("未取得本轮的来宾网络结果。请确认已登录原虚拟机，来宾工具与 Clash 服务正常。");
        }
    }

    internal static JsonDocument ParseQuickNetworkOutput(string output, SessionState state, string runId)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(QuickNetworkPrefix, StringComparison.Ordinal)).ToArray();
        if (lines.Length != 1 || lines[0].Length > 4096)
            throw new SetupException("来宾未返回可用的网络检查结果。");
        var document = JsonDocument.Parse(lines[0][QuickNetworkPrefix.Length..]);
        try { GuestEvidence.RequireCurrent(document.RootElement, state, runId); return document; }
        catch { document.Dispose(); throw; }
    }
}
