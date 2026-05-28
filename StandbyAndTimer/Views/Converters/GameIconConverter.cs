using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StandbyAndTimer.Views.Converters;

internal sealed class GameIconConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        if (_cache.TryGetValue(path, out var cached))
            return cached;

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;

            var img = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            img.Freeze();
            _cache[path] = img;
            return img;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
