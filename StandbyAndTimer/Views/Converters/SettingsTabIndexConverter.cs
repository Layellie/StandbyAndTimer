using System.Globalization;
using System.Windows.Data;

namespace StandbyAndTimer.Views.Converters;

/// <summary>
/// Maps <c>SettingsViewModel.IsOpen</c> onto the main TabControl's
/// SelectedIndex: false → 0 (Status), true → 1 (Settings). IsOpen keeps its
/// meaning of "the Settings page is on screen", which is what drives the
/// log-tail refresh / cancel in SettingsViewModel.
/// </summary>
[ValueConversion(typeof(bool), typeof(int))]
public sealed class SettingsTabIndexConverter : IValueConverter
{
    public const int SettingsTabIndex = 1;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? SettingsTabIndex : 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index && index == SettingsTabIndex;
}
