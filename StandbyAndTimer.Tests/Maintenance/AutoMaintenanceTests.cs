using System.IO;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests.Maintenance;

// Auto maintenance: one button next to Rescan opens a dialog with a "safe"
// section (nothing there is lost: ticked) and a "review" section (the user
// decides: unticked). Shader caches are in neither: a game would rebuild them
// on its next start. Optionally the safe section also runs by itself the
// first time the app starts after the chosen number of days; nothing runs in
// the background.
public class AutoMaintenanceTests
{
    private static readonly DateTime Today = new(2026, 11, 2, 20, 0, 0);
    private static CleanupScan Mb(long mb) => new(true, mb * 1024 * 1024, 10);

    private static readonly string[] SafeIds =
        [CleanupTargetIds.UserTemp, CleanupTargetIds.WindowsTemp, CleanupTargetIds.WindowsLogs,
         CleanupTargetIds.CrashDumps, CleanupTargetIds.DeliveryOptimization, CleanupTargetIds.WindowsUpdate];
    private static readonly string[] ReviewIds =
        [CleanupTargetIds.RecycleBin, CleanupTargetIds.WindowsOld, CleanupTargetIds.DriverLeftovers, CleanupTargetIds.SystemCrashDumps];
    private static readonly string[] OtherIds =
        [CleanupTargetIds.NvidiaShader, CleanupTargetIds.SteamShader, CleanupTargetIds.DirectXShader];

    private static (MaintenanceViewModel vm, Dictionary<string, FakeTarget> t, InMemoryCleanupStatsStore store) Make()
    {
        var t = SafeIds.Concat(ReviewIds).Concat(OtherIds).ToDictionary(id => id, id => new FakeTarget(id, Mb(100)));
        var store = new InMemoryCleanupStatsStore();
        var vm = new MaintenanceViewModel(t.Values.ToArray(), store, NullProxy.Of<ISystemInfoService>(), new FakeTools(),
                                          NullProxy.Of<IDownloadsService>(), new FakeLocalization())
        { Now = () => Today, ResultDelay = (_, ct) => Task.Delay(Timeout.Infinite, ct) };
        return (vm, t, store);
    }

    // ── The dialog ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_dialog_lists_safe_items_ticked_and_review_items_unticked_without_shader_caches()
    {
        var (vm, _, _) = Make();

        await vm.OpenAutoCommand.ExecuteAsync(null);

        Assert.True(vm.IsAutoVisible);
        Assert.Equal(SafeIds, vm.AutoSafeItems.Select(i => i.Id));
        Assert.All(vm.AutoSafeItems, i => Assert.True(i.IsChecked));
        Assert.Equal(ReviewIds, vm.AutoReviewItems.Select(i => i.Id));
        Assert.All(vm.AutoReviewItems, i => Assert.False(i.IsChecked));
        Assert.DoesNotContain(vm.AutoSafeItems.Concat(vm.AutoReviewItems), i => OtherIds.Contains(i.Id));
    }

    [Fact]
    public async Task The_total_follows_the_ticks()
    {
        var (vm, _, _) = Make();
        await vm.OpenAutoCommand.ExecuteAsync(null);
        Assert.Equal(6 * 100L * 1024 * 1024, vm.AutoSelectedBytes);

        vm.AutoReviewItems[0].IsChecked = true;
        vm.AutoSafeItems[0].IsChecked = false;

        Assert.Equal(6 * 100L * 1024 * 1024, vm.AutoSelectedBytes);   // one in, one out
        vm.AutoSafeItems[1].IsChecked = false;
        Assert.Equal(5 * 100L * 1024 * 1024, vm.AutoSelectedBytes);
    }

