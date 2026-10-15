using System.Globalization;
using Microsoft.Win32;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Infrastructure;

namespace StandbyAndTimer.Services;

internal sealed class SettingsService : ISettingsService
{
    private readonly string _keyPath;

    // DI uses this one (it only sees public constructors).
    public SettingsService() : this(AppConstants.SettingsRegistryPath) { }

    // Tests point it at a throwaway HKCU key.
    internal SettingsService(string keyPath) => _keyPath = keyPath;

    // Set when Load could not read what is stored. Save then writes nothing
    // for the rest of the session: otherwise the defaults Load fell back to
    // would overwrite every stored setting at exit (an update with a read
    // bug would wipe the user's settings).
    private bool _loadFailed;

    // Tests: makes the next Load fail the way a read bug would.
    internal Action? BeforeRead { get; set; }

    public AppSettings Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            BeforeRead?.Invoke();
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
                TimerLocked           = GetBool(key,     "TimerLocked"),
                TimerLock             = GetEnum(key, "TimerLockMode", TimerLockMode.Unlocked),
                AutoPurgeOnlyInGames  = GetBool(key,     "AutoPurgeOnlyInGames"),
                Language              = GetEnum(key, "Language", Language.English),
                Theme                 = GetEnum(key, "Theme",    Theme.Dark),
                UpdateCheckEnabled    = GetBool(key,     "UpdateCheckEnabled", defaultValue: true),
                FirstRunCompleted     = GetBool(key,     "FirstRunCompleted"),

                NotifyOnPurge         = GetBool(key,     "NotifyOnPurge",        defaultValue: true),
                NotifyOnTimerToggle   = GetBool(key,     "NotifyOnTimerToggle",  defaultValue: true),
                NotifyOnGameDetected  = GetBool(key,     "NotifyOnGameDetected", defaultValue: true),

                AutoPurgeIdleOnly     = GetBool(key,     "AutoPurgeIdleOnly"),
                LightPurge            = GetBool(key,     "LightPurge"),
                DeepPurge             = GetBool(key,     "DeepPurge"),
                IdleThresholdMinutes  = GetInt (key,     "IdleThresholdMinutes", 5),
                TimerIdleOffEnabled   = GetBool(key,     "TimerIdleOffEnabled"),
                TimerIdleOffMinutes   = GetInt (key,     "TimerIdleOffMinutes", 10),
                GlobalTimerPromptAnswered = GetBool(key, "GlobalTimerPromptAnswered"),
                GlobalTimerSetByApp       = GetBool(key, "GlobalTimerSetByApp"),
                GlobalTimerChangedUtc     = GetUtc (key, "GlobalTimerChangedUtc"),
                GlobalTimerBootValue      = GetBool(key, "GlobalTimerBootValue"),

