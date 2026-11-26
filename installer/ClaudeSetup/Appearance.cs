using System.Windows.Media;

namespace ClaudeSetup;

public static class AppearanceColorExtensions
{
    extension(Appearance)
    {
        public static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        public static Color WithAlpha(Color color, double opacity)
        {
            var alpha = (byte)Math.Clamp((int)Math.Round(Appearance.ClampOpacity(opacity) * 255d), 13, 255);
            return System.Windows.Media.Color.FromArgb(alpha, color.R, color.G, color.B);
        }
        public static Color Mix(Color a, Color b, double amount) => System.Windows.Media.Color.FromRgb((byte)(a.R * (1 - amount) + b.R * amount), (byte)(a.G * (1 - amount) + b.G * amount), (byte)(a.B * (1 - amount) + b.B * amount));
        public static double Luminance(Color color)
        {
            double Channel(byte x) { var v = x / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        }
        public static Color Legible(Color background) => Luminance(background) > .179 ? Colors.Black : Colors.White;
        public static double Contrast(Color a, Color b) { var x = Luminance(a); var y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05); }
    }
}
