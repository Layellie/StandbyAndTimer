using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Tests;

public class TimerLockModeTests
{
    [Fact]
    public async Task Lock_cycles_unlocked_locked_in_game_and_persists_each_step()
    {
        var h = new Harness();
        await h.Main.InitializeAsync();

        await h.Main.ToggleTimerLockCommand.ExecuteAsync(null);
        Assert.Equal(TimerLockMode.Locked, h.Main.TimerLock);
        Assert.Equal(TimerLockMode.Locked, h.LastSaved.TimerLock);

        await h.Main.ToggleTimerLockCommand.ExecuteAsync(null);
        Assert.Equal(TimerLockMode.InGame, h.Main.TimerLock);
        Assert.Equal(TimerLockMode.InGame, h.LastSaved.TimerLock);

        await h.Main.ToggleTimerLockCommand.ExecuteAsync(null);
        Assert.Equal(TimerLockMode.Unlocked, h.Main.TimerLock);
        Assert.Equal(TimerLockMode.Unlocked, h.LastSaved.TimerLock);
    }

    [Fact]
    public async Task Legacy_locked_flag_loads_as_locked()
    {
        var h = new Harness(new AppSettings { TimerLocked = true, FirstRunCompleted = true });

        await h.Main.InitializeAsync();

        Assert.Equal(TimerLockMode.Locked, h.Main.TimerLock);
        Assert.True(h.Main.TimerActive);
    }

    [Fact]
    public async Task In_game_mode_follows_games_starting_and_stopping()
    {
        var h = new Harness(new AppSettings { TimerLock = TimerLockMode.InGame, FirstRunCompleted = true });
        await h.Main.InitializeAsync();
        Assert.False(h.Main.TimerActive);

        await h.Main.ApplyGameStateAsync(anyGameRunning: true);
        Assert.True(h.Main.TimerActive);

        await h.Main.ApplyGameStateAsync(anyGameRunning: true);   // still running: no second activation
        Assert.Equal(1, h.Timer.Activations);

        await h.Main.ApplyGameStateAsync(anyGameRunning: false);
        Assert.False(h.Main.TimerActive);
    }

    [Fact]
    public async Task In_game_mode_leaves_a_timer_the_user_turned_on_by_hand()
    {
        var h = new Harness(new AppSettings { TimerLock = TimerLockMode.InGame, FirstRunCompleted = true });
        await h.Main.InitializeAsync();

        await h.Main.ToggleTimerResolutionCommand.ExecuteAsync(null);   // user: on
        await h.Main.ApplyGameStateAsync(true);
        await h.Main.ApplyGameStateAsync(false);

        Assert.True(h.Main.TimerActive);
    }

    [Fact]
    public async Task Game_state_is_ignored_outside_in_game_mode()
    {
        var h = new Harness();
        await h.Main.InitializeAsync();

        await h.Main.ApplyGameStateAsync(true);

        Assert.False(h.Main.TimerActive);
    }
}

public class AutoPurgeOnlyInGamesTests
{
    [Fact]
    public void Only_in_games_is_pushed_to_the_monitor_and_persisted()
    {
        var mem = new FakeMemory();
        var h = new Harness(memory: mem);

        h.Main.AutoPurgeOnlyInGames = true;

        Assert.True(mem.AutoPurgeOnlyInGames);
        Assert.True(h.LastSaved.AutoPurgeOnlyInGames);
    }

    [Fact]
    public void Only_in_games_and_only_when_idle_exclude_each_other()
    {
        var h = new Harness();

        h.Main.AutoPurgeIdleOnly = true;
        h.Main.AutoPurgeOnlyInGames = true;
        Assert.False(h.Main.AutoPurgeIdleOnly);

        h.Main.AutoPurgeIdleOnly = true;
        Assert.False(h.Main.AutoPurgeOnlyInGames);
    }

    [Fact]
    public void Decision_waits_for_a_game_when_only_in_games_is_on()
    {
        var low = new MemorySnapshot(16_000, 500, 6_000);

        Assert.False(Services.MemoryMonitorService.ShouldAutoPurge(true, 4096, 1024, low, true, false, onlyInGames: true, anyGameRunning: false));
        Assert.True(Services.MemoryMonitorService.ShouldAutoPurge(true, 4096, 1024, low, true, false, onlyInGames: true, anyGameRunning: true));
    }
}
