using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;

namespace StandbyAndTimer.Tests.Maintenance;

public class RecycleBinTargetTests
{
    // Reads the real Recycle Bin size: proves the struct layout (a wrong
    // cbSize makes SHQueryRecycleBin fail with E_INVALIDARG). Never cleans.
    [Fact]
    public async Task Scanning_the_real_bin_works()
    {
        var scan = await new RecycleBinTarget().ScanAsync(default);

        Assert.True(scan.Found);
        Assert.True(scan.Bytes >= 0 && scan.Files >= 0);
        Assert.Equal(CleanupTargetIds.RecycleBin, new RecycleBinTarget().Id);
    }

    [Fact]
    public async Task StaTask_runs_on_an_sta_thread_and_returns_the_value()
    {
        var apartment = await StaTask.Run(() => Thread.CurrentThread.GetApartmentState());
        await Assert.ThrowsAsync<InvalidOperationException>(() => StaTask.Run<int>(() => throw new InvalidOperationException()));

        Assert.Equal(ApartmentState.STA, apartment);
    }
}
