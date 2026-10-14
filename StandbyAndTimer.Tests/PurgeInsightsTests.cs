using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests;

public class PurgeInsightsTests
{
    private static MemoryListInfo Lists(long freeAndZero) => new(freeAndZero, 0, 0, new long[8]);

    private static PurgeResult Result(long freed, PurgeKind kind = PurgeKind.Normal, PurgeTrigger trigger = PurgeTrigger.Manual) =>
        new(new DateTime(2026, 10, 14, 21, 5, 9), kind, trigger, freed);

    // ── 1. Result and history ─────────────────────────────────────────────

    [Fact]
    public void Freed_is_free_memory_gained_and_never_negative()
    {
        Assert.Equal(1800, StandbyPurgeService.FreedMb(Lists(3000), Lists(4800)));
        Assert.Equal(0, StandbyPurgeService.FreedMb(Lists(3000), Lists(2900)));   // something allocated meanwhile
        Assert.Equal(0, StandbyPurgeService.FreedMb(null, Lists(4800)));          // could not read before
    }

    [Theory]
    [InlineData(false, false, PurgeKind.Normal)]
    [InlineData(true,  false, PurgeKind.Light)]
    [InlineData(false, true,  PurgeKind.Deep)]
    [InlineData(true,  true,  PurgeKind.Deep)]
    public void Kind_follows_the_modes(bool light, bool deep, PurgeKind expected) =>
        Assert.Equal(expected, StandbyPurgeService.KindFor(light, deep));

    [Fact]
    public void A_purge_is_counted_shown_and_kept_newest_first()
    {
        var h = new Harness();
        h.Main.Now = () => new DateTime(2026, 10, 14, 22, 0, 0);   // the purges below ran "today"
        PurgeNotice? notice = null;
        h.Main.PurgeNotification += (_, n) => notice = n;

        h.Main.RecordPurge(Result(500));
        h.Main.RecordPurge(Result(1843, PurgeKind.Deep, PurgeTrigger.GameStart));

        Assert.Equal(2, h.Main.PurgeCount);
        Assert.Equal("Str_Status_PurgeFreed", h.Main.StatusMessage);
        Assert.Equal(1843, h.Main.PurgeHistory[0].Result.FreedMb);
        Assert.Equal("Str_PurgeKind_Deep, Str_PurgeTrigger_GameStart", h.Main.PurgeHistory[0].What);
        Assert.Equal("21:05:09", h.Main.PurgeHistory[0].Time);
        Assert.Equal(2, notice!.TotalPurges);
    }

    [Fact]
    public void History_keeps_the_last_twenty()
    {
        var h = new Harness();
        for (int i = 1; i <= 25; i++) h.Main.RecordPurge(Result(i));

        Assert.Equal(MainViewModel.PurgeHistoryMax, h.Main.PurgeHistory.Count);
        Assert.Equal(25, h.Main.PurgeHistory[0].Result.FreedMb);
        Assert.Equal(6, h.Main.PurgeHistory[^1].Result.FreedMb);
        Assert.Equal(25, h.Main.PurgeCount);
    }

    [Theory]
    [InlineData(0L, "0 MB")]
    [InlineData(350L, "350 MB")]
    [InlineData(1023L, "1023 MB")]
    [InlineData(1843L, "1.8 GB")]
    public void Sizes_read_naturally(long mb, string expected)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { Assert.Equal(expected, MainViewModel.FormatSize(mb)); }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void A_manual_purge_is_marked_manual()
    {
        var purge = new FakePurge();
        var h = new Harness(purge: purge);

        h.Main.ManualPurgeCommand.Execute(null);

        Assert.Equal([PurgeTrigger.Manual], purge.Triggers);
    }

    // ── 2. Purge once when a game starts ──────────────────────────────────

    private static (Harness h, FakePurge purge) WithGames(params (string name, bool purgeOnStart)[] games)
    {
        var purge = new FakePurge();
        var settings = new AppSettings { FirstRunCompleted = true };
        foreach (var (name, purgeOnStart) in games)
            settings.Games.Add(new GameEntry { DisplayName = name, ExecutablePath = $@"C:\Games\{name}.exe", PurgeOnStart = purgeOnStart });
        var h = new Harness(settings, purge: purge);
        foreach (var g in settings.Games) h.Main.Games.Add(g);
        return (h, purge);
    }

    [Fact]
    public async Task A_game_with_purge_on_start_triggers_one_purge_when_it_starts()
    {
        var (h, purge) = WithGames(("cs2", true));

        await h.Main.HandleRunningGamesAsync([@"C:\Games\cs2.exe"]);
        await h.Main.HandleRunningGamesAsync([@"C:\Games\cs2.exe"]);   // still running: no second purge

        Assert.Equal([PurgeTrigger.GameStart], purge.Triggers);
    }

    [Fact]
    public async Task Two_such_games_starting_together_purge_once()
    {
        var (h, purge) = WithGames(("cs2", true), ("witcher3", true));

        await h.Main.HandleRunningGamesAsync([@"C:\Games\cs2.exe", @"C:\Games\witcher3.exe"]);

        Assert.Single(purge.Triggers);
    }

    [Fact]
    public async Task Games_without_the_option_and_restarts_behave()
    {
        var (h, purge) = WithGames(("cs2", false), ("witcher3", true));

        await h.Main.HandleRunningGamesAsync([@"C:\Games\cs2.exe"]);
        Assert.Empty(purge.Triggers);

        await h.Main.HandleRunningGamesAsync([@"C:\Games\witcher3.exe"]);   // witcher3 starts
        await h.Main.HandleRunningGamesAsync([]);                             // exits
        await h.Main.HandleRunningGamesAsync([@"C:\Games\witcher3.exe"]);   // starts again
        Assert.Equal(2, purge.Triggers.Count);
    }

    [Fact]
    public void The_switch_is_saved_and_marks_the_game_as_custom()
    {
        var (h, _) = WithGames(("cs2", false));
        h.Main.SelectedGame = h.Main.Games[0];

        h.Main.SelectedGamePurgeOnStart = true;

        Assert.True(h.Main.Games[0].PurgeOnStart);
        Assert.True(h.Main.Games[0].HasCustomRules);
        Assert.True(h.LastSaved.Games[0].PurgeOnStart);
    }

    // ── 3. What a purge would free, in the selected mode ──────────────────

    [Fact]
    public void Clearable_follows_the_selected_purge_mode()
    {
        var h = new Harness();
        h.Main.ShowSnapshot(new MemorySnapshot(16_000, 800, 2_900) { StandbyLowMb = 2, ModifiedMb = 120 });

        Assert.Equal(2_900, h.Main.ClearableMb);
        Assert.Equal("Str_Card_StandbyClearable", h.Main.ClearableLabel);

        h.Main.LightPurge = true;
        Assert.Equal(2, h.Main.ClearableMb);
        Assert.Equal("MB", h.Main.ClearableUnit);
        Assert.Equal("Str_Card_ClearableLight", h.Main.ClearableLabel);

        h.Main.DeepPurge = true;
        Assert.Equal(3_020, h.Main.ClearableMb);
        Assert.Equal("GB", h.Main.ClearableUnit);
        Assert.Equal("Str_Card_ClearableDeep", h.Main.ClearableLabel);
    }
}
