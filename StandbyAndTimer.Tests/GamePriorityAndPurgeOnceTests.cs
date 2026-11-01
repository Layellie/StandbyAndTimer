using System.Diagnostics;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests;

// The Game Mode strip's two menus: Priority (High / Above normal / Don't
// change) and Purge once (Off / At start / At exit / Both).
public class GamePriorityAndPurgeOnceTests
{
    private const string Exe = @"C:\Windows\System32\notepad.exe";
    private readonly FakeMemory _mem = new();
    private readonly Harness _h;

    public GamePriorityAndPurgeOnceTests()
    {
        _h = new Harness(memory: _mem);
        _h.Main.AddGameFromPath(Exe);
        _h.Main.SelectedGame = _h.Main.Games[0];
    }

    [Fact]
    public void A_new_game_gets_high_priority_and_no_purge_once()
    {
        Assert.Equal(GamePriority.High, _h.Main.SelectedGamePriority);
        Assert.Equal(PurgeOnceOption.Off, _h.Main.SelectedGamePurgeOnce);
        Assert.Empty(_mem.AboveNormalGamePaths);
    }

    [Fact]
    public void Above_normal_still_boosts_the_game_but_at_the_lower_class()
    {
        _h.Main.SelectedGamePriority = GamePriority.AboveNormal;

        Assert.Equal([Exe], _mem.GamePaths);              // cores + EcoQoS opt-out as before
        Assert.Equal([Exe], _mem.AboveNormalGamePaths);
        Assert.True(_h.Main.Games[0].HasCustomRules);
        Assert.Equal(GamePriority.AboveNormal, _h.LastSaved.Games[0].Priority);
    }

    [Fact]
    public void Dont_change_leaves_the_process_alone_like_the_old_unticked_box()
    {
        _h.Main.SelectedGamePriority = GamePriority.Unchanged;

        Assert.Empty(_mem.GamePaths);
        Assert.Empty(_mem.AboveNormalGamePaths);
        Assert.False(_h.LastSaved.Games[0].BoostPriority);
    }

    [Theory]
    [InlineData(PurgeOnceOption.AtStart, true, false)]
    [InlineData(PurgeOnceOption.AtExit, false, true)]
    [InlineData(PurgeOnceOption.Both, true, true)]
    [InlineData(PurgeOnceOption.Off, false, false)]
    public void Purge_once_sets_the_start_and_exit_switches(PurgeOnceOption option, bool atStart, bool atExit)
    {
        _h.Main.SelectedGamePurgeOnce = option;

        Assert.Equal(atStart, _h.Main.Games[0].PurgeOnStart);
        Assert.Equal(atExit, _h.Main.Games[0].PurgeOnExit);
        Assert.Equal(option, _h.Main.SelectedGamePurgeOnce);
        Assert.Equal(atExit, _h.LastSaved.Games[0].PurgeOnExit);
    }

    [Fact]
    public void The_boost_service_picks_the_class_per_game()
    {
        var aboveNormal = new HashSet<string>([Exe], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(ProcessPriorityClass.AboveNormal, ProcessOptimizationService.PriorityFor(Exe.ToUpperInvariant(), aboveNormal));
        Assert.Equal(ProcessPriorityClass.High, ProcessOptimizationService.PriorityFor(@"C:\Games\cs2.exe", aboveNormal));
        Assert.Equal(ProcessPriorityClass.High, ProcessOptimizationService.PriorityFor(Exe, null));
    }
}

public class PurgeOnExitTests
{
    private static (Harness h, FakePurge purge) WithGame(bool onStart, bool onExit)
    {
        var purge = new FakePurge();
        var settings = new AppSettings { FirstRunCompleted = true };
        settings.Games.Add(new GameEntry
        {
            DisplayName = "witcher3", ExecutablePath = @"C:\Games\witcher3.exe",
            PurgeOnStart = onStart, PurgeOnExit = onExit,
        });
        var h = new Harness(settings, purge: purge);
        foreach (var g in settings.Games) h.Main.Games.Add(g);
        return (h, purge);
    }

    [Fact]
    public async Task A_game_with_purge_at_exit_purges_once_when_it_closes()
    {
        var (h, purge) = WithGame(onStart: false, onExit: true);

        await h.Main.HandleRunningGamesAsync([@"C:\Games\witcher3.exe"]);
        Assert.Empty(purge.Triggers);                                   // not at start

        await h.Main.HandleRunningGamesAsync([]);
        await h.Main.HandleRunningGamesAsync([]);                       // still closed: no second purge

        Assert.Equal([PurgeTrigger.GameExit], purge.Triggers);
    }

    [Fact]
    public async Task Both_purges_at_start_and_at_exit()
    {
        var (h, purge) = WithGame(onStart: true, onExit: true);

        await h.Main.HandleRunningGamesAsync([@"C:\Games\witcher3.exe"]);
        await h.Main.HandleRunningGamesAsync([]);

        Assert.Equal([PurgeTrigger.GameStart, PurgeTrigger.GameExit], purge.Triggers);
    }

    [Fact]
    public async Task A_game_without_it_does_not_purge_when_it_closes()
    {
        var (h, purge) = WithGame(onStart: false, onExit: false);

        await h.Main.HandleRunningGamesAsync([@"C:\Games\witcher3.exe"]);
        await h.Main.HandleRunningGamesAsync([]);

        Assert.Empty(purge.Triggers);
    }

    [Fact]
    public void The_history_names_a_purge_at_game_exit()
    {
        var h = new Harness();

        string what = h.Main.DescribePurge(new PurgeResult(DateTime.Now, PurgeKind.Normal, PurgeTrigger.GameExit, 500));

        Assert.Equal("Str_PurgeKind_Normal, Str_PurgeTrigger_GameExit", what);
    }
}
