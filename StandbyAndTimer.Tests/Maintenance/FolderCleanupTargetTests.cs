using System.IO;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;

namespace StandbyAndTimer.Tests.Maintenance;

public sealed class FolderCleanupTargetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sat-target-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task All_folders_missing_is_not_found_and_cleans_nothing()
    {
        var t = new FolderCleanupTarget("x", [Path.Combine(_dir, "a"), Path.Combine(_dir, "b")]);

        Assert.Equal(CleanupScan.NotFound, await t.ScanAsync(default));
        Assert.Equal(CleanupResult.Nothing, await t.CleanAsync(default));
    }

    [Fact]
    public async Task Sums_every_existing_folder()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a"));
        Directory.CreateDirectory(Path.Combine(_dir, "b"));
        File.WriteAllBytes(Path.Combine(_dir, "a", "1.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_dir, "b", "2.bin"), new byte[50]);
        var t = new FolderCleanupTarget("x", [Path.Combine(_dir, "a"), Path.Combine(_dir, "b"), Path.Combine(_dir, "missing")]);

        var scan = await t.ScanAsync(default);

        Assert.Equal(new CleanupScan(true, 150, 2), scan);
    }

    [Fact]
    public async Task The_keep_window_uses_the_targets_clock()
    {
        string root = Path.Combine(_dir, "t");
        Directory.CreateDirectory(root);
        string f = Path.Combine(root, "x.tmp");
        File.WriteAllBytes(f, new byte[10]);
        var written = new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(f, written);
        var t = new FolderCleanupTarget("x", [root], TimeSpan.FromHours(24)) { UtcNow = () => written.AddHours(1) };

        Assert.Equal(0, (await t.ScanAsync(default)).Bytes);
        t.UtcNow = () => written.AddHours(25);
        Assert.Equal(10, (await t.ScanAsync(default)).Bytes);
    }

    [Fact]
    public void Default_targets_have_the_spec_ids_and_rules()
    {
        Assert.Equal(CleanupTargetIds.NvidiaShader, CleanupTargets.NvidiaShader().Id);
        Assert.True(CleanupTargets.NvidiaShader().BlockedWhileGameRuns);
        Assert.True(CleanupTargets.AmdShader().BlockedWhileGameRuns);
        Assert.False(CleanupTargets.UserTemp().BlockedWhileGameRuns);
        Assert.Equal(CleanupTargetIds.WindowsUpdate, CleanupTargets.WindowsUpdate().Id);

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Contains(Path.Combine(local, "NVIDIA", "DXCache"), CleanupTargets.NvidiaShader().Roots);
        Assert.Contains(Path.Combine(local, "AMD", "DxCache"), CleanupTargets.AmdShader().Roots);
        Assert.Equal([Path.Combine(windows, "Temp")], CleanupTargets.WindowsTemp().Roots);
        Assert.Equal([Path.Combine(windows, "SoftwareDistribution", "Download")], CleanupTargets.WindowsUpdate().Roots);
    }
}
