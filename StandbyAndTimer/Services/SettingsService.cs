using Microsoft.Win32;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services;

internal sealed class SettingsService : ISettingsService
{
    private const string RegKey = @"SOFTWARE\StandbyAndTimer";

    public AppSettings Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey);
            if (key is null)
                return new AppSettings();

            var settings = new AppSettings
            {
                StandbyLimitMb        = GetInt(key,      "StandbyLimitMb",  1024),
                FreeLimitMb           = GetInt(key,      "FreeLimitMb",      1024),
                GameModeEnabled       = GetBool(key,     "GameModeEnabled"),
                AutoStartEnabled      = GetBool(key,     "AutoStartEnabled"),
                TimerResolutionActive = GetBool(key,     "TimerResolutionActive"),
                Language              = GetEnum(key, "Language", Language.English),
                Theme                 = GetEnum(key, "Theme",    Theme.Dark),
                UpdateCheckEnabled    = GetBool(key,     "UpdateCheckEnabled", defaultValue: true),
            };

            string pathsRaw = key.GetValue("GamePaths")?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(pathsRaw))
            {
                settings.Games = pathsRaw
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Where(File.Exists)
                    .Select(p => new GameEntry
                    {
                        DisplayName    = Path.GetFileNameWithoutExtension(p),
                        ExecutablePath = p
                    })
                    .ToList();
            }

            return settings;
        }
        catch (Exception ex)
        {
            Logger.Error("SettingsService.Load", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegKey);
            key.SetValue("StandbyLimitMb",        settings.StandbyLimitMb.ToString());
            key.SetValue("FreeLimitMb",            settings.FreeLimitMb.ToString());
            key.SetValue("GameModeEnabled",        settings.GameModeEnabled        ? "1" : "0");
            key.SetValue("AutoStartEnabled",       settings.AutoStartEnabled       ? "1" : "0");
            key.SetValue("TimerResolutionActive",  settings.TimerResolutionActive  ? "1" : "0");
            key.SetValue("UpdateCheckEnabled",     settings.UpdateCheckEnabled     ? "1" : "0");

            key.SetValue("Language", settings.Language.ToString());
            key.SetValue("Theme",    settings.Theme.ToString());

            if (settings.Games.Count > 0)
                key.SetValue("GamePaths", string.Join(";", settings.Games.Select(g => g.ExecutablePath)));
            else
                key.DeleteValue("GamePaths", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Logger.Error("SettingsService.Save", ex);
        }
    }

    private static int GetInt(RegistryKey key, string name, int fallback) =>
        int.TryParse(key.GetValue(name)?.ToString(), out int v) ? v : fallback;

    private static bool GetBool(RegistryKey key, string name, bool defaultValue = false)
    {
        var v = key.GetValue(name)?.ToString();
        if (v is null) return defaultValue;
        return v == "1";
    }

    private static TEnum GetEnum<TEnum>(RegistryKey key, string name, TEnum fallback) where TEnum : struct =>
        Enum.TryParse<TEnum>(key.GetValue(name)?.ToString(), out var v) ? v : fallback;
}