    [Fact]
    public async Task Cleaning_from_the_dialog_cleans_only_the_ticked_items_and_records_them()
    {
        var (vm, t, store) = Make();
        await vm.OpenAutoCommand.ExecuteAsync(null);
        vm.AutoSafeItems.Single(i => i.Id == CleanupTargetIds.WindowsLogs).IsChecked = false;
        vm.AutoReviewItems.Single(i => i.Id == CleanupTargetIds.WindowsOld).IsChecked = true;

        await vm.CleanAutoCommand.ExecuteAsync(null);

        Assert.Equal(1, t[CleanupTargetIds.UserTemp].Cleaned);
        Assert.Equal(1, t[CleanupTargetIds.WindowsOld].Cleaned);          // the user ticked it
        Assert.Equal(0, t[CleanupTargetIds.WindowsLogs].Cleaned);         // the user unticked it
        Assert.Equal(0, t[CleanupTargetIds.RecycleBin].Cleaned);          // never ticked
        Assert.All(OtherIds, id => Assert.Equal(0, t[id].Cleaned));       // not part of it at all
        Assert.Equal(6, store.Load().Recent.Count);                        // 5 safe + Windows.old
    }

    [Fact]
    public async Task The_dialog_does_not_change_the_main_cards_ticks()
    {
        var (vm, _, _) = Make();
        vm.Initialize(CleanupTargetIds.UserTemp);
        await vm.OpenAutoCommand.ExecuteAsync(null);

        vm.AutoReviewItems[0].IsChecked = true;
        vm.AutoSafeItems[2].IsChecked = false;

        Assert.Equal(CleanupTargetIds.UserTemp, vm.SelectionText);
    }

    // ── Running by itself ─────────────────────────────────────────────────

    private static async Task<(MaintenanceViewModel vm, Dictionary<string, FakeTarget> t, int changes)> Armed(
        DateTime lastRun, int days = 7, bool enabled = true, bool gameRunning = false)
    {
        var (vm, t, _) = Make();
        vm.InitializeAuto(enabled, days, lastRun.ToUniversalTime());
        vm.AnyGameRunning = () => gameRunning;
        int changes = 0;
        vm.SettingsChanged += (_, _) => changes++;
        await vm.RunAutoIfDueAsync();
        return (vm, t, changes);
    }

    [Fact]
    public async Task When_due_only_the_safe_items_are_cleaned_and_the_run_is_remembered()
    {
        var (vm, t, changes) = await Armed(Today.AddDays(-8));

        Assert.All(SafeIds, id => Assert.Equal(1, t[id].Cleaned));
        Assert.All(ReviewIds.Concat(OtherIds), id => Assert.Equal(0, t[id].Cleaned));
        Assert.Equal(Today, vm.AutoCleanLastRun);
        Assert.True(changes > 0);   // saved
    }

    [Theory]
    [InlineData(false, 8, false)]   // switched off
    [InlineData(true, 3, false)]    // 3 of 7 days
    [InlineData(true, 8, true)]     // a game runs: wait for the next start
    public async Task Otherwise_nothing_is_cleaned_and_the_last_run_stays(bool enabled, int daysAgo, bool gameRunning)
    {
        var last = Today.AddDays(-daysAgo);
        var (vm, t, _) = await Armed(last, enabled: enabled, gameRunning: gameRunning);

        Assert.All(t.Values, target => Assert.Equal(0, target.Cleaned));
        Assert.Equal(last, vm.AutoCleanLastRun);
    }

    [Theory]
    [InlineData("2026-10-26 23:50", "2026-11-02 00:10", true)]    // 7 calendar days, 6 days 20 min of time
    [InlineData("2026-10-26 08:00", "2026-11-01 23:59", false)]   // the day before
    [InlineData("2026-11-01 23:59", "2026-11-02 00:01", true)]    // daily: the next calendar day
    public void Due_is_counted_in_calendar_days(string last, string now, bool due)
    {
        int days = last.StartsWith("2026-11-01") ? 1 : 7;

        Assert.Equal(due, MaintenanceViewModel.IsAutoDue(true, days, DateTime.Parse(last), DateTime.Parse(now)));
    }

