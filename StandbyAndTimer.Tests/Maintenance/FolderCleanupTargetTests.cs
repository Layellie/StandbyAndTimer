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

    [Fact]
    public async Task Roots_are_read_at_each_scan()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a"));
        File.WriteAllBytes(Path.Combine(_dir, "a", "1.bin"), new byte[10]);
        IReadOnlyList<string> roots = [Path.Combine(_dir, "missing")];
        var t = new FolderCleanupTarget("x", () => roots);

        Assert.False((await t.ScanAsync(default)).Found);
        roots = [Path.Combine(_dir, "a")];        // e.g. a Steam library added since
        Assert.Equal(10, (await t.ScanAsync(default)).Bytes);
    }

    [Fact]
    public void Steam_libraries_from_libraryfolders_vdf()
    {
        string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\Program Files (x86)\Steam"
                    "label"		""
                }
                "1"
                {
                    "path"		"D:\SteamLibrary"
                }
                "2"
                {
                    "path"		"relative\nope"
                    "path"
                    garbage "path" "E:\Games"
                }
            }
            """;

        var roots = CleanupTargets.SteamShaderRoots("c:/program files (x86)/steam", vdf);

        Assert.Equal([@"c:\program files (x86)\steam\steamapps\shadercache", @"D:\SteamLibrary\steamapps\shadercache"], roots);
    }

    [Fact]
    public void Steam_roots_without_steam_or_its_library_file()
    {
        Assert.Empty(CleanupTargets.SteamShaderRoots(null, null));
        Assert.Equal([@"C:\Steam\steamapps\shadercache"], CleanupTargets.SteamShaderRoots(@"C:\Steam", null));
        Assert.Equal([@"C:\Steam\steamapps\shadercache"], CleanupTargets.SteamShaderRoots(@"C:\Steam", "{ not a vdf"));
    }

    [Fact]
    public void New_targets_have_their_ids_locations_and_rules()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string drive = Path.GetPathRoot(Environment.SystemDirectory)!;

        Assert.Equal(CleanupTargetIds.DirectXShader, CleanupTargets.DirectXShader().Id);
        Assert.True(CleanupTargets.DirectXShader().BlockedWhileGameRuns);
        Assert.Equal([Path.Combine(local, "D3DSCache")], CleanupTargets.DirectXShader().Roots);

        Assert.Equal(CleanupTargetIds.SteamShader, CleanupTargets.SteamShader().Id);
        Assert.True(CleanupTargets.SteamShader().BlockedWhileGameRuns);

        Assert.Equal(CleanupTargetIds.DriverLeftovers, CleanupTargets.DriverLeftovers().Id);
        Assert.False(CleanupTargets.DriverLeftovers().BlockedWhileGameRuns);
        Assert.Equal([Path.Combine(drive, "NVIDIA"), Path.Combine(drive, "AMD"), Path.Combine(common, "NVIDIA Corporation", "Downloader")],
                     CleanupTargets.DriverLeftovers().Roots);

        Assert.Equal(CleanupTargetIds.CrashDumps, CleanupTargets.CrashDumps().Id);
        Assert.False(CleanupTargets.CrashDumps().BlockedWhileGameRuns);
        Assert.Contains(Path.Combine(local, "CrashDumps"), CleanupTargets.CrashDumps().Roots);
        Assert.Contains(Path.Combine(common, "Microsoft", "Windows", "WER", "ReportArchive"), CleanupTargets.CrashDumps().Roots);
        Assert.Contains(Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"), CleanupTargets.CrashDumps().Roots);
    }

    [Fact]
    public void Round_two_targets_have_their_ids_locations_and_rules()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.Equal(CleanupTargetIds.SystemCrashDumps, CleanupTargets.SystemCrashDumps().Id);
        Assert.Equal([Path.Combine(windows, "MEMORY.DMP"), Path.Combine(windows, "Minidump"), Path.Combine(windows, "LiveKernelReports")],
                     CleanupTargets.SystemCrashDumps().Roots);

        Assert.Equal(CleanupTargetIds.WindowsLogs, CleanupTargets.WindowsLogs().Id);
        Assert.Equal([Path.Combine(windows, "Logs", "CBS"), Path.Combine(windows, "Logs", "DISM")], CleanupTargets.WindowsLogs().Roots);

        Assert.Contains(Path.Combine(common, "NVIDIA Corporation", "Downloader"), CleanupTargets.DriverLeftovers().Roots);
    }

    [Fact]
    public async Task Windows_logs_keep_todays_files()
    {
        string root = Path.Combine(_dir, "CBS");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "CBS.log"), new byte[10]);   // written just now
        var t = new FolderCleanupTarget("x", [root], CleanupTargets.TempKeepWindow);

        Assert.Equal(0, (await t.ScanAsync(default)).Bytes);
        Assert.Equal(CleanupTargets.TempKeepWindow, CleanupTargets.LogKeepWindow);
    }
}
