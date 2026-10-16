using System.IO;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;

namespace StandbyAndTimer.Tests.Maintenance;

public sealed class DeliveryOptimizationTargetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sat-do-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Make(string name, int bytes) => File.WriteAllBytes(Path.Combine(_dir, name), new byte[bytes]);

    [Fact]
    public async Task Windows_cleans_it_and_the_freed_size_is_before_minus_after()
    {
        Make("a.bin", 300);
        Make("b.bin", 200);
        int runs = 0;
        // Stands in for Delete-DeliveryOptimizationCache: removes what it can, keeps one file.
        var t = new DeliveryOptimizationTarget(_dir, () => { runs++; File.Delete(Path.Combine(_dir, "a.bin")); return true; });

        var scan = await t.ScanAsync(default);
        var result = await t.CleanAsync(default);

        Assert.Equal(new CleanupScan(true, 500, 2), scan);
        Assert.Equal(1, runs);
        Assert.Equal(new CleanupResult(300, 1, 1), result);
        Assert.True(File.Exists(Path.Combine(_dir, "b.bin")));   // the app itself deleted nothing
        Assert.Equal(CleanupTargetIds.DeliveryOptimization, t.Id);
        Assert.False(t.BlockedWhileGameRuns);
    }

    [Fact]
    public async Task A_failing_windows_command_is_an_error_on_the_row()
    {
        Make("a.bin", 10);
        var t = new DeliveryOptimizationTarget(_dir, () => false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => t.CleanAsync(default));
        Assert.True(File.Exists(Path.Combine(_dir, "a.bin")));
    }

    [Fact]
    public async Task A_missing_cache_is_not_found_and_windows_is_not_called()
    {
        int runs = 0;
        var t = new DeliveryOptimizationTarget(Path.Combine(_dir, "nope"), () => { runs++; return true; });

        Assert.Equal(CleanupScan.NotFound, await t.ScanAsync(default));
        Assert.Equal(CleanupResult.Nothing, await t.CleanAsync(default));
        Assert.Equal(0, runs);
    }

    [Fact]
    public void The_windows_command_is_fixed_and_from_system32()
    {
        var psi = DeliveryOptimizationTarget.DeleteCommand();

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), psi.FileName);
        Assert.Equal("-NoProfile -NonInteractive -Command Delete-DeliveryOptimizationCache -Force", psi.Arguments);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
    }
}
