using System.Drawing;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Tray;

namespace StandbyAndTimer.Tests;

public sealed class TrayMenuPaletteTests
{
    [Theory]
    [InlineData(Theme.Dark,  0x16181B)]
    [InlineData(Theme.Black, 0x0E0F11)]   // Dark's page colour, not its card colour
    [InlineData(Theme.Light, 0xF5F6F8)]
    [InlineData(Theme.White, 0xF5F6F8)]
    public void Each_theme_gets_its_own_menu_surface(Theme theme, int rgb)
    {
        Color bg = TrayMenuPalette.For(theme).SurfaceBg;

        Assert.Equal(rgb, bg.ToArgb() & 0xFFFFFF);
    }

    [Fact]
    public void Unresolved_system_theme_falls_back_to_dark() =>
        Assert.Same(TrayMenuPalette.Dark, TrayMenuPalette.For(Theme.System));
}
