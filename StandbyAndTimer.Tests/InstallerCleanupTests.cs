using System.IO;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

public sealed class InstallerCleanupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sat-cleanup-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, [1, 2, 3]);
        return p;
    }

    [Fact]
    public void Deletes_downloaded_setups_and_nothing_else()
    {
        string old1  = Touch("StandbyAndTimer_Setup_v2.2.1.exe");
        string old2  = Touch("StandbyAndTimer_Setup_latest.exe");
        string other = Touch("OtherApp_Setup_1.0.exe");
        string text  = Touch("StandbyAndTimer_Setup_notes.txt");

        int deleted = UpdateService.DeleteDownloadedInstallers(_dir);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(old1));
        Assert.False(File.Exists(old2));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(text));
    }

    [Fact]
    public void A_setup_still_running_is_skipped_not_fatal()
    {
        string running = Touch("StandbyAndTimer_Setup_v2.3.1.exe");
        string old     = Touch("StandbyAndTimer_Setup_v2.3.0.exe");

        int deleted;
        using (new FileStream(running, FileMode.Open, FileAccess.Read, FileShare.Read))   // locked, like a running exe
            deleted = UpdateService.DeleteDownloadedInstallers(_dir);

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(running));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task The_setup_that_started_the_app_is_deleted_once_it_exits()
    {
        string running = Touch("StandbyAndTimer_Setup_v2.4.0.exe");
        var lockStream = new FileStream(running, FileMode.Open, FileAccess.Read, FileShare.Read);

        Task<int> cleanup = UpdateService.DeleteDownloadedInstallersAsync(
            _dir, TimeSpan.FromMilliseconds(20), maxAttempts: 200);
        await Task.Delay(100);
        Assert.True(File.Exists(running));
        lockStream.Dispose();                                   // setup exits

        Assert.Equal(1, await cleanup);
        Assert.False(File.Exists(running));
    }

    [Fact]
    public async Task Gives_up_after_the_last_attempt()
    {
        string running = Touch("StandbyAndTimer_Setup_v2.4.0.exe");
        using var lockStream = new FileStream(running, FileMode.Open, FileAccess.Read, FileShare.Read);

        int deleted = await UpdateService.DeleteDownloadedInstallersAsync(
            _dir, TimeSpan.FromMilliseconds(1), maxAttempts: 3);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(running));
    }

    [Fact]
    public void Missing_folder_is_not_an_error() =>
        Assert.Equal(0, UpdateService.DeleteDownloadedInstallers(Path.Combine(_dir, "nope")));
}
