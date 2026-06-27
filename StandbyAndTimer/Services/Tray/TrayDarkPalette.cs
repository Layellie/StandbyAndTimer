using System.Drawing;

namespace StandbyAndTimer.Services.Tray;

// Color tokens used by the WinForms tray menu. Kept in sync (by eye) with
// the WPF Palette.Dark.xaml — they're separate because WinForms can't
// resolve DynamicResource lookups, so we duplicate the literal values.
// If you change one palette, change the other.
internal static class TrayDarkPalette
{
    internal static readonly Color SurfaceBg     = Color.FromArgb(0x13, 0x13, 0x1A);
    internal static readonly Color AppFg         = Color.FromArgb(0xE2, 0xE2, 0xF0);
    internal static readonly Color AccentPrimary = Color.FromArgb(0x7C, 0x6A, 0xF7);
    internal static readonly Color SelectionBg   = Color.FromArgb(0x3D, 0x36, 0x70);
    internal static readonly Color AccentPressed = Color.FromArgb(0x64, 0x55, 0xE0);
    internal static readonly Color BorderColor   = Color.FromArgb(0x2A, 0x2A, 0x3D);
    internal static readonly Color DisabledFg    = Color.FromArgb(0x70, 0x70, 0xA0);
}