    [Fact]
    public void Never_run_before_is_not_due_until_the_interval_has_passed_since_it_was_switched_on()
    {
        Assert.False(MaintenanceViewModel.IsAutoDue(true, 7, null, Today));
    }

    [Fact]
    public void Switching_it_on_starts_the_interval_today_and_saves()
    {
        var (vm, _, _) = Make();
        vm.InitializeAuto(false, 7, null);
        int changes = 0;
        vm.SettingsChanged += (_, _) => changes++;

        vm.AutoCleanEnabled = true;

        Assert.Equal(Today, vm.AutoCleanLastRun);
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task One_failing_target_does_not_stop_the_others()
    {
        var (vm, t, _) = Make();
        t[CleanupTargetIds.UserTemp].Throw = new IOException("locked");
        vm.InitializeAuto(true, 7, Today.AddDays(-8).ToUniversalTime());

        await vm.RunAutoIfDueAsync();

        Assert.Equal(1, t[CleanupTargetIds.WindowsUpdate].Cleaned);
        Assert.Equal(Today, vm.AutoCleanLastRun);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(30, 30)]
    [InlineData(0, 7)]      // out of the menu: the default
    [InlineData(-3, 7)]
    [InlineData(5000, 7)]
    public void The_interval_is_one_of_the_menu_choices(int stored, int expected)
    {
        var (vm, _, _) = Make();

        vm.InitializeAuto(true, stored, null);

        Assert.Equal(expected, vm.AutoCleanIntervalDays);
    }

    // ── Persistence and start-up ──────────────────────────────────────────

    private static (MainViewModel main, MaintenanceViewModel m, FakeSettingsStore store) MakeMain(AppSettings? settings = null)
    {
        var store = new FakeSettingsStore(settings);
        var loc = new FakeLocalization();
        var s = new SettingsViewModel(NullProxy.Of<IAutoStartService>(), loc, store, NullProxy.Of<IUpdateService>(),
                                      NullProxy.Of<IThemeService>(), NullProxy.Of<ILogTailReader>());
        var targets = SafeIds.Select(id => new FakeTarget(id, Mb(100))).ToArray();
        var m = new MaintenanceViewModel(targets, new InMemoryCleanupStatsStore(), NullProxy.Of<ISystemInfoService>(),
                                         new FakeTools(), NullProxy.Of<IDownloadsService>(), loc) { Now = () => Today };
        var main = new MainViewModel(new FakeTimer(), NullProxy.Of<IStandbyPurgeService>(), NullProxy.Of<IMemoryMonitorService>(),
                                     store, loc, NullProxy.Of<IGameDetectionService>(), s, maintenance: m);
        return (main, m, store);
    }

    [Fact]
    public void Changing_the_settings_saves_them()
    {
        var (_, m, store) = MakeMain();
        m.InitializeAuto(false, 7, null);

        m.AutoCleanEnabled = true;
        m.AutoCleanIntervalDays = 14;

        var saved = store.Saved[^1];
        Assert.True(saved.AutoCleanEnabled);
        Assert.Equal(14, saved.AutoCleanIntervalDays);
        Assert.Equal(Today.ToUniversalTime(), saved.AutoCleanLastRunUtc);
    }

    [Fact]
    public async Task Saved_settings_are_applied_on_load()
    {
        var last = Today.AddDays(-2).ToUniversalTime();
        var (main, m, _) = MakeMain(new AppSettings { FirstRunCompleted = true, AutoCleanEnabled = true,
                                                      AutoCleanIntervalDays = 14, AutoCleanLastRunUtc = last });
        main.AutoMaintenanceDelay = (_, ct) => Task.Delay(Timeout.Infinite, ct);   // not due: nothing to wait for anyway

        await main.InitializeAsync();

        Assert.True(m.AutoCleanEnabled);
        Assert.Equal(14, m.AutoCleanIntervalDays);
        Assert.Equal(Today.AddDays(-2), m.AutoCleanLastRun);
    }

    [Fact]
    public async Task Start_up_runs_the_safe_cleanup_once_when_it_is_due()
    {
        var (main, m, _) = MakeMain(new AppSettings
        {
            FirstRunCompleted = true, AutoCleanEnabled = true, AutoCleanIntervalDays = 7,
            AutoCleanLastRunUtc = Today.AddDays(-8).ToUniversalTime(),
        });
        main.AutoMaintenanceDelay = (_, _) => Task.CompletedTask;

        await main.InitializeAsync();
        await main.AutoMaintenanceTask!;

        Assert.Equal(Today, m.AutoCleanLastRun);
        Assert.All(m.Rows, r => Assert.Equal(1, ((FakeTarget)r.Target).Cleaned));
    }

    [Fact]
    public async Task An_unattended_run_says_so_with_a_notification()
    {
        var (main, _, _) = MakeMain(new AppSettings
        {
            FirstRunCompleted = true, AutoCleanEnabled = true, AutoCleanIntervalDays = 7,
            AutoCleanLastRunUtc = Today.AddDays(-8).ToUniversalTime(),
        });
        main.AutoMaintenanceDelay = (_, _) => Task.CompletedTask;
        var bodies = new List<string>();
        main.AutoMaintenanceNotification += (_, body) => bodies.Add(body);

        await main.InitializeAsync();
        await main.AutoMaintenanceTask!;

        Assert.Equal(["Str_Tray_NotifyAutoCleanBody"], bodies);   // FakeLocalization returns the key
    }

    [Fact]
    public async Task The_notification_can_be_switched_off_but_the_cleanup_still_runs()
    {
        var (main, m, _) = MakeMain(new AppSettings
        {
            FirstRunCompleted = true, AutoCleanEnabled = true, AutoCleanIntervalDays = 7,
            AutoCleanLastRunUtc = Today.AddDays(-8).ToUniversalTime(), NotifyOnAutoClean = false,
        });
        main.AutoMaintenanceDelay = (_, _) => Task.CompletedTask;
        var bodies = new List<string>();
        main.AutoMaintenanceNotification += (_, body) => bodies.Add(body);

        await main.InitializeAsync();
        await main.AutoMaintenanceTask!;

        Assert.Empty(bodies);
        Assert.Equal(Today, m.AutoCleanLastRun);                                   // it did run
        Assert.All(m.Rows, r => Assert.Equal(1, ((FakeTarget)r.Target).Cleaned));  // and cleaned
    }

    [Fact]
    public void The_notification_switch_is_saved()
    {
        var (main, _, store) = MakeMain();

        main.Settings.NotifyOnAutoClean = false;

        Assert.False(store.Saved[^1].NotifyOnAutoClean);
    }

    [Fact]
    public async Task Start_up_does_nothing_when_it_is_switched_off()
    {
        var (main, m, _) = MakeMain(new AppSettings { FirstRunCompleted = true });
        main.AutoMaintenanceDelay = (_, _) => Task.CompletedTask;

        await main.InitializeAsync();
        if (main.AutoMaintenanceTask is { } task) await task;

        Assert.All(m.Rows, r => Assert.Equal(0, ((FakeTarget)r.Target).Cleaned));
    }

    [Fact]
    public async Task An_import_keeps_this_machines_last_run()
    {
        var (main, m, store) = MakeMain(new AppSettings
        {
            FirstRunCompleted = true, AutoCleanEnabled = true,
            AutoCleanLastRunUtc = Today.AddDays(-2).ToUniversalTime(),
        });
        main.AutoMaintenanceDelay = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        await main.InitializeAsync();

        main.Settings.ApplyImportedSettings(new AppSettings
        {
            FirstRunCompleted = true, AutoCleanEnabled = true,
            AutoCleanLastRunUtc = Today.AddDays(-40).ToUniversalTime(),
        });

        Assert.Equal(Today.AddDays(-2), m.AutoCleanLastRun);
    }
}
