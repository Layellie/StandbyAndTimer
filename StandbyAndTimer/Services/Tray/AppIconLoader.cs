using System.IO;
using System.Windows;

namespace StandbyAndTimer.Services.Tray;

// Centralizes app-icon construction: loads the bundled .ico, falls back to
// a programmatic icon if missing, and builds the grey "idle" variant the
// tray shows while the timer is off (lime = on). Kept static
// because it has no state and is called exactly once at startup — making
// it a DI service would only add ceremony.
internal static class AppIconLoader
{
    // Loads the bundled app_icon.ico at the tray size (16 px at 100 %, more
    // at higher scaling). The .ico holds 16-256 px images; asking for the
    // size picks the matching one instead of the default 32 px shrunk by the
    // shell. Returns a fallback bitmap-built icon if the resource is missing.
    internal static System.Drawing.Icon LoadAppIcon() =>
        LoadAppIcon(System.Windows.Forms.SystemInformation.SmallIconSize);

    internal static System.Drawing.Icon LoadAppIcon(System.Drawing.Size size)
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/app_icon.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream, size);
            }
        }
        catch (Exception ex) { Logger.Warn($"LoadAppIcon: {ex.Message}"); }

        return CreateFallbackIcon();
    }

    // The tray icon while the timer is off: the app icon in grey. Lime (the
    // normal icon) then means "timer on" at a glance, even at 16 px where a
    // corner dot would cover the ring.
    internal static System.Drawing.Icon BuildIdleIcon(System.Drawing.Icon appIcon)
    {
        try
        {
            using var src = appIcon.ToBitmap();
            using var bmp = new System.Drawing.Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            // Luma, scaled down a little so the grey ring stays calm.
            const float k = 0.8f;
            var gray = new System.Drawing.Imaging.ColorMatrix(
            [
                [0.299f * k, 0.299f * k, 0.299f * k, 0, 0],
                [0.587f * k, 0.587f * k, 0.587f * k, 0, 0],
                [0.114f * k, 0.114f * k, 0.114f * k, 0, 0],
                [0,          0,          0,          1, 0],
                [0,          0,          0,          0, 1],
            ]);
            using (var attrs = new System.Drawing.Imaging.ImageAttributes())
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                attrs.SetColorMatrix(gray);
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, src.Width, src.Height),
                            0, 0, src.Width, src.Height, System.Drawing.GraphicsUnit.Pixel, attrs);
            }
            return BitmapToIcon(bmp);
        }
        catch (Exception ex)
        {
            Logger.Warn($"BuildIdleIcon: {ex.Message}");
            return (System.Drawing.Icon)appIcon.Clone();
        }
    }

    internal static void DisposeIcon(ref System.Drawing.Icon? icon)
    {
        if (icon is null) return;
        try { icon.Dispose(); } catch { }
        icon = null;
    }

    // Fallback if app_icon.ico isn't bundled — 32x32 purple square with a
    // white "S". Renders to a stream-backed Icon so disposal frees the HICON
    // cleanly (no leak).
    private static System.Drawing.Icon CreateFallbackIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(System.Drawing.Color.Transparent);

            using var bg = new System.Drawing.SolidBrush(
                System.Drawing.Color.FromArgb(255, 124, 106, 247));
            g.FillRectangle(bg, 0, 0, 32, 32);

            using var font = new System.Drawing.Font(
                "Segoe UI", 19, System.Drawing.FontStyle.Bold,
                System.Drawing.GraphicsUnit.Pixel);
            var sf = new System.Drawing.StringFormat
            {
                Alignment     = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center
            };
            g.DrawString("S", font, System.Drawing.Brushes.White,
                new System.Drawing.RectangleF(0, 1, 32, 32), sf);
        }

        return BitmapToIcon(bmp);
    }

    // Bitmap -> Icon through an in-memory .ico holding the bitmap as PNG.
    // Keeps the full alpha channel; the GetHicon + Icon.Save path dropped
    // most of the semi-transparent edge (anti-aliasing) and needed a
    // DestroyIcon.
    private static System.Drawing.Icon BitmapToIcon(System.Drawing.Bitmap bmp)
    {
        using var png = new MemoryStream();
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);

        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);       // ICONDIR, one image
            w.Write((byte)(bmp.Width  >= 256 ? 0 : bmp.Width));
            w.Write((byte)(bmp.Height >= 256 ? 0 : bmp.Height));
            w.Write((byte)0); w.Write((byte)0);                               // no palette
            w.Write((ushort)1); w.Write((ushort)32);                          // planes, bpp
            w.Write((int)png.Length); w.Write(6 + 16);                        // size, offset
            png.Position = 0;
            png.CopyTo(ico);
        }
        ico.Position = 0;
        return new System.Drawing.Icon(ico);
    }
}
