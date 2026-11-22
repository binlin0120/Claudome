using Avalonia;

namespace ClaudeSetup;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        HostPlatform.Environment = new MacHostEnvironment();
        Catalog.RegisterAssembly(typeof(Program).Assembly);

        if (args.Length > 0 && args[0] == "--self-test")
            return MacSelfTests.Run(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "Claudome-Mac-tests"));

        if (args.Length > 0 && args[0] == "--preview")
        {
            PreviewMode = true;
            PreviewOutputDirectory = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "Claudome-Mac-preview");
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static bool PreviewMode { get; private set; }
    internal static string PreviewOutputDirectory { get; private set; } = "";

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
