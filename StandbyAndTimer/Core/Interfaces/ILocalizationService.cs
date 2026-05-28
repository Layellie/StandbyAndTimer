using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface ILocalizationService
{
    Language CurrentLanguage { get; }
    void SetLanguage(Language lang);
    string GetString(string key);
    event EventHandler? LanguageChanged;
}
