using System.Drawing;

namespace StandbyAndTimer.Services.Tray;

// Color tokens used by the WinForms tray menu. Kept in sync (by eye) with
// the WPF Palette.Dark.xaml — they're separate because WinForms can't
// resolve DynamicResource lookups, so we duplicate the literal values.
// If you change one palette, change the other.
internal static class TrayDarkPalette
{
    internal static readonly Color SurfaceBg   = Color.FromArgb(0x1C, 0x1F, 0x24); // SurfaceBg
    internal static readonly Color AppFg       = Color.FromArgb(0xF3, 0xF5, 0xF8); // AppFg
    internal static readonly Color HoverBg     = Color.FromArgb(0x26, 0x2A, 0x31); // HoverBg
    internal static readonly Color PressedBg   = Color.FromArgb(0x1F, 0x2E, 0x0A); // AccentSubtle
    internal static readonly Color BorderColor = Color.FromArgb(0x2A, 0x2E, 0x35); // BorderColor
    internal static readonly Color DisabledFg  = Color.FromArgb(0x8E, 0x95, 0x9F); // 5.5:1 on SurfaceBg
}
