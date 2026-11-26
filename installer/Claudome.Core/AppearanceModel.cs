using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed record Appearance(string Name, string Accent, string Background)
{
    public const double DefaultOpacity = 0.56;
    public const double MinOpacity = 0.05;
    public const double MaxOpacity = 1.00;
    public double Opacity { get; init; } = DefaultOpacity;

    public static Appearance Default => new("星穹穹金", "#CC5E26", "#FAF3E8");
    public static List<Appearance> Presets =>
    [
        Default,
        new("雾白青", "#087F8C", "#F4F6FA"),
        new("海盐蓝", "#4169E1", "#F2F5FC"),
        new("黑曜夜穹", "#EE8E4D", "#15100D")
    ];
    public static string DefaultPath => Path.Combine(ProductProfile.AppDataDirectory, "appearance.json");
    public static double ClampOpacity(double value) => double.IsNaN(value) || double.IsInfinity(value) ? DefaultOpacity : Math.Clamp(value, MinOpacity, MaxOpacity);
    public static void Validate(Appearance value)
    {
        if (!Regex.IsMatch(value.Accent, "^#[a-fA-F0-9]{6}$") || !Regex.IsMatch(value.Background, "^#[a-fA-F0-9]{6}$"))
            throw new SetupException("颜色请填写 # 加 6 位十六进制，例如 #C9643B。");
        if (double.IsNaN(value.Opacity) || double.IsInfinity(value.Opacity) || value.Opacity < MinOpacity || value.Opacity > MaxOpacity)
            throw new SetupException("界面透明度请控制在 5% 到 100% 之间。");
    }
    public static Appearance Load(string? path = null)
    {
        path ??= DefaultPath;
        try { if (!File.Exists(path)) return Default; var value = JsonFile.Read<Appearance>(path); Validate(value); return value; } catch { return Default; }
    }
    public void Save(string? path = null) { Validate(this); JsonFile.Write(path ?? DefaultPath, this); }
}
