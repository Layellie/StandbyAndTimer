using System.IO;
using System.Reflection;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;
using StandbyAndTimer.Services.Tray;
using StandbyAndTimer.ViewModels;
using WinForms = System.Windows.Forms;

namespace StandbyAndTimer.Tests;

public class PurgeStatsTests
{
    private static PurgeResult R(DateTime at, long mb, PurgeKind kind = PurgeKind.Normal) => new(at, kind, PurgeTrigger.Manual, mb);
    private static readonly DateTime Today = new(2026, 10, 14, 21, 0, 0);

    [Fact]
    public void Totals_by_day_week_kind_and_all_time()
    {
        var s = new PurgeStats();
        s.Add(R(Today.AddDays(-10), 4000));
        s.Add(R(Today.AddDays(-3), 1000, PurgeKind.Deep));
        s.Add(R(Today.AddHours(-2), 500, PurgeKind.Light));
        s.Add(R(Today, 1500));

        Assert.Equal(2, s.LastDays(1, Today).Count);
        Assert.Equal(2000, s.LastDays(1, Today).FreedMb);
        Assert.Equal(3000, s.LastDays(7, Today).FreedMb);
        Assert.Equal(7000, s.AllTime.FreedMb);
        Assert.Equal(4, s.AllTime.Count);
        Assert.Equal(5500, s.Of(PurgeKind.Normal).FreedMb);
        Assert.Equal(1, s.Of(PurgeKind.Deep).Count);
        Assert.Equal(Today.AddDays(-10), s.Since);
    }

    [Fact]
    public void Keeps_the_last_twenty_and_sixty_days_but_all_time_totals()
    {
        var s = new PurgeStats();
        for (int i = 100; i >= 0; i--) s.Add(R(Today.AddDays(-i), 10));

        Assert.Equal(PurgeStats.RecentMax, s.Recent.Count);
        Assert.Equal(Today, s.Recent[0].At);
        Assert.Equal(PurgeStats.KeepDays, s.Days.Count);
        Assert.Equal(101, s.AllTime.Count);
    }
}

public sealed class PurgeStatsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sat-history-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Round_trip_through_the_file()
    {
        string path = Path.Combine(_dir, "purge-history.json");
        var stats = new PurgeStats();
        stats.Add(new PurgeResult(new DateTime(2026, 10, 14, 21, 5, 9), PurgeKind.Deep, PurgeTrigger.GameStart, 1843));

        new PurgeStatsStore(path).Save(stats);
        var back = new PurgeStatsStore(path).Load();

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(stats.Recent[0], back.Recent[0]);
        Assert.Equal(1843, back.AllTime.FreedMb);
        Assert.Equal(1, back.Of(PurgeKind.Deep).Count);
        Assert.Contains("\"Deep\"", File.ReadAllText(path));   // enums as names, readable
    }

    [Fact]
    public void A_damaged_or_missing_file_starts_a_fresh_history()
    {
        string path = Path.Combine(_dir, "purge-history.json");
        Assert.Empty(new PurgeStatsStore(path).Load().Recent);

        File.WriteAllText(path, "{ not json");
        Assert.Equal(0, new PurgeStatsStore(path).Load().AllTime.Count);
    }
}

public class PurgeHistoryViewModelTests
{
    private static PurgeResult R(DateTime at, long mb) => new(at, PurgeKind.Normal, PurgeTrigger.Manual, mb);

    [Fact]
    public void History_survives_a_restart_and_shows_totals()
    {
        var store = new InMemoryPurgeStatsStore();
        var first = new Harness(purgeStats: store);
        first.Main.Now = () => new DateTime(2026, 10, 14, 22, 0, 0);
        first.Main.RecordPurge(R(new DateTime(2026, 10, 13, 20, 0, 0), 1024));
        first.Main.RecordPurge(R(new DateTime(2026, 10, 14, 21, 0, 0), 512));

        var second = new Harness(purgeStats: store);   // the app started again
        second.Main.Now = () => new DateTime(2026, 10, 14, 22, 0, 0);

        Assert.Equal(2, second.Main.PurgeHistory.Count);
        Assert.Equal(512, second.Main.PurgeHistory[0].Result.FreedMb);
        Assert.Equal("512 MB", second.Main.StatsTodayFreed);
        Assert.Equal("1.5 GB", second.Main.StatsWeekFreed.Replace(',', '.'));
        Assert.Equal(0, second.Main.PurgeCount);   // the card counts this session only
    }

    [Fact]
    public void The_week_chart_has_seven_days_scaled_to_the_busiest()
    {
        var h = new Harness();
        var today = new DateTime(2026, 10, 14, 22, 0, 0);
        h.Main.Now = () => today;
        h.Main.RecordPurge(R(today.AddDays(-9), 9000));   // older than 7 days: not shown
        h.Main.RecordPurge(R(today.AddDays(-6), 2000));
        h.Main.RecordPurge(R(today.AddDays(-2), 4000));
        h.Main.RecordPurge(R(today.AddDays(-2).AddHours(-1), 4000));
        h.Main.RecordPurge(R(today, 10));

        var bars = h.Main.WeekBars;

        Assert.Equal(7, bars.Count);
        Assert.True(bars[^1].IsToday);
        Assert.Single(bars, b => b.IsToday);
        Assert.Equal(MainViewModel.WeekBarMaxHeight, bars[4].Height);       // 8000 MB, the busiest
        Assert.Equal(MainViewModel.WeekBarMaxHeight / 4, bars[0].Height);   // 2000 MB
        Assert.Equal(0, bars[1].Height);                                    // no purge
        Assert.Equal(3, bars[6].Height);                                    // 10 MB: still visible
    }

