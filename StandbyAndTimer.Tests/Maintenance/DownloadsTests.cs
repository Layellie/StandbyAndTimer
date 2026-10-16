using System.IO;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests.Maintenance;

public sealed class DownloadsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sat-dl-").FullName;
    public void Dispose()
    {
        foreach (var d in Directory.EnumerateDirectories(_dir)
                     .Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList())
            Directory.Delete(d);
        Directory.Delete(_dir, recursive: true);
    }

    private void Make(string relative, int bytes)
    {
        string p = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes]);
    }

    [Fact]
    public async Task Lists_top_level_files_and_folders_with_sizes()
    {
        Make("setup.exe", 300);
        Make("photos/a.jpg", 100);
        Make("photos/b.jpg", 50);
        var svc = new DownloadsService(_dir);

        var list = await svc.ListAsync(default);
        var scan = await svc.ScanAsync(default);

        Assert.Equal(2, list.Count);
        Assert.Equal(300, list.Single(e => e.Name == "setup.exe").Bytes);
        Assert.True(list.Single(e => e.Name == "photos").IsFolder);
        Assert.Equal(150, list.Single(e => e.Name == "photos").Bytes);
        Assert.Equal(new CleanupScan(true, 450, 2), scan);
    }

    [Fact]
    public async Task Links_and_hidden_system_files_are_not_listed()
    {
        Make("real.bin", 10);
        Make("desktop.ini", 1);
        File.SetAttributes(Path.Combine(_dir, "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
        string outside = Directory.CreateTempSubdirectory("sat-dl-out-").FullName;
        try
        {
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                       $"/c mklink /J \"{Path.Combine(_dir, "link")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!)
                p.WaitForExit();

            var list = await new DownloadsService(_dir).ListAsync(default);

            Assert.Equal(["real.bin"], list.Select(e => e.Name));
        }
        finally { Directory.Delete(Path.Combine(_dir, "link")); Directory.Delete(outside, true); }
    }

    [Fact]
    public async Task Moving_reports_the_entries_that_failed()
    {
        Make("a.bin", 1);
        Make("b.bin", 1);
        var svc = new DownloadsService(_dir, recycle: path => !path.EndsWith("b.bin"));
        var list = await svc.ListAsync(default);

        var failed = await svc.MoveToRecycleBinAsync(list);

        Assert.Equal(["b.bin"], failed.Select(e => e.Name));
    }

    [Fact]
    public async Task A_missing_folder_lists_nothing()
    {
        var svc = new DownloadsService(Path.Combine(_dir, "nope"));

        Assert.Null(svc.FolderPath);
        Assert.Empty(await svc.ListAsync(default));
        Assert.Equal(CleanupScan.NotFound, await svc.ScanAsync(default));
    }

    // DI builds the service at start-up: the shell lookup must wait for first use.
    [Fact]
    public async Task The_folder_is_looked_up_on_first_use_not_at_construction()
    {
        Make("a.bin", 1);
        int lookups = 0;

        var svc = new DownloadsService(() => { lookups++; return _dir; });
        Assert.Equal(0, lookups);

        await svc.ListAsync(default);
        Assert.Equal(_dir, svc.FolderPath);
        Assert.Equal(1, lookups);
    }

    // ── DownloadsViewModel ───────────────────────────────────────────────────

    private sealed class FakeDownloads(params DownloadEntry[] entries) : IDownloadsService
    {
        public List<DownloadEntry> Moved { get; } = [];
        public HashSet<string> FailNames { get; } = [];
        public string? FolderPath => @"C:\Users\x\Downloads";
        public Task<CleanupScan> ScanAsync(CancellationToken ct) => Task.FromResult(new CleanupScan(true, entries.Sum(e => e.Bytes), entries.Length));
        public Task<IReadOnlyList<DownloadEntry>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DownloadEntry>>(entries.Where(e => !Moved.Contains(e)).ToList());
        public Task<IReadOnlyList<DownloadEntry>> MoveToRecycleBinAsync(IReadOnlyList<DownloadEntry> chosen)
        {
            var failed = chosen.Where(e => FailNames.Contains(e.Name)).ToList();
            Moved.AddRange(chosen.Except(failed));
            return Task.FromResult<IReadOnlyList<DownloadEntry>>(failed);
        }
    }

    private static DownloadEntry E(string name, long mb) => new(@"C:\d\" + name, name, false, mb * 1024 * 1024, new DateTime(2026, 9, 1));

    [Fact]
    public async Task The_dialog_lists_largest_first_and_moves_only_ticked_items()
    {
        var fake = new FakeDownloads(E("small.zip", 1), E("big.iso", 900), E("mid.exe", 50));
        (long bytes, int moved, int failed) report = default;
        var vm = new DownloadsViewModel(fake, new FakeLocalization(), (b, m, f) => report = (b, m, f));

        await vm.LoadAsync();
        Assert.Equal(["big.iso", "mid.exe", "small.zip"], vm.Items.Select(i => i.Name));

        vm.Items[0].IsChecked = true;
        vm.Items[2].IsChecked = true;
        fake.FailNames.Add("small.zip");
        await vm.MoveCommand.ExecuteAsync(null);

        Assert.Equal(["big.iso"], fake.Moved.Select(e => e.Name));
        Assert.Equal((900L * 1024 * 1024, 1, 1), report);
        Assert.Equal(["mid.exe", "small.zip"], vm.Items.Select(i => i.Name));   // reloaded
    }

    [Fact]
    public async Task Select_all_and_none()
    {
        var vm = new DownloadsViewModel(new FakeDownloads(E("a", 1), E("b", 2)), new FakeLocalization(), (_, _, _) => { });
        await vm.LoadAsync();

        vm.SelectAllCommand.Execute(null);
        Assert.All(vm.Items, i => Assert.True(i.IsChecked));
        Assert.True(vm.MoveCommand.CanExecute(null));

        vm.SelectNoneCommand.Execute(null);
        Assert.All(vm.Items, i => Assert.False(i.IsChecked));
        Assert.False(vm.MoveCommand.CanExecute(null));
    }

    private sealed class HangingDownloads : IDownloadsService
    {
        public string? FolderPath => @"C:\Users\x\Downloads";
        public Task<CleanupScan> ScanAsync(CancellationToken ct) => Task.FromResult(CleanupScan.NotFound);
        public async Task<IReadOnlyList<DownloadEntry>> ListAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }
        public Task<IReadOnlyList<DownloadEntry>> MoveToRecycleBinAsync(IReadOnlyList<DownloadEntry> entries) => Task.FromResult(entries);
    }

    [Fact]
    public async Task Closing_the_dialog_cancels_a_listing_in_progress()
    {
        var vm = new DownloadsViewModel(new HangingDownloads(), new FakeLocalization(), (_, _, _) => { });

        var load = vm.LoadAsync();
        vm.Dispose();
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsLoading);
    }

    // Dates follow the app's language, not the Windows culture (English UI on a Turkish Windows).
    [Fact]
    public async Task Dates_follow_the_app_language()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
        try
        {
            var loc = new FakeLocalization { CurrentLanguage = Language.English };
            var vm = new DownloadsViewModel(new FakeDownloads(E("a.iso", 1)), loc, (_, _, _) => { });

            await vm.LoadAsync();

            Assert.Equal("1 Sep 2026", vm.Items[0].DateText);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }
}
