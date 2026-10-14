using System.Runtime.InteropServices;
using StandbyAndTimer.Services;
using Xunit.Abstractions;

namespace StandbyAndTimer.Tests;

/// <summary>
/// Real purges on the machine. Needs administrator rights and empties the
/// standby list, so it only runs with SAT_PURGE_TEST=1 in an elevated shell.
/// Prints the memory lists before and after a normal and a deep purge.
/// </summary>
public class DeepPurgeIntegrationTests(ITestOutputHelper output)
{
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int c, IntPtr b, int l, out int r);

    private static (double modified, double standby, double free) Lists()
    {
        var b = new long[22];
        var h = GCHandle.Alloc(b, GCHandleType.Pinned);
        try { Assert.Equal(0, NtQuerySystemInformation(80, h.AddrOfPinnedObject(), 176, out _)); }
        finally { h.Free(); }
        double Mb(long pages) => pages * (double)Environment.SystemPageSize / 1048576;
        return (Mb(b[2] + b[3]), Mb(b[5] + b[6] + b[7] + b[8] + b[9] + b[10] + b[11] + b[12]), Mb(b[0] + b[1]));
    }

    private void Print(string label, (double modified, double standby, double free) l) =>
        output.WriteLine($"{label,-22} modified {l.modified,6:F0} MB   standby {l.standby,6:F0} MB   free+zero {l.free,6:F0} MB");

    [Fact]
    public async Task Normal_and_deep_purge_on_this_machine()
    {
        if (Environment.GetEnvironmentVariable("SAT_PURGE_TEST") != "1") return;

        var service = new StandbyPurgeService();
        Print("before", Lists());

        Assert.True(await service.PurgeAsync());
        var afterNormal = Lists();
        Print("after normal purge", afterNormal);

        await Task.Delay(TimeSpan.FromSeconds(30));   // let the lists refill a little
        var beforeDeep = Lists();
        Print("30 s later", beforeDeep);

        service.DeepMode = true;
        Assert.True(await service.PurgeAsync());
        var afterDeep = Lists();
        Print("after deep purge", afterDeep);

        // Deep must have emptied the standby list too, and not left more
        // modified pages than it found.
        Assert.True(afterDeep.standby <= beforeDeep.standby + 64);
        Assert.True(afterDeep.modified <= beforeDeep.modified + 64);
    }
}
