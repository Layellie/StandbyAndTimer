using System.Diagnostics;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

public class ProcessSnapshotTests
{
    [Fact]
    public void The_snapshot_reader_sees_the_same_processes_as_Process_GetProcesses()
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses()) using (p) expected.Add(p.ProcessName);
        expected.Remove("Idle");   // Process names PID 0; the snapshot leaves it unnamed

        var names = RunningGames.Take().Names;

        // Processes start and exit between the two snapshots, so compare the
        // stable part: everything but a handful of names must agree.
        int missing = expected.Count(n => !names.Contains(n));
        Assert.True(missing <= 3, $"missing {missing}: {string.Join(", ", expected.Where(n => !names.Contains(n)).Take(5))}");
        Assert.Contains(Process.GetCurrentProcess().ProcessName, names);
    }

    [Fact]
    public void Repeated_reads_reuse_the_buffer_and_stay_correct()
    {
        string self = Process.GetCurrentProcess().ProcessName;
        for (int i = 0; i < 20; i++)
            Assert.Contains(self, RunningGames.Take().Names);
    }
}
