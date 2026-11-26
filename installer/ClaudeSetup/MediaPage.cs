using System.Windows;
using System.Windows.Controls;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    private readonly ComboBox windowsImages=Choice([]);
    private readonly TextBlock mediaDetails=Text("尚未识别镜像。先读取版本和架构，再核对所选版本的许可。",12);
    private WindowsMediaIdentity? selectedMedia;
    private void RefreshConsent()
    {
        var image=windowsImages.SelectedItem as WindowsImageChoice;
        var platformName=(platform.SelectedItem as PlatformChoice)?.Name??"所选虚拟机软件";
        var scope=$"我已查看组件来源与许可，同意准备 {platformName}，创建本任务的虚拟机并安装{(image is null?"待识别的系统（需先读取镜像版本）":image.Name+" x64")}及来宾环境；所需主机设置会单独说明并确认。";
        if(((TextBlock)consent.Content).Text!=scope)consent.IsChecked=false;
        ((TextBlock)consent.Content).Text=scope;
        var homeChinaBlocked = image?.IsHomeChinaBlocked == true;
        if (homeChinaBlocked) license.IsChecked = false;
        ((TextBlock)license.Content).Text = image is null
            ? "请先读取镜像版本，再核对其许可与使用授权"
            : homeChinaBlocked
                ? "所选索引为家庭中文版（CoreCountrySpecific），请改选专业版 / 企业版"
                : "我已核对 " + image.Name + " 用于虚拟机的许可与使用授权（勾选不会授予许可证或激活系统）";
        license.IsEnabled = !busy && !mediaBusy && image is not null && !homeChinaBlocked;
        if (image is not null)
        {
            imageIndex.Text = image.Index.ToString();
            mediaDetails.Text = homeChinaBlocked
                ? $"⚠ 所选索引 {image.Index} 为 Windows 11 家庭中文版（{image.Edition}），Hyper-V 独立版不支持。\n请在上方下拉框改选同镜像中的「Windows 11 专业版 / 企业版」，或重新下载官方多版本 ISO。"
                : image.Label + "\n这是镜像身份读取结果，不是微软签名、镜像正版或许可证有效性的认证。";
        }
    }
    private void ClearMedia()
    {
        selectedMedia=null;windowsImages.ItemsSource=null;license.IsChecked=false;
        mediaDetails.Text="尚未识别镜像。Windows 10、Server、ARM64、Linux 及无法识别的介质不会进入当前 Windows 11 x64 安装流程。";RefreshConsent();
    }
    internal static int ResolvePreferredImageIndex(WindowsMediaIdentity media, int? explicitIndex, bool preserveExplicitSelection)
    {
        if (preserveExplicitSelection && explicitIndex is int target &&
            media.Images.FirstOrDefault(x => x.Index == target && !x.IsHomeChinaBlocked) is { } preserved)
            return preserved.Index;
        if (media.Images.FirstOrDefault(x => !x.IsHomeChinaBlocked && x.Edition.Equals("Professional", StringComparison.OrdinalIgnoreCase)) is { } pro)
            return pro.Index;
        return media.Images.FirstOrDefault(x => !x.IsHomeChinaBlocked)?.Index ?? media.Images[0].Index;
    }

    private void ApplyMedia(WindowsMediaIdentity media, int preferredIndex)
    {
        media.Validate(); selectedMedia = media; isoHash.Text = media.Sha256;
        windowsImages.ItemsSource = media.Images; windowsImages.SelectedValue = preferredIndex;
        if (windowsImages.SelectedIndex < 0) windowsImages.SelectedIndex = 0;
        license.IsChecked = false; RefreshConsent();
    }
    private delegate Task<WindowsMediaIdentity> MediaReader(string workspaceRoot, string isoPath,
        IProgress<string> messages, IProgress<DownloadProgress> downloads, CancellationToken token);

    internal delegate Task<WindowsMediaIdentity> HookedMediaReader(string workspaceRoot, string isoPath,
        IProgress<string> messages, IProgress<DownloadProgress> downloads, CancellationToken token,
        Action onElevationStart, Action onWaitingWhileElevated);

    private Task InspectMedia(MediaReader? readMedia = null) =>
        InspectMediaCore(readMedia is null
            ? (rootDir, isoFile, msgs, dl, tok, onStart, onWait) =>
                WindowsMedia.Inspect(rootDir, isoFile, msgs, dl, tok, onStart, onWait)
            : (rootDir, isoFile, msgs, dl, tok, _, _) =>
                readMedia(rootDir, isoFile, msgs, dl, tok));

    internal async Task InspectMediaWithHooks(HookedMediaReader hookedReader) =>
        await InspectMediaCore(hookedReader);

    private async Task InspectMediaCore(HookedMediaReader reader)
    {
        if (busy || mediaBusy) { Notice("镜像正在后台下载或识别中，请先等待完成，或暂停后再读取版本；此时您可先去配置网络节点和账号。"); return; }

        var expectedTask = state;
        var expectedWorkspaceId = state.Options.WorkspaceId;
        var workspaceRoot = root.Text.Trim();
        var expectedRoot = System.IO.Path.GetFullPath(workspaceRoot);
        var isoPath = iso.Text.Trim();
        var preferredIndex = int.TryParse(imageIndex.Text, out var index) ? index : 1;
        var previousMediaSha = selectedMedia?.Sha256 ?? "";
        using var operation = new CancellationTokenSource();
        mediaCancellation = operation;
        mediaBusy = true;
        mediaInElevatedInspect = false;
        try
        {
            ClearMedia();
            UpdateMediaLockState("正在后台读取 Windows 镜像版本… 可先去「网络节点 / 本机资料」继续配置");
            banner.Text = "⬇ 后台正在读取 Windows 镜像版本… 您可先配置「网络节点」与「本机资料」";
            var media = await reader(workspaceRoot, isoPath,
                new Progress<string>(message =>
                {
                    if (mediaStatusText is not null) mediaStatusText.Text = message + "（支持先配置网络节点与账号）";
                    banner.Text = "⬇ 后台读取镜像：" + message;
                }),
                new Progress<DownloadProgress>(p =>
                {
                    progressText.Text = p.Display;
                    if (mediaProgressBar is not null)
                    {
                        mediaProgressBar.Visibility = Visibility.Visible;
                        mediaProgressBar.IsIndeterminate = p.Total is not > 0;
                        mediaProgressBar.Value = p.Total > 0 ? p.Received * 100d / p.Total.Value : 0;
                    }
                    if (mediaStatusText is not null) mediaStatusText.Text = p.Display;
                }),
                operation.Token,
                () => _ = Dispatcher.BeginInvoke(() => { if (mediaBusy) mediaInElevatedInspect = true; }),
                () => _ = Dispatcher.BeginInvoke(() =>
                {
                    if (!mediaBusy) return;
                    mediaStatusText.Text = "正在等待管理员授权 / 镜像检查子进程（DISM）安全结束后暂停…";
                    banner.Text = "正在等待镜像检查子进程安全结束后暂停，请完成或关闭管理员窗口…";
                }));
            if (operation.IsCancellationRequested || !IsSameMediaWorkspace(expectedTask, expectedWorkspaceId, expectedRoot))
            {
                banner.Text = "镜像检查结果未应用（操作已取消或当前任务目录已切换）。";
                return;
            }
            var sameSavedMedia = (!string.IsNullOrWhiteSpace(previousMediaSha) && previousMediaSha.Equals(media.Sha256, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(state.Options.IsoSha256) && state.Options.IsoSha256.Equals(media.Sha256, StringComparison.OrdinalIgnoreCase));
            var resolvedIndex = ResolvePreferredImageIndex(media, preferredIndex, sameSavedMedia);
            ApplyMedia(media, resolvedIndex);
            var picked = windowsImages.SelectedItem as WindowsImageChoice;
            var hasPro = media.Images.Any(x => !x.IsHomeChinaBlocked && x.Edition.Equals("Professional", StringComparison.OrdinalIgnoreCase));
            banner.Text = !sameSavedMedia && !hasPro && picked is not null
                ? $"已读取镜像版本（未检测到专业版，已暂选「{picked.Name}」）。请核对选择你的许可对应版本，并重新确认授权。"
                : "已读取镜像版本。请核对所选版本是否符合你的使用许可，并重新确认授权。";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            banner.Text = "已暂停或取消读取镜像版本。";
        }
        catch (Exception ex)
        {
            var message = Redaction.SafeError(ex);
            banner.Text = message;
            if (!closeAfterPause) Notice(message);
        }
        finally
        {
            mediaInElevatedInspect = false;
            mediaBusy = false;
            mediaCancellation = null;
            UpdateMediaLockState();
            CompleteCloseAfterPause();
        }
    }
}
