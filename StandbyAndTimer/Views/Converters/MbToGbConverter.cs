using System.Globalization;
using System.Windows.Data;

namespace StandbyAndTimer.Views.Converters;

internal sealed class MbToGbConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double mb = value switch
        {
            long l   => l,
            int  i   => i,
            double d => d,
            _        => 0
        };
        return (mb / 1024.0).ToString("0.0", culture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
