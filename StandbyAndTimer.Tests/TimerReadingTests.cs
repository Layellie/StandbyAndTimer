using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Tests;

// The timer card shows Windows' real timer resolution. While the app's own
// timer is off that is whatever Windows runs at (15.62 ms, or what another
// program asked for), never 0.00 or a stale 0.50.
public class TimerReadingTests
{
    [Fact]
    public async Task Starting_with_the_timer_off_shows_windows_resolution_not_zero()
    {
        var h = new Harness(new AppSettings { TimerLock = TimerLockMode.InGame, FirstRunCompleted = true });
        h.Timer.CurrentMs = 15.625;

        await h.Main.InitializeAsync();

        Assert.False(h.Main.TimerActive);
        Assert.Equal(15.625, h.Main.ActualTimerMs);
    }

    [Fact]
    public async Task The_last_game_exiting_shows_windows_resolution_again()
    {
        var h = new Harness(new AppSettings { TimerLock = TimerLockMode.InGame, FirstRunCompleted = true });
        await h.Main.InitializeAsync();
        await h.Main.ApplyGameStateAsync(anyGameRunning: true);
        Assert.Equal(0.5, h.Main.ActualTimerMs);

        h.Timer.CurrentMs = 1.0;                       // e.g. a browser holds 1 ms
        await h.Main.ApplyGameStateAsync(anyGameRunning: false);

        Assert.False(h.Main.TimerActive);
        Assert.Equal(1.0, h.Main.ActualTimerMs);
    }

    [Fact]
    public async Task Turning_the_timer_off_by_hand_shows_windows_resolution()
    {
        var h = new Harness();
        await h.Main.InitializeAsync();
        await h.Main.ToggleTimerResolutionCommand.ExecuteAsync(null);   // on
        h.Timer.CurrentMs = 15.625;

        await h.Main.ToggleTimerResolutionCommand.ExecuteAsync(null);   // off

        Assert.Equal(15.625, h.Main.ActualTimerMs);
    }
}
