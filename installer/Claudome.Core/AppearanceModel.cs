using System.Text.RegularExpressions;

namespace ClaudeSetup;

public sealed record Appearance(string Name, string Accent, string Background)
{
    public static Appearance Default => new("雾白青", "#087F8C", "#F4F6FA");
    public static List<Appearance> Presets => [Default, new("海盐蓝", "#4169E1", "#F2F5FC"), new("暖杏", "#B85F39", "#FAF6F0"), new("深海", "#51C9B1", "#121922")];
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeEnvironmentSetup", "appearance.json");
    public static void Validate(Appearance value)
    {
        if (!Regex.IsMatch(value.Accent, "^#[a-fA-F0-9]{6}$") || !Regex.IsMatch(value.Background, "^#[a-fA-F0-9]{6}$"))
            throw new SetupException("颜色请填写 # 加 6 位十六进制，例如 #087F8C。");
    }
    public static Appearance Load(string? path = null)
    {
        path ??= DefaultPath;
        try { if (!File.Exists(path)) return Default; var value = JsonFile.Read<Appearance>(path); Validate(value); return value; } catch { return Default; }
    }
    public void Save(string? path = null) { Validate(this); JsonFile.Write(path ?? DefaultPath, this); }
}
