using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface ISettingsService
{
    /// <summary>Reads settings from HKCU. Returns defaults if the key does not exist.</summary>
    AppSettings Load();

    /// <summary>Persists settings to HKCU atomically.</summary>
    void Save(AppSettings settings);
}
