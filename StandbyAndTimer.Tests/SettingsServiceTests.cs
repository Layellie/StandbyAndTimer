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
                                BoostPriority = false, AllowAutoPurge = false },
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
}