                PurgeHotkey           = key.GetValue("PurgeHotkey")?.ToString() ?? "Ctrl+Alt+P",
                TimerHotkey           = key.GetValue("TimerHotkey")?.ToString() ?? "Ctrl+Alt+T",
                AutoPurgeHotkey       = key.GetValue("AutoPurgeHotkey")?.ToString() ?? "Ctrl+Alt+O",
                CleanupSelection      = key.GetValue("CleanupSelection")?.ToString(),
            };

            // AutoGamePaths is the subset of GamePaths that fullscreen
            // detection added (drives the "AUTO" tag); kept as a separate
            // value so GamePaths stays readable by older versions.
            var autoPaths = new HashSet<string>(SplitPaths(key.GetValue("AutoGamePaths")), StringComparer.OrdinalIgnoreCase);
            // Per-game switches are stored as exception lists (default on),
            // so games saved by older versions keep the old behaviour.
            var noBoost   = new HashSet<string>(SplitPaths(key.GetValue("NoBoostGamePaths")), StringComparer.OrdinalIgnoreCase);
            var noPurge   = new HashSet<string>(SplitPaths(key.GetValue("NoAutoPurgeGamePaths")), StringComparer.OrdinalIgnoreCase);
            var purgeOnStart = new HashSet<string>(SplitPaths(key.GetValue("PurgeOnStartGamePaths")), StringComparer.OrdinalIgnoreCase);
            settings.Games = SplitPaths(key.GetValue("GamePaths"))
                .Where(File.Exists)
                .Select(p => new GameEntry
                {
                    DisplayName    = Path.GetFileNameWithoutExtension(p),
                    ExecutablePath = p,
                    AutoAdded      = autoPaths.Contains(p),
                    BoostPriority  = !noBoost.Contains(p),
                    AllowAutoPurge = !noPurge.Contains(p),
                    PurgeOnStart   = purgeOnStart.Contains(p)
                })
                .ToList();
            settings.IgnoredGamePaths = SplitPaths(key.GetValue("IgnoredGamePaths")).ToList();

            _loadFailed = false;
            return settings;
        }
        catch (Exception ex)
        {
            Logger.Error("SettingsService.Load", ex);
            _loadFailed = true;   // see the field: keep what is stored
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        if (_loadFailed)
        {
            Logger.Warn("SettingsService.Save skipped: settings failed to load this session, stored values kept");
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath);

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
            key.SetValue("TimerLocked",            settings.TimerLocked            ? "1" : "0");
            key.SetValue("TimerLockMode",          settings.TimerLock.ToString());
            key.SetValue("AutoPurgeOnlyInGames",   settings.AutoPurgeOnlyInGames   ? "1" : "0");
            key.SetValue("UpdateCheckEnabled",     settings.UpdateCheckEnabled     ? "1" : "0");
            key.SetValue("FirstRunCompleted",      settings.FirstRunCompleted      ? "1" : "0");

            key.SetValue("NotifyOnPurge",          settings.NotifyOnPurge          ? "1" : "0");
            key.SetValue("NotifyOnTimerToggle",    settings.NotifyOnTimerToggle    ? "1" : "0");
            key.SetValue("NotifyOnGameDetected",   settings.NotifyOnGameDetected   ? "1" : "0");
            key.SetValue("AutoPurgeIdleOnly",      settings.AutoPurgeIdleOnly      ? "1" : "0");
            key.SetValue("LightPurge",             settings.LightPurge             ? "1" : "0");
            key.SetValue("DeepPurge",              settings.DeepPurge              ? "1" : "0");
            key.SetValue("IdleThresholdMinutes",   settings.IdleThresholdMinutes,  RegistryValueKind.DWord);
            key.SetValue("TimerIdleOffEnabled",    settings.TimerIdleOffEnabled    ? "1" : "0");
            key.SetValue("TimerIdleOffMinutes",    settings.TimerIdleOffMinutes,   RegistryValueKind.DWord);
            key.SetValue("GlobalTimerPromptAnswered", settings.GlobalTimerPromptAnswered ? "1" : "0");
            key.SetValue("GlobalTimerSetByApp",       settings.GlobalTimerSetByApp       ? "1" : "0");
            key.SetValue("GlobalTimerBootValue",      settings.GlobalTimerBootValue      ? "1" : "0");
            if (settings.GlobalTimerChangedUtc is { } changed)
                key.SetValue("GlobalTimerChangedUtc", changed.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            else
                key.DeleteValue("GlobalTimerChangedUtc", throwOnMissingValue: false);
            key.SetValue("PurgeHotkey",            settings.PurgeHotkey            ?? "Ctrl+Alt+P");
            key.SetValue("TimerHotkey",            settings.TimerHotkey            ?? "Ctrl+Alt+T");
            key.SetValue("AutoPurgeHotkey",        settings.AutoPurgeHotkey        ?? "Ctrl+Alt+O");
            if (settings.CleanupSelection is not null) key.SetValue("CleanupSelection", settings.CleanupSelection);
            else                                      key.DeleteValue("CleanupSelection", throwOnMissingValue: false);

            key.SetValue("Language", settings.Language.ToString());
            key.SetValue("Theme",    settings.Theme.ToString());

            SetPaths(key, "GamePaths",        settings.Games.Select(g => g.ExecutablePath));
            SetPaths(key, "AutoGamePaths",    settings.Games.Where(g => g.AutoAdded).Select(g => g.ExecutablePath));
            SetPaths(key, "IgnoredGamePaths", settings.IgnoredGamePaths ?? []);
            SetPaths(key, "NoBoostGamePaths",     settings.Games.Where(g => !g.BoostPriority).Select(g => g.ExecutablePath));
            SetPaths(key, "NoAutoPurgeGamePaths", settings.Games.Where(g => !g.AllowAutoPurge).Select(g => g.ExecutablePath));
            SetPaths(key, "PurgeOnStartGamePaths", settings.Games.Where(g => g.PurgeOnStart).Select(g => g.ExecutablePath));
        }
        catch (Exception ex)
        {
            Logger.Error("SettingsService.Save", ex);
        }
    }

    // ';'-separated exe path lists (paths cannot contain ';' on Windows).
    private static string[] SplitPaths(object? raw) =>
        (raw?.ToString() ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void SetPaths(RegistryKey key, string name, IEnumerable<string> paths)
    {
        string joined = string.Join(";", paths);
        if (joined.Length > 0) key.SetValue(name, joined);
        else                   key.DeleteValue(name, throwOnMissingValue: false);
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

    // Round-trip ("o") UTC timestamp; missing or unparsable is null.
    private static DateTime? GetUtc(RegistryKey key, string name) =>
        DateTime.TryParse(key.GetValue(name)?.ToString(), CultureInfo.InvariantCulture,
                          DateTimeStyles.RoundtripKind, out var t)
            ? t.ToUniversalTime() : null;

    // Enum.TryParse also accepts any number ("7"), giving a value the enum
    // does not define; a hand-edited or corrupt value falls back instead.
    private static TEnum GetEnum<TEnum>(RegistryKey key, string name, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(key.GetValue(name)?.ToString(), out var v) && Enum.IsDefined(v) ? v : fallback;
}
