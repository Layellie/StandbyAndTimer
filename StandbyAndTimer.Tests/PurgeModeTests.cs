using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

// Records the mode the view model pushes; never calls NtSetSystemInformation.
public sealed class FakePurge : IStandbyPurgeService
{
    public bool LightMode { get; set; }
    public bool DeepMode  { get; set; }
    public int Purges { get; private set; }
    public List<PurgeTrigger> Triggers { get; } = [];
    public event EventHandler<PurgeResult>? PurgeSucceeded { add { } remove { } }
    public Task<bool> PurgeAsync(PurgeTrigger trigger = PurgeTrigger.Manual) { Purges++; Triggers.Add(trigger); return Task.FromResult(true); }
}

public class PurgeModeTests
{
    [Fact]
    public void Full_purge_uses_the_standby_list_command_and_light_the_low_priority_one()
    {
        Assert.Equal(4, StandbyPurgeService.CommandFor(lightMode: false)); // MemoryPurgeStandbyList
        Assert.Equal(5, StandbyPurgeService.CommandFor(lightMode: true));  // MemoryPurgeLowPriorityStandbyList
    }

    [Fact]
    public void Light_purge_setting_is_pushed_to_the_service_and_persisted()
    {
        var purge = new FakePurge();
        var h = new Harness(purge: purge);

        h.Main.LightPurge = true;

        Assert.True(purge.LightMode);
        Assert.True(h.LastSaved.LightPurge);
    }

    [Fact]
    public async Task Saved_light_purge_is_applied_on_start()
    {
        var purge = new FakePurge();
        var h = new Harness(new AppSettings { LightPurge = true, FirstRunCompleted = true }, purge: purge);

        await h.Main.InitializeAsync();

        Assert.True(h.Main.LightPurge);
        Assert.True(purge.LightMode);
    }

    [Fact]
    public void Deep_purge_moves_the_modified_list_and_system_cache_to_standby_before_purging_it()
    {
        Assert.Equal(
            [StandbyPurgeService.Step.FlushModifiedList, StandbyPurgeService.Step.TrimSystemFileCache, StandbyPurgeService.Step.PurgeStandby],
            StandbyPurgeService.PlanFor(lightMode: false, deepMode: true));
        Assert.Equal([StandbyPurgeService.Step.PurgeStandby], StandbyPurgeService.PlanFor(false, false));
        Assert.Equal([StandbyPurgeService.Step.PurgeLowPriorityStandby], StandbyPurgeService.PlanFor(true, false));
        // Both set (an old import, a hand-edited key): deep wins.
        Assert.Equal(StandbyPurgeService.PlanFor(false, true), StandbyPurgeService.PlanFor(true, true));
    }

    [Fact]
    public void Deep_and_light_exclude_each_other_and_reach_the_service()
    {
        var purge = new FakePurge();
        var h = new Harness(purge: purge);

        h.Main.LightPurge = true;
        h.Main.DeepPurge  = true;
        Assert.False(h.Main.LightPurge);
        Assert.True(purge.DeepMode);
        Assert.False(purge.LightMode);
        Assert.True(h.LastSaved.DeepPurge);
        Assert.False(h.LastSaved.LightPurge);

        h.Main.LightPurge = true;
        Assert.False(h.Main.DeepPurge);
        Assert.False(purge.DeepMode);
    }

    [Fact]
    public async Task Saved_deep_purge_is_applied_at_start_and_wins_over_light()
    {
        var purge = new FakePurge();
        var h = new Harness(new AppSettings { DeepPurge = true, LightPurge = true, FirstRunCompleted = true }, purge: purge);

        await h.Main.InitializeAsync();

        Assert.True(h.Main.DeepPurge);
        Assert.False(h.Main.LightPurge);
        Assert.True(purge.DeepMode);
    }
}
