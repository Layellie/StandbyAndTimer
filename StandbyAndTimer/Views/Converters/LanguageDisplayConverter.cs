using System.Globalization;
using System.Windows.Data;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Views.Converters;

[ValueConversion(typeof(Language), typeof(string))]
public sealed class LanguageDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Language lang ? lang switch
        {
            Language.English => "English",
            Language.Turkish => "Türkçe",
            _                => value.ToString() ?? string.Empty
        } : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
