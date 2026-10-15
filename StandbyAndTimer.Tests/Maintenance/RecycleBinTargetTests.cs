using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;
using StandbyAndTimer.Services.Native;

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

    // ── Clean logic, with the shell calls replaced (never touches the real bin) ──

    private sealed class FakeBin(params (long Bytes, long Items)[] states)
    {
        private int _query;
        public int Empties { get; private set; }
        public int EmptyResult { get; set; }
        public NativeMethods.SHQUERYRBINFO Query()
        {
            var (bytes, items) = states[Math.Min(_query++, states.Length - 1)];
            return new NativeMethods.SHQUERYRBINFO { i64Size = bytes, i64NumItems = items };
        }
        public int Empty() { Empties++; return EmptyResult; }
        public RecycleBinTarget Target() => new(Query, Empty);
    }

    [Fact]
    public async Task Cleaning_empties_the_bin_and_reports_what_went()
    {
        var bin = new FakeBin((1000, 5), (0, 0));

        var result = await bin.Target().CleanAsync(default);

        Assert.Equal(1, bin.Empties);
        Assert.Equal(new CleanupResult(1000, 5, 0), result);
    }

    [Fact]
    public async Task Items_still_there_after_emptying_count_as_skipped()
    {
        var bin = new FakeBin((1000, 5), (200, 1));

        var result = await bin.Target().CleanAsync(default);

        Assert.Equal(new CleanupResult(800, 4, 1), result);
    }

    [Fact]
    public async Task An_empty_bin_is_not_touched()
    {
        var bin = new FakeBin((0, 0));

        var result = await bin.Target().CleanAsync(default);

        Assert.Equal(0, bin.Empties);
        Assert.Equal(CleanupResult.Nothing, result);
    }

    [Fact]
    public async Task A_failed_empty_throws_so_the_row_shows_the_error()
    {
        var bin = new FakeBin((1000, 5)) { EmptyResult = unchecked((int)0x80004005) };   // E_FAIL

        await Assert.ThrowsAsync<System.Runtime.InteropServices.COMException>(() => bin.Target().CleanAsync(default));
    }
}
