using System.Globalization;
using System.Windows;
using System.Windows.Data;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Views.Converters;

[ValueConversion(typeof(Theme), typeof(string))]
public sealed class ThemeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Theme t) return string.Empty;

        string key = t switch
        {
            Theme.Dark   => "Str_Theme_Dark",
            Theme.Light  => "Str_Theme_Light",
            Theme.System => "Str_Theme_System",
            _            => string.Empty
        };

        return Application.Current?.Resources[key] as string ?? t.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
