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
        ((TextBlock)license.Content).Text=image is null?"请先读取镜像版本，再核对其许可与使用授权":"我已核对 "+image.Name+" 用于虚拟机的许可与使用授权（勾选不会授予许可证或激活系统）";
        license.IsEnabled=!busy&&image is not null;
        if(image is not null) {imageIndex.Text=image.Index.ToString();mediaDetails.Text=image.Label+"\n这是镜像身份读取结果，不是微软签名、镜像正版或许可证有效性的认证。";}
    }
    private void ClearMedia()
    {
        selectedMedia=null;windowsImages.ItemsSource=null;license.IsChecked=false;
        mediaDetails.Text="尚未识别镜像。Windows 10、Server、ARM64、Linux 及无法识别的介质不会进入当前 Windows 11 x64 安装流程。";RefreshConsent();
    }
    private void ApplyMedia(WindowsMediaIdentity media,int preferredIndex)
    {
        media.Validate();selectedMedia=media;isoHash.Text=media.Sha256;
        windowsImages.ItemsSource=media.Images;windowsImages.SelectedValue=preferredIndex;
        if(windowsImages.SelectedIndex<0)windowsImages.SelectedIndex=0;
        license.IsChecked=false;RefreshConsent();
    }
    private delegate Task<WindowsMediaIdentity> MediaReader(string workspaceRoot, string isoPath,
        IProgress<string> messages, IProgress<DownloadProgress> downloads, CancellationToken token);

    private async Task InspectMedia(MediaReader? readMedia = null)
    {
        if (busy) { Notice("请等待当前操作完成，或先暂停。"); return; }

        var workspaceRoot = root.Text.Trim();
        var isoPath = iso.Text.Trim();
        var preferredIndex = int.TryParse(imageIndex.Text, out var index) ? index : 1;
        readMedia ??= WindowsMedia.Inspect;
        using var operation = new CancellationTokenSource();
        cancellation = operation;
        busy = true;
        try
        {
            ClearMedia();
            AllowEditing(false);
            banner.Text = "正在准备读取 Windows 镜像版本…";
            var media = await readMedia(workspaceRoot, isoPath,
                new Progress<string>(message => banner.Text = message),
                new Progress<DownloadProgress>(p => progressText.Text = p.Display), operation.Token);
            ApplyMedia(media, preferredIndex);
            banner.Text = "已读取镜像版本。请选择你的许可对应的版本，并重新确认授权。";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            banner.Text = "已取消读取镜像版本。";
        }
        catch (Exception ex)
        {
            var message = Redaction.SafeError(ex);
            banner.Text = message;
            if (!closeAfterPause) Notice(message);
        }
        finally
        {
            busy = false;
            cancellation = null;
            AllowEditing(true);
            CompleteCloseAfterPause();
        }
    }
}
