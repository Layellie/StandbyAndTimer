using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services.Tray;

// Centralizes app-icon construction: loads the bundled .ico, falls back to
// a programmatic icon if missing, and builds the "active" variant with a
// green dot overlay used to signal "timer locked" in the tray. Kept static
// because it has no state and is called exactly once at startup — making
// it a DI service would only add ceremony.
internal static class AppIconLoader
{
    // Loads the bundled app_icon.ico from the WPF resource stream. Returns
    // a fallback bitmap-built icon if the resource is missing so the app
    // can still show *some* icon in the tray.
    internal static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/app_icon.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream);
            }
        }
        catch (Exception ex) { Logger.Warn($"LoadAppIcon: {ex.Message}"); }

        return CreateFallbackIcon();
    }

    // Draws a green dot in the bottom-right corner of the base icon. Used
    // as the tray icon when the high-resolution timer is locked, so the
    // user has a glance-able "active" indicator without opening the window.
    internal static System.Drawing.Icon BuildActiveIcon(System.Drawing.Icon baseIcon)
    {
        try
        {
            using var bmp = baseIcon.ToBitmap();
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int size = Math.Max(8, bmp.Width / 3);
                int x    = bmp.Width  - size - 1;
                int y    = bmp.Height - size - 1;
                using var dot    = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 40, 220, 90));
                using var border = new System.Drawing.Pen(System.Drawing.Color.Black, 1.5f);
                g.FillEllipse(dot, x, y, size, size);
                g.DrawEllipse(border, x, y, size, size);
            }
            return BitmapToIcon(bmp);
        }
        catch (Exception ex)
        {
            Logger.Warn($"BuildActiveIcon: {ex.Message}");
            return (System.Drawing.Icon)baseIcon.Clone();
        }
    }

    // Converts a System.Drawing.Icon into a WPF BitmapSource so it can be
    // used as Window.Icon. CreateBitmapSourceFromHIcon borrows the HICON
    // without taking ownership, so the source Icon must outlive the source
    // BitmapSource — App.OnExit handles that ordering.
    internal static BitmapSource IconToImageSource(System.Drawing.Icon icon) =>
        System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            System.Windows.Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());

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

    // Bitmap → HICON → Icon via GetHicon, but we destroy the temporary HICON
    // immediately after cloning it through a memory stream — no leak.
    private static System.Drawing.Icon BitmapToIcon(System.Drawing.Bitmap bmp)
    {
        IntPtr hicon = bmp.GetHicon();
        try
        {
            using var temp = System.Drawing.Icon.FromHandle(hicon);
            using var ms   = new MemoryStream();
            temp.Save(ms);
            ms.Position = 0;
            return new System.Drawing.Icon(ms);
        }
        finally
        {
            NativeMethods.DestroyIcon(hicon);
        }
    }
}
