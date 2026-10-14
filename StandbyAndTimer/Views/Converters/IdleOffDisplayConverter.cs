using System.Globalization;
using System.Windows;
using System.Windows.Data;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Views.Converters;

// "Turn the timer off when idle" picker: 0 -> Off, minutes -> "10 min",
// SettingsViewModel.IdleOffCustom -> Custom.
[ValueConversion(typeof(int), typeof(string))]
public sealed class IdleOffDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int option) return string.Empty;

        string Res(string key) => Application.Current?.Resources[key] as string ?? key;
        return option switch
        {
            0                                 => Res("Str_IdleOff_Off"),
            SettingsViewModel.IdleOffCustom   => Res("Str_IdleOff_Custom"),
            _ => string.Format(CultureInfo.CurrentCulture, Res("Str_IdleOff_Minutes"), option),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
