using System.Diagnostics;

namespace StandbyAndTimer.Tests;

// The app used to put its whole process at High priority at start-up. Only
// the timer watchdog needs priority, and that thread has its own (MMCSS);
// the rest (window, monitor loop, a purge's kernel work) must not run ahead
// of a game.
public class OwnPriorityTests
{
    [Fact]
    public async Task Starting_up_leaves_the_apps_own_priority_alone()
    {
        using var self = Process.GetCurrentProcess();
        self.PriorityClass = ProcessPriorityClass.Normal;

        var h = new Harness();
        await h.Main.InitializeAsync();

        self.Refresh();
        Assert.Equal(ProcessPriorityClass.Normal, self.PriorityClass);
    }
}
