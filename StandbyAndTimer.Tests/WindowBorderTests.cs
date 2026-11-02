using System.Windows.Media;

namespace StandbyAndTimer.Tests;

// Windows 11 draws a 1 px border of its own around the window (the outermost
// ring), dark grey by default: a dark outline around a light theme. The app
// gives it the theme's own border colour (DWMWA_BORDER_COLOR takes a COLORREF).
public class WindowBorderTests
{
    [Theory]
    [InlineData(0xD5, 0xDA, 0xE0, 0x00E0DAD5)]   // Light theme BorderColor #D5DAE0
    [InlineData(0x24, 0x27, 0x2C, 0x002C2724)]   // Dark theme BorderColor #24272C
    [InlineData(0x00, 0x00, 0x00, 0x00000000)]
    [InlineData(0xFF, 0xFF, 0xFF, 0x00FFFFFF)]
    public void A_colour_becomes_a_colorref_blue_green_red(byte r, byte g, byte b, int expected)
    {
        Assert.Equal(expected, MainWindow.ColorRef(Color.FromRgb(r, g, b)));
    }
}
