using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

// What runs once a second for as long as the app is open, also in the tray.
public class TickCostTests
{
    [Fact]
    public void The_monitors_memory_reading_allocates_nothing()
    {
        Assert.True(MemoryLists.TryReadTotals(out _, out _, out _));   // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) MemoryLists.TryReadTotals(out _, out _, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void The_monitors_memory_reading_gives_the_same_numbers_as_the_full_one()
    {
        Assert.True(MemoryLists.TryRead(out var lists));
        Assert.True(MemoryLists.TryReadTotals(out long standby, out long low, out long modified));

        // Two live readings a moment apart.
        Assert.InRange(standby, lists.StandbyMb - 64, lists.StandbyMb + 64);
        Assert.InRange(low, lists.StandbyLowMb - 64, lists.StandbyLowMb + 64);
        Assert.InRange(modified, lists.ModifiedMb - 64, lists.ModifiedMb + 64);
    }

    private static MemorySnapshot Snapshot(long standby) =>
        new(16_000, 800, standby) { StandbyLowMb = 2, ModifiedMb = 120 };

    [Fact]
    public void A_tick_with_the_same_numbers_redraws_nothing_on_the_purge_card()
    {
        var h = new Harness();
        h.Main.ShowSnapshot(Snapshot(2_900));
        var raised = new List<string?>();
        h.Main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.Main.ShowSnapshot(Snapshot(2_900));
        h.Main.ShowSnapshot(Snapshot(2_900));

        Assert.Empty(raised);
    }

    [Fact]
    public void A_tick_with_new_numbers_updates_the_value_and_the_tooltip_but_not_the_label()
    {
        var h = new Harness();
        h.Main.ShowSnapshot(Snapshot(2_900));
        var raised = new List<string?>();
        h.Main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.Main.ShowSnapshot(Snapshot(3_100));

        Assert.Contains(nameof(h.Main.ClearableMb), raised);
        Assert.Single(raised, nameof(h.Main.ClearableTip));
        Assert.DoesNotContain(nameof(h.Main.ClearableLabel), raised);
        Assert.Equal(3_100, h.Main.ClearableMb);
    }

    [Fact]
    public void Switching_the_purge_mode_still_updates_label_value_and_tooltip()
    {
        var h = new Harness();
        h.Main.ShowSnapshot(Snapshot(2_900));
        var raised = new List<string?>();
        h.Main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.Main.DeepPurge = true;

        Assert.Contains(nameof(h.Main.ClearableLabel), raised);
        Assert.Contains(nameof(h.Main.ClearableTip), raised);
        Assert.Equal(3_020, h.Main.ClearableMb);
    }
}
