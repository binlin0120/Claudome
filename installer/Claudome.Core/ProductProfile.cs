using System.IO;

namespace ClaudeSetup;

/// <summary>
/// Centralizes edition-specific product identity, data paths, and update channel behavior
/// between the standard Claudome build and the Hyper-V standalone build.
/// </summary>
public static class ProductProfile
{
#if CLAUDOME_HYPERV
    public static bool IsHyperVStandalone => true;
    public static string ChannelId => "hyperv";
    public static string ProductName => "Claudome Hyper-V";
    public static string WindowTitle => "Claudome";
    public static string BrandTagline => "Windows 11 独立安全工作区";
    public static string PackagePrefix => "Claudome-HyperV";
    public static string QuickStartFileName => "快速开始-HyperV.txt";
    public static string AppDataFolderName => "ClaudomeHyperVSetup";
    public static string DefaultWorkspaceFolderName => "Claudome";
    public static bool OnlineUpdatesEnabled => true;
    public static string DefaultManifestUrl => "https://github.com/binlin0120/Claudome/releases/download/hyperv-latest/hyperv-latest.json";
    public static string PreReleasePageUrl => "https://github.com/binlin0120/Claudome/releases";
#else
    public static bool IsHyperVStandalone => false;
    public static string ChannelId => "standard";
    public static string ProductName => "Claudome";
    public static string WindowTitle => "Claudome";
    public static string BrandTagline => "给 Claude 一个独立的空间";
    public static string PackagePrefix => "Claudome";
    public static string QuickStartFileName => "快速开始.txt";
    public static string AppDataFolderName => "ClaudeEnvironmentSetup";
    public static string DefaultWorkspaceFolderName => "ClaudeWorkspace";
    public static bool OnlineUpdatesEnabled => true;
    public static string DefaultManifestUrl => "https://github.com/binlin0120/Claudome/releases/latest/download/latest.json";
    public static string PreReleasePageUrl => "https://github.com/binlin0120/Claudome/releases";
#endif

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDataFolderName);

    public static string DefaultWorkspaceRoot =>
        Directory.Exists("D:\\")
            ? $"D:\\{DefaultWorkspaceFolderName}"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultWorkspaceFolderName);
}
