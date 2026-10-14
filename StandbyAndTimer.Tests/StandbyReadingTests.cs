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
            double fromCounters = counters.Sum(c => (double)c.NextValue());

            Assert.True(MemoryMonitorService.TryReadStandbyBytes(new long[22], out long native));

            // Both are live readings a moment apart: allow 2 % (or 64 MB).
            double tolerance = Math.Max(64 * 1024 * 1024, fromCounters * 0.02);
            Assert.InRange(native, fromCounters - tolerance, fromCounters + tolerance);
            Assert.True(native > 0);
        }
        finally
        {
            foreach (var c in counters) c.Dispose();
        }
    }

    [Fact]
    public void A_too_small_buffer_is_refused_not_overrun() =>
        Assert.False(MemoryMonitorService.TryReadStandbyBytes(new long[5], out _));
}
