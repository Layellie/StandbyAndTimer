using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Tests;

public class TimerLockTests
{
    [Fact]
    public async Task Unlocked_start_keeps_the_timer_off_even_if_last_session_left_it_on()
    {
        var h = new Harness(new AppSettings { TimerResolutionActive = true, FirstRunCompleted = true });

        await h.Main.InitializeAsync();

        Assert.False(h.Main.TimerActive);
        Assert.Equal(0, h.Timer.Activations);
        Assert.Equal("Str_Status_Ready", h.Main.StatusMessage);
    }

    [Fact]
    public async Task Locked_start_turns_the_timer_on_and_shows_it_in_the_status()
    {
        var h = new Harness(new AppSettings { TimerLocked = true, FirstRunCompleted = true });

        await h.Main.InitializeAsync();

        Assert.True(h.Main.TimerActive);
        Assert.Equal(1, h.Timer.Activations);
        Assert.Equal("Str_Status_TimerActive", h.Main.StatusMessage);
    }

    [Fact]
    public async Task Timer_button_turns_it_off_while_the_lock_stays()
    {
        var h = new Harness(new AppSettings { TimerLocked = true, FirstRunCompleted = true });
        await h.Main.InitializeAsync();

        await h.Main.ToggleTimerResolutionCommand.ExecuteAsync(null);

        Assert.False(h.Main.TimerActive);
        Assert.True(h.LastSaved.TimerLocked);
    }

    [Fact]
    public async Task Locking_turns_the_timer_on_now()
    {
        var h = new Harness();
        await h.Main.InitializeAsync();

        await h.Main.ToggleTimerLockCommand.ExecuteAsync(null);

        Assert.True(h.Main.TimerActive);
        Assert.True(h.LastSaved.TimerLocked);
    }

    [Fact]
    public async Task Import_does_not_show_the_timer_active_without_activating_it()
    {
        var h = new Harness();
        await h.Main.InitializeAsync();

        h.Settings.ApplyImportedSettings(new AppSettings { TimerResolutionActive = true });

        Assert.False(h.Main.TimerActive);
        Assert.Equal(0, h.Timer.Activations);
    }
}
