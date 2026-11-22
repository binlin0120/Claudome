using Microsoft.Win32;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    private async Task ExportDiagnostics()
    {
        if (busy) return;
        var dialog = new SaveFileDialog { Title = "导出 Claudome 诊断信息", Filter = "诊断信息|*.json", FileName = "Claudome-诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json" };
        if (dialog.ShowDialog(this) != true) return;
        busy = true;
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var report = await Diagnostics.Collect(timeout.Token); JsonFile.Write(dialog.FileName, report);
            banner.Text = "诊断信息已导出：仅含版本、系统条件及虚拟机数量，不含账号、订阅、虚拟机名称或文件路径。";
        } catch (Exception ex) { Notice(Redaction.SafeError(ex)); }
        finally { busy = false; CompleteCloseAfterPause(); }
    }
}
