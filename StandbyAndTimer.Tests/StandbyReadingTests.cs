using System.Diagnostics;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

public class StandbyReadingTests
{
    [Fact]
    public void Native_standby_matches_the_performance_counters()
    {
        var counters = new[] { "Standby Cache Normal Priority Bytes", "Standby Cache Reserve Bytes", "Standby Cache Core Bytes" }
            .Select(n => new PerformanceCounter("Memory", n, null)).ToArray();
        try
        {
            foreach (var c in counters) c.NextValue();
            double fromCountersMb = counters.Sum(c => (double)c.NextValue()) / (1024 * 1024);

            Assert.True(MemoryLists.TryRead(out var lists));

            // Both are live readings a moment apart: allow 2 % (or 64 MB).
            double tolerance = Math.Max(64, fromCountersMb * 0.02);
            Assert.InRange(lists.StandbyMb, fromCountersMb - tolerance, fromCountersMb + tolerance);
            Assert.True(lists.StandbyMb > 0);
        }
        finally
        {
            foreach (var c in counters) c.Dispose();
        }
    }

    [Fact]
    public void The_lists_are_consistent()
    {
        Assert.True(MemoryLists.TryRead(out var lists));

        Assert.Equal(8, lists.StandbyByPriorityMb.Length);
        Assert.Equal(lists.StandbyByPriorityMb[0], lists.StandbyLowMb);
        Assert.True(lists.StandbyLowMb <= lists.StandbyMb);
        Assert.True(lists.ModifiedMb >= 0 && lists.FreeAndZeroMb >= 0);
    }
}
