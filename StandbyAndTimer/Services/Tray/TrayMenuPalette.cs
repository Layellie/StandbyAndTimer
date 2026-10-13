using System.Drawing;

namespace StandbyAndTimer.Services.Tray;

// Color tokens used by the WinForms tray menu, one instance per theme. Kept
// in sync (by eye) with the WPF Palette.Dark.xaml / Palette.Light.xaml —
// they're separate because WinForms can't resolve DynamicResource lookups,
// so we duplicate the literal values. If you change one palette, change the
// matching one here.
internal sealed class TrayMenuPalette
{
    internal static readonly TrayMenuPalette Dark = new(
        surfaceBg:   Color.FromArgb(0x1C, 0x1F, 0x24), // SurfaceBg
        appFg:       Color.FromArgb(0xF3, 0xF5, 0xF8), // AppFg
        hoverBg:     Color.FromArgb(0x26, 0x2A, 0x31), // HoverBg
        pressedBg:   Color.FromArgb(0x1F, 0x2E, 0x0A), // AccentSubtle
        borderColor: Color.FromArgb(0x2A, 0x2E, 0x35), // BorderColor
        disabledFg:  Color.FromArgb(0x8E, 0x95, 0x9F)); // 5.5:1 on SurfaceBg

    internal static readonly TrayMenuPalette Light = new(
        surfaceBg:   Color.FromArgb(0xFF, 0xFF, 0xFF), // SurfaceBg
        appFg:       Color.FromArgb(0x16, 0x18, 0x1C), // AppFg
        hoverBg:     Color.FromArgb(0xEE, 0xF0, 0xF3), // HoverBg
        pressedBg:   Color.FromArgb(0xEC, 0xF7, 0xCF), // AccentSubtle
        borderColor: Color.FromArgb(0xDF, 0xE3, 0xE8), // BorderColor
        disabledFg:  Color.FromArgb(0x68, 0x70, 0x7A)); // 5.0:1 on SurfaceBg

    internal Color SurfaceBg   { get; }
    internal Color AppFg       { get; }
    internal Color HoverBg     { get; }
    internal Color PressedBg   { get; }
    internal Color BorderColor { get; }
    internal Color DisabledFg  { get; }

    private TrayMenuPalette(Color surfaceBg, Color appFg, Color hoverBg,
                            Color pressedBg, Color borderColor, Color disabledFg)
    {
        SurfaceBg   = surfaceBg;
        AppFg       = appFg;
        HoverBg     = hoverBg;
        PressedBg   = pressedBg;
        BorderColor = borderColor;
        DisabledFg  = disabledFg;
    }
}
