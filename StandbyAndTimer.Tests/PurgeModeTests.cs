using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

// Records the mode the view model pushes; never calls NtSetSystemInformation.
public sealed class FakePurge : IStandbyPurgeService
{
    public bool LightMode { get; set; }
    public int Purges { get; private set; }
    public event EventHandler? PurgeSucceeded { add { } remove { } }
    public Task<bool> PurgeAsync() { Purges++; return Task.FromResult(true); }
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
}
