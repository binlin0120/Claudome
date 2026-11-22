using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace ClaudeSetup;

public sealed partial class SetupWindow
{
    private const int ImmersiveDarkMode = 20;
    private const int CaptionColor = 35;
    private const int CaptionTextColor = 36;
    private const uint DefaultFrameColor = 0xFFFFFFFF;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, in uint value, int size);

    private static uint NativeColor(Color value) => (uint)(value.R | value.G << 8 | value.B << 16);

    private Color WorkspaceCaptionColor()
    {
        var background = Appearance.Color(appearance.Background);
        return Appearance.Mix(background, Appearance.Color(appearance.Accent), .12);
    }

    private bool UpdateNativeCaption()
    {
        var window = new WindowInteropHelper(this).Handle;
        if (window == IntPtr.Zero) return false;

        var caption = WorkspaceCaptionColor();
        var highContrast = SystemParameters.HighContrast;
        var dark = !highContrast && Appearance.Luminance(caption) < .179 ? 1u : 0u;
        var darkModeResult = DwmSetWindowAttribute(window, ImmersiveDarkMode, in dark, sizeof(uint));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return darkModeResult >= 0;

        var fill = highContrast ? DefaultFrameColor : NativeColor(caption);
        var text = highContrast ? DefaultFrameColor : NativeColor(Appearance.Legible(caption));
        var fillResult = DwmSetWindowAttribute(window, CaptionColor, in fill, sizeof(uint));
        var textResult = DwmSetWindowAttribute(window, CaptionTextColor, in text, sizeof(uint));
        return fillResult >= 0 && textResult >= 0;
    }

    private static Style WorkspaceScrollbarStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="ScrollBar">
            <Setter Property="Width" Value="12"/>
            <Setter Property="MinWidth" Value="12"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ScrollBar">
                        <Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="Vertical">
                            <Track.Resources>
                                <Style TargetType="RepeatButton">
                                    <Setter Property="Focusable" Value="False"/>
                                    <Setter Property="Template">
                                        <Setter.Value>
                                            <ControlTemplate TargetType="RepeatButton">
                                                <Border Background="Transparent"/>
                                            </ControlTemplate>
                                        </Setter.Value>
                                    </Setter>
                                </Style>
                            </Track.Resources>
                            <Track.DecreaseRepeatButton>
                                <RepeatButton Command="ScrollBar.PageUpCommand"/>
                            </Track.DecreaseRepeatButton>
                            <Track.Thumb>
                                <Thumb x:Name="ScrollHandle">
                                    <Thumb.Template>
                                        <ControlTemplate TargetType="Thumb">
                                            <Grid Background="Transparent">
                                                <Border x:Name="Grip" Width="4" CornerRadius="2"
                                                        Background="{DynamicResource ScrollThumb}" Opacity=".45"/>
                                            </Grid>
                                            <ControlTemplate.Triggers>
                                                <Trigger Property="IsMouseOver" Value="True">
                                                    <Setter TargetName="Grip" Property="Background" Value="{DynamicResource ScrollThumbActive}"/>
                                                    <Setter TargetName="Grip" Property="Opacity" Value="1"/>
                                                </Trigger>
                                                <Trigger Property="IsDragging" Value="True">
                                                    <Setter TargetName="Grip" Property="Background" Value="{DynamicResource ScrollThumbActive}"/>
                                                    <Setter TargetName="Grip" Property="Opacity" Value="1"/>
                                                </Trigger>
                                            </ControlTemplate.Triggers>
                                        </ControlTemplate>
                                    </Thumb.Template>
                                </Thumb>
                            </Track.Thumb>
                            <Track.IncreaseRepeatButton>
                                <RepeatButton Command="ScrollBar.PageDownCommand"/>
                            </Track.IncreaseRepeatButton>
                        </Track>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """);
}
