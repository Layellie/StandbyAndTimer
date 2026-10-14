using StandbyAndTimer.Core.Models;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests;

public class TimerIdleOffTests
{
    private static readonly TimeSpan Active = TimeSpan.FromSeconds(2);

    private static async Task<Harness> LockedWithIdleOff(int minutes = 10, bool enabled = true)
    {
        var h = new Harness(new AppSettings
        {
            TimerLock = TimerLockMode.Locked, FirstRunCompleted = true,
            TimerIdleOffEnabled = enabled, TimerIdleOffMinutes = minutes,
        });
        await h.Main.InitializeAsync();
        Assert.True(h.Main.TimerActive);
        return h;
    }

    [Fact]
    public async Task Idle_past_the_limit_turns_the_timer_off_and_input_turns_it_back_on()
    {
        var h = await LockedWithIdleOff(10);

        await h.Main.ApplyIdleStateAsync(TimeSpan.FromMinutes(10), anyGameRunning: false);
        Assert.False(h.Main.TimerActive);
        Assert.Equal(1, h.Timer.Deactivations);
        Assert.Equal("Str_Status_TimerIdleOff", h.Main.StatusMessage);

        await h.Main.ApplyIdleStateAsync(Active, anyGameRunning: false);
        Assert.True(h.Main.TimerActive);
        Assert.Equal(2, h.Timer.Activations);
        Assert.Equal("Str_Status_TimerIdleBack", h.Main.StatusMessage);
    }

    [Fact]
    public async Task Below_the_limit_nothing_happens()
    {
        var h = await LockedWithIdleOff(10);

        await h.Main.ApplyIdleStateAsync(TimeSpan.FromMinutes(9.9), anyGameRunning: false);

        Assert.True(h.Main.TimerActive);
        Assert.Equal(0, h.Timer.Deactivations);
    }

    [Fact]
    public async Task Switched_off_setting_never_turns_the_timer_off()
    {
        var h = await LockedWithIdleOff(enabled: false);

        await h.Main.ApplyIdleStateAsync(TimeSpan.FromHours(3), anyGameRunning: false);

        Assert.True(h.Main.TimerActive);
    }

    [Fact]
    public async Task A_running_game_keeps_the_timer_on_gamepad_input_is_not_seen()
    {
        var h = await LockedWithIdleOff(5);

        await h.Main.ApplyIdleStateAsync(TimeSpan.FromMinutes(30), anyGameRunning: true);

        Assert.True(h.Main.TimerActive);
    }

    [Fact]
    public async Task A_timer_the_user_switched_off_is_not_switched_on_by_input()
    {
        var h = await LockedWithIdleOff(10);
        await h.Main.ToggleTimerResolutionCommand.ExecuteAsync(null);   // user: off

        await h.Main.ApplyIdleStateAsync(TimeSpan.FromMinutes(20), anyGameRunning: false);
        await h.Main.ApplyIdleStateAsync(Active, anyGameRunning: false);

        Assert.False(h.Main.TimerActive);
        Assert.Equal(1, h.Timer.Activations);
    }

    [Fact]
    public async Task Timer_press_while_paused_hands_control_back_to_the_user()
    {
        var h = await LockedWithIdleOff(10);
        await h.Main.ApplyIdleStateAsync(TimeSpan.FromMinutes(10), anyGameRunning: false);

        await h.Main.ToggleTimerResolutionCommand.ExecuteAsync(null);   // user: on
        await h.Main.ToggleTimerResolutionCommand.ExecuteAsync(null);   // user: off
        await h.Main.ApplyIdleStateAsync(Active, anyGameRunning: false);

        Assert.False(h.Main.TimerActive);
    }

    [Fact]
    public async Task Exiting_while_paused_saves_the_timer_as_on()
    {
        var h = await LockedWithIdleOff(10);
        await h.Main.ApplyIdleStateAsync(TimeSpan.FromMinutes(10), anyGameRunning: false);

        Assert.True(h.Main.BuildCurrentSettings().TimerResolutionActive);
    }

    [Fact]
    public async Task Disabling_the_setting_while_paused_turns_the_timer_back_on()
    {
        var h = await LockedWithIdleOff(10);
        await h.Main.ApplyIdleStateAsync(TimeSpan.FromMinutes(10), anyGameRunning: false);

        h.Main.Settings.SelectedIdleOffOption = 0;
        await h.Main.ApplyIdleStateAsync(TimeSpan.FromMinutes(10), anyGameRunning: false);

        Assert.True(h.Main.TimerActive);
    }

    [Theory]
    [InlineData(false, 10, 0)]
    [InlineData(true, 5, 5)]
    [InlineData(true, 15, 15)]
    [InlineData(true, 25, SettingsViewModel.IdleOffCustom)]
    public void Picker_reflects_the_stored_values(bool enabled, int minutes, int expected) =>
        Assert.Equal(expected, SettingsViewModel.IdleOffOptionFor(enabled, minutes));

    [Fact]
    public async Task Picking_a_preset_saves_enabled_and_minutes()
    {
        var h = await LockedWithIdleOff(enabled: false);

        h.Main.Settings.SelectedIdleOffOption = 15;

        Assert.True(h.LastSaved.TimerIdleOffEnabled);
        Assert.Equal(15, h.LastSaved.TimerIdleOffMinutes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100_000, SettingsViewModel.IdleOffMaxMinutes)]
    [InlineData(42, 42)]
    public async Task Custom_minutes_are_clamped(int typed, int expected)
    {
        var h = await LockedWithIdleOff();
        h.Main.Settings.SelectedIdleOffOption = SettingsViewModel.IdleOffCustom;

        h.Main.Settings.TimerIdleOffMinutes = typed;

        Assert.Equal(expected, h.Main.Settings.TimerIdleOffMinutes);
        Assert.Equal(expected, h.LastSaved.TimerIdleOffMinutes);
    }

    [Fact]
    public async Task Settings_round_trip_keeps_custom_minutes()
    {
        var h = await LockedWithIdleOff(25);

        Assert.Equal(SettingsViewModel.IdleOffCustom, h.Main.Settings.SelectedIdleOffOption);
        Assert.True(h.Main.Settings.IsIdleOffCustom);
        Assert.Equal(25, h.Main.BuildCurrentSettings().TimerIdleOffMinutes);
    }
}
