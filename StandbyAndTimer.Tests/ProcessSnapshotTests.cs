using System.Diagnostics;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

public class ProcessSnapshotTests
{
    [Fact]
    public void The_snapshot_reader_sees_the_same_processes_as_Process_GetProcesses()
    {
        static HashSet<string> Managed()
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses()) using (p) s.Add(p.ProcessName);
            s.Remove("Idle");   // Process names PID 0; the snapshot leaves it unnamed
            return s;
        }

        // Processes start and exit all the time (a test run once overlapped
        // a script launching short-lived ones), so only names present both
        // before and after the snapshot must be in it.
        var before = Managed();
        var names  = RunningGames.Take().Names;
        var stable = Managed();
        stable.IntersectWith(before);

        var missing = stable.Where(n => !names.Contains(n)).ToList();
        Assert.True(missing.Count == 0, $"missing: {string.Join(", ", missing.Take(5))}");
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
