using System.Windows;
using Microsoft.Win32;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services;

internal sealed class ThemeService : IThemeService
{
    private static readonly Dictionary<Theme, string> PalettePaths = new()
    {
        [Theme.Dark]  = "Views/Styles/Palette.Dark.xaml",
        [Theme.Light] = "Views/Styles/Palette.Light.xaml",
    };

    // Slot 0 = Theme.xaml (styles), Slot 1 = Palette.*.xaml (swapped here),
    // Slot 2 = Strings.*.xaml (swapped by LocalizationService).
    private const int PaletteSlot = 1;

    private bool _disposed;

    public Theme CurrentTheme   { get; private set; } = Theme.Dark;
    public Theme EffectiveTheme => CurrentTheme == Theme.System ? DetectSystemTheme() : CurrentTheme;

    public event EventHandler? ThemeChanged;

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void SetTheme(Theme theme)
    {
        if (theme == CurrentTheme) return;
        CurrentTheme = theme;
        ApplyPalette(EffectiveTheme);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        if (CurrentTheme != Theme.System) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            ApplyPalette(EffectiveTheme);
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private static void ApplyPalette(Theme effective)
    {
        var key  = effective == Theme.Light ? Theme.Light : Theme.Dark;
        var uri  = new Uri(PalettePaths[key], UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        var merged = Application.Current.Resources.MergedDictionaries;

        if (merged.Count > PaletteSlot)
            merged[PaletteSlot] = dict;
        else
            merged.Insert(PaletteSlot, dict);
    }

    private static Theme DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            if (v is int i) return i == 0 ? Theme.Dark : Theme.Light;
        }
        catch { /* fall through */ }
        return Theme.Dark;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
