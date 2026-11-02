using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

// "Turn the timer off on battery": unplugging a laptop switches a running
// timer off (it keeps the CPU out of its sleep states), plugging in switches
// it back on. Same rules as the idle switch-off: never while a listed game
// runs, and a TIMER press on battery stays in charge until the power changes.
public class TimerOffOnBatteryTests
{
    private static async Task<Harness> LockedTimer(bool enabled = true, bool startOnBattery = false)
    {
        var h = new Harness(new AppSettings
        {
            TimerLock = TimerLockMode.Locked, FirstRunCompleted = true, TimerOffOnBattery = enabled,
        });
        h.Main.ReadOnBattery = () => startOnBattery;
        await h.Main.InitializeAsync();
        return h;
    }

    [Fact]
    public async Task Unplugging_turns_the_timer_off_and_plugging_in_turns_it_back_on()
    {
        var h = await LockedTimer();
        Assert.True(h.Main.TimerActive);

        await h.Main.ApplyBatteryStateAsync(onBattery: true, anyGameRunning: false);
        Assert.False(h.Main.TimerActive);
        Assert.Equal("Str_Status_TimerBatteryOff", h.Main.StatusMessage);

        await h.Main.ApplyBatteryStateAsync(onBattery: false, anyGameRunning: false);
        Assert.True(h.Main.TimerActive);
        Assert.Equal("Str_Status_TimerBatteryBack", h.Main.StatusMessage);
        Assert.Equal(2, h.Timer.Activations);    // start-up + plugged in
    }

    [Fact]
    public async Task With_the_setting_off_the_battery_changes_nothing()
    {
        var h = await LockedTimer(enabled: false);

        await h.Main.ApplyBatteryStateAsync(onBattery: true, anyGameRunning: false);

        Assert.True(h.Main.TimerActive);
        Assert.Equal(0, h.Timer.Deactivations);
    }

    [Fact]
    public async Task A_running_game_keeps_the_timer_on_even_on_battery()
    {
        var h = await LockedTimer();

        await h.Main.ApplyBatteryStateAsync(onBattery: true, anyGameRunning: true);

        Assert.True(h.Main.TimerActive);
    }

    [Fact]
    public async Task The_timer_goes_off_when_the_game_closes_on_battery_and_back_on_when_one_starts()
    {
        var h = await LockedTimer();
        await h.Main.ApplyBatteryStateAsync(onBattery: true, anyGameRunning: true);

        await h.Main.ApplyBatteryStateAsync(onBattery: true, anyGameRunning: false);   // game closed
        Assert.False(h.Main.TimerActive);

        await h.Main.ApplyBatteryStateAsync(onBattery: true, anyGameRunning: true);    // next game
        Assert.True(h.Main.TimerActive);
    }

    [Fact]
    public async Task A_timer_press_on_battery_stays_on_until_the_power_changes()
    {
        var h = await LockedTimer();
        await h.Main.ApplyBatteryStateAsync(onBattery: true, anyGameRunning: false);
        Assert.False(h.Main.TimerActive);

        await h.Main.ToggleTimerResolutionCommand.ExecuteAsync(null);                  // user: on
        await h.Main.ApplyBatteryStateAsync(onBattery: true, anyGameRunning: false);   // next tick
        Assert.True(h.Main.TimerActive);

        await h.Main.ApplyBatteryStateAsync(onBattery: false, anyGameRunning: false);  // plugged in...
        await h.Main.ApplyBatteryStateAsync(onBattery: true, anyGameRunning: false);   // ...and out again
        Assert.False(h.Main.TimerActive);                                              // the setting is back in charge
    }

    [Fact]
    public async Task A_timer_the_user_switched_off_is_not_switched_on_by_plugging_in()
    {
        var h = await LockedTimer();
        await h.Main.ApplyBatteryStateAsync(onBattery: true, anyGameRunning: false);   // paused by the battery
        await h.Main.ToggleTimerResolutionCommand.ExecuteAsync(null);                  // user: on
        await h.Main.ToggleTimerResolutionCommand.ExecuteAsync(null);                  // user: off again

        await h.Main.ApplyBatteryStateAsync(onBattery: false, anyGameRunning: false);

        Assert.False(h.Main.TimerActive);
    }

    [Fact]
    public async Task Starting_on_battery_with_the_lock_leaves_the_timer_off_until_plugged_in()
    {
        var h = await LockedTimer(startOnBattery: true);

        Assert.False(h.Main.TimerActive);

        await h.Main.ApplyBatteryStateAsync(onBattery: false, anyGameRunning: false);
        Assert.True(h.Main.TimerActive);
    }

    [Fact]
    public async Task A_battery_pause_is_saved_as_a_timer_that_is_still_wanted()
    {
        var h = await LockedTimer();
        await h.Main.ApplyBatteryStateAsync(onBattery: true, anyGameRunning: false);

        h.Main.Settings.TimerOffOnBattery = true;   // any setting change saves

        Assert.True(h.LastSaved.TimerOffOnBattery);
        Assert.True(h.LastSaved.TimerResolutionActive);   // paused, not off
    }

    [Theory]
    [InlineData(0, 1, true)]       // AC offline, battery present
    [InlineData(1, 1, false)]      // plugged in
    [InlineData(255, 1, false)]    // unknown: do not guess
    [InlineData(0, 128, false)]    // no battery (a desktop): never "on battery"
    [InlineData(1, 128, false)]
    public void Windows_power_status_maps_to_on_battery(byte acLine, byte batteryFlag, bool expected)
    {
        Assert.Equal(expected, PowerSource.OnBatteryFrom(acLine, batteryFlag));
    }
}
