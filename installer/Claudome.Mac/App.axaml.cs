using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ClaudeSetup;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (Program.PreviewMode)
            {
                window.Opened += async (_, _) =>
                {
                    var ok = await window.RunPreviewChecksAsync(Program.PreviewOutputDirectory);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown(ok ? 0 : 1));
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
