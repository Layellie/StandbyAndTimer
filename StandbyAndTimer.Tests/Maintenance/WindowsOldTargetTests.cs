using System.IO;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;

namespace StandbyAndTimer.Tests.Maintenance;

public sealed class WindowsOldTargetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sat-wold-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Old => Path.Combine(_dir, "Windows.old");

    [Fact]
    public async Task Disk_cleanup_removes_it_and_the_freed_size_is_before_minus_after()
    {
        Directory.CreateDirectory(Path.Combine(Old, "Windows"));
        File.WriteAllBytes(Path.Combine(Old, "Windows", "a.bin"), new byte[400]);
        File.WriteAllBytes(Path.Combine(Old, "b.bin"), new byte[100]);
        int runs = 0;
        // Stands in for cleanmgr: removes the folder's content.
        var t = new WindowsOldTarget(Old, () => { runs++; Directory.Delete(Path.Combine(Old, "Windows"), true); return true; });

        var scan = await t.ScanAsync(default);
        var result = await t.CleanAsync(default);

        Assert.Equal(new CleanupScan(true, 500, 2), scan);
        Assert.Equal(1, runs);
        Assert.Equal(new CleanupResult(400, 1, 1), result);
        Assert.Equal(CleanupTargetIds.WindowsOld, t.Id);
        Assert.False(t.BlockedWhileGameRuns);
    }

    [Fact]
    public async Task A_failing_disk_cleanup_is_an_error_on_the_row()
    {
        Directory.CreateDirectory(Old);
        File.WriteAllBytes(Path.Combine(Old, "a.bin"), new byte[1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsOldTarget(Old, () => false).CleanAsync(default));
    }

    [Fact]
    public async Task No_Windows_old_is_not_found_and_disk_cleanup_is_not_run()
    {
        int runs = 0;
        var t = new WindowsOldTarget(Old, () => { runs++; return true; });

        Assert.Equal(CleanupScan.NotFound, await t.ScanAsync(default));
        Assert.Equal(CleanupResult.Nothing, await t.CleanAsync(default));
        Assert.Equal(0, runs);
    }

    [Fact]
    public void The_preset_flag_is_removed_even_when_disk_cleanup_fails()
    {
        var calls = new List<string>();

        bool ok = WindowsOldTarget.RunWithPreset(() => calls.Add("set"), () => calls.Add("clear"), () => { calls.Add("run"); return false; });
        Assert.Throws<IOException>(() =>
            WindowsOldTarget.RunWithPreset(() => calls.Add("set"), () => calls.Add("clear"), () => throw new IOException()));

        Assert.False(ok);
        Assert.Equal(["set", "run", "clear", "set", "clear"], calls);
    }

    [Fact]
    public void Disk_cleanup_runs_from_system32_with_the_fixed_preset()
    {
        var psi = WindowsOldTarget.DiskCleanupCommand();

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"), psi.FileName);
        Assert.Equal("/sagerun:9917", psi.Arguments);
        Assert.False(psi.UseShellExecute);
    }
}
