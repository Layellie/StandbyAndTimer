using System.ComponentModel;
using System.Diagnostics;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

// Anti-cheat protected games refuse the handle the boost needs. The monitor
// ticks every second: such a process is tried once, not once a second for
// the whole session.
public class ProcessBoostDeniedTests
{
    private static readonly int Self = Environment.ProcessId;
    private const string Exe = @"C:\Games\Protected\game.exe";

    private static IReadOnlyList<int> PidsOf(string name) => [Self];

    [Fact]
    public async Task A_process_that_denies_access_is_tried_once()
    {
        int attempts = 0;
        var svc = new ProcessOptimizationService
        {
            Apply = (_, _, _) => { attempts++; throw new Win32Exception(5); },
        };

        for (int tick = 0; tick < 5; tick++)
            await svc.CheckAndOptimizeAsync([Exe], PidsOf, _ => true);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_boosted_process_is_not_touched_again()
    {
        int attempts = 0;
        var svc = new ProcessOptimizationService { Apply = (_, _, _) => attempts++ };

        for (int tick = 0; tick < 5; tick++)
            await svc.CheckAndOptimizeAsync([Exe], PidsOf, _ => true);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_denied_pid_is_forgotten_when_the_process_exits_and_after_Clear()
    {
        int attempts = 0;
        var svc = new ProcessOptimizationService
        {
            Apply = (_, _, _) => { attempts++; throw new Win32Exception(5); },
        };

        await svc.CheckAndOptimizeAsync([Exe], PidsOf, _ => true);
        await svc.CheckAndOptimizeAsync([], PidsOf, _ => false);   // the snapshot no longer has the PID
        await svc.CheckAndOptimizeAsync([Exe], PidsOf, _ => true); // a new process with a reused PID
        Assert.Equal(2, attempts);

        svc.Clear();
        await svc.CheckAndOptimizeAsync([Exe], PidsOf, _ => true);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task The_chosen_priority_reaches_the_process_step()
    {
        ProcessPriorityClass? seen = null;
        var svc = new ProcessOptimizationService { Apply = (_, priority, _) => seen = priority };

        await svc.CheckAndOptimizeAsync([Exe], PidsOf, _ => true, aboveNormalPaths: [Exe]);

        Assert.Equal(ProcessPriorityClass.AboveNormal, seen);
    }
}
