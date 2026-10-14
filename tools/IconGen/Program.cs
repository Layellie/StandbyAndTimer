using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// The icon: the app's lime diamond ring on a dark tile (rounded square or
// circle), in the Dark palette colours (AppBg / Surface2Bg / AccentPrimary).
// Each size is drawn on its own with sizes snapped to whole pixels.
internal static class Program
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    private static readonly Color TileTop    = Color.FromRgb(0x24, 0x28, 0x2E);
    private static readonly Color TileBottom = Color.FromRgb(0x0E, 0x0F, 0x11);
    private static readonly Color Edge       = Color.FromArgb(0x55, 0x3A, 0x40, 0x48);
    private static readonly Color LimeLight  = Color.FromRgb(0xD4, 0xFF, 0x6A);
    private static readonly Color Lime       = Color.FromRgb(0xB8, 0xF0, 0x3A);
    private static readonly Color LimeDark   = Color.FromRgb(0x8E, 0xC2, 0x22);

    [STAThread]
    private static int Main(string[] args)
    {
        string shape   = args.Length > 0 ? args[0] : "square";
        string output  = args.Length > 1 ? args[1] : "app_icon.ico";
        string preview = args.Length > 2 ? args[2] : Path.GetDirectoryName(Path.GetFullPath(output))!;
        if (shape is not ("square" or "circle")) { Console.Error.WriteLine("shape: square or circle"); return 2; }

        var pngs = Sizes.ToDictionary(s => s, s => Png(Render(s, shape == "circle")));
        WriteIco(output, pngs);
        Directory.CreateDirectory(preview);
        File.WriteAllBytes(Path.Combine(preview, $"icon-{shape}-sheet.png"), Png(Sheet(shape == "circle")));
        Console.WriteLine($"{output}: {string.Join(", ", Sizes)} px, {new FileInfo(output).Length} bytes");
        return 0;
    }

    private static BitmapSource Render(int px, bool circle)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen()) Draw(dc, px, circle);
        var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }

    private static void Draw(DrawingContext dc, double px, bool circle)
    {
        // Small sizes get a tile to the edges and a thicker ring.
        double margin = px <= 24 ? 0 : Math.Round(px * 0.03);
        var tile = new Rect(margin, margin, px - 2 * margin, px - 2 * margin);
        var fill = new LinearGradientBrush(TileTop, TileBottom, 90);
        var edge = px >= 32 ? new Pen(new SolidColorBrush(Edge), Math.Max(1, px / 64.0)) : null;
        if (circle)
            dc.DrawEllipse(fill, edge, new Point(px / 2, px / 2), tile.Width / 2, tile.Height / 2);
        else
        {
            double r = tile.Width * 0.24;
            dc.DrawRoundedRectangle(fill, edge, tile, r, r);
        }

        // Diamond ring: a rounded square turned 45°.
        double stroke = px <= 16 ? 2 : px <= 24 ? 2.5 : Math.Round(px * 0.105 * 2) / 2;
        double side   = Math.Round(px * (circle ? 0.40 : 0.43));
        double c      = px / 2;
        var square = new Rect(c - side / 2, c - side / 2, side, side);
        var ringBrush = new LinearGradientBrush(
            new GradientStopCollection { new(LimeLight, 0), new(Lime, 0.45), new(LimeDark, 1) },
            new Point(0, 0), new Point(1, 1));
        var pen = new Pen(px <= 24 ? new SolidColorBrush(Lime) : ringBrush, stroke) { LineJoin = PenLineJoin.Round };
        dc.PushTransform(new RotateTransform(45, c, c));
        double cr = side * 0.16;
        dc.DrawRoundedRectangle(null, pen, square, cr, cr);
        dc.Pop();

        // Centre dot: the "active core" (dropped at 16 px where it would blur).
        if (px >= 20)
            dc.DrawEllipse(new SolidColorBrush(Lime), null, new Point(c, c), px * 0.065, px * 0.065);
    }

    // Preview: every size at 1:1 plus 256 px, on a dark and a light taskbar.
    private static BitmapSource Sheet(bool circle)
    {
        const int w = 760, h = 330;
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, w, h / 2));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)), null, new Rect(0, h / 2, w, h / 2));
            foreach (var (y, _) in new[] { (12.0, 0), (h / 2 + 12.0, 1) })
            {
                double x = 12;
                dc.DrawImage(Render(128, circle), new Rect(x, y, 128, 128));
                x += 150;
                foreach (int s in new[] { 64, 48, 40, 32, 24, 20, 16 })
                {
                    dc.DrawImage(Render(s, circle), new Rect(x, y + 128 - s, s, s));
                    x += s + 22;
                }
                // 16 and 32 blown up 4x / 2x to judge the pixels.
                x += 10;
                dc.DrawImage(Render(32, circle), new Rect(x, y, 128, 128));
            }
        }
        RenderOptions.SetBitmapScalingMode(v, BitmapScalingMode.NearestNeighbor);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        return bmp;
    }

    private static byte[] Png(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // ICO with PNG-compressed entries (supported since Vista).
    private static void WriteIco(string path, Dictionary<int, byte[]> pngs)
    {
        using var fs = File.Create(path);
        using var w  = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)pngs.Count);
        int offset = 6 + 16 * pngs.Count;
        foreach (var (size, data) in pngs)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(data.Length); w.Write(offset);
            offset += data.Length;
        }
        foreach (var data in pngs.Values) w.Write(data);
    }
}
