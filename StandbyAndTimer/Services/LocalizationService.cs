using System.Windows;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services;

internal sealed class LocalizationService : ILocalizationService
{
    private static readonly Dictionary<Language, string> DictPaths = new()
    {
        [Language.English] = "Views/Strings/Strings.en-US.xaml",
        [Language.Turkish] = "Views/Strings/Strings.tr-TR.xaml",
    };

    // Slot index in Application.Resources.MergedDictionaries reserved for strings.
    // Slot 0 = Theme.xaml (styles), Slot 1 = active palette, Slot 2 = active strings.
    private const int StringSlot = 2;

    public Language CurrentLanguage { get; private set; } = Language.English;

    public event EventHandler? LanguageChanged;

    public void SetLanguage(Language lang)
    {
        if (lang == CurrentLanguage) return;
        CurrentLanguage = lang;

        Application.Current.Dispatcher.Invoke(() =>
        {
            var uri  = new Uri(DictPaths[lang], UriKind.Relative);
            var dict = new ResourceDictionary { Source = uri };
            var merged = Application.Current.Resources.MergedDictionaries;

            if (merged.Count > StringSlot)
                merged[StringSlot] = dict;
            else
                merged.Add(dict);
        });

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string GetString(string key) =>
        Application.Current.Resources[key] as string ?? key;
}
