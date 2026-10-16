using System.IO;
using Microsoft.Win32;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

/// <summary>
/// Real registry round trips, under a throwaway HKCU key (never the app's
/// own HKCU\SOFTWARE\StandbyAndTimer). Game paths must exist on disk, since
/// Load drops games whose exe is gone, so the tests create temp files.
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private const string ParentPath = @"SOFTWARE\StandbyAndTimer.Tests";
    private readonly string _keyPath = $@"{ParentPath}\{Guid.NewGuid():N}";
    private readonly string _dir     = Directory.CreateTempSubdirectory("sat-tests-").FullName;
    private readonly SettingsService _service;

    public SettingsServiceTests() => _service = new SettingsService(_keyPath);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(ParentPath))
            if (parent is { SubKeyCount: 0, ValueCount: 0 })
                Registry.CurrentUser.DeleteSubKey(ParentPath, throwOnMissingSubKey: false);
        Directory.Delete(_dir, recursive: true);
    }

    private string Exe(string name)
    {
        string p = Path.Combine(_dir, name + ".exe");
        File.WriteAllBytes(p, []);
        return p;
    }

    private RegistryKey Key() => Registry.CurrentUser.CreateSubKey(_keyPath);

    [Fact]
    public void Missing_key_loads_defaults()
    {
        var s = _service.Load();

        Assert.False(s.AutoPurgeEnabled);
        Assert.True(s.UpdateCheckEnabled);
        Assert.Equal(TimerLockMode.Unlocked, s.TimerLock);
        Assert.Empty(s.Games);
    }

    [Fact]
    public void Everything_saved_loads_back()
    {
        string cs2 = Exe("cs2"), val = Exe("VALORANT");
        var saved = new AppSettings
        {
            StandbyLimitMb = 4096, FreeLimitMb = 1024, AutoPurgeEnabled = true,
            GameModeEnabled = true, TimerLock = TimerLockMode.InGame,
            AutoPurgeOnlyInGames = true, LightPurge = true, IdleThresholdMinutes = 7,
            Language = Language.Turkish, Theme = Theme.Light,
            PurgeHotkey = "Ctrl+Shift+F9", NotifyOnPurge = false,
            TimerIdleOffEnabled = true, TimerIdleOffMinutes = 25,
            Games =
            [
                new GameEntry { DisplayName = "cs2", ExecutablePath = cs2 },
                new GameEntry { DisplayName = "VALORANT", ExecutablePath = val, AutoAdded = true,
                                BoostPriority = false, AllowAutoPurge = false, PurgeOnStart = true },
            ],
        };

        _service.Save(saved);
        var s = _service.Load();

        Assert.Equal(4096, s.StandbyLimitMb);
        Assert.Equal(1024, s.FreeLimitMb);
        Assert.True(s.AutoPurgeEnabled);
        Assert.Equal(TimerLockMode.InGame, s.TimerLock);
        Assert.True(s.AutoPurgeOnlyInGames);
        Assert.True(s.LightPurge);
        Assert.Equal(7, s.IdleThresholdMinutes);
        Assert.Equal(Language.Turkish, s.Language);
        Assert.Equal(Theme.Light, s.Theme);
        Assert.Equal("Ctrl+Shift+F9", s.PurgeHotkey);
        Assert.False(s.NotifyOnPurge);
        Assert.True(s.TimerIdleOffEnabled);
        Assert.Equal(25, s.TimerIdleOffMinutes);

        Assert.Equal([cs2, val], s.Games.Select(g => g.ExecutablePath));
        Assert.True(s.Games[0].BoostPriority);
        Assert.True(s.Games[0].AllowAutoPurge);
        Assert.False(s.Games[0].AutoAdded);
        Assert.True(s.Games[1].AutoAdded);
        Assert.False(s.Games[1].BoostPriority);
        Assert.True(s.Games[1].PurgeOnStart);
        Assert.False(s.Games[0].PurgeOnStart);
        Assert.False(s.Games[1].AllowAutoPurge);
    }

    [Fact]
    public void Locked_is_also_written_as_the_2_2_flag()
    {
        _service.Save(new AppSettings { TimerLock = TimerLockMode.Locked, TimerLocked = true });

        using var key = Key();
        Assert.Equal("1", key.GetValue("TimerLocked"));
        Assert.Equal("Locked", key.GetValue("TimerLockMode"));
    }

    [Fact]
    public void Key_written_by_2_2_loads_with_the_old_lock_and_default_game_switches()
    {
        string cs2 = Exe("cs2");
        using (var key = Key())
        {
            key.SetValue("TimerLocked", "1");
            key.SetValue("GamePaths", cs2);
        }

        var s = _service.Load();

        Assert.True(s.TimerLocked);
        Assert.Equal(TimerLockMode.Unlocked, s.TimerLock);   // the view model maps this to Locked
        Assert.True(s.Games.Single().BoostPriority);
        Assert.True(s.Games.Single().AllowAutoPurge);
    }

    [Fact]
    public void Games_whose_exe_is_gone_are_dropped()
    {
        string cs2 = Exe("cs2");
        using (var key = Key())
            key.SetValue("GamePaths", $@"{cs2};{_dir}\deleted.exe");

        Assert.Equal([cs2], _service.Load().Games.Select(g => g.ExecutablePath));
    }

    [Theory]
    [InlineData("7")]          // a number outside the enum
    [InlineData("-1")]
    [InlineData("Sometimes")]
    public void Unknown_lock_mode_falls_back_to_unlocked(string raw)
    {
        using (var key = Key())
            key.SetValue("TimerLockMode", raw);

        Assert.Equal(TimerLockMode.Unlocked, _service.Load().TimerLock);
    }

    [Theory]
    [InlineData(Theme.Black)]
    [InlineData(Theme.White)]
    [InlineData(Theme.System)]
    public void Every_theme_loads_back(Theme theme)
    {
        _service.Save(new AppSettings { Theme = theme });

        Assert.Equal(theme, _service.Load().Theme);
    }

    [Theory]                   // values written before Black/White existed
    [InlineData("Dark", Theme.Dark)]
    [InlineData("Light", Theme.Light)]
    [InlineData("System", Theme.System)]
    public void Themes_saved_by_older_versions_still_load(string raw, Theme expected)
    {
        using (var key = Key())
            key.SetValue("Theme", raw);

        Assert.Equal(expected, _service.Load().Theme);
    }

    [Fact]
    public void Imported_true_false_and_string_numbers_are_read()
    {
        using (var key = Key())
        {
            key.SetValue("AutoPurgeEnabled", "True");
            key.SetValue("StandbyLimitMb", "2048");
        }

        var s = _service.Load();

        Assert.True(s.AutoPurgeEnabled);
        Assert.Equal(2048, s.StandbyLimitMb);
    }

    [Fact]
    public void A_failed_load_never_overwrites_the_stored_settings()
    {
        _service.Save(new AppSettings { StandbyLimitMb = 4096, Theme = Theme.Black, TimerLock = TimerLockMode.Locked });
        var broken = new SettingsService(_keyPath) { BeforeRead = () => throw new InvalidOperationException("read bug") };

        var fallback = broken.Load();          // what a buggy update would see
        broken.Save(fallback);                 // and write back at exit

        var s = _service.Load();
        Assert.Equal(4096, s.StandbyLimitMb);
        Assert.Equal(Theme.Black, s.Theme);
        Assert.Equal(TimerLockMode.Locked, s.TimerLock);
    }

    [Fact]
    public void Settings_stored_by_2_4_0_survive_an_update_unchanged()
    {
        string cs2 = Exe("cs2");
        // Value for value what 2.4.0 writes (a real user's key, game paths swapped).
        using (var key = Key())
        {
            key.SetValue("StandbyLimitMb", 60, RegistryValueKind.DWord);
            key.SetValue("FreeLimitMb", 60, RegistryValueKind.DWord);
            foreach (var (n, v) in new[] {
                ("GameModeEnabled", "1"), ("AutoStartEnabled", "1"), ("TimerResolutionActive", "1"),
                ("UpdateCheckEnabled", "1"), ("Language", "English"), ("Theme", "Light"),
                ("AutoPurgeEnabled", "0"), ("FirstRunCompleted", "1"), ("NotifyOnPurge", "1"),
                ("NotifyOnTimerToggle", "1"), ("NotifyOnGameDetected", "0"), ("AutoPurgeIdleOnly", "0"),
                ("PurgeHotkey", "Ctrl+Alt+P"), ("TimerHotkey", "Ctrl+Shift+T"), ("AutoPurgeHotkey", "Ctrl+Alt+O"),
                ("IgnoredGamePaths", @"C:\Games\witcher3.exe"), ("TimerLocked", "1"), ("TimerLockMode", "Locked"),
                ("AutoPurgeOnlyInGames", "0"), ("LightPurge", "1"), ("TimerIdleOffEnabled", "1"),
                ("GamePaths", cs2), ("NoBoostGamePaths", cs2) })
                key.SetValue(n, v);
            key.SetValue("IdleThresholdMinutes", 5, RegistryValueKind.DWord);
            key.SetValue("TimerIdleOffMinutes", 25, RegistryValueKind.DWord);
        }

        var first = _service.Load();
        _service.Save(first);                  // the updated app saves at exit
        var second = _service.Load();

        Assert.Equal(60, first.StandbyLimitMb);
        Assert.Equal(Theme.Light, first.Theme);
        Assert.Equal("Ctrl+Shift+T", first.TimerHotkey);
        Assert.False(first.NotifyOnGameDetected);
        Assert.True(first.LightPurge);
        Assert.False(first.DeepPurge);   // not in a 2.4.0 key: off
        Assert.Equal(TimerLockMode.Locked, first.TimerLock);
        Assert.Equal(25, first.TimerIdleOffMinutes);
        Assert.False(Assert.Single(first.Games).BoostPriority);
        Assert.Equal([@"C:\Games\witcher3.exe"], first.IgnoredGamePaths);
        // Nothing changes on the way through a save.
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
    }
    [Fact]
    public void Cleanup_selection_round_trips_and_missing_stays_missing()
    {
        var s = _service.Load();
        Assert.Null(s.CleanupSelection);            // 2.5.2 key or fresh install: defaults apply
        _service.Save(s);
        using (var key = Key())
            Assert.Null(key.GetValue("CleanupSelection"));

        s.CleanupSelection = "";                    // the user unticked everything
        _service.Save(s);
        Assert.Equal("", _service.Load().CleanupSelection);

        s.CleanupSelection = "user-temp,nvidia-shader";
        _service.Save(s);
        Assert.Equal("user-temp,nvidia-shader", _service.Load().CleanupSelection);
    }

    [Fact]
    public void Settings_stored_by_2_5_2_survive_an_update_unchanged()
    {
        string cs2 = Exe("cs2"), val = Exe("VALORANT");
        // Value for value what 2.5.2 writes: every name SettingsService.Save
        // wrote in 2.5.2, with the same value kinds. No CleanupSelection.
        using (var key = Key())
        {
            foreach (var (n, v) in new[] { ("StandbyLimitMb", 2048), ("FreeLimitMb", 512),
                                           ("IdleThresholdMinutes", 5), ("TimerIdleOffMinutes", 15) })
                key.SetValue(n, v, RegistryValueKind.DWord);
            foreach (var (n, v) in new[] {
                ("AutoPurgeEnabled", "1"), ("GameModeEnabled", "1"), ("AutoStartEnabled", "1"),
                ("TimerResolutionActive", "0"), ("TimerLocked", "0"), ("TimerLockMode", "InGame"),
                ("AutoPurgeOnlyInGames", "0"), ("UpdateCheckEnabled", "1"), ("FirstRunCompleted", "1"),
                ("NotifyOnPurge", "1"), ("NotifyOnTimerToggle", "0"), ("NotifyOnGameDetected", "1"),
                ("AutoPurgeIdleOnly", "0"), ("LightPurge", "0"), ("DeepPurge", "1"),
                ("TimerIdleOffEnabled", "1"), ("GlobalTimerPromptAnswered", "1"), ("GlobalTimerSetByApp", "1"),
                ("GlobalTimerBootValue", "1"), ("GlobalTimerChangedUtc", "2026-09-30T18:12:45.1234567Z"),
                ("PurgeHotkey", "Ctrl+Alt+P"), ("TimerHotkey", "Ctrl+Alt+T"), ("AutoPurgeHotkey", "Ctrl+Alt+O"),
                ("Language", "Turkish"), ("Theme", "Black"),
                ("GamePaths", cs2 + ";" + val), ("AutoGamePaths", val), ("IgnoredGamePaths", @"C:\Games\witcher3.exe"),
                ("NoBoostGamePaths", cs2), ("NoAutoPurgeGamePaths", val), ("PurgeOnStartGamePaths", cs2) })
                key.SetValue(n, v);
        }
        var before = Values();

        var first = _service.Load();
        _service.Save(first);                  // the updated app saves at exit

        Assert.Null(first.CleanupSelection);   // not in a 2.5.2 key: the default ticks apply
        Assert.Equal(before, Values());        // every value, name and kind unchanged, nothing added
    }

    private List<(string Name, RegistryValueKind Kind, string Value)> Values()
    {
        using var key = Key();
        return key.GetValueNames().Order(StringComparer.Ordinal)
                  .Select(n => (n, key.GetValueKind(n), Convert.ToString(key.GetValue(n), System.Globalization.CultureInfo.InvariantCulture)!))
                  .ToList();
    }
}
