using System.Drawing;
using System.IO;
using StandbyAndTimer.Services.Tray;

namespace StandbyAndTimer.Tests;

public class AppIconTests
{
    private static string IcoPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "StandbyAndTimer", "app_icon.ico");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("app_icon.ico");
    }

    // The old icon held a single 32 px image that Windows blew up (blurry).
    [Fact]
    public void The_icon_has_every_size_from_16_to_256()
    {
        byte[] d = File.ReadAllBytes(IcoPath());
        int n = BitConverter.ToUInt16(d, 4);
        var sizes = Enumerable.Range(0, n).Select(i => d[6 + 16 * i] == 0 ? 256 : d[6 + 16 * i]).ToList();

        Assert.Equal([16, 20, 24, 32, 40, 48, 64, 256], sizes);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void The_tray_size_is_taken_as_is(int px)
    {
        using var icon = new Icon(IcoPath(), px, px);
        Assert.Equal(px, icon.Width);
    }

    [Fact]
    public void The_idle_icon_is_the_same_icon_in_grey()
    {
        using var lime = new Icon(IcoPath(), 32, 32);
        using var idle = AppIconLoader.BuildIdleIcon(lime);
        using var a = lime.ToBitmap();
        using var b = idle.ToBitmap();

        int coloured = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                Color c = b.GetPixel(x, y), o = a.GetPixel(x, y);
                Assert.InRange(c.A - o.A, -1, 1);             // same shape, anti-aliased edge kept
                if (Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 6) coloured++;
            }
        Assert.Equal(0, coloured);
    }
}
