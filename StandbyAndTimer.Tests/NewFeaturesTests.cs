using System.IO;
using System.Reflection;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;
using StandbyAndTimer.Services.Maintenance;
using StandbyAndTimer.Services.Tray;
using StandbyAndTimer.ViewModels;
using WinForms = System.Windows.Forms;

namespace StandbyAndTimer.Tests;

public sealed class NewFeaturesTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("sat-newfeatures-").FullName;
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // ── #4: Smart Cooldown & Recommended Thresholds ──────────────────────

    [Fact]
    public void Cooldown_is_complete_when_disabled_or_elapsed()
    {
        var t0 = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(MemoryMonitorService.IsCooldownComplete(null, t0, 60));
        Assert.True(MemoryMonitorService.IsCooldownComplete(t0, t0.AddSeconds(5), 0));
        Assert.False(MemoryMonitorService.IsCooldownComplete(t0, t0.AddSeconds(30), 60));
        Assert.True(MemoryMonitorService.IsCooldownComplete(t0, t0.AddSeconds(60), 60));
        Assert.True(MemoryMonitorService.IsCooldownComplete(t0, t0.AddSeconds(90), 60));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-10, 60)]
    [InlineData(2, 5)]
    [InlineData(45, 45)]
    [InlineData(999, 600)]
    public void SystemViewModel_clamps_cooldown_seconds(int input, int expected)
    {
        Assert.Equal(expected, SystemViewModel.ClampCooldown(input));
    }

    [Theory]
    [InlineData(4096,  512,  512)]
    [InlineData(8192,  1024, 1024)]
    [InlineData(16384, 2048, 1024)]
    [InlineData(32768, 4096, 2048)]
    [InlineData(65536, 8192, 2048)]
    public void Recommended_thresholds_match_ram_tiers(long totalRamMb, int expectedStandby, int expectedFree)
    {
        var (standby, free) = MainViewModel.GetRecommendedThresholds(totalRamMb);

        Assert.Equal(expectedStandby, standby);
        Assert.Equal(expectedFree, free);
    }

    [Fact]
    public void ApplyRecommendedThresholds_updates_limits_and_persists()
    {
        var h = new Harness();
        typeof(MainViewModel).GetField("_totalRamMb", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(h.Main, 8192L);

        h.Main.ApplyRecommendedThresholdsCommand.Execute(null);

        Assert.Equal(1024, h.Main.StandbyLimitMb);
        Assert.Equal(1024, h.Main.FreeLimitMb);
        Assert.Equal(1024, h.Store.Saved[^1].StandbyLimitMb);
        Assert.Equal(1024, h.Store.Saved[^1].FreeLimitMb);
    }

    // ── #3: Intel Shader Cache & Downloads Quick Filters ─────────────────

    [Fact]
    public void Intel_shader_cache_target_is_configured_and_blocked_while_game_runs()
    {
        var target = CleanupTargets.IntelShader();

        Assert.Equal(CleanupTargetIds.IntelShader, target.Id);
        Assert.True(target.BlockedWhileGameRuns);
        Assert.NotEmpty(target.Roots);
    }

    private sealed class FakeDownloadsForFilters(IReadOnlyList<DownloadEntry> entries) : IDownloadsService
    {
        public string? FolderPath => @"C:\Users\x\Downloads";
        public Task<CleanupScan> ScanAsync(CancellationToken ct) =>
            Task.FromResult(new CleanupScan(true, entries.Sum(e => e.Bytes), entries.Count));
        public Task<IReadOnlyList<DownloadEntry>> ListAsync(CancellationToken ct) =>
            Task.FromResult(entries);
        public Task<IReadOnlyList<DownloadEntry>> MoveToRecycleBinAsync(IReadOnlyList<DownloadEntry> e) =>
            Task.FromResult<IReadOnlyList<DownloadEntry>>([]);
    }

    [Fact]
    public async Task Downloads_quick_filters_select_installers_and_old_items()
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0);
        var entries = new[]
        {
            new DownloadEntry(@"C:\Dl\setup.exe",   "setup.exe",   false, 100 << 20, now.AddDays(-5)),
            new DownloadEntry(@"C:\Dl\archive.zip", "archive.zip", false, 200 << 20, now.AddDays(-40)),
            new DownloadEntry(@"C:\Dl\notes.txt",   "notes.txt",   false, 1 << 20,   now.AddDays(-60)),
            new DownloadEntry(@"C:\Dl\folder.zip",  "folder.zip",  true,  50 << 20,  now.AddDays(-10)),
        };
        using var vm = new DownloadsViewModel(new FakeDownloadsForFilters(entries), new FakeLocalization(), (_, _, _) => { })
        {
            Now = () => now
        };
        await vm.LoadAsync();
        DownloadRow Row(string name) => vm.Items.Single(i => i.Name == name);

        vm.SelectInstallersCommand.Execute(null);
        Assert.True(Row("setup.exe").IsChecked);
        Assert.True(Row("archive.zip").IsChecked);
        Assert.False(Row("notes.txt").IsChecked);
        Assert.False(Row("folder.zip").IsChecked);   // directory named folder.zip

        vm.SelectOldCommand.Execute(null);
        Assert.False(Row("setup.exe").IsChecked);    // 5 days old
        Assert.True(Row("archive.zip").IsChecked);   // 40 days old
        Assert.True(Row("notes.txt").IsChecked);     // 60 days old
        Assert.False(Row("folder.zip").IsChecked);   // 10 days old
    }

    // ── #2: Tray Auto Purge Toggle & Live RAM Tooltip ────────────────────

    private sealed class TrayFormatLoc : ILocalizationService
    {
        public Language CurrentLanguage => Language.English;
        public void SetLanguage(Language lang) { }
        public string GetString(string key) => key switch
        {
            "Str_Tray_TooltipFormat"    => "StandbyAndTimer\nTimer: {0}  •  Purges: {1}",
            "Str_Tray_TooltipRamFormat" => "Free: {0}  •  Standby: {1}",
            "Str_Tray_TimerStateOn"     => "ON",
            "Str_Tray_TimerStateOff"    => "OFF",
            "Str_Tray_AutoPurge"        => "Auto purge: {0}",
            "Str_Tray_On"               => "On",
            "Str_Tray_Off"              => "Off",
            _                           => key,
        };
        public event EventHandler? LanguageChanged { add { } remove { } }
    }

    [Fact]
    public void Tray_menu_includes_auto_purge_and_tooltip_includes_ram_summary()
    {
        var tray = new TrayIconService(new TrayFormatLoc());
        var menu = (WinForms.ContextMenuStrip)typeof(TrayIconService)
            .GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, null)!;
        bool toggled = false;
        tray.AutoPurgeToggleRequested += (_, _) => toggled = true;

        var state = new TrayState(true, 4, DeepPurge: false, GameMode: true, TimerLock: TimerLockMode.Locked,
                                  AutoPurge: true, FreeRamMb: 4096, StandbyRamMb: 2048);
        tray.Refresh(state);

        var autoItem = menu.Items.OfType<WinForms.ToolStripMenuItem>().Single(i => (i.Text ?? "").StartsWith("Auto purge:"));
        Assert.Equal("Auto purge: On", autoItem.Text);
        autoItem.PerformClick();
        Assert.True(toggled);

        string tooltip = tray.FormatTooltip(state);
        Assert.Contains("Free: 4.0 GB", tooltip.Replace(',', '.'));
        Assert.Contains("Standby: 2.0 GB", tooltip.Replace(',', '.'));
        Assert.True(tooltip.Length <= 127);

        menu.Dispose();
    }

    // ── #1: Steam & Epic Installed Games Scanner & Game Mode Actions ─────

    [Fact]
    public void Steam_scanner_finds_game_exe_and_skips_redistributables_and_crash_handlers()
    {
        string steamApps = Path.Combine(_tempDir, "steamapps");
        string common = Path.Combine(steamApps, "common");
        Directory.CreateDirectory(common);

        // 1. Valid game with a crash handler and the real game binary
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_730.acf"), """
            "AppState"
            {
                "appid"      "730"
                "name"       "Counter-Strike 2"
                "installdir" "Counter-Strike Global Offensive"
            }
            """);
        string csDir = Path.Combine(common, "Counter-Strike Global Offensive", "game", "bin", "win64");
        Directory.CreateDirectory(csDir);
        File.WriteAllBytes(Path.Combine(csDir, "UnityCrashHandler64.exe"), new byte[4096]);
        string csExe = Path.Combine(csDir, "cs2.exe");
        File.WriteAllBytes(csExe, new byte[2048]);

        // 2. Steamworks Common Redistributables (should be ignored)
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_228980.acf"), """
            "AppState"
            {
                "appid"      "228980"
                "name"       "Steamworks Common Redistributables"
                "installdir" "Steamworks Shared"
            }
            """);
        string redistDir = Path.Combine(common, "Steamworks Shared");
        Directory.CreateDirectory(redistDir);
        File.WriteAllBytes(Path.Combine(redistDir, "vc_redist.x64.exe"), new byte[8192]);

        var found = InstalledGamesScanner.ScanDirectories([steamApps], []);

        var game = Assert.Single(found);
        Assert.Equal("Counter-Strike 2", game.Name);
        Assert.Equal(csExe, game.ExecutablePath);
        Assert.Equal("Steam", game.Source);
    }

    [Fact]
    public void Epic_scanner_reads_item_manifests()
    {
        string manifestsDir = Path.Combine(_tempDir, "EpicManifests");
        string gameDir = Path.Combine(_tempDir, "EpicGames", "AlanWake2");
        Directory.CreateDirectory(manifestsDir);
        Directory.CreateDirectory(gameDir);

        string exePath = Path.Combine(gameDir, "AlanWake2.exe");
        File.WriteAllBytes(exePath, new byte[1024]);

        string itemPath = Path.Combine(manifestsDir, "aw2.item");
        File.WriteAllText(itemPath, $$"""
            {
                "DisplayName": "Alan Wake 2",
                "InstallLocation": {{System.Text.Json.JsonSerializer.Serialize(gameDir)}},
                "LaunchExecutable": "AlanWake2.exe",
                "bIsIncompleteInstall": false
            }
            """);

        var found = InstalledGamesScanner.ScanDirectories([], [manifestsDir]);

        var game = Assert.Single(found);
        Assert.Equal("Alan Wake 2", game.Name);
        Assert.Equal(exePath, game.ExecutablePath);
        Assert.Equal("Epic", game.Source);
    }

    [Fact]
    public async Task InstalledGames_dialog_marks_existing_and_adds_selected()
    {
        string exe1 = Path.Combine(_tempDir, "game1.exe");
        string exe2 = Path.Combine(_tempDir, "game2.exe");
        File.WriteAllBytes(exe1, [1]);
        File.WriteAllBytes(exe2, [2]);

        var h = new Harness();
        h.Main.AddGameFromPath(exe1);
        h.Main.ScanInstalledGames = _ => Task.FromResult<IReadOnlyList<InstalledGameCandidate>>(
        [
            new("Game One", exe1, "Steam"),
            new("Game Two", exe2, "Epic"),
        ]);

        await h.Main.OpenInstalledGamesCommand.ExecuteAsync(null);
        var dialog = h.Main.InstalledGamesDialog!;

        Assert.Equal(2, dialog.Items.Count);
        Assert.True(dialog.Items[0].AlreadyAdded);
        Assert.False(dialog.Items[0].CanSelect);
        Assert.False(dialog.Items[1].AlreadyAdded);
        Assert.True(dialog.Items[1].CanSelect);

        dialog.Items[1].IsChecked = true;
        Assert.True(dialog.AddSelectedCommand.CanExecute(null));
        dialog.AddSelectedCommand.Execute(null);

        Assert.Null(h.Main.InstalledGamesDialog);
        Assert.Equal(2, h.Main.Games.Count);
        Assert.Contains(h.Main.Games, g => g.ExecutablePath == exe2);
    }
}
