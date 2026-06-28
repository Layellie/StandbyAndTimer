using System.Globalization;
using Microsoft.Win32;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Infrastructure;

namespace StandbyAndTimer.Services;

internal sealed class SettingsService : ISettingsService
{
    public AppSettings Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppConstants.SettingsRegistryPath);
            if (key is null)
                return new AppSettings();

            var settings = new AppSettings
            {
                // Defaults intentionally 0 / false so an upgrade *or* fresh install
                // never auto-purges until the user explicitly opts in via the
                // AUTO PURGE toggle and sets non-zero thresholds.
                StandbyLimitMb        = GetInt(key,      "StandbyLimitMb",      0),
                FreeLimitMb           = GetInt(key,      "FreeLimitMb",         0),
                AutoPurgeEnabled      = GetBool(key,     "AutoPurgeEnabled"),
                GameModeEnabled       = GetBool(key,     "GameModeEnabled"),
                AutoStartEnabled      = GetBool(key,     "AutoStartEnabled"),
                TimerResolutionActive = GetBool(key,     "TimerResolutionActive"),
                Language              = GetEnum(key, "Language", Language.English),
                Theme                 = GetEnum(key, "Theme",    Theme.Dark),
                UpdateCheckEnabled    = GetBool(key,     "UpdateCheckEnabled", defaultValue: true),
                FirstRunCompleted     = GetBool(key,     "FirstRunCompleted"),

                NotifyOnPurge         = GetBool(key,     "NotifyOnPurge",        defaultValue: true),
                NotifyOnTimerToggle   = GetBool(key,     "NotifyOnTimerToggle",  defaultValue: true),
                NotifyOnGameDetected  = GetBool(key,     "NotifyOnGameDetected", defaultValue: true),

                AutoPurgeIdleOnly     = GetBool(key,     "AutoPurgeIdleOnly"),
                IdleThresholdMinutes  = GetInt (key,     "IdleThresholdMinutes", 5),

                PurgeHotkey           = key.GetValue("PurgeHotkey")?.ToString() ?? "Ctrl+Alt+P",
                TimerHotkey           = key.GetValue("TimerHotkey")?.ToString() ?? "Ctrl+Alt+T",
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
            using var key = Registry.CurrentUser.CreateSubKey(AppConstants.SettingsRegistryPath);

            // Ints stored as REG_DWORD — native typing means regedit shows them
            // as numbers (not quoted strings) and Load() doesn't have to parse.
            // GetInt below still tolerates legacy REG_SZ values so an upgrade
            // from a pre-DWORD install doesn't lose the user's thresholds.
            key.SetValue("StandbyLimitMb", settings.StandbyLimitMb, RegistryValueKind.DWord);
            key.SetValue("FreeLimitMb",    settings.FreeLimitMb,    RegistryValueKind.DWord);

            key.SetValue("AutoPurgeEnabled",       settings.AutoPurgeEnabled       ? "1" : "0");
            key.SetValue("GameModeEnabled",        settings.GameModeEnabled        ? "1" : "0");
            key.SetValue("AutoStartEnabled",       settings.AutoStartEnabled       ? "1" : "0");
            key.SetValue("TimerResolutionActive",  settings.TimerResolutionActive  ? "1" : "0");
            key.SetValue("UpdateCheckEnabled",     settings.UpdateCheckEnabled     ? "1" : "0");
            key.SetValue("FirstRunCompleted",      settings.FirstRunCompleted      ? "1" : "0");

            key.SetValue("NotifyOnPurge",          settings.NotifyOnPurge          ? "1" : "0");
            key.SetValue("NotifyOnTimerToggle",    settings.NotifyOnTimerToggle    ? "1" : "0");
            key.SetValue("NotifyOnGameDetected",   settings.NotifyOnGameDetected   ? "1" : "0");
            key.SetValue("AutoPurgeIdleOnly",      settings.AutoPurgeIdleOnly      ? "1" : "0");
            key.SetValue("IdleThresholdMinutes",   settings.IdleThresholdMinutes,  RegistryValueKind.DWord);
            key.SetValue("PurgeHotkey",            settings.PurgeHotkey            ?? "Ctrl+Alt+P");
            key.SetValue("TimerHotkey",            settings.TimerHotkey            ?? "Ctrl+Alt+T");

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

    // Reads either REG_DWORD (current format) or REG_SZ (legacy / Import JSON
    // path which writes string-encoded numbers). InvariantCulture parse so a
    // tr-TR machine doesn't mis-parse "1024" written under en-US, etc.
    private static int GetInt(RegistryKey key, string name, int fallback)
    {
        var raw = key.GetValue(name);
        return raw switch
        {
            int i                                                                          => i,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) => v,
            _                                                                              => fallback,
        };
    }

    // Accepts the legacy "1"/"0" registry format AND "true"/"false" — the JSON
    // Export/Import path round-trips bool as the latter, so without the
    // TryParse fallback every imported settings file would silently reset all
    // toggles to their defaults on the next Load.
    private static bool GetBool(RegistryKey key, string name, bool defaultValue = false)
    {
        var v = key.GetValue(name)?.ToString();
        if (v is null) return defaultValue;
        if (v == "1")  return true;
        if (v == "0")  return false;
        return bool.TryParse(v, out bool parsed) ? parsed : defaultValue;
    }

    private static TEnum GetEnum<TEnum>(RegistryKey key, string name, TEnum fallback) where TEnum : struct =>
        Enum.TryParse<TEnum>(key.GetValue(name)?.ToString(), out var v) ? v : fallback;
}