    [Theory]
    [InlineData(Language.English, "Wed", "13 Oct 20:00")]
    [InlineData(Language.Turkish, "Çar", "13 Eki 20:00")]
    public void Dates_follow_the_app_language_not_windows(Language lang, string today, string row)
    {
        var h = new Harness();
        h.Loc.CurrentLanguage = lang;
        h.Main.Now = () => new DateTime(2026, 10, 14, 22, 0, 0);   // a Wednesday
        h.Main.RecordPurge(R(new DateTime(2026, 10, 13, 20, 0, 0), 100));

        Assert.Equal(today, h.Main.WeekBars[^1].Day);
        Assert.Equal(row, h.Main.PurgeHistory[0].Time);
    }

    [Fact]
    public void Reset_needs_a_second_press()
    {
        var store = new InMemoryPurgeStatsStore();
        var h = new Harness(purgeStats: store);
        h.Main.RecordPurge(R(DateTime.Now, 100));

        h.Main.ResetPurgeHistoryCommand.Execute(null);
        Assert.True(h.Main.IsResetArmed);
        Assert.Single(h.Main.PurgeHistory);

        h.Main.ResetPurgeHistoryCommand.Execute(null);
        Assert.False(h.Main.IsResetArmed);
        Assert.Empty(h.Main.PurgeHistory);
        Assert.Equal(0, store.Load().AllTime.Count);
    }

    [Fact]
    public void Closing_disarms_the_reset()
    {
        var h = new Harness();
        h.Main.ResetPurgeHistoryCommand.Execute(null);

        h.Main.ClosePurgeHistoryCommand.Execute(null);

        Assert.False(h.Main.IsResetArmed);
    }
}

public class TrayQuickSettingsTests
{
    // Returns "{0}"-style formats so the state words show in the labels.
    private sealed class FormatLoc : ILocalizationService
    {
        public Language CurrentLanguage => Language.English;
        public void SetLanguage(Language lang) { }
        public string GetString(string key) => key switch
        {
            "Str_Tray_DeepPurge" => "Deep {0}",
            "Str_Tray_GameMode"  => "Game {0}",
            "Str_Tray_TimerLock" => "Lock {0}",
            _                    => key,
        };
        public event EventHandler? LanguageChanged { add { } remove { } }
    }

    private static List<string> Labels(TrayState state)
    {
        var tray = new TrayIconService(new FormatLoc());
        var menu = (WinForms.ContextMenuStrip)typeof(TrayIconService)
            .GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, null)!;
        tray.Refresh(state);
        var labels = menu.Items.OfType<WinForms.ToolStripMenuItem>().Select(i => i.Text ?? "").ToList();
        menu.Dispose();
        return labels;
    }

    [Fact]
    public void The_menu_shows_deep_purge_game_mode_and_the_lock_state()
    {
        var on  = Labels(new TrayState(true, 3, DeepPurge: true, GameMode: true, TimerLock: TimerLockMode.InGame));
        var off = Labels(new TrayState(false, 0));

        Assert.Contains("Deep Str_Tray_On", on);
        Assert.Contains("Game Str_Tray_On", on);
        Assert.Contains("Lock Str_Card_TimerInGame", on);
        Assert.Contains("Str_Tray_TimerDisable", on);
        Assert.Contains("Deep Str_Tray_Off", off);
        Assert.Contains("Lock Str_Card_TimerUnlocked", off);
    }

    [Fact]
    public void A_language_change_keeps_the_timer_item_state()
    {
        var tray = new TrayIconService(new FormatLoc());
        var menu = (WinForms.ContextMenuStrip)typeof(TrayIconService)
            .GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, null)!;
        tray.Refresh(new TrayState(TimerActive: true, PurgeCount: 0));

        tray.RefreshLabels();   // what a language change calls

        Assert.Contains(menu.Items.OfType<WinForms.ToolStripMenuItem>(), i => i.Text == "Str_Tray_TimerDisable");
        menu.Dispose();
    }
}

public class GameLaunchTests
{
    [Fact]
    public void A_listed_game_starts_through_the_launcher()
    {
        var h = new Harness();
        string? started = null;
        h.Main.StartGame = p => { started = p; return true; };
        string exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        h.Main.LaunchGame(new GameEntry { DisplayName = "notepad", ExecutablePath = exe });

        Assert.Equal(exe, started);
        Assert.Equal("Str_Status_GameLaunching", h.Main.StatusMessage);
    }

    [Fact]
    public void A_missing_file_is_reported_not_started()
    {
        var h = new Harness();
        bool called = false;
        h.Main.StartGame = _ => called = true;

        h.Main.LaunchGame(new GameEntry { DisplayName = "gone", ExecutablePath = @"C:\nope\gone.exe" });

        Assert.False(called);
        Assert.Equal("Str_Status_GameMissing", h.Main.StatusMessage);
    }

    [Fact]
    public void A_failed_start_says_so()
    {
        var h = new Harness();
        h.Main.StartGame = _ => false;

        h.Main.LaunchGame(new GameEntry { DisplayName = "notepad", ExecutablePath = Path.Combine(Environment.SystemDirectory, "notepad.exe") });

        Assert.Equal("Str_Status_GameLaunchFailed", h.Main.StatusMessage);
    }
}
