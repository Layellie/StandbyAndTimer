using System.Diagnostics;
using System.IO;
using StandbyAndTimer.Services.Maintenance;

namespace StandbyAndTimer.Tests.Maintenance;

public sealed class SafeFolderCleanerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sat-clean-").FullName;
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        // Remove junctions first (non-recursive), so cleanup never walks into a target.
        foreach (var d in Directory.EnumerateDirectories(_dir, "*", SearchOption.AllDirectories)
                     .Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList())
            Directory.Delete(d);
        Directory.Delete(_dir, recursive: true);
    }

    private string File_(string relative, int bytes, DateTime? writtenUtc = null)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, writtenUtc ?? Now.AddDays(-3));
        return path;
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "mklink /J failed");
    }

    [Fact]
    public void Scan_counts_bytes_and_files_and_clean_frees_the_same()
    {
        string root = Path.Combine(_dir, "root");
        File_("root/a.bin", 1000);
        File_("root/sub/b.bin", 500);

        var scan = SafeFolderCleaner.Scan([root], null, Now, default);
        var result = SafeFolderCleaner.Clean([root], null, Now, default);

        Assert.True(scan.Found);
        Assert.Equal(1500, scan.Bytes);
        Assert.Equal(2, scan.Files);
        Assert.Equal(new Core.Models.CleanupResult(1500, 2, 0), result);
    }

    [Fact]
    public void The_root_survives_and_empty_sub_folders_go()
    {
        string root = Path.Combine(_dir, "root");
        File_("root/sub/deeper/x.bin", 10);

        SafeFolderCleaner.Clean([root], null, Now, default);

        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void A_junction_inside_is_removed_but_its_target_survives()
    {
        string root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        string outside = File_("outside/keep.bin", 100);
        Junction(Path.Combine(root, "link"), Path.Combine(_dir, "outside"));

        var scan = SafeFolderCleaner.Scan([root], null, Now, default);
        SafeFolderCleaner.Clean([root], null, Now, default);

        Assert.Equal(0, scan.Bytes);                   // the link's target is not counted
        Assert.True(File.Exists(outside));             // nor touched
        Assert.False(Directory.Exists(Path.Combine(root, "link")));
    }

    [Fact]
    public void A_file_in_use_is_skipped_and_counted()
    {
        string root = Path.Combine(_dir, "root");
        string locked = File_("root/locked.bin", 100);
        File_("root/free.bin", 50);

        Core.Models.CleanupResult r;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            r = SafeFolderCleaner.Clean([root], null, Now, default);

        Assert.Equal(50, r.FreedBytes);
        Assert.Equal(1, r.Deleted);
        Assert.Equal(1, r.Skipped);
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void Files_newer_than_the_keep_window_are_left_alone()
    {
        string root = Path.Combine(_dir, "root");
        string fresh = File_("root/fresh.tmp", 10, Now.AddHours(-2));
        File_("root/old.tmp", 20, Now.AddDays(-2));

        var scan = SafeFolderCleaner.Scan([root], TimeSpan.FromHours(24), Now, default);
        var result = SafeFolderCleaner.Clean([root], TimeSpan.FromHours(24), Now, default);

        Assert.Equal(20, scan.Bytes);
        Assert.Equal(new Core.Models.CleanupResult(20, 1, 0), result);
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Read_only_files_are_deleted()
    {
        string root = Path.Combine(_dir, "root");
        string ro = File_("root/ro.bin", 10);
        File.SetAttributes(ro, FileAttributes.ReadOnly);

        var result = SafeFolderCleaner.Clean([root], null, Now, default);

        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(ro));
    }

    [Fact]
    public void Missing_roots_report_not_found()
    {
        var scan = SafeFolderCleaner.Scan([Path.Combine(_dir, "nope")], null, Now, default);
        var result = SafeFolderCleaner.Clean([Path.Combine(_dir, "nope")], null, Now, default);

        Assert.False(scan.Found);
        Assert.Equal(Core.Models.CleanupResult.Nothing, result);
    }

    [Fact]
    public void A_cancelled_scan_throws()
    {
        string root = Path.Combine(_dir, "root");
        File_("root/a.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => SafeFolderCleaner.Scan([root], null, Now, cts.Token));
    }
}
