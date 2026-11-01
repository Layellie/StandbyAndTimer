using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Tests;

// "Timer on while it runs" per game: the IN GAME lock turns the timer on only
// for listed games that want it. Default on, so lists from before keep working.
public class PerGameTimerTests
{
    private const string Wants = @"C:\Games\shooter.exe";
    private const string Skips = @"C:\Games\strategy.exe";

    private static Harness Make() => new(new AppSettings
    {
        TimerLock = TimerLockMode.InGame,
        FirstRunCompleted = true,
        Games =
        [
            new GameEntry { DisplayName = "shooter", ExecutablePath = Wants },
            new GameEntry { DisplayName = "strategy", ExecutablePath = Skips, TimerInGame = false },
        ],
    });

    [Fact]
    public async Task A_game_that_skips_the_timer_does_not_turn_it_on()
    {
        var h = Make();
        await h.Main.InitializeAsync();

        await h.Main.HandleRunningGamesAsync([Skips]);

        Assert.False(h.Main.TimerActive);
    }

    [Fact]
    public async Task The_timer_follows_the_games_that_want_it()
    {
        var h = Make();
        await h.Main.InitializeAsync();
        await h.Main.HandleRunningGamesAsync([Skips]);

        await h.Main.HandleRunningGamesAsync([Skips, Wants]);   // a game that wants it starts too
        Assert.True(h.Main.TimerActive);

        await h.Main.HandleRunningGamesAsync([Skips]);          // it exits, the other keeps running
        Assert.False(h.Main.TimerActive);
    }

    [Fact]
    public async Task Turning_it_off_for_a_running_game_turns_the_timer_off()
    {
        var h = Make();
        await h.Main.InitializeAsync();
        await h.Main.HandleRunningGamesAsync([Wants]);
        Assert.True(h.Main.TimerActive);

        h.Main.SelectedGame = h.Main.Games[0];
        h.Main.SelectedGameTimerInGame = false;
        await h.Main.HandleRunningGamesAsync([Wants]);          // next monitor tick

        Assert.False(h.Main.TimerActive);
        Assert.False(h.LastSaved.Games[0].TimerInGame);         // saved at once
        Assert.True(h.Main.Games[0].HasCustomRules);            // the row shows its custom marker
    }
}
