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
                NotifyOnAutoClean     = GetBool(key,     "NotifyOnAutoClean", defaultValue: true),

                AutoPurgeIdleOnly     = GetBool(key,     "AutoPurgeIdleOnly"),
                LightPurge            = GetBool(key,     "LightPurge"),
                DeepPurge             = GetBool(key,     "DeepPurge"),
                IdleThresholdMinutes  = GetInt (key,     "IdleThresholdMinutes", 5),
                TimerIdleOffEnabled   = GetBool(key,     "TimerIdleOffEnabled"),
                TimerIdleOffMinutes   = GetInt (key,     "TimerIdleOffMinutes", 10),
                TimerOffOnBattery     = GetBool(key,     "TimerOffOnBattery"),
                AutoCleanEnabled      = GetBool(key,     "AutoCleanEnabled"),
                AutoCleanIntervalDays = GetInt (key,     "AutoCleanIntervalDays", 7),
                AutoCleanLastRunUtc   = GetUtc (key,     "AutoCleanLastRunUtc"),
                GlobalTimerPromptAnswered = GetBool(key, "GlobalTimerPromptAnswered"),
                GlobalTimerSetByApp       = GetBool(key, "GlobalTimerSetByApp"),
                GlobalTimerChangedUtc     = GetUtc (key, "GlobalTimerChangedUtc"),
                GlobalTimerBootValue      = GetBool(key, "GlobalTimerBootValue"),
                AutoPurgeCooldownEnabled  = GetBool(key, "AutoPurgeCooldownEnabled"),
                AutoPurgeCooldownSeconds  = GetInt (key, "AutoPurgeCooldownSeconds", 60),
                CloseToTrayDirectly       = GetBool(key, "CloseToTrayDirectly"),

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
            // 3.0.5: games the IN GAME lock leaves alone (an exception list, so older lists keep the timer on).
            var noTimer   = new HashSet<string>(SplitPaths(key.GetValue("NoTimerGamePaths")), StringComparer.OrdinalIgnoreCase);
            // 3.0.5: boosted games at Above normal instead of High, and games purged once when they close.
            var aboveNormal = new HashSet<string>(SplitPaths(key.GetValue("AboveNormalGamePaths")), StringComparer.OrdinalIgnoreCase);
            var purgeOnExit = new HashSet<string>(SplitPaths(key.GetValue("PurgeOnExitGamePaths")), StringComparer.OrdinalIgnoreCase);
            GameEntry GameAt(string p, bool missing) => new()
            {
                DisplayName    = Path.GetFileNameWithoutExtension(p),
                ExecutablePath = p,
                IsMissing      = missing,   // kept though its drive is away: the list shows it dimmed
                AutoAdded      = autoPaths.Contains(p),
                Priority       = noBoost.Contains(p)     ? GamePriority.Unchanged
                               : aboveNormal.Contains(p) ? GamePriority.AboveNormal
                               :                           GamePriority.High,
                AllowAutoPurge = !noPurge.Contains(p),
                PurgeOnStart   = purgeOnStart.Contains(p),
                PurgeOnExit    = purgeOnExit.Contains(p),
                TimerInGame    = !noTimer.Contains(p)
            };
            settings.Games = SplitPaths(key.GetValue("GamePaths"))
                .Select(p => (Path: p, Exists: File.Exists(p)))   // the disk is asked once per game
                .Where(g => KeepSavedGame(g.Path, _ => g.Exists, Directory.Exists))
                .Select(g => GameAt(g.Path, missing: !g.Exists))
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

            // The value names and formats below are what every installed
            // version reads: never rename one, never change how one is written
            // (the helpers after this method say how). Settings added since
            // 2.4.0 are stored only when they differ from their default, so a
            // key written by an older version stays exactly as it was.

            // ── Standby purge ────────────────────────────────────────────
            Number(key, "StandbyLimitMb",        settings.StandbyLimitMb);
            Number(key, "FreeLimitMb",           settings.FreeLimitMb);
            Flag  (key, "AutoPurgeEnabled",      settings.AutoPurgeEnabled);
            Flag  (key, "AutoPurgeIdleOnly",     settings.AutoPurgeIdleOnly);
            Flag  (key, "AutoPurgeOnlyInGames",  settings.AutoPurgeOnlyInGames);
            Number(key, "IdleThresholdMinutes",  settings.IdleThresholdMinutes);
            Flag  (key, "LightPurge",            settings.LightPurge);
            Flag  (key, "DeepPurge",             settings.DeepPurge);
            FlagWhenOn    (key, "AutoPurgeCooldownEnabled", settings.AutoPurgeCooldownEnabled);
            NumberUnless  (key, "AutoPurgeCooldownSeconds", settings.AutoPurgeCooldownSeconds, defaultValue: 60);

            // ── Timer ────────────────────────────────────────────────────
            Flag  (key, "TimerResolutionActive", settings.TimerResolutionActive);
            Flag  (key, "TimerLocked",           settings.TimerLocked);
            Text  (key, "TimerLockMode",         settings.TimerLock.ToString());
            Flag  (key, "TimerIdleOffEnabled",   settings.TimerIdleOffEnabled);
            Number(key, "TimerIdleOffMinutes",   settings.TimerIdleOffMinutes);
            FlagWhenOn(key, "TimerOffOnBattery", settings.TimerOffOnBattery);

            // ── App ──────────────────────────────────────────────────────
            Flag  (key, "GameModeEnabled",       settings.GameModeEnabled);
            Flag  (key, "AutoStartEnabled",      settings.AutoStartEnabled);
            Flag  (key, "UpdateCheckEnabled",    settings.UpdateCheckEnabled);
            Flag  (key, "FirstRunCompleted",     settings.FirstRunCompleted);
            Text  (key, "Language",              settings.Language.ToString());
            Text  (key, "Theme",                 settings.Theme.ToString());
            FlagWhenOn(key, "CloseToTrayDirectly", settings.CloseToTrayDirectly);

            // ── Notifications and hotkeys ────────────────────────────────
            Flag  (key, "NotifyOnPurge",         settings.NotifyOnPurge);
            Flag  (key, "NotifyOnTimerToggle",   settings.NotifyOnTimerToggle);
            Flag  (key, "NotifyOnGameDetected",  settings.NotifyOnGameDetected);
            FlagWhenOff(key, "NotifyOnAutoClean", settings.NotifyOnAutoClean);   // on is the default
            Text  (key, "PurgeHotkey",           settings.PurgeHotkey     ?? "Ctrl+Alt+P");
            Text  (key, "TimerHotkey",           settings.TimerHotkey     ?? "Ctrl+Alt+T");
            Text  (key, "AutoPurgeHotkey",       settings.AutoPurgeHotkey ?? "Ctrl+Alt+O");

            // ── System settings (this machine) ───────────────────────────
            Flag  (key, "GlobalTimerPromptAnswered", settings.GlobalTimerPromptAnswered);
            Flag  (key, "GlobalTimerSetByApp",       settings.GlobalTimerSetByApp);
            Flag  (key, "GlobalTimerBootValue",      settings.GlobalTimerBootValue);
            Utc   (key, "GlobalTimerChangedUtc",     settings.GlobalTimerChangedUtc);

            // ── Maintenance ──────────────────────────────────────────────
            TextOrNothing(key, "CleanupSelection",      settings.CleanupSelection);
            FlagWhenOn   (key, "AutoCleanEnabled",      settings.AutoCleanEnabled);
            NumberUnless (key, "AutoCleanIntervalDays", settings.AutoCleanIntervalDays, defaultValue: 7);
            Utc          (key, "AutoCleanLastRunUtc",   settings.AutoCleanLastRunUtc);

            // ── Games: the list, then one list per per-game switch that is
            //    not at its default (so lists saved by older versions keep
            //    the old behaviour) ────────────────────────────────────────
            var games = settings.Games;
            IEnumerable<string> PathsWhere(Func<GameEntry, bool> rule) => games.Where(rule).Select(g => g.ExecutablePath);
            SetPaths(key, "GamePaths",             games.Select(g => g.ExecutablePath));
            SetPaths(key, "AutoGamePaths",         PathsWhere(g => g.AutoAdded));
            SetPaths(key, "IgnoredGamePaths",      settings.IgnoredGamePaths ?? []);
            SetPaths(key, "NoBoostGamePaths",      PathsWhere(g => !g.BoostPriority));
            SetPaths(key, "AboveNormalGamePaths",  PathsWhere(g => g.Priority == GamePriority.AboveNormal));
            SetPaths(key, "NoAutoPurgeGamePaths",  PathsWhere(g => !g.AllowAutoPurge));
            SetPaths(key, "PurgeOnStartGamePaths", PathsWhere(g => g.PurgeOnStart));
            SetPaths(key, "PurgeOnExitGamePaths",  PathsWhere(g => g.PurgeOnExit));
            SetPaths(key, "NoTimerGamePaths",      PathsWhere(g => !g.TimerInGame));
        }
        catch (Exception ex)
        {
            Logger.Error("SettingsService.Save", ex);
        }
    }

    // ── How each kind of value is written ────────────────────────────────────

    private static void Remove(RegistryKey key, string name) => key.DeleteValue(name, throwOnMissingValue: false);

    /// <summary>A switch, always stored: REG_SZ "1" or "0".</summary>
    private static void Flag(RegistryKey key, string name, bool value) => key.SetValue(name, value ? "1" : "0");

    /// <summary>A switch that is off by default: "1" when on, nothing when off.</summary>
    private static void FlagWhenOn(RegistryKey key, string name, bool value)
    {
        if (value) key.SetValue(name, "1"); else Remove(key, name);
    }

    /// <summary>A switch that is on by default: "0" when off, nothing when on.</summary>
    private static void FlagWhenOff(RegistryKey key, string name, bool value)
    {
        if (value) Remove(key, name); else key.SetValue(name, "0");
    }

    /// <summary>
    /// A number, always stored, as REG_DWORD (regedit shows a number, Load
    /// does not have to parse; GetInt still reads the REG_SZ numbers of very
    /// old versions and of imported settings).
    /// </summary>
    private static void Number(RegistryKey key, string name, int value) => key.SetValue(name, value, RegistryValueKind.DWord);

    /// <summary>A number stored only when it is not <paramref name="defaultValue"/>.</summary>
    private static void NumberUnless(RegistryKey key, string name, int value, int defaultValue)
    {
        if (value != defaultValue) Number(key, name, value); else Remove(key, name);
    }

    private static void Text(RegistryKey key, string name, string value) => key.SetValue(name, value);

    /// <summary>Text, or no value at all for null (which Load gives back as null).</summary>
    private static void TextOrNothing(RegistryKey key, string name, string? value)
    {
        if (value is not null) Text(key, name, value); else Remove(key, name);
    }

    /// <summary>A moment in UTC, round-trip format ("o"); nothing for null.</summary>
    private static void Utc(RegistryKey key, string name, DateTime? value)
    {
        if (value is { } moment) Text(key, name, moment.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        else Remove(key, name);
    }

    /// <summary>
    /// Whether a saved game stays in the list at load. One whose exe is gone
    /// from a drive that is there was uninstalled and is dropped. One whose
    /// drive itself is missing (a USB disk unplugged, a drive that mounts
    /// after logon while the app auto-starts) is kept: dropping it erased the
    /// game and its per-game rules from the registry at the next save.
    /// </summary>
    internal static bool KeepSavedGame(string path, Func<string, bool> fileExists, Func<string, bool> driveExists)
    {
        if (fileExists(path)) return true;
        string? root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && Path.IsPathFullyQualified(path) && !driveExists(root);
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
