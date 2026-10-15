# Maintenance Tab (3.0.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** add a **Maintenance** tab between Status and Settings. It cleans shader caches, temp files, Windows Update leftovers and the Recycle Bin, lets the user pick Downloads items to send to the Recycle Bin, shows the power plan and page files, opens Windows' own tools, keeps a cleanup history, and adds a Maintenance page to the ? guide. Ships as `3.0.0-beta1`.

**Architecture:**
- Each cleanup row is an `ICleanupTarget` (scan / clean).
- The folder targets share one deletion engine, `SafeFolderCleaner`; it is the only code that deletes files.
- `MaintenanceViewModel` drives the tab. `MainViewModel` owns it: it persists the tick selection with the other settings, tells it when its tab is selected, and tells it whether a game runs.
- The tab's view is a `DataTemplate`, so WPF builds it only when the tab is selected. Start-up and tray cost stay as in 2.5.2.

**Tech stack:**
- WPF on .NET 10 (`net10.0-windows`), CommunityToolkit.Mvvm 8, Microsoft.Extensions.DependencyInjection 10, xUnit 2.9.
- P/Invoke to `shell32`, `powrprof`, `ntdll`, `kernel32`, `ole32`.
- No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-10-16-maintenance-tab-design.md`. Read it before starting.

## Global Constraints

- **Native code:** all P/Invoke goes in `StandbyAndTimer/Services/Native/NativeMethods.cs`; nothing else declares `DllImport`.
- **Packages:** no new NuGet packages.
- **Settings:**
  - Existing registry value names and stored enum names are never renamed.
  - The new value is `CleanupSelection`. Missing means "use the default ticks".
- **Default ticks:** `user-temp`, `windows-temp`, `windows-update`.
- **Persisted target ids, never renamed:** `nvidia-shader`, `amd-shader`, `user-temp`, `windows-temp`, `windows-update`, `recycle-bin`. `downloads` appears only in the history.
- **Layout:** the window stays a fixed 760×520 with no page ScrollViewer. UiRender must pass with zero FAIL in 4 themes × 2 languages.
- **No background work:** no timers in the Maintenance code. Scans run only when the tab is selected (or Rescan, or after a clean). Leaving the tab cancels a running scan.
- **Strings:** every UI string goes in both `Views/Strings/Strings.en-US.xaml` and `Views/Strings/Strings.tr-TR.xaml`, under the same key.
- **Verification commands** (run from the repo root):
  - Build: `dotnet build StandbyAndTimer/StandbyAndTimer.csproj -c Release -warnaserror` must report 0 warnings.
  - Tests: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release`.
  - UiRender: `dotnet run --project tools/UiRender -c Release` must end with `RESULT: all binding checks passed` and print no `FAIL`.
- **Commits:** Conventional Commits, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Work on branch `feat/maintenance-tab`.
- **Release gate** (Task 14): tray RAM and CPU, start-up time and the no-empty-window check must match 2.5.2.

## Review Focus

These are the input classes the spec implies but no feature test would hit by
accident. Each line is pinned by a test in the task named.

1. **A junction or symlink inside a cleaned folder that points outside it.** Its target must survive; only the link goes. Task 2 test `A_junction_inside_is_removed_but_its_target_survives`.
2. **A file held open by another process.** It must be skipped and counted, the clean must go on, and nothing must throw. Task 2 test `A_file_in_use_is_skipped_and_counted`.
3. **Downloads entries that are links (junctions, symlinks).** They must never be listed, because recycling a junction could hit its target. Task 7 test `Links_and_hidden_system_files_are_not_listed`.
4. **One target throwing in the middle of "Clean selected".** The other ticked targets must still run, and the failing row must show the error. Task 8 test `A_failing_target_does_not_stop_the_others`.
5. **Leaving the tab while a slow scan runs.** The scan must be cancelled and must not write results afterwards. Task 8 test `Leaving_the_tab_cancels_the_scan`.

---

## File map

**Create:**

| File | Responsibility |
|---|---|
| `StandbyAndTimer/Core/Interfaces/ICleanupTarget.cs` | Scan / clean contract for one row |
| `StandbyAndTimer/Core/Interfaces/ICleanupStatsStore.cs` | Where the cleanup history lives |
| `StandbyAndTimer/Core/Interfaces/ISystemInfoService.cs` | Power plan and page files |
| `StandbyAndTimer/Core/Interfaces/ISystemToolLauncher.cs` | Opens Windows tools |
| `StandbyAndTimer/Core/Interfaces/IDownloadsService.cs` | Lists Downloads and recycles chosen items |
| `StandbyAndTimer/Core/Models/Cleanup.cs` | `CleanupScan`, `CleanupResult`, `CleanupRecord`, `CleanupTargetIds` |
| `StandbyAndTimer/Core/Models/CleanupStats.cs` | Bounded history and totals |
| `StandbyAndTimer/Core/Models/SystemInfo.cs` | `PowerPlanInfo`, `PageFileInfo`, `SystemTool`, `DownloadEntry` |
| `StandbyAndTimer/Services/Maintenance/SafeFolderCleaner.cs` | The only code that deletes files |
| `StandbyAndTimer/Services/Maintenance/FolderCleanupTarget.cs` | Folder target and the default target factory |
| `StandbyAndTimer/Services/Maintenance/RecycleBinTarget.cs` | Recycle Bin through the shell |
| `StandbyAndTimer/Services/Maintenance/StaTask.cs` | Runs shell calls on an STA thread |
| `StandbyAndTimer/Services/Maintenance/CleanupStatsStore.cs` | `cleanup-history.json`, plus an in-memory store |
| `StandbyAndTimer/Services/Maintenance/SystemInfoService.cs` | Power plan and page file reading |
| `StandbyAndTimer/Services/Maintenance/SystemToolLauncher.cs` | Starts the Windows tools |
| `StandbyAndTimer/Services/Maintenance/DownloadsService.cs` | Downloads folder listing and recycling |
| `StandbyAndTimer/ViewModels/MaintenanceViewModel.cs` | Tab logic, row view model, history rows |
| `StandbyAndTimer/ViewModels/DownloadsViewModel.cs` | The Downloads dialog |
| `StandbyAndTimer/Views/MaintenancePanelView.xaml(.cs)` | The tab page |
| `StandbyAndTimer/Views/Overlays/DownloadsOverlay.xaml(.cs)` | Downloads dialog |
| `StandbyAndTimer/Views/Overlays/CleanupHistoryOverlay.xaml(.cs)` | Maintenance history dialog |
| `StandbyAndTimer.Tests/Maintenance/*.cs` | Tests per task |

**Modify:**

| File | Change |
|---|---|
| `Services/Native/NativeMethods.cs` | New P/Invoke declarations |
| `Core/Models/AppSettings.cs` | `CleanupSelection` |
| `Services/SettingsService.cs` | Load / save `CleanupSelection` |
| `ViewModels/MainViewModel.cs` | `Maintenance`, `SelectedTab`, persistence |
| `Infrastructure/AppBootstrapper.cs` | DI registrations |
| `MainWindow.xaml` | Third tab, two overlays, `SelectedTab` binding |
| `Views/Overlays/AboutOverlay.xaml` | General / Maintenance tabs |
| `Views/Strings/Strings.*.xaml` | All new strings |
| `tools/UiRender/Program.cs` | Renders and checks for the new screens |
| `StandbyAndTimer/StandbyAndTimer.csproj` | `<Version>3.0.0-beta1</Version>` (Task 14) |

**Delete:** `Views/Converters/SettingsTabIndexConverter.cs`. It is replaced by `MainViewModel.SelectedTab`.

---

### Task 1: Contracts, models and the cleanup history model

**Files:**
- Create: `StandbyAndTimer/Core/Interfaces/ICleanupTarget.cs`, `ICleanupStatsStore.cs`, `ISystemInfoService.cs`, `ISystemToolLauncher.cs`, `IDownloadsService.cs`
- Create: `StandbyAndTimer/Core/Models/Cleanup.cs`, `CleanupStats.cs`, `SystemInfo.cs`
- Test: `StandbyAndTimer.Tests/Maintenance/CleanupStatsTests.cs`

**Interfaces:**
- Produces: every type below, used by all later tasks.

- [ ] **Step 1: Write the models and interfaces**

`StandbyAndTimer/Core/Models/Cleanup.cs`:
```csharp
namespace StandbyAndTimer.Core.Models;

/// <summary>What a target holds now. Found = false when none of its folders exist.</summary>
public sealed record CleanupScan(bool Found, long Bytes, int Files)
{
    public static readonly CleanupScan NotFound = new(false, 0, 0);
}

/// <summary>What a clean did: bytes of the files actually deleted, files deleted, files skipped (in use / denied).</summary>
public sealed record CleanupResult(long FreedBytes, int Deleted, int Skipped)
{
    public static readonly CleanupResult Nothing = new(0, 0, 0);
}

/// <summary>One history entry (local time).</summary>
public sealed record CleanupRecord(DateTime At, string TargetId, long FreedBytes, int Skipped);

/// <summary>Persisted target ids: never rename (CleanupSelection and the history store them).</summary>
public static class CleanupTargetIds
{
    public const string NvidiaShader  = "nvidia-shader";
    public const string AmdShader     = "amd-shader";
    public const string UserTemp      = "user-temp";
    public const string WindowsTemp   = "windows-temp";
    public const string WindowsUpdate = "windows-update";
    public const string RecycleBin    = "recycle-bin";
    public const string Downloads     = "downloads";   // history only

    /// <summary>Ticked when CleanupSelection was never saved.</summary>
    public static readonly IReadOnlyList<string> DefaultSelection = [UserTemp, WindowsTemp, WindowsUpdate];
}
```

`StandbyAndTimer/Core/Models/CleanupStats.cs`:
```csharp
using System.Globalization;

namespace StandbyAndTimer.Core.Models;

public sealed class CleanupTotal
{
    public int  Count      { get; set; }
    public long FreedBytes { get; set; }

    internal void Add(long freedBytes) { Count++; FreedBytes += freedBytes; }
}

/// <summary>
/// The Maintenance history kept across restarts: the last <see cref="RecentMax"/>
/// cleanups, per-day totals for <see cref="KeepDays"/> days, totals per target and
/// for all time. Bounded, so the file stays a few KB. Same shape as PurgeStats.
/// </summary>
public sealed class CleanupStats
{
    public const int RecentMax = 20;
    public const int KeepDays  = 60;

    /// <summary>Newest first.</summary>
    public List<CleanupRecord>              Recent   { get; set; } = [];
    /// <summary>Local date "yyyy-MM-dd" -> that day's cleanups.</summary>
    public Dictionary<string, CleanupTotal> Days     { get; set; } = [];
    /// <summary>Target id -> cleanups of that target, all time.</summary>
    public Dictionary<string, CleanupTotal> ByTarget { get; set; } = [];
    public CleanupTotal                     AllTime  { get; set; } = new();
    /// <summary>When the first cleanup in these totals ran (or the last reset).</summary>
    public DateTime?                        Since    { get; set; }

    internal static string DayKey(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Add(CleanupRecord record)
    {
        Since ??= record.At;
        Recent.Insert(0, record);
        if (Recent.Count > RecentMax) Recent.RemoveRange(RecentMax, Recent.Count - RecentMax);

        string day = DayKey(record.At);
        if (!Days.TryGetValue(day, out var total)) Days[day] = total = new CleanupTotal();
        total.Add(record.FreedBytes);

        if (!ByTarget.TryGetValue(record.TargetId, out var byTarget)) ByTarget[record.TargetId] = byTarget = new CleanupTotal();
        byTarget.Add(record.FreedBytes);

        AllTime.Add(record.FreedBytes);

        string oldest = DayKey(record.At.Date.AddDays(-(KeepDays - 1)));
        foreach (string old in Days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList())
            Days.Remove(old);
    }

    /// <summary>Cleanups of the <paramref name="days"/> days ending with <paramref name="today"/> (1 = today only).</summary>
    public CleanupTotal LastDays(int days, DateTime today)
    {
        var sum = new CleanupTotal();
        for (int i = 0; i < days; i++)
            if (Days.TryGetValue(DayKey(today.Date.AddDays(-i)), out var t))
            {
                sum.Count      += t.Count;
                sum.FreedBytes += t.FreedBytes;
            }
        return sum;
    }

    public CleanupTotal Of(string targetId) => ByTarget.TryGetValue(targetId, out var t) ? t : new CleanupTotal();
}
```

`StandbyAndTimer/Core/Models/SystemInfo.cs`:
```csharp
namespace StandbyAndTimer.Core.Models;

/// <summary>Active power plan. Name is null when Windows would not give one.</summary>
public sealed record PowerPlanInfo(Guid Id, string? Name);

/// <summary>One page file. Drive is "C:".</summary>
public sealed record PageFileInfo(string Drive, long TotalMb, long InUseMb, bool SystemManaged);

/// <summary>Windows tools the Maintenance tab opens. New members go at the end.</summary>
public enum SystemTool { OptimizeDrives, DiskCleanup, RepairSystemFiles, PowerOptions, VirtualMemory }

/// <summary>A top-level item of the Downloads folder.</summary>
public sealed record DownloadEntry(string Path, string Name, bool IsFolder, long Bytes, DateTime Modified);
```

`StandbyAndTimer/Core/Interfaces/ICleanupTarget.cs`:
```csharp
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

/// <summary>One row of the Maintenance tab's cleanup box.</summary>
public interface ICleanupTarget
{
    /// <summary>Stable id (see CleanupTargetIds), persisted.</summary>
    string Id { get; }

    /// <summary>True for targets that must not run while a listed game runs (shader caches).</summary>
    bool BlockedWhileGameRuns { get; }

    /// <summary>What a clean would remove now. Never throws for single files; may throw OperationCanceledException.</summary>
    Task<CleanupScan> ScanAsync(CancellationToken ct);

    /// <summary>Removes what it can; files in use are skipped and counted.</summary>
    Task<CleanupResult> CleanAsync(CancellationToken ct);
}
```

`StandbyAndTimer/Core/Interfaces/ICleanupStatsStore.cs`:
```csharp
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

/// <summary>Where the Maintenance history lives between runs.</summary>
public interface ICleanupStatsStore
{
    /// <summary>The saved history, or an empty one (missing or unreadable file).</summary>
    CleanupStats Load();

    /// <summary>Writes the history; failures are logged, never thrown.</summary>
    void Save(CleanupStats stats);
}
```

`StandbyAndTimer/Core/Interfaces/ISystemInfoService.cs`:
```csharp
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface ISystemInfoService
{
    /// <summary>The active power plan, or null if it cannot be read.</summary>
    PowerPlanInfo? GetPowerPlan();

    /// <summary>The page files in use (empty when there is none or it cannot be read).</summary>
    IReadOnlyList<PageFileInfo> GetPageFiles();
}
```

`StandbyAndTimer/Core/Interfaces/ISystemToolLauncher.cs`:
```csharp
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface ISystemToolLauncher
{
    /// <summary>Starts the Windows tool; false (and a log line) when it could not be started.</summary>
    bool Launch(SystemTool tool);
}
```

`StandbyAndTimer/Core/Interfaces/IDownloadsService.cs`:
```csharp
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface IDownloadsService
{
    /// <summary>The user's Downloads folder (also when moved), or null if it does not exist.</summary>
    string? FolderPath { get; }

    /// <summary>Total size of what <see cref="ListAsync"/> would list.</summary>
    Task<CleanupScan> ScanAsync(CancellationToken ct);

    /// <summary>Top-level files and folders; links and hidden system files are left out.</summary>
    Task<IReadOnlyList<DownloadEntry>> ListAsync(CancellationToken ct);

    /// <summary>Sends the entries to the Recycle Bin; returns the ones that could not be moved.</summary>
    Task<IReadOnlyList<DownloadEntry>> MoveToRecycleBinAsync(IReadOnlyList<DownloadEntry> entries);
}
```

- [ ] **Step 2: Write the failing tests for CleanupStats**

`StandbyAndTimer.Tests/Maintenance/CleanupStatsTests.cs`:
```csharp
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Tests.Maintenance;

public class CleanupStatsTests
{
    private static readonly DateTime Today = new(2026, 10, 16, 21, 0, 0);
    private static CleanupRecord R(DateTime at, string id, long mb, int skipped = 0) => new(at, id, mb * 1024 * 1024, skipped);

    [Fact]
    public void Totals_by_day_week_target_and_all_time()
    {
        var s = new CleanupStats();
        s.Add(R(Today.AddDays(-10), CleanupTargetIds.UserTemp, 400));
        s.Add(R(Today.AddDays(-3), CleanupTargetIds.NvidiaShader, 1000));
        s.Add(R(Today.AddHours(-2), CleanupTargetIds.UserTemp, 50));
        s.Add(R(Today, CleanupTargetIds.WindowsUpdate, 2000));

        Assert.Equal(2, s.LastDays(1, Today).Count);
        Assert.Equal(2050L * 1024 * 1024, s.LastDays(1, Today).FreedBytes);
        Assert.Equal(3050L * 1024 * 1024, s.LastDays(7, Today).FreedBytes);
        Assert.Equal(4, s.AllTime.Count);
        Assert.Equal(2, s.Of(CleanupTargetIds.UserTemp).Count);
        Assert.Equal(0, s.Of(CleanupTargetIds.AmdShader).Count);
        Assert.Equal(Today.AddDays(-10), s.Since);
        Assert.Equal(Today, s.Recent[0].At);
    }

    [Fact]
    public void Keeps_the_last_twenty_and_sixty_days_but_all_time_totals()
    {
        var s = new CleanupStats();
        for (int i = 100; i >= 0; i--) s.Add(R(Today.AddDays(-i), CleanupTargetIds.UserTemp, 1));

        Assert.Equal(CleanupStats.RecentMax, s.Recent.Count);
        Assert.Equal(CleanupStats.KeepDays, s.Days.Count);
        Assert.Equal(101, s.AllTime.Count);
    }
}
```

- [ ] **Step 3: Run the tests**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~CleanupStatsTests`
Expected: PASS (2 tests). The model was written in Step 1. If the build fails, fix the compile error first.

- [ ] **Step 4: Build with warnings as errors**

Run: `dotnet build StandbyAndTimer/StandbyAndTimer.csproj -c Release -warnaserror`
Expected: `0 Uyarı`, `0 Hata` (or `0 Warning(s)`, `0 Error(s)`).

- [ ] **Step 5: Commit**

```bash
git add StandbyAndTimer/Core StandbyAndTimer.Tests/Maintenance/CleanupStatsTests.cs
git commit -m "feat(maintenance): cleanup contracts, models and history totals"
```

---

### Task 2: SafeFolderCleaner, the only code that deletes files

**Files:**
- Create: `StandbyAndTimer/Services/Maintenance/SafeFolderCleaner.cs`
- Test: `StandbyAndTimer.Tests/Maintenance/SafeFolderCleanerTests.cs`

**Interfaces:**
- Consumes: `CleanupScan`, `CleanupResult` (Task 1).
- Produces:
  - `internal static CleanupScan SafeFolderCleaner.Scan(IReadOnlyList<string> roots, TimeSpan? keepNewerThan, DateTime nowUtc, CancellationToken ct)`
  - `internal static CleanupResult SafeFolderCleaner.Clean(IReadOnlyList<string> roots, TimeSpan? keepNewerThan, DateTime nowUtc, CancellationToken ct)`
  - Missing roots are ignored. `Scan` returns `Found = true` when at least one root exists.

- [ ] **Step 1: Write the failing tests**

`StandbyAndTimer.Tests/Maintenance/SafeFolderCleanerTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~SafeFolderCleanerTests`
Expected: build error `The name 'SafeFolderCleaner' does not exist`.

- [ ] **Step 3: Implement SafeFolderCleaner**

`StandbyAndTimer/Services/Maintenance/SafeFolderCleaner.cs`:
```csharp
using System.IO;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>
/// Deletes the contents of known folders, safely:
/// 1. never enters a reparse point (junction / symlink / mount point); a
///    link inside a root is removed as a link, its target is never touched;
/// 2. files in use or denied are skipped and counted, never thrown;
/// 3. optionally keeps files written within <c>keepNewerThan</c> (temp folders:
///    a running installer may still need them);
/// 4. never deletes a root itself; empty sub-folders go, deepest first;
/// 5. FreedBytes is the size of the files actually deleted.
/// Scan walks with the same rules, so it shows what a clean would remove.
/// This is the only code in the app that deletes files.
/// </summary>
internal static class SafeFolderCleaner
{
    public static CleanupScan Scan(IReadOnlyList<string> roots, TimeSpan? keepNewerThan, DateTime nowUtc, CancellationToken ct)
    {
        bool found = false;
        long bytes = 0;
        int files = 0;
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            found = true;
            Walk(new DirectoryInfo(root), isRoot: true, keepNewerThan, nowUtc, delete: false, ct,
                 onFile: f => { bytes += f.Length; files++; return true; });
        }
        return found ? new CleanupScan(true, bytes, files) : CleanupScan.NotFound;
    }

    public static CleanupResult Clean(IReadOnlyList<string> roots, TimeSpan? keepNewerThan, DateTime nowUtc, CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            Walk(new DirectoryInfo(root), isRoot: true, keepNewerThan, nowUtc, delete: true, ct, onFile: f =>
            {
                long length = f.Length;
                try
                {
                    if (f.Attributes.HasFlag(FileAttributes.ReadOnly))
                        f.Attributes &= ~FileAttributes.ReadOnly;
                    f.Delete();
                    freed += length;
                    deleted++;
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    return false;
                }
            });
        }
        return new CleanupResult(freed, deleted, skipped);
    }

    // Post-order walk. onFile returns whether the file is gone (clean) / counted (scan).
    private static void Walk(DirectoryInfo dir, bool isRoot, TimeSpan? keepNewerThan, DateTime nowUtc,
                             bool delete, CancellationToken ct, Func<FileInfo, bool> onFile)
    {
        ct.ThrowIfCancellationRequested();

        IEnumerable<FileSystemInfo> entries;
        try { entries = dir.EnumerateFileSystemInfos().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var entry in entries)
        {
            bool isLink;
            try { isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            if (isLink)
            {
                // Never follow it. Clean removes only the link itself:
                // Directory.Delete(non-recursive) / File.Delete on a link
                // deletes the link, not what it points to.
                if (delete)
                {
                    try
                    {
                        if (entry is DirectoryInfo) Directory.Delete(entry.FullName, recursive: false);
                        else                        File.Delete(entry.FullName);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                continue;
            }

            if (entry is DirectoryInfo sub)
            {
                Walk(sub, isRoot: false, keepNewerThan, nowUtc, delete, ct, onFile);
                continue;
            }

            if (entry is FileInfo file)
            {
                if (keepNewerThan is { } keep && nowUtc - SafeWriteTime(file) < keep)
                    continue;
                onFile(file);
            }
        }

        if (delete && !isRoot)
        {
            try { dir.Delete(recursive: false); }   // only succeeds when empty
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static DateTime SafeWriteTime(FileInfo f)
    {
        try { return f.LastWriteTimeUtc; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MaxValue; }
    }
}
```

> Note for the implementer: `SafeWriteTime` returns `DateTime.MaxValue` on error, so `nowUtc - MaxValue` is negative, which is less than `keep`, and the file is kept. A file whose age cannot be read is never deleted under the keep rule.

- [ ] **Step 4: Run the tests**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~SafeFolderCleanerTests`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add StandbyAndTimer/Services/Maintenance/SafeFolderCleaner.cs StandbyAndTimer.Tests/Maintenance/SafeFolderCleanerTests.cs
git commit -m "feat(maintenance): SafeFolderCleaner, never follows links, skips files in use"
```

---

### Task 3: Folder targets and their default locations

**Files:**
- Create: `StandbyAndTimer/Services/Maintenance/FolderCleanupTarget.cs`
- Test: `StandbyAndTimer.Tests/Maintenance/FolderCleanupTargetTests.cs`

**Interfaces:**
- Consumes: `ICleanupTarget`, `CleanupTargetIds` (Task 1); `SafeFolderCleaner` (Task 2).
- Produces:
  - `internal sealed class FolderCleanupTarget(string id, IReadOnlyList<string> roots, TimeSpan? keepNewerThan = null, bool blockedWhileGameRuns = false) : ICleanupTarget`, with `IReadOnlyList<string> Roots` and `Func<DateTime> UtcNow`.
  - `internal static class CleanupTargets` with `NvidiaShader()`, `AmdShader()`, `UserTemp()`, `WindowsTemp()`, `WindowsUpdate()`, and `TempKeepWindow = TimeSpan.FromHours(24)`.

- [ ] **Step 1: Write the failing tests**

`StandbyAndTimer.Tests/Maintenance/FolderCleanupTargetTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~FolderCleanupTargetTests`
Expected: build error `FolderCleanupTarget` not found.

- [ ] **Step 3: Implement**

`StandbyAndTimer/Services/Maintenance/FolderCleanupTarget.cs`:
```csharp
using System.IO;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>A cleanup row backed by one or more folders, through SafeFolderCleaner.</summary>
internal sealed class FolderCleanupTarget(string id, IReadOnlyList<string> roots,
                                          TimeSpan? keepNewerThan = null, bool blockedWhileGameRuns = false) : ICleanupTarget
{
    public string Id => id;
    public bool BlockedWhileGameRuns => blockedWhileGameRuns;
    public IReadOnlyList<string> Roots => roots;

    // Tests set a fixed clock for the keep window.
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public Task<CleanupScan> ScanAsync(CancellationToken ct) =>
        Task.Run(() => SafeFolderCleaner.Scan(roots, keepNewerThan, UtcNow(), ct), ct);

    public Task<CleanupResult> CleanAsync(CancellationToken ct) =>
        Task.Run(() => SafeFolderCleaner.Clean(roots, keepNewerThan, UtcNow(), ct), ct);
}

/// <summary>The default folder targets (spec §4.2). Row order = registration order in AppBootstrapper.</summary>
internal static class CleanupTargets
{
    /// <summary>Temp folders keep files written in the last 24 hours (a running installer may need them).</summary>
    internal static readonly TimeSpan TempKeepWindow = TimeSpan.FromHours(24);

    private static string Local    => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string LocalLow => Path.Combine(Path.GetDirectoryName(Local)!, "LocalLow");
    private static string Common   => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string Windows  => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static FolderCleanupTarget NvidiaShader() => new(CleanupTargetIds.NvidiaShader,
    [
        Path.Combine(Local, "NVIDIA", "DXCache"),
        Path.Combine(Local, "NVIDIA", "GLCache"),
        Path.Combine(LocalLow, "NVIDIA", "PerDriverVersion", "DXCache"),
        Path.Combine(Common, "NVIDIA Corporation", "NV_Cache"),
    ], blockedWhileGameRuns: true);

    public static FolderCleanupTarget AmdShader() => new(CleanupTargetIds.AmdShader,
    [
        Path.Combine(Local, "AMD", "DxCache"),
        Path.Combine(Local, "AMD", "DxcCache"),
        Path.Combine(Local, "AMD", "VkCache"),
        Path.Combine(Local, "AMD", "GLCache"),
    ], blockedWhileGameRuns: true);

    public static FolderCleanupTarget UserTemp() =>
        new(CleanupTargetIds.UserTemp, [Path.TrimEndingDirectorySeparator(Path.GetTempPath())], TempKeepWindow);

    public static FolderCleanupTarget WindowsTemp() =>
        new(CleanupTargetIds.WindowsTemp, [Path.Combine(Windows, "Temp")], TempKeepWindow);

    public static FolderCleanupTarget WindowsUpdate() =>
        new(CleanupTargetIds.WindowsUpdate, [Path.Combine(Windows, "SoftwareDistribution", "Download")]);
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~FolderCleanupTargetTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add StandbyAndTimer/Services/Maintenance/FolderCleanupTarget.cs StandbyAndTimer.Tests/Maintenance/FolderCleanupTargetTests.cs
git commit -m "feat(maintenance): folder targets for shader caches, temp and Update leftovers"
```

---

### Task 4: Shell P/Invoke, STA helper and the Recycle Bin target

**Files:**
- Modify: `StandbyAndTimer/Services/Native/NativeMethods.cs`, adding a `// ── shell32.dll / powrprof.dll / ole32.dll ──` section at the end of the class (before the structs), plus the structs.
- Create: `StandbyAndTimer/Services/Maintenance/StaTask.cs`, `StandbyAndTimer/Services/Maintenance/RecycleBinTarget.cs`
- Test: `StandbyAndTimer.Tests/Maintenance/RecycleBinTargetTests.cs`

**Interfaces:**
- Consumes: `ICleanupTarget`, `CleanupTargetIds` (Task 1).
- Produces:
  - **NativeMethods:** `SHQueryRecycleBin`, `SHEmptyRecycleBin`, `SHERB_*`, `SHFileOperation`, `SHFILEOPSTRUCT`, `FO_DELETE`, `FOF_*`, `SHGetKnownFolderPath`, `CoTaskMemFree`, `FOLDERID_Downloads`, `PowerGetActiveScheme`, `PowerReadFriendlyName`, `LocalFree`, `SystemPageFileInformation = 18`, and `SHQUERYRBINFO`.
  - `internal static Task<T> StaTask.Run<T>(Func<T> work)`.
  - `internal sealed class RecycleBinTarget : ICleanupTarget`.

- [ ] **Step 1: Add the P/Invoke declarations**

Append inside `internal static class NativeMethods` in `StandbyAndTimer/Services/Native/NativeMethods.cs`, after the dwmapi section:
```csharp
    // ── shell32.dll ──────────────────────────────────────────────────────────

    /// <summary>Size and item count of the Recycle Bin (pszRootPath null = all drives).</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>Empties the Recycle Bin (pszRootPath null = all drives).</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    internal const uint SHERB_NOCONFIRMATION = 0x1;
    internal const uint SHERB_NOPROGRESSUI   = 0x2;
    internal const uint SHERB_NOSOUND        = 0x4;

    /// <summary>Shell file operation; with FO_DELETE + FOF_ALLOWUNDO it moves to the Recycle Bin.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    internal const uint   FO_DELETE           = 0x3;
    internal const ushort FOF_SILENT          = 0x0004;
    internal const ushort FOF_NOCONFIRMATION  = 0x0010;
    internal const ushort FOF_ALLOWUNDO       = 0x0040;
    internal const ushort FOF_NOERRORUI       = 0x0400;
    // Asks before an item that is too big for the Recycle Bin would be
    // deleted for good: never delete a user's file permanently in silence.
    internal const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Known folder path (Downloads also when the user moved it). Free with CoTaskMemFree.</summary>
    [DllImport("shell32.dll")]
    internal static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags,
                                                    IntPtr hToken, out IntPtr ppszPath);

    internal static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    // ── ole32.dll ────────────────────────────────────────────────────────────

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr pv);

    // ── powrprof.dll ─────────────────────────────────────────────────────────

    /// <summary>Active power scheme GUID; the returned pointer is freed with LocalFree.</summary>
    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    /// <summary>Friendly name of a scheme (UTF-16, size in bytes). Buffer null = size query.</summary>
    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
                                                      IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid,
                                                      IntPtr buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>NtQuerySystemInformation class: a chain of SYSTEM_PAGEFILE_INFORMATION entries.</summary>
    internal const int SystemPageFileInformation = 18;
```

Add next to the other structs in the same file:
```csharp
    [StructLayout(LayoutKind.Sequential)]
    internal struct SHQUERYRBINFO
    {
        internal int  cbSize;
        internal long i64Size;
        internal long i64NumItems;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCT
    {
        internal IntPtr hwnd;
        internal uint   wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] internal string  pFrom;   // double-null terminated
        [MarshalAs(UnmanagedType.LPWStr)] internal string? pTo;
        internal ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] internal bool fAnyOperationsAborted;
        internal IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? lpszProgressTitle;
    }
```

- [ ] **Step 2: Write StaTask and RecycleBinTarget**

`StandbyAndTimer/Services/Maintenance/StaTask.cs`:
```csharp
namespace StandbyAndTimer.Services.Maintenance;

/// <summary>
/// Runs a shell call on its own STA thread: the shell's file and Recycle Bin
/// functions are documented for single-threaded apartments, the thread pool
/// is MTA, and the UI thread must not block on them.
/// </summary>
internal static class StaTask
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "ShellSta" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
```

`StandbyAndTimer/Services/Maintenance/RecycleBinTarget.cs`:
```csharp
using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>The Recycle Bin of every drive, through the shell (no dialog, no sound).</summary>
internal sealed class RecycleBinTarget : ICleanupTarget
{
    public string Id => CleanupTargetIds.RecycleBin;
    public bool BlockedWhileGameRuns => false;

    public Task<CleanupScan> ScanAsync(CancellationToken ct) => StaTask.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var info = Query();
        return new CleanupScan(true, info.i64Size, (int)Math.Min(info.i64NumItems, int.MaxValue));
    });

    public Task<CleanupResult> CleanAsync(CancellationToken ct) => StaTask.Run(() =>
    {
        var before = Query();
        if (before.i64NumItems == 0) return CleanupResult.Nothing;
        int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
            NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
        if (hr != 0) throw new COMException("SHEmptyRecycleBin failed", hr);
        var after = Query();
        return new CleanupResult(Math.Max(0, before.i64Size - after.i64Size),
                                 (int)Math.Max(0, before.i64NumItems - after.i64NumItems),
                                 (int)Math.Min(after.i64NumItems, int.MaxValue));
    });

    private static NativeMethods.SHQUERYRBINFO Query()
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        int hr = NativeMethods.SHQueryRecycleBin(null, ref info);
        if (hr != 0) throw new COMException("SHQueryRecycleBin failed", hr);
        return info;
    }
}
```

- [ ] **Step 3: Write the test (read-only: it never empties the real bin)**

`StandbyAndTimer.Tests/Maintenance/RecycleBinTargetTests.cs`:
```csharp
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
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~RecycleBinTargetTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add StandbyAndTimer/Services/Native/NativeMethods.cs StandbyAndTimer/Services/Maintenance/StaTask.cs StandbyAndTimer/Services/Maintenance/RecycleBinTarget.cs StandbyAndTimer.Tests/Maintenance/RecycleBinTargetTests.cs
git commit -m "feat(maintenance): Recycle Bin target and shell P/Invoke"
```

---

### Task 5: Cleanup history store

**Files:**
- Create: `StandbyAndTimer/Services/Maintenance/CleanupStatsStore.cs`
- Test: `StandbyAndTimer.Tests/Maintenance/CleanupStatsStoreTests.cs`

**Interfaces:**
- Consumes: `ICleanupStatsStore`, `CleanupStats` (Task 1); `Logger.DataDirectory` (existing).
- Produces: `internal sealed class CleanupStatsStore : ICleanupStatsStore` (public parameterless constructor plus `internal CleanupStatsStore(string path)`), and `internal sealed class InMemoryCleanupStatsStore : ICleanupStatsStore`.

- [ ] **Step 1: Write the failing tests**

`StandbyAndTimer.Tests/Maintenance/CleanupStatsStoreTests.cs`:
```csharp
using System.IO;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;

namespace StandbyAndTimer.Tests.Maintenance;

public sealed class CleanupStatsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sat-chist-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Round_trip_through_the_file()
    {
        string path = Path.Combine(_dir, "cleanup-history.json");
        var stats = new CleanupStats();
        stats.Add(new CleanupRecord(new DateTime(2026, 10, 16, 20, 1, 2), CleanupTargetIds.NvidiaShader, 123456789, 4));

        new CleanupStatsStore(path).Save(stats);
        var back = new CleanupStatsStore(path).Load();

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(stats.Recent[0], back.Recent[0]);
        Assert.Equal(1, back.Of(CleanupTargetIds.NvidiaShader).Count);
        Assert.Equal(123456789, back.AllTime.FreedBytes);
    }

    [Fact]
    public void A_damaged_or_missing_file_starts_a_fresh_history()
    {
        string path = Path.Combine(_dir, "cleanup-history.json");
        Assert.Empty(new CleanupStatsStore(path).Load().Recent);

        File.WriteAllText(path, "{ broken");
        Assert.Equal(0, new CleanupStatsStore(path).Load().AllTime.Count);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~CleanupStatsStoreTests`
Expected: build error `CleanupStatsStore` not found.

- [ ] **Step 3: Implement**

`StandbyAndTimer/Services/Maintenance/CleanupStatsStore.cs`:
```csharp
using System.IO;
using System.Text.Json;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>
/// cleanup-history.json next to the log (%APPDATA%\StandbyAndTimer, removed on
/// uninstall), written like purge-history.json: temp file, then moved over the
/// old one; an unreadable file starts a fresh history instead of failing.
/// </summary>
internal sealed class CleanupStatsStore : ICleanupStatsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();

    // DI uses this one (it only sees public constructors).
    public CleanupStatsStore() : this(Path.Combine(Logger.DataDirectory, "cleanup-history.json")) { }

    // Tests point it at a temp file.
    internal CleanupStatsStore(string path) => _path = path;

    public CleanupStats Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new CleanupStats();
                return JsonSerializer.Deserialize<CleanupStats>(File.ReadAllText(_path), Options) ?? new CleanupStats();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.Warn($"Cleanup history unreadable, starting a new one: {ex.Message}");
                return new CleanupStats();
            }
        }
    }

    public void Save(CleanupStats stats)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(stats, Options));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Cleanup history not saved: {ex.Message}");
            }
        }
    }
}

/// <summary>Keeps the history in memory only (tests, UiRender, the inert view model).</summary>
internal sealed class InMemoryCleanupStatsStore : ICleanupStatsStore
{
    private CleanupStats _stats = new();
    public CleanupStats Load() => _stats;
    public void Save(CleanupStats stats) => _stats = stats;
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~CleanupStatsStoreTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add StandbyAndTimer/Services/Maintenance/CleanupStatsStore.cs StandbyAndTimer.Tests/Maintenance/CleanupStatsStoreTests.cs
git commit -m "feat(maintenance): cleanup-history.json store"
```

---

### Task 6: System info (power plan, page files) and the tool launcher

**Files:**
- Create: `StandbyAndTimer/Services/Maintenance/SystemInfoService.cs`, `StandbyAndTimer/Services/Maintenance/SystemToolLauncher.cs`
- Test: `StandbyAndTimer.Tests/Maintenance/SystemInfoTests.cs`

**Interfaces:**
- Consumes: `ISystemInfoService`, `ISystemToolLauncher`, `PowerPlanInfo`, `PageFileInfo`, `SystemTool` (Task 1); the NativeMethods entries from Task 4; the existing `NativeMethods.NtQuerySystemInformation` and `STATUS_INFO_LENGTH_MISMATCH`.
- Produces:
  - `internal sealed class SystemInfoService : ISystemInfoService`.
  - Its pure helpers: `internal static List<RawPageFile> ParsePageFiles(IntPtr buffer, int length)`, `internal static string DriveOf(string ntName)`, `internal static bool IsSystemManaged(IEnumerable<string> pagingFiles, string drive)`, and `internal readonly record struct RawPageFile(string Drive, long TotalPages, long InUsePages)`.
  - `internal sealed class SystemToolLauncher : ISystemToolLauncher`, with `internal static ProcessStartInfo StartInfoFor(SystemTool tool)`.

- [ ] **Step 1: Write the failing tests**

`StandbyAndTimer.Tests/Maintenance/SystemInfoTests.cs`:
```csharp
using System.IO;
using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;

namespace StandbyAndTimer.Tests.Maintenance;

public class SystemInfoTests
{
    [Theory]
    [InlineData(@"\??\C:\pagefile.sys", "C:")]
    [InlineData(@"\??\d:\pagefile.sys", "D:")]
    [InlineData(@"C:\pagefile.sys", "C:")]
    public void Drive_of_an_nt_page_file_name(string name, string drive) =>
        Assert.Equal(drive, SystemInfoService.DriveOf(name));

    [Theory]
    [InlineData(new[] { @"?:\pagefile.sys" }, "C:", true)]          // "automatically manage for all drives"
    [InlineData(new[] { @"C:\pagefile.sys 0 0" }, "C:", true)]       // system managed size
    [InlineData(new[] { @"C:\pagefile.sys" }, "C:", true)]
    [InlineData(new[] { @"C:\pagefile.sys 4096 8192" }, "C:", false)] // custom size
    [InlineData(new[] { @"D:\pagefile.sys 0 0" }, "C:", false)]
    public void System_managed_from_the_paging_files_setting(string[] entries, string drive, bool expected) =>
        Assert.Equal(expected, SystemInfoService.IsSystemManaged(entries, drive));

    // Builds one SYSTEM_PAGEFILE_INFORMATION (x64 layout) in unmanaged memory,
    // name stored after the 32-byte header like Windows does.
    [Fact]
    public void Parses_a_page_file_entry()
    {
        string name = @"\??\C:\pagefile.sys";
        int nameBytes = name.Length * 2;
        IntPtr buf = Marshal.AllocHGlobal(32 + nameBytes);
        try
        {
            Marshal.WriteInt32(buf, 0, 0);          // NextEntryOffset: last
            Marshal.WriteInt32(buf, 4, 1048576);    // TotalSize (pages)
            Marshal.WriteInt32(buf, 8, 300000);     // TotalInUse
            Marshal.WriteInt32(buf, 12, 400000);    // PeakUsage
            Marshal.WriteInt16(buf, 16, (short)nameBytes);
            Marshal.WriteInt16(buf, 18, (short)nameBytes);
            Marshal.WriteIntPtr(buf, 24, buf + 32);
            Marshal.Copy(name.ToCharArray(), 0, buf + 32, name.Length);

            var list = SystemInfoService.ParsePageFiles(buf, 32 + nameBytes);

            Assert.Equal([new SystemInfoService.RawPageFile("C:", 1048576, 300000)], list);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [Fact]
    public void Reads_this_machines_power_plan_and_page_files()
    {
        var info = new SystemInfoService();

        var plan = info.GetPowerPlan();
        var files = info.GetPageFiles();

        Assert.NotNull(plan);
        Assert.False(string.IsNullOrWhiteSpace(plan!.Name));
        Assert.All(files, f => Assert.Matches("^[A-Z]:$", f.Drive));
    }

    [Theory]
    [InlineData(SystemTool.OptimizeDrives, "dfrgui.exe", "")]
    [InlineData(SystemTool.DiskCleanup, "cleanmgr.exe", "")]
    [InlineData(SystemTool.PowerOptions, "control.exe", "/name Microsoft.PowerOptions")]
    [InlineData(SystemTool.VirtualMemory, "SystemPropertiesPerformance.exe", "")]
    [InlineData(SystemTool.RepairSystemFiles, "cmd.exe", "/k \"DISM /Online /Cleanup-Image /RestoreHealth & sfc /scannow\"")]
    public void Tools_start_from_system32_with_fixed_arguments(SystemTool tool, string exe, string args)
    {
        var psi = SystemToolLauncher.StartInfoFor(tool);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, exe), psi.FileName);
        Assert.Equal(args, psi.Arguments);
        Assert.False(psi.UseShellExecute);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~SystemInfoTests`
Expected: build error `SystemInfoService` not found.

- [ ] **Step 3: Implement SystemInfoService**

`StandbyAndTimer/Services/Maintenance/SystemInfoService.cs`:
```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>Power plan (powrprof) and page files (NtQuerySystemInformation 18 + the PagingFiles setting).</summary>
internal sealed class SystemInfoService : ISystemInfoService
{
    private const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const uint ErrorMoreData = 234;

    public PowerPlanInfo? GetPowerPlan()
    {
        try
        {
            if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out IntPtr guidPtr) != 0 || guidPtr == IntPtr.Zero)
                return null;
            try
            {
                Guid id = Marshal.PtrToStructure<Guid>(guidPtr);
                uint size = 0;
                uint rc = NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
                if ((rc != 0 && rc != ErrorMoreData) || size == 0) return new PowerPlanInfo(id, null);
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
                        return new PowerPlanInfo(id, null);
                    return new PowerPlanInfo(id, Marshal.PtrToStringUni(buffer));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { NativeMethods.LocalFree(guidPtr); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Logger.Warn($"Power plan unreadable: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<PageFileInfo> GetPageFiles()
    {
        int size = 4096;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NativeMethods.NtQuerySystemInformation(NativeMethods.SystemPageFileInformation, buffer, size, out int needed);
                if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH) { size = Math.Max(size * 2, needed); continue; }
                if (status != 0 || needed == 0) return [];
                string[] setting = ReadPagingFilesSetting();
                long pageSize = Environment.SystemPageSize;
                return ParsePageFiles(buffer, needed)
                    .Select(p => new PageFileInfo(p.Drive, p.TotalPages * pageSize / (1024 * 1024),
                                                  p.InUsePages * pageSize / (1024 * 1024), IsSystemManaged(setting, p.Drive)))
                    .ToList();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return [];
    }

    internal readonly record struct RawPageFile(string Drive, long TotalPages, long InUsePages);

    // SYSTEM_PAGEFILE_INFORMATION (x64): ULONG NextEntryOffset @0, TotalSize @4,
    // TotalInUse @8, PeakUsage @12, UNICODE_STRING PageFileName @16
    // (USHORT Length @16, MaximumLength @18, PWSTR Buffer @24).
    internal static List<RawPageFile> ParsePageFiles(IntPtr buffer, int length)
    {
        var list = new List<RawPageFile>();
        int offset = 0;
        while (offset + 32 <= length)
        {
            uint next      = (uint)Marshal.ReadInt32(buffer, offset);
            long total     = (uint)Marshal.ReadInt32(buffer, offset + 4);
            long inUse     = (uint)Marshal.ReadInt32(buffer, offset + 8);
            int nameBytes  = (ushort)Marshal.ReadInt16(buffer, offset + 16);
            IntPtr namePtr = Marshal.ReadIntPtr(buffer, offset + 24);
            string name    = namePtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(namePtr, nameBytes / 2);
            list.Add(new RawPageFile(DriveOf(name), total, inUse));
            if (next == 0) break;
            offset += (int)next;
        }
        return list;
    }

    internal static string DriveOf(string ntName)
    {
        string n = ntName.StartsWith(@"\??\", StringComparison.Ordinal) ? ntName[4..] : ntName;
        return n.Length >= 2 && n[1] == ':' ? n[..2].ToUpperInvariant() : n;
    }

    // PagingFiles entries: "?:\pagefile.sys" (automatic for all drives),
    // "C:\pagefile.sys 0 0" or "C:\pagefile.sys" (system managed size),
    // "C:\pagefile.sys 4096 8192" (custom size, MB).
    internal static bool IsSystemManaged(IEnumerable<string> pagingFiles, string drive)
    {
        foreach (string raw in pagingFiles)
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;
            if (parts[0].StartsWith(@"?:\", StringComparison.Ordinal)) return true;
            if (!parts[0].StartsWith(drive, StringComparison.OrdinalIgnoreCase)) continue;
            return parts.Length < 3 || (parts[1] == "0" && parts[2] == "0");
        }
        return false;
    }

    private static string[] ReadPagingFilesSetting()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MemoryManagementKey);
            return key?.GetValue("PagingFiles") as string[] ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return [];
        }
    }
}
```

- [ ] **Step 4: Implement SystemToolLauncher**

`StandbyAndTimer/Services/Maintenance/SystemToolLauncher.cs`:
```csharp
using System.Diagnostics;
using System.IO;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>
/// Opens Windows' own tools from System32 with fixed arguments (nothing from
/// user input). The app is elevated, so they start elevated too, which is
/// what dfrgui, cleanmgr and DISM/SFC need.
/// </summary>
internal sealed class SystemToolLauncher : ISystemToolLauncher
{
    public bool Launch(SystemTool tool)
    {
        try
        {
            using var p = Process.Start(StartInfoFor(tool));
            Logger.Info($"System tool opened: {tool}");
            return p is not null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"System tool {tool} failed to start: {ex.Message}");
            return false;
        }
    }

    internal static ProcessStartInfo StartInfoFor(SystemTool tool)
    {
        string sys = Environment.SystemDirectory;
        (string exe, string args) = tool switch
        {
            SystemTool.OptimizeDrives    => ("dfrgui.exe", ""),
            SystemTool.DiskCleanup       => ("cleanmgr.exe", ""),
            SystemTool.PowerOptions      => ("control.exe", "/name Microsoft.PowerOptions"),
            SystemTool.VirtualMemory     => ("SystemPropertiesPerformance.exe", ""),
            // A visible console; "&" runs SFC even when DISM fails (no internet).
            SystemTool.RepairSystemFiles => ("cmd.exe", "/k \"DISM /Online /Cleanup-Image /RestoreHealth & sfc /scannow\""),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
        };
        return new ProcessStartInfo(Path.Combine(sys, exe), args) { UseShellExecute = false };
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~SystemInfoTests`
Expected: PASS (15 cases).

- [ ] **Step 6: Commit**

```bash
git add StandbyAndTimer/Services/Maintenance/SystemInfoService.cs StandbyAndTimer/Services/Maintenance/SystemToolLauncher.cs StandbyAndTimer.Tests/Maintenance/SystemInfoTests.cs
git commit -m "feat(maintenance): power plan, page file info and Windows tool launcher"
```

---

### Task 7: Downloads service and the Downloads dialog view model

**Files:**
- Create: `StandbyAndTimer/Services/Maintenance/DownloadsService.cs`, `StandbyAndTimer/ViewModels/DownloadsViewModel.cs`
- Test: `StandbyAndTimer.Tests/Maintenance/DownloadsTests.cs`

**Interfaces:**
- Consumes: `IDownloadsService`, `DownloadEntry`, `CleanupScan` (Task 1); `SafeFolderCleaner.Scan` (Task 2); `StaTask`, and from NativeMethods `SHGetKnownFolderPath`, `SHFileOperation` and `FOLDERID_Downloads` (Task 4); `ILocalizationService` (existing).
- Produces:
  - `internal sealed class DownloadsService : IDownloadsService`:
    - public parameterless constructor;
    - `internal DownloadsService(string? folder, Func<string, bool>? recycle = null)`.
  - `public sealed partial class DownloadsViewModel : ObservableObject`:
    - constructor `(IDownloadsService service, ILocalizationService loc, Action<long, int, int> moved)`, where `moved` is (bytes, movedCount, failedCount);
    - `ObservableCollection<DownloadRow> Items`;
    - `IsLoading`, `Message`, `SelectedText`;
    - `SelectAllCommand`, `SelectNoneCommand`, `MoveCommand`;
    - `internal Task LoadAsync()`, `internal void Cancel()`.
  - `public sealed partial class DownloadRow : ObservableObject`, with `Entry`, `Name`, `SizeText`, `DateText`, `IsChecked` and `Icon` (`"📁"` or `""`).

- [ ] **Step 1: Write the failing tests**

`StandbyAndTimer.Tests/Maintenance/DownloadsTests.cs`:
```csharp
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
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~DownloadsTests`
Expected: build error `DownloadsService` not found.

- [ ] **Step 3: Implement DownloadsService**

`StandbyAndTimer/Services/Maintenance/DownloadsService.cs`:
```csharp
using System.IO;
using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services.Maintenance;

/// <summary>
/// The user's Downloads folder: top-level entries with sizes, and moving
/// chosen ones to the Recycle Bin (never deleting them). Links are not listed:
/// recycling a junction could reach what it points to.
/// </summary>
internal sealed class DownloadsService : IDownloadsService
{
    private readonly string? _folder;
    private readonly Func<string, bool> _recycle;

    // DI uses this one (it only sees public constructors).
    public DownloadsService() : this(FindDownloads()) { }

    // Tests: a temp folder, and a fake recycler (true = moved).
    internal DownloadsService(string? folder, Func<string, bool>? recycle = null)
    {
        _folder  = folder is not null && Directory.Exists(folder) ? folder : null;
        _recycle = recycle ?? SendToRecycleBin;
    }

    public string? FolderPath => _folder;

    public async Task<CleanupScan> ScanAsync(CancellationToken ct)
    {
        if (_folder is null) return CleanupScan.NotFound;
        var list = await ListAsync(ct).ConfigureAwait(false);
        return new CleanupScan(true, list.Sum(e => e.Bytes), list.Count);
    }

    public Task<IReadOnlyList<DownloadEntry>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<DownloadEntry>>(() =>
    {
        if (_folder is null) return [];
        var list = new List<DownloadEntry>();
        foreach (var info in new DirectoryInfo(_folder).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            FileAttributes a;
            try { a = info.Attributes; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (a.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (a.HasFlag(FileAttributes.Hidden) && a.HasFlag(FileAttributes.System)) continue;   // desktop.ini

            long bytes = info is FileInfo f ? f.Length
                       : SafeFolderCleaner.Scan([info.FullName], null, DateTime.UtcNow, ct).Bytes;
            list.Add(new DownloadEntry(info.FullName, info.Name, info is DirectoryInfo, bytes, info.LastWriteTime));
        }
        return list;
    }, ct);

    public async Task<IReadOnlyList<DownloadEntry>> MoveToRecycleBinAsync(IReadOnlyList<DownloadEntry> entries)
    {
        var failed = new List<DownloadEntry>();
        foreach (var e in entries)
        {
            bool ok;
            try { ok = await StaTask.Run(() => _recycle(e.Path)).ConfigureAwait(false); }
            catch (Exception ex) { Logger.Warn($"Recycle {e.Path}: {ex.Message}"); ok = false; }
            if (!ok) failed.Add(e);
        }
        Logger.Info($"Downloads: {entries.Count - failed.Count} moved to the Recycle Bin, {failed.Count} failed");
        return failed;
    }

    private static bool SendToRecycleBin(string path)
    {
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            wFunc  = NativeMethods.FO_DELETE,
            pFrom  = path + "\0",   // + the marshaller's null = double-null terminated
            fFlags = (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION |
                              NativeMethods.FOF_SILENT | NativeMethods.FOF_NOERRORUI | NativeMethods.FOF_WANTNUKEWARNING),
        };
        int rc = NativeMethods.SHFileOperation(ref op);
        return rc == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
    }

    private static string? FindDownloads()
    {
        if (NativeMethods.SHGetKnownFolderPath(NativeMethods.FOLDERID_Downloads, 0, IntPtr.Zero, out IntPtr p) == 0)
        {
            try { return Marshal.PtrToStringUni(p); }
            finally { NativeMethods.CoTaskMemFree(p); }
        }
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(fallback) ? fallback : null;
    }
}
```

- [ ] **Step 4: Implement DownloadsViewModel**

`StandbyAndTimer/ViewModels/DownloadsViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.ViewModels;

public sealed partial class DownloadRow(DownloadEntry entry, string sizeText, string dateText) : ObservableObject
{
    public DownloadEntry Entry    { get; } = entry;
    public string        Name     => Entry.Name;
    public string        SizeText { get; } = sizeText;
    public string        DateText { get; } = dateText;
    public string        Icon     => Entry.IsFolder ? "📁" : "";
    [ObservableProperty] private bool _isChecked;
}

/// <summary>
/// The Downloads dialog: largest first, tick what to send to the Recycle Bin.
/// Exists only while the dialog is open (MaintenanceViewModel drops it).
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    private readonly IDownloadsService _service;
    private readonly ILocalizationService _loc;
    private readonly Action<long, int, int> _moved;
    private readonly CancellationTokenSource _cts = new();

    public DownloadsViewModel(IDownloadsService service, ILocalizationService loc, Action<long, int, int> moved)
    {
        _service = service;
        _loc     = loc;
        _moved   = moved;
    }

    public ObservableCollection<DownloadRow> Items { get; } = [];

    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _selectedText = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    private bool _isMoving;

    internal async Task LoadAsync()
    {
        IsLoading = true;
        Message = _loc.GetString("Str_Dl_Loading");
        foreach (var r in Items) r.PropertyChanged -= OnRowChanged;
        Items.Clear();
        try
        {
            if (_service.FolderPath is null) { Message = _loc.GetString("Str_Dl_NotFound"); return; }
            var list = await _service.ListAsync(_cts.Token);
            var culture = CultureInfo.CurrentCulture;
            foreach (var e in list.OrderByDescending(e => e.Bytes))
            {
                var row = new DownloadRow(e, MaintenanceViewModel.SizeText(e.Bytes), e.Modified.ToString("d MMM yyyy", culture));
                row.PropertyChanged += OnRowChanged;
                Items.Add(row);
            }
            Message = Items.Count == 0 ? _loc.GetString("Str_Dl_Empty") : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warn($"Downloads listing failed: {ex.Message}");
            Message = _loc.GetString("Str_Dl_NotFound");
        }
        finally
        {
            IsLoading = false;
            UpdateSelected();
        }
    }

    internal void Cancel() => _cts.Cancel();

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadRow.IsChecked)) UpdateSelected();
    }

    private void UpdateSelected()
    {
        var chosen = Items.Where(i => i.IsChecked).ToList();
        SelectedText = string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Dl_Selected"),
                                     chosen.Count, MaintenanceViewModel.SizeText(chosen.Sum(i => i.Entry.Bytes)));
        MoveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private void SelectAll()  { foreach (var i in Items) i.IsChecked = true; }
    [RelayCommand] private void SelectNone() { foreach (var i in Items) i.IsChecked = false; }

    private bool CanMove() => !IsMoving && Items.Any(i => i.IsChecked);

    [RelayCommand(CanExecute = nameof(CanMove))]
    private async Task MoveAsync()
    {
        IsMoving = true;
        try
        {
            var chosen = Items.Where(i => i.IsChecked).Select(i => i.Entry).ToList();
            var failed = await _service.MoveToRecycleBinAsync(chosen);
            var moved = chosen.Except(failed).ToList();
            _moved(moved.Sum(e => e.Bytes), moved.Count, failed.Count);
            await LoadAsync();
            Message = failed.Count == 0
                ? string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Dl_Moved"), MaintenanceViewModel.SizeText(moved.Sum(e => e.Bytes)))
                : string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Dl_Failed"), failed.Count);
        }
        finally { IsMoving = false; }
    }
}
```

> **Note:** `MaintenanceViewModel.SizeText(long)` is created in Task 8. Until then the code above does not compile, so implement Task 7's VM file together with Task 8's static helper. Alternatively, add this to a new `ViewModels/MaintenanceViewModel.cs` now and Task 8 fills in the rest:
> ```csharp
> namespace StandbyAndTimer.ViewModels;
> public sealed partial class MaintenanceViewModel
> {
>     /// <summary>"845 MB", "1.2 GB", "&lt; 1 MB", "0 MB".</summary>
>     internal static string SizeText(long bytes) =>
>         bytes > 0 && bytes < 1024 * 1024 ? "< 1 MB" : MainViewModel.FormatSize(bytes / (1024 * 1024));
> }
> ```
> Task 8 makes the class `: ObservableObject` (an `ObservableObject` base declared on one partial part is enough).

- [ ] **Step 5: Run the tests**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~DownloadsTests`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add StandbyAndTimer/Services/Maintenance/DownloadsService.cs StandbyAndTimer/ViewModels/DownloadsViewModel.cs StandbyAndTimer/ViewModels/MaintenanceViewModel.cs StandbyAndTimer.Tests/Maintenance/DownloadsTests.cs
git commit -m "feat(maintenance): Downloads listing and Recycle Bin moves, dialog view model"
```

---

### Task 8: MaintenanceViewModel

**Files:**
- Modify (fill in): `StandbyAndTimer/ViewModels/MaintenanceViewModel.cs`
- Test: `StandbyAndTimer.Tests/Maintenance/MaintenanceViewModelTests.cs`

**Interfaces:**
- Consumes:
  - Task 1: all contracts.
  - Task 5: `InMemoryCleanupStatsStore`.
  - Task 7: `DownloadsViewModel`.
  - Existing: `MainViewModel.FormatSize(long mb)` (internal static), `Logger`, `ILocalizationService`.
- Produces (`public sealed partial class MaintenanceViewModel : ObservableObject`):
  - **Constructor:** `(IEnumerable<ICleanupTarget> targets, ICleanupStatsStore store, ISystemInfoService info, ISystemToolLauncher tools, IDownloadsService downloads, ILocalizationService loc)`.
  - **Inert instance:** `internal static MaintenanceViewModel Inert()`.
  - **Rows and history:**
    - `ObservableCollection<CleanupRowViewModel> Rows`;
    - `ObservableCollection<CleanupHistoryRow> History`.
  - **State properties:**
    - `bool IsActive` (set by `MainViewModel`), `bool IsCleaning`;
    - `string PowerPlanText`, `string PageFileText`;
    - `string DownloadsSizeText`, `string CleanButtonText`, `string FreedSoFarText`;
    - `bool IsRepairArmed`, `string RepairButtonText`;
    - `bool IsHistoryVisible`, `bool IsResetArmed`;
    - `DownloadsViewModel? DownloadsDialog`, `bool IsDownloadsVisible`.
  - **History text:** `StatsTodayFreed/Count`, `StatsWeekFreed/Count`, `StatsAllFreed/Count`, `ByTargetText`.
  - **Commands:** `CleanSelectedCommand` (async), `RescanCommand` (async), `OpenToolCommand` (`SystemTool` parameter), `OpenDownloadsCommand` (async), `CloseDownloadsCommand`, `ShowHistoryCommand`, `CloseHistoryCommand`, `ResetHistoryCommand`.
  - **Called by MainViewModel:** `void Initialize(string? selection)`, `string? SelectionText`, `event EventHandler? SettingsChanged`, `event EventHandler<string>? StatusRequested`, `Func<bool> AnyGameRunning`, `Func<DateTime> Now`, `internal Task RefreshAsync(bool keepResults = false)`.
  - **Helper:** `internal static string SizeText(long bytes)`.
  - **Row view model** (`public sealed partial class CleanupRowViewModel : ObservableObject`): `Target`, `Id`, `Name`, `Tip`, `IsChecked`, `Scan`, `SizeText`, `ResultText`, `DisplayText`, `IsBlocked`, `CanClean`.
  - **History row:** `public sealed record CleanupHistoryRow(CleanupRecord Record, string Time, string What, string Freed)`.

- [ ] **Step 1: Write the failing tests**

`StandbyAndTimer.Tests/Maintenance/MaintenanceViewModelTests.cs`:
```csharp
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests.Maintenance;

internal sealed class FakeTarget(string id, CleanupScan scan, bool blockedWhileGameRuns = false) : ICleanupTarget
{
    public string Id => id;
    public bool BlockedWhileGameRuns => blockedWhileGameRuns;
    public int Cleaned { get; private set; }
    public Exception? Throw { get; set; }
    public CleanupResult Result { get; set; } = new(100L * 1024 * 1024, 3, 1);
    public TaskCompletionSource? ScanGate { get; set; }
    public bool ScanCompleted { get; private set; }

    public async Task<CleanupScan> ScanAsync(CancellationToken ct)
    {
        if (ScanGate is not null) await ScanGate.Task.WaitAsync(ct);
        ScanCompleted = true;
        return scan;
    }

    public Task<CleanupResult> CleanAsync(CancellationToken ct)
    {
        Cleaned++;
        if (Throw is not null) throw Throw;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeTools : ISystemToolLauncher
{
    public List<SystemTool> Launched { get; } = [];
    public bool Result { get; set; } = true;
    public bool Launch(SystemTool tool) { Launched.Add(tool); return Result; }
}

public class MaintenanceViewModelTests
{
    private static CleanupScan Mb(long mb) => new(true, mb * 1024 * 1024, 10);

    private static (MaintenanceViewModel vm, FakeTarget[] t, FakeTools tools, InMemoryCleanupStatsStore store) Make(params FakeTarget[] targets)
    {
        var store = new InMemoryCleanupStatsStore();
        var tools = new FakeTools();
        var vm = new MaintenanceViewModel(targets, store, NullProxy.Of<ISystemInfoService>(), tools,
                                          NullProxy.Of<IDownloadsService>(), new FakeLocalization())
        { Now = () => new DateTime(2026, 10, 16, 20, 0, 0) };
        return (vm, targets, tools, store);
    }

    [Fact]
    public async Task Scan_results_reach_the_rows()
    {
        var (vm, _, _, _) = Make(new FakeTarget("user-temp", Mb(845)), new FakeTarget("amd-shader", CleanupScan.NotFound));

        await vm.RefreshAsync();

        Assert.Equal("845 MB", vm.Rows[0].SizeText);
        Assert.True(vm.Rows[0].CanClean);
        Assert.Equal("Str_Maint_NotFound", vm.Rows[1].SizeText);
        Assert.False(vm.Rows[1].CanClean);
    }

    [Fact]
    public async Task Only_ticked_rows_are_cleaned_and_recorded()
    {
        var (vm, t, _, store) = Make(new FakeTarget("user-temp", Mb(845)), new FakeTarget("windows-update", Mb(2000)));
        vm.Initialize("user-temp");
        await vm.RefreshAsync();

        await vm.CleanSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, t[0].Cleaned);
        Assert.Equal(0, t[1].Cleaned);
        var record = Assert.Single(store.Load().Recent);
        Assert.Equal("user-temp", record.TargetId);
        Assert.Equal(1, record.Skipped);
        Assert.Contains("Str_Maint_ResultFreed", vm.Rows[0].DisplayText);
    }

    [Fact]
    public async Task A_failing_target_does_not_stop_the_others()
    {
        var bad = new FakeTarget("user-temp", Mb(10)) { Throw = new UnauthorizedAccessException("denied") };
        var good = new FakeTarget("windows-temp", Mb(20));
        var (vm, _, _, _) = Make(bad, good);
        vm.Initialize("user-temp,windows-temp");
        await vm.RefreshAsync();

        await vm.CleanSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, good.Cleaned);
        Assert.Contains("Str_Maint_ResultFailed", vm.Rows[0].DisplayText);
        Assert.False(vm.IsCleaning);
    }

    [Fact]
    public async Task Leaving_the_tab_cancels_the_scan()
    {
        var slow = new FakeTarget("user-temp", Mb(10)) { ScanGate = new TaskCompletionSource() };
        var (vm, _, _, _) = Make(slow);

        var scan = vm.RefreshAsync();
        vm.IsActive = true;    // selecting starts its own scan, cancelling the first
        vm.IsActive = false;   // leaving cancels that one too
        slow.ScanGate.SetResult();
        await scan;

        Assert.False(slow.ScanCompleted);
        Assert.NotEqual("10 MB", vm.Rows[0].SizeText);
    }

    [Fact]
    public async Task Shader_rows_are_blocked_while_a_game_runs()
    {
        var (vm, _, _, _) = Make(new FakeTarget("nvidia-shader", Mb(500), blockedWhileGameRuns: true));
        vm.AnyGameRunning = () => true;
        vm.Initialize("nvidia-shader");

        await vm.RefreshAsync();

        Assert.True(vm.Rows[0].IsBlocked);
        Assert.False(vm.Rows[0].CanClean);
        Assert.False(vm.CleanSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void Ticks_default_save_and_load()
    {
        var (vm, _, _, _) = Make(new FakeTarget("user-temp", Mb(1)), new FakeTarget("windows-temp", Mb(1)),
                                 new FakeTarget("windows-update", Mb(1)), new FakeTarget("recycle-bin", Mb(1)));
        int changes = 0;
        vm.SettingsChanged += (_, _) => changes++;

        vm.Initialize(null);
        Assert.Equal("user-temp,windows-temp,windows-update", vm.SelectionText);
        Assert.Equal(0, changes);

        vm.Rows[3].IsChecked = true;
        Assert.Equal(1, changes);
        Assert.Equal("user-temp,windows-temp,windows-update,recycle-bin", vm.SelectionText);

        vm.Initialize("");
        Assert.All(vm.Rows, r => Assert.False(r.IsChecked));
    }

    [Fact]
    public async Task The_clean_button_shows_the_selected_total()
    {
        var (vm, _, _, _) = Make(new FakeTarget("user-temp", Mb(1024)), new FakeTarget("windows-temp", Mb(512)));
        vm.Initialize("user-temp,windows-temp");

        await vm.RefreshAsync();

        Assert.Equal("Str_Maint_CleanSelected", vm.CleanButtonText);   // FakeLocalization returns keys; format has no {0}
        Assert.Equal(1536L * 1024 * 1024, vm.SelectedBytes);
    }

    [Fact]
    public void Repair_needs_a_second_press_other_tools_open_at_once()
    {
        var (vm, _, tools, _) = Make();

        vm.OpenToolCommand.Execute(SystemTool.OptimizeDrives);
        vm.OpenToolCommand.Execute(SystemTool.RepairSystemFiles);
        Assert.Equal([SystemTool.OptimizeDrives], tools.Launched);
        Assert.True(vm.IsRepairArmed);

        vm.OpenToolCommand.Execute(SystemTool.RepairSystemFiles);
        Assert.Equal([SystemTool.OptimizeDrives, SystemTool.RepairSystemFiles], tools.Launched);
        Assert.False(vm.IsRepairArmed);
    }

    [Fact]
    public async Task History_totals_follow_cleanups_and_reset_needs_two_presses()
    {
        var (vm, _, _, store) = Make(new FakeTarget("user-temp", Mb(10)));
        vm.Initialize("user-temp");
        await vm.RefreshAsync();
        await vm.CleanSelectedCommand.ExecuteAsync(null);

        Assert.Equal("100 MB", vm.StatsTodayFreed);
        Assert.Single(vm.History);

        vm.ResetHistoryCommand.Execute(null);
        Assert.True(vm.IsResetArmed);
        vm.ResetHistoryCommand.Execute(null);
        Assert.Empty(vm.History);
        Assert.Equal(0, store.Load().AllTime.Count);
    }

    [Fact]
    public void The_inert_model_keeps_the_saved_selection()
    {
        var vm = MaintenanceViewModel.Inert();
        vm.Initialize("user-temp");

        Assert.Equal("user-temp", vm.SelectionText);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~MaintenanceViewModelTests`
Expected: build errors (constructor, `Rows` and the other members do not exist).

- [ ] **Step 3: Implement MaintenanceViewModel**

Replace the content of `StandbyAndTimer/ViewModels/MaintenanceViewModel.cs` with:
```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;
using StandbyAndTimer.Services.Maintenance;

namespace StandbyAndTimer.ViewModels;

/// <summary>One cleanup row: tick, name, size (or state), result of the last clean.</summary>
public sealed partial class CleanupRowViewModel : ObservableObject
{
    public CleanupRowViewModel(ICleanupTarget target) => Target = target;

    public ICleanupTarget Target { get; }
    public string Id => Target.Id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _tip  = "";
    [ObservableProperty] private bool   _isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClean))]
    private CleanupScan? _scan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClean))]
    private bool _isBlocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _sizeText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _resultText = "";

    /// <summary>The last clean's result until the next plain scan, else the size.</summary>
    public string DisplayText => ResultText.Length > 0 ? ResultText : SizeText;

    public bool CanClean => Scan is { Found: true } && !IsBlocked;
}

public sealed record CleanupHistoryRow(CleanupRecord Record, string Time, string What, string Freed);

/// <summary>
/// The Maintenance tab. MainViewModel owns it: sets IsActive when the tab is
/// selected or left, persists SelectionText, says whether a game runs.
/// Nothing runs while the tab is not shown: scans start on selection (and
/// Rescan / after a clean) and are cancelled when the tab is left.
/// </summary>
public sealed partial class MaintenanceViewModel : ObservableObject
{
    private readonly ICleanupStatsStore _store;
    private readonly ISystemInfoService _info;
    private readonly ISystemToolLauncher _tools;
    private readonly IDownloadsService _downloads;
    private readonly ILocalizationService _loc;
    private CleanupStats? _stats;               // loaded on first use, not at start-up
    private CancellationTokenSource? _scanCts;
    private bool _initializing;
    private string? _rawSelection;

    public MaintenanceViewModel(IEnumerable<ICleanupTarget> targets, ICleanupStatsStore store, ISystemInfoService info,
                                ISystemToolLauncher tools, IDownloadsService downloads, ILocalizationService loc)
    {
        _store     = store;
        _info      = info;
        _tools     = tools;
        _downloads = downloads;
        _loc       = loc;
        foreach (var t in targets)
        {
            var row = new CleanupRowViewModel(t);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        Relabel();
        _loc.LanguageChanged += (_, _) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(Relabel);
    }

    /// <summary>No targets, nothing on disk: tests and tools that don't need the tab.</summary>
    internal static MaintenanceViewModel Inert() =>
        new([], new InMemoryCleanupStatsStore(), new NoInfo(), new NoTools(), new NoDownloads(), new InertLocalization());

    public ObservableCollection<CleanupRowViewModel> Rows    { get; } = [];
    public ObservableCollection<CleanupHistoryRow>   History { get; } = [];

    public event EventHandler? SettingsChanged;
    public event EventHandler<string>? StatusRequested;

    internal Func<bool>     AnyGameRunning { get; set; } = () => false;
    internal Func<DateTime> Now            { get; set; } = () => DateTime.Now;

    [ObservableProperty] private bool   _isActive;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    private bool _isCleaning;
    [ObservableProperty] private string _powerPlanText     = "";
    [ObservableProperty] private string _pageFileText      = "";
    [ObservableProperty] private string _downloadsSizeText = "";
    [ObservableProperty] private string _cleanButtonText   = "";
    [ObservableProperty] private long   _selectedBytes;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepairButtonText))]
    private bool _isRepairArmed;
    [ObservableProperty] private bool _isHistoryVisible;
    [ObservableProperty] private bool _isResetArmed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadsVisible))]
    private DownloadsViewModel? _downloadsDialog;

    public bool IsDownloadsVisible => DownloadsDialog is not null;

    public string RepairButtonText => _loc.GetString(IsRepairArmed ? "Str_Maint_RepairConfirm" : "Str_Maint_Repair");

    // ── Selection (persisted by MainViewModel as CleanupSelection) ───────────

    /// <summary>Comma-separated ticked ids. With no rows (inert), what Initialize got.</summary>
    public string? SelectionText => Rows.Count == 0 ? _rawSelection : string.Join(",", Rows.Where(r => r.IsChecked).Select(r => r.Id));

    public void Initialize(string? selection)
    {
        _rawSelection = selection;
        var ids = selection is null
            ? CleanupTargetIds.DefaultSelection.ToHashSet()
            : selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        _initializing = true;
        try { foreach (var r in Rows) r.IsChecked = ids.Contains(r.Id); }
        finally { _initializing = false; }
        UpdateSelectedTotal();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanupRowViewModel.IsChecked) or nameof(CleanupRowViewModel.CanClean) or nameof(CleanupRowViewModel.Scan))
            UpdateSelectedTotal();
        if (e.PropertyName == nameof(CleanupRowViewModel.IsChecked) && !_initializing)
            SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSelectedTotal()
    {
        SelectedBytes = Rows.Where(r => r.IsChecked && r.CanClean).Sum(r => r.Scan!.Bytes);
        CleanButtonText = string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_CleanSelected"), SizeText(SelectedBytes));
        CleanSelectedCommand.NotifyCanExecuteChanged();
    }

    // ── Tab activation and scanning ──────────────────────────────────────────

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
        {
            if (!IsCleaning) _ = RefreshAsync();   // a running clean rescans by itself when done
        }
        else
        {
            _scanCts?.Cancel();
            IsRepairArmed = false;
        }
    }

    // No scan while cleaning (spec §4.6).
    private bool CanRescan() => !IsCleaning;

    [RelayCommand(CanExecute = nameof(CanRescan))] private Task RescanAsync() => RefreshAsync();

    internal async Task RefreshAsync(bool keepResults = false)
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        var cts = _scanCts = new CancellationTokenSource();
        RefreshInfo();
        RefreshHistory();
        bool gameRunning = AnyGameRunning();
        var work = Rows.Select(r => ScanRowAsync(r, gameRunning, keepResults, cts.Token)).ToList();
        work.Add(ScanDownloadsAsync(cts.Token));
        try { await Task.WhenAll(work); }
        catch (OperationCanceledException) { }
    }

    private async Task ScanRowAsync(CleanupRowViewModel row, bool gameRunning, bool keepResults, CancellationToken ct)
    {
        row.IsBlocked = gameRunning && row.Target.BlockedWhileGameRuns;
        if (!keepResults) row.ResultText = "";
        row.SizeText = _loc.GetString("Str_Maint_Scanning");
        try
        {
            var scan = await row.Target.ScanAsync(ct);
            ct.ThrowIfCancellationRequested();
            row.Scan = scan;
            row.SizeText = scan.Found ? SizeText(scan.Bytes) : _loc.GetString("Str_Maint_NotFound");
        }
        catch (OperationCanceledException)
        {
            row.SizeText = "";
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Cleanup scan {row.Id} failed: {ex.Message}");
            row.Scan = null;
            row.SizeText = _loc.GetString("Str_Maint_ReadError");
        }
        if (row.IsBlocked) row.Tip = _loc.GetString("Str_Maint_BlockedGame");
    }

    private async Task ScanDownloadsAsync(CancellationToken ct)
    {
        try
        {
            var scan = await _downloads.ScanAsync(ct);
            ct.ThrowIfCancellationRequested();
            DownloadsSizeText = scan is { Found: true } ? SizeText(scan.Bytes) : _loc.GetString("Str_Maint_NotFound");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"Downloads scan failed: {ex.Message}");
            DownloadsSizeText = _loc.GetString("Str_Maint_ReadError");
        }
    }

    private void RefreshInfo()
    {
        var plan = _info.GetPowerPlan();
        PowerPlanText = plan?.Name ?? _loc.GetString("Str_Maint_PowerUnknown");
        var files = _info.GetPageFiles() ?? [];
        PageFileText = files.Count == 0
            ? _loc.GetString("Str_Maint_PageFileOff")
            : string.Join(" · ", files.Select(f => string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_PageFileFormat"),
                  f.Drive, MainViewModel.FormatSize(f.TotalMb),
                  _loc.GetString(f.SystemManaged ? "Str_Maint_SystemManaged" : "Str_Maint_Custom"),
                  MainViewModel.FormatSize(f.InUseMb))));
    }

    // ── Cleaning ─────────────────────────────────────────────────────────────

    private bool CanCleanSelected() => !IsCleaning && Rows.Any(r => r.IsChecked && r.CanClean);

    [RelayCommand(CanExecute = nameof(CanCleanSelected))]
    private async Task CleanSelectedAsync()
    {
        IsCleaning = true;
        _scanCts?.Cancel();
        var stats = Stats;
        long total = 0;
        try
        {
            foreach (var row in Rows.Where(r => r.IsChecked && r.CanClean).ToList())
            {
                row.ResultText = _loc.GetString("Str_Maint_Cleaning");
                try
                {
                    var result = await row.Target.CleanAsync(CancellationToken.None);
                    total += result.FreedBytes;
                    row.ResultText = ResultText(result);
                    if (result.Deleted > 0 || result.Skipped > 0 || result.FreedBytes > 0)
                        stats.Add(new CleanupRecord(Now(), row.Id, result.FreedBytes, result.Skipped));
                    Logger.Info($"Cleanup {row.Id}: freed {result.FreedBytes / (1024 * 1024)} MB, {result.Deleted} deleted, {result.Skipped} skipped");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Cleanup {row.Id} failed: {ex.Message}");
                    row.ResultText = string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_ResultFailed"), ex.Message);
                }
            }
            _store.Save(stats);
            RefreshHistory();
            StatusRequested?.Invoke(this, string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Status_CleanDone"), SizeText(total)));
        }
        finally { IsCleaning = false; }
        await RefreshAsync(keepResults: true);
    }

    private string ResultText(CleanupResult r)
    {
        string text = string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_ResultFreed"), SizeText(r.FreedBytes));
        if (r.Skipped > 0)
            text += " · " + string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_ResultSkipped"), r.Skipped);
        return text;
    }

    // ── Windows tools ────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenTool(SystemTool tool)
    {
        if (tool == SystemTool.RepairSystemFiles && !IsRepairArmed) { IsRepairArmed = true; return; }
        IsRepairArmed = false;
        if (!_tools.Launch(tool))
            StatusRequested?.Invoke(this, string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Status_ToolFailed"), tool));
    }

    // ── Downloads dialog ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenDownloadsAsync()
    {
        var dialog = new DownloadsViewModel(_downloads, _loc, OnDownloadsMoved);
        DownloadsDialog = dialog;
        await dialog.LoadAsync();
    }

    [RelayCommand]
    private void CloseDownloads()
    {
        DownloadsDialog?.Cancel();
        DownloadsDialog = null;
        if (IsActive) _ = RefreshAsync(keepResults: true);
    }

    private void OnDownloadsMoved(long bytes, int moved, int failed)
    {
        if (moved == 0) return;
        Stats.Add(new CleanupRecord(Now(), CleanupTargetIds.Downloads, bytes, failed));
        _store.Save(Stats);
        RefreshHistory();
    }

    // ── History ──────────────────────────────────────────────────────────────

    private CleanupStats Stats => _stats ??= _store.Load();

    [RelayCommand] private void ShowHistory() { RefreshHistory(); IsHistoryVisible = true; }
    [RelayCommand] private void CloseHistory() { IsHistoryVisible = false; IsResetArmed = false; }

    [RelayCommand]
    private void ResetHistory()
    {
        if (!IsResetArmed) { IsResetArmed = true; return; }
        IsResetArmed = false;
        _stats = new CleanupStats();
        _store.Save(_stats);
        RefreshHistory();
        Logger.Info("Cleanup history reset");
    }

    public string StatsTodayFreed => SizeText(Stats.LastDays(1, Now()).FreedBytes);
    public string StatsTodayCount => CountText(Stats.LastDays(1, Now()).Count);
    public string StatsWeekFreed  => SizeText(Stats.LastDays(7, Now()).FreedBytes);
    public string StatsWeekCount  => CountText(Stats.LastDays(7, Now()).Count);
    public string StatsAllFreed   => SizeText(Stats.AllTime.FreedBytes);
    public string StatsAllCount   => CountText(Stats.AllTime.Count);
    public string FreedSoFarText  => string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_FreedSoFar"), StatsAllFreed);

    /// <summary>"NVIDIA shader cache 3.4 GB (2) · Temporary files (user) 1.1 GB (5)", targets with cleanups only.</summary>
    public string ByTargetText => string.Join(" · ", Stats.ByTarget
        .Where(kv => kv.Value.Count > 0)
        .OrderByDescending(kv => kv.Value.FreedBytes)
        .Select(kv => $"{TargetName(kv.Key)} {SizeText(kv.Value.FreedBytes)} ({kv.Value.Count})"));

    private void RefreshHistory()
    {
        History.Clear();
        foreach (var r in Stats.Recent) History.Add(RowFor(r));
        foreach (var p in new[] { nameof(StatsTodayFreed), nameof(StatsTodayCount), nameof(StatsWeekFreed), nameof(StatsWeekCount),
                                  nameof(StatsAllFreed), nameof(StatsAllCount), nameof(FreedSoFarText), nameof(ByTargetText) })
            OnPropertyChanged(p);
    }

    private CultureInfo DateCulture => CultureInfo.GetCultureInfo(_loc.CurrentLanguage == Language.Turkish ? "tr-TR" : "en-US");

    private CleanupHistoryRow RowFor(CleanupRecord r)
    {
        string time = r.At.Date == Now().Date ? r.At.ToString("HH:mm", DateCulture) : r.At.ToString("d MMM HH:mm", DateCulture);
        string what = TargetName(r.TargetId);
        if (r.Skipped > 0) what += " · " + string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_ResultSkipped"), r.Skipped);
        return new CleanupHistoryRow(r, time, what, SizeText(r.FreedBytes));
    }

    private string CountText(int count) =>
        string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_MHist_Count"), count);

    private string TargetName(string id) => _loc.GetString("Str_Clean_" + id);

    private void Relabel()
    {
        foreach (var r in Rows)
        {
            r.Name = TargetName(r.Id);
            r.Tip  = _loc.GetString("Str_CleanTip_" + r.Id);
        }
        UpdateSelectedTotal();
        OnPropertyChanged(nameof(RepairButtonText));
        if (_stats is not null) RefreshHistory();
    }

    /// <summary>"845 MB", "1.2 GB", "&lt; 1 MB", "0 MB".</summary>
    internal static string SizeText(long bytes) =>
        bytes > 0 && bytes < 1024 * 1024 ? "< 1 MB" : MainViewModel.FormatSize(bytes / (1024 * 1024));

    // ── Inert stand-ins ──────────────────────────────────────────────────────

    private sealed class NoInfo : ISystemInfoService
    {
        public PowerPlanInfo? GetPowerPlan() => null;
        public IReadOnlyList<PageFileInfo> GetPageFiles() => [];
    }

    private sealed class NoTools : ISystemToolLauncher
    {
        public bool Launch(SystemTool tool) => false;
    }

    private sealed class NoDownloads : IDownloadsService
    {
        public string? FolderPath => null;
        public Task<CleanupScan> ScanAsync(CancellationToken ct) => Task.FromResult(CleanupScan.NotFound);
        public Task<IReadOnlyList<DownloadEntry>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DownloadEntry>>([]);
        public Task<IReadOnlyList<DownloadEntry>> MoveToRecycleBinAsync(IReadOnlyList<DownloadEntry> entries) => Task.FromResult(entries);
    }

    private sealed class InertLocalization : ILocalizationService
    {
        public Language CurrentLanguage => Language.English;
        public void SetLanguage(Language lang) { }
        public string GetString(string key) => key;
        public event EventHandler? LanguageChanged { add { } remove { } }
    }
}
```

> Implementer notes:
> - The test `Leaving_the_tab_cancels_the_scan` relies on `RefreshAsync` cancelling the previous `_scanCts`. `ScanRowAsync` then throws `OperationCanceledException` out of `WaitAsync(ct)` before `ScanCompleted` is set.
> - `NullProxy.Of<ISystemInfoService>()` returns null for `GetPageFiles()`, which is why `RefreshInfo` uses `?? []`. Keep it.
> - Check the `ILocalizationService` member list (`CurrentLanguage`, `SetLanguage`, `GetString`, `LanguageChanged`) against `Core/Interfaces/ILocalizationService.cs`. If it has more members, add them to `InertLocalization` the same way `FakeLocalization` in `StandbyAndTimer.Tests/TestDoubles.cs` does.

- [ ] **Step 4: Run the tests**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter FullyQualifiedName~Maintenance`
Expected: PASS (all Maintenance tests from Tasks 1–8).

- [ ] **Step 5: Commit**

```bash
git add StandbyAndTimer/ViewModels/MaintenanceViewModel.cs StandbyAndTimer.Tests/Maintenance/MaintenanceViewModelTests.cs
git commit -m "feat(maintenance): MaintenanceViewModel, scan on tab, clean selected, history, tools"
```

---

### Task 9: Settings value, MainViewModel wiring and DI

**Files:**
- Modify: `StandbyAndTimer/Core/Models/AppSettings.cs` (after `IgnoredGamePaths`)
- Modify: `StandbyAndTimer/Services/SettingsService.cs` (`Load` initializer, `Save`)
- Modify: `StandbyAndTimer/ViewModels/MainViewModel.cs` (constructor, `ApplySettings`, `BuildCurrentSettings`, the new `SelectedTab`)
- Modify: `StandbyAndTimer/Infrastructure/AppBootstrapper.cs`
- Test: `StandbyAndTimer.Tests/SettingsServiceTests.cs`, `StandbyAndTimer.Tests/Maintenance/MainViewModelMaintenanceTests.cs`

**Interfaces:**
- Consumes: `MaintenanceViewModel` (Task 8), the targets and services (Tasks 3–7).
- Produces:
  - `AppSettings.CleanupSelection` (`string?`).
  - On `MainViewModel`: `MaintenanceViewModel Maintenance`, `int SelectedTab`, and the constants `StatusTab = 0`, `MaintenanceTab = 1`, `SettingsTab = 2`. The constructor takes a new optional last parameter `MaintenanceViewModel? maintenance = null`.

- [ ] **Step 1: Write the failing tests**

Append to `StandbyAndTimer.Tests/SettingsServiceTests.cs`, inside the class:
```csharp
    [Fact]
    public void Cleanup_selection_round_trips_and_missing_stays_missing()
    {
        var s = _service.Load();
        Assert.Null(s.CleanupSelection);            // 2.5.2 key or fresh install: defaults apply
        _service.Save(s);
        using (var key = Key())
            Assert.Null(key.GetValue("CleanupSelection"));

        s.CleanupSelection = "";                    // the user unticked everything
        _service.Save(s);
        Assert.Equal("", _service.Load().CleanupSelection);

        s.CleanupSelection = "user-temp,nvidia-shader";
        _service.Save(s);
        Assert.Equal("user-temp,nvidia-shader", _service.Load().CleanupSelection);
    }
```
> `Key()` is the existing helper in `SettingsServiceTests` that opens or creates the test key. Check its exact name with `grep -n "Key()" StandbyAndTimer.Tests/SettingsServiceTests.cs`, and use whatever the 2.4.0 test uses.

`StandbyAndTimer.Tests/Maintenance/MainViewModelMaintenanceTests.cs`:
```csharp
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests.Maintenance;

public class MainViewModelMaintenanceTests
{
    private static (MainViewModel main, MaintenanceViewModel m, FakeSettingsStore store) Make()
    {
        var store = new FakeSettingsStore(null);
        var loc = new FakeLocalization();
        var settings = new SettingsViewModel(NullProxy.Of<IAutoStartService>(), loc, store, NullProxy.Of<IUpdateService>(),
                                             NullProxy.Of<IThemeService>(), NullProxy.Of<ILogTailReader>());
        var m = new MaintenanceViewModel([new FakeTarget("user-temp", new CleanupScan(true, 1, 1)),
                                          new FakeTarget("recycle-bin", new CleanupScan(true, 1, 1))],
                                         new InMemoryCleanupStatsStore(), NullProxy.Of<ISystemInfoService>(),
                                         new FakeTools(), NullProxy.Of<IDownloadsService>(), loc);
        var main = new MainViewModel(new FakeTimer(), NullProxy.Of<IStandbyPurgeService>(), NullProxy.Of<IMemoryMonitorService>(),
                                     store, loc, NullProxy.Of<IGameDetectionService>(), settings, maintenance: m);
        return (main, m, store);
    }

    [Fact]
    public void Ticking_a_row_saves_the_selection()
    {
        var (_, m, store) = Make();
        m.Initialize(null);

        m.Rows[1].IsChecked = true;

        Assert.Equal("user-temp,recycle-bin", store.Saved[^1].CleanupSelection);
    }

    [Fact]
    public void Tabs_drive_settings_open_and_maintenance_active()
    {
        var (main, m, _) = Make();

        main.SelectedTab = MainViewModel.MaintenanceTab;
        Assert.True(m.IsActive);
        Assert.False(main.Settings.IsOpen);

        main.SelectedTab = MainViewModel.SettingsTab;
        Assert.False(m.IsActive);
        Assert.True(main.Settings.IsOpen);

        main.Settings.IsOpen = false;               // e.g. something closes Settings
        Assert.Equal(MainViewModel.StatusTab, main.SelectedTab);

        main.Settings.IsOpen = true;                // tray "Settings"
        Assert.Equal(MainViewModel.SettingsTab, main.SelectedTab);
    }
}
```
> `FakeSettingsStore` and `FakeTimer` live in `StandbyAndTimer.Tests/TestDoubles.cs`. Check that `FakeSettingsStore`'s constructor takes `AppSettings?` (it does in `Harness`) and that it exposes `Saved`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release --filter "FullyQualifiedName~Cleanup_selection|FullyQualifiedName~MainViewModelMaintenanceTests"`
Expected: build errors (`CleanupSelection`, `maintenance`, `SelectedTab` missing).

- [ ] **Step 3: Add the setting**

In `AppSettings.cs`, after `IgnoredGamePaths`:
```csharp

    // v3.0.0 — ticked Maintenance cleanup rows (comma-separated target ids,
    // CleanupTargetIds). null = never saved: the default ticks apply; "" = the
    // user unticked everything. A user preference, so an import takes it.
    public string? CleanupSelection          { get; set; }
```

In `SettingsService.Load`, inside the `new AppSettings { ... }` initializer, after `AutoPurgeHotkey = ...`:
```csharp
                CleanupSelection      = key.GetValue("CleanupSelection")?.ToString(),
```

In `SettingsService.Save`, after the `AutoPurgeHotkey` line:
```csharp
            if (settings.CleanupSelection is not null) key.SetValue("CleanupSelection", settings.CleanupSelection);
            else                                      key.DeleteValue("CleanupSelection", throwOnMissingValue: false);
```

- [ ] **Step 4: Wire MainViewModel**

In the `MainViewModel` constructor signature, add a last parameter:
```csharp
        IPurgeStatsStore?           purgeStats      = null,
        MaintenanceViewModel?       maintenance     = null)
```

In the constructor body, after `SystemSettings.SettingsChanged += (_, _) => PersistSettings();`:
```csharp
        Maintenance = maintenance ?? MaintenanceViewModel.Inert();
        Maintenance.SettingsChanged += (_, _) => PersistSettings();
        Maintenance.StatusRequested += (_, text) => StatusMessage = text;
        Maintenance.AnyGameRunning  = () => _lastSnapshot?.AnyGameRunning == true;
        // The Settings tab can also be opened from outside (tray "Settings"):
        // keep the selected tab in step with Settings.IsOpen.
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SettingsViewModel.IsOpen)) return;
            if (Settings.IsOpen && SelectedTab != SettingsTab)       SelectedTab = SettingsTab;
            else if (!Settings.IsOpen && SelectedTab == SettingsTab) SelectedTab = StatusTab;
        };
```

Add these members (for example after the `Settings` / `SystemSettings` properties):
```csharp
    /// <summary>The Maintenance tab (created by DI; inert in tests).</summary>
    public MaintenanceViewModel Maintenance { get; }

    // Main TabControl: Status | Maintenance | Settings (bound to SelectedIndex).
    public const int StatusTab = 0, MaintenanceTab = 1, SettingsTab = 2;

    [ObservableProperty] private int _selectedTab;

    partial void OnSelectedTabChanged(int value)
    {
        if (Settings.IsOpen != (value == SettingsTab)) Settings.IsOpen = value == SettingsTab;
        Maintenance.IsActive = value == MaintenanceTab;
    }
```
> Check the name of the field holding the last snapshot with `grep -n "_lastSnapshot" StandbyAndTimer/ViewModels/MainViewModel.cs`. It is assigned in `ShowSnapshot`.

In `ApplySettings`, inside the `using (new InitScope(...))` block, after `SystemSettings.Initialize(...)`:
```csharp
            Maintenance.Initialize(s.CleanupSelection);
```

In `BuildCurrentSettings`, after `IgnoredGamePaths = [.. _ignoredGamePaths]`, change the last line so that it reads:
```csharp
        IgnoredGamePaths      = [.. _ignoredGamePaths],
        CleanupSelection      = Maintenance.SelectionText
```

- [ ] **Step 5: Register in DI**

In `Infrastructure/AppBootstrapper.cs`, add `using StandbyAndTimer.Services.Maintenance;`, then after `services.AddSingleton<IPurgeStatsStore, PurgeStatsStore>();` add:
```csharp
        // Maintenance tab. Row order = registration order.
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.NvidiaShader());
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.AmdShader());
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.UserTemp());
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.WindowsTemp());
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.WindowsUpdate());
        services.AddSingleton<ICleanupTarget, RecycleBinTarget>();
        services.AddSingleton<ICleanupStatsStore,  CleanupStatsStore>();
        services.AddSingleton<ISystemInfoService,  SystemInfoService>();
        services.AddSingleton<ISystemToolLauncher, SystemToolLauncher>();
        services.AddSingleton<IDownloadsService,   DownloadsService>();
        services.AddSingleton<MaintenanceViewModel>();
```
`MaintenanceViewModel` must be registered before `MainViewModel` is resolved. Registration order does not matter for DI, but keep it above `services.AddSingleton<MainViewModel>();` for readability.

> `DownloadsService`'s public constructor calls `SHGetKnownFolderPath` (fast, about 1 ms) when DI first creates it, which happens when `MainViewModel` is resolved at start-up. Nothing else in the Maintenance graph touches the disk at construction; `CleanupStats` loads on first use.

- [ ] **Step 6: Run the whole test suite and build**

Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release`
Expected: all pass (202 earlier tests plus the new ones).
Run: `dotnet build StandbyAndTimer/StandbyAndTimer.csproj -c Release -warnaserror`
Expected: 0 warnings, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add StandbyAndTimer/Core/Models/AppSettings.cs StandbyAndTimer/Services/SettingsService.cs StandbyAndTimer/ViewModels/MainViewModel.cs StandbyAndTimer/Infrastructure/AppBootstrapper.cs StandbyAndTimer.Tests/SettingsServiceTests.cs StandbyAndTimer.Tests/Maintenance/MainViewModelMaintenanceTests.cs
git commit -m "feat(maintenance): CleanupSelection setting, SelectedTab, DI wiring"
```

---

### Task 10: The Maintenance tab page, the third tab and the strings

**Files:**
- Create: `StandbyAndTimer/Views/MaintenancePanelView.xaml`, `StandbyAndTimer/Views/MaintenancePanelView.xaml.cs`
- Modify: `StandbyAndTimer/MainWindow.xaml` (lines 37 and 63–104)
- Delete: `StandbyAndTimer/Views/Converters/SettingsTabIndexConverter.cs`
- Modify: `StandbyAndTimer/Views/Strings/Strings.en-US.xaml`, `StandbyAndTimer/Views/Strings/Strings.tr-TR.xaml`

**Interfaces:**
- Consumes: `MainViewModel.Maintenance` and `SelectedTab` (Task 9); the `MaintenanceViewModel` members (Task 8).
- Produces: string keys used by Tasks 8, 11 and 12, listed in Step 4. The view binds only to the members listed in Task 8.

- [ ] **Step 1: Create the view**

`StandbyAndTimer/Views/MaintenancePanelView.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace StandbyAndTimer.Views;

// The Maintenance tab page. Built by MainWindow's TabItem.ContentTemplate
// only when the tab is selected (so it costs nothing at start-up).
public partial class MaintenancePanelView : UserControl
{
    public MaintenancePanelView() => InitializeComponent();
}
```

`StandbyAndTimer/Views/MaintenancePanelView.xaml`:
```xml
<UserControl x:Class="StandbyAndTimer.Views.MaintenancePanelView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:models="clr-namespace:StandbyAndTimer.Core.Models"
             FontFamily="{StaticResource AppFont}" FontSize="13">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
        <Style x:Key="ValueTextStyle" TargetType="TextBlock">
            <Setter Property="FontSize"          Value="12"/>
            <Setter Property="Foreground"        Value="{DynamicResource AppFgDim}"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
            <Setter Property="TextTrimming"      Value="CharacterEllipsis"/>
        </Style>
        <Style x:Key="ToolButtonStyle" TargetType="Button" BasedOn="{StaticResource GhostButtonStyle}">
            <Setter Property="Height"                     Value="30"/>
            <Setter Property="Margin"                     Value="0,4,0,0"/>
            <Setter Property="HorizontalContentAlignment" Value="Center"/>
        </Style>
    </UserControl.Resources>

    <!-- Maintenance TAB page: same boxed layout as Settings. Left: cleanup
         rows (tick, name, size / result), Downloads, Clean selected, freed so
         far (opens the history). Right: power plan + virtual memory and
         buttons that open Windows' own tools. No ScrollViewer: fits 760x520. -->
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="12"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <Grid Grid.Row="0" Grid.Column="0">
            <TextBlock Text="{DynamicResource Str_Maint_CleanupSection}" Style="{StaticResource PageSectionLabelStyle}"/>
            <!-- Rescan: the sizes are read once on selection; this reads them again. -->
            <Button Content="{DynamicResource Str_Maint_Rescan}" Command="{Binding RescanCommand}"
                    Style="{StaticResource GhostButtonStyle}" Height="22" MinWidth="64" FontSize="11"
                    HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,0,0,4"/>
        </Grid>
        <TextBlock Grid.Row="0" Grid.Column="2" Text="{DynamicResource Str_Maint_WindowsSection}" Style="{StaticResource PageSectionLabelStyle}"/>

        <!-- ═══════════════ LEFT: CLEANUP ═══════════════ -->
        <Border Grid.Row="1" Grid.Column="0" Style="{StaticResource SettingsGroupStyle}">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>  <!-- rows -->
                    <RowDefinition Height="Auto"/>  <!-- downloads -->
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>  <!-- clean button -->
                    <RowDefinition Height="Auto"/>  <!-- freed so far -->
                </Grid.RowDefinitions>

                <ItemsControl Grid.Row="0" ItemsSource="{Binding Rows}" Focusable="False">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <!-- Transparent background: the whole row shows the tooltip. -->
                            <Grid Margin="0,3" Background="Transparent" ToolTip="{Binding Tip}">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <CheckBox IsChecked="{Binding IsChecked}"
                                          IsEnabled="{Binding CanClean}"
                                          Style="{StaticResource DarkCheckBoxStyle}"
                                          VerticalAlignment="Center"/>
                                <TextBlock Grid.Column="1" Text="{Binding Name}"
                                           Style="{StaticResource SettingTitleStyle}"
                                           TextTrimming="CharacterEllipsis"
                                           VerticalAlignment="Center" Margin="6,0,10,0"/>
                                <TextBlock Grid.Column="2" Text="{Binding DisplayText}"
                                           Style="{StaticResource ValueTextStyle}" MaxWidth="190"/>
                            </Grid>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>

                <Grid Grid.Row="1" Margin="0,3" Background="Transparent" ToolTip="{DynamicResource Str_CleanTip_downloads}">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="22"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="1" Text="{DynamicResource Str_Clean_downloads}"
                               Style="{StaticResource SettingTitleStyle}" VerticalAlignment="Center" Margin="0,0,10,0"/>
                    <TextBlock Grid.Column="2" Text="{Binding DownloadsSizeText}" Style="{StaticResource ValueTextStyle}" Margin="0,0,8,0"/>
                    <Button Grid.Column="3" Content="{DynamicResource Str_Maint_Choose}"
                            Command="{Binding OpenDownloadsCommand}"
                            Style="{StaticResource GhostButtonStyle}" Height="26" MinWidth="64"/>
                </Grid>

                <Button Grid.Row="3" Margin="0,8,0,0" Height="32"
                        Content="{Binding CleanButtonText}"
                        Command="{Binding CleanSelectedCommand}"
                        Style="{StaticResource DarkButtonStyle}"/>

                <TextBlock Grid.Row="4" Margin="0,8,0,0" HorizontalAlignment="Center"
                           Text="{Binding FreedSoFarText}" FontSize="11"
                           Foreground="{DynamicResource AccentText}" Cursor="Hand"
                           ToolTip="{DynamicResource Str_Maint_HistoryTip}">
                    <TextBlock.InputBindings>
                        <MouseBinding MouseAction="LeftClick" Command="{Binding ShowHistoryCommand}"/>
                    </TextBlock.InputBindings>
                </TextBlock>
            </Grid>
        </Border>

        <!-- ═══════════════ RIGHT: WINDOWS ═══════════════ -->
        <Border Grid.Row="1" Grid.Column="2" Style="{StaticResource SettingsGroupStyle}">
            <StackPanel>
                <Grid Margin="0,2,0,4" Background="Transparent" ToolTip="{DynamicResource Str_Maint_PowerTip}">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <StackPanel VerticalAlignment="Center" Margin="0,0,10,0">
                        <TextBlock Text="{DynamicResource Str_Maint_PowerPlan}" Style="{StaticResource SettingTitleStyle}"/>
                        <TextBlock Text="{Binding PowerPlanText}" Style="{StaticResource ValueTextStyle}"/>
                    </StackPanel>
                    <Button Grid.Column="1" Content="{DynamicResource Str_Maint_Change}"
                            Command="{Binding OpenToolCommand}" CommandParameter="{x:Static models:SystemTool.PowerOptions}"
                            Style="{StaticResource GhostButtonStyle}" Height="26" MinWidth="72" VerticalAlignment="Center"/>
                </Grid>

                <Grid Margin="0,4" Background="Transparent" ToolTip="{DynamicResource Str_Maint_AdjustTip}">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <StackPanel VerticalAlignment="Center" Margin="0,0,10,0">
                        <TextBlock Text="{DynamicResource Str_Maint_VirtualMemory}" Style="{StaticResource SettingTitleStyle}"/>
                        <TextBlock Text="{Binding PageFileText}" Style="{StaticResource ValueTextStyle}" TextWrapping="Wrap" TextTrimming="None"/>
                    </StackPanel>
                    <Button Grid.Column="1" Content="{DynamicResource Str_Maint_Adjust}"
                            Command="{Binding OpenToolCommand}" CommandParameter="{x:Static models:SystemTool.VirtualMemory}"
                            Style="{StaticResource GhostButtonStyle}" Height="26" MinWidth="72" VerticalAlignment="Center"/>
                </Grid>

                <Border Height="1" Margin="0,8,0,4" Background="{DynamicResource SeparatorBrush}"/>

                <Button Content="{DynamicResource Str_Maint_OptimizeDrives}" ToolTip="{DynamicResource Str_Maint_OptimizeDrivesTip}"
                        Command="{Binding OpenToolCommand}" CommandParameter="{x:Static models:SystemTool.OptimizeDrives}"
                        Style="{StaticResource ToolButtonStyle}"/>
                <Button Content="{DynamicResource Str_Maint_DiskCleanup}" ToolTip="{DynamicResource Str_Maint_DiskCleanupTip}"
                        Command="{Binding OpenToolCommand}" CommandParameter="{x:Static models:SystemTool.DiskCleanup}"
                        Style="{StaticResource ToolButtonStyle}"/>
                <Button Content="{Binding RepairButtonText}" ToolTip="{DynamicResource Str_Maint_RepairTip}"
                        Command="{Binding OpenToolCommand}" CommandParameter="{x:Static models:SystemTool.RepairSystemFiles}"
                        Style="{StaticResource ToolButtonStyle}"/>
            </StackPanel>
        </Border>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Add the third tab and the SelectedTab binding in MainWindow.xaml**

- Delete the line `<conv:SettingsTabIndexConverter x:Key="SettingsTabIndexConv"/>` (line 37). If the `conv:` xmlns is now unused, leave it; it is harmless.
- Change the TabControl's `SelectedIndex` attribute to:
```xml
                        SelectedIndex="{Binding SelectedTab, Mode=TwoWay}">
```
- Between the Status `</TabItem>` and the Settings `<TabItem ...>`, insert:
```xml
                <!-- Built only while selected (ContentTemplate), DataContext =
                     Content = MainViewModel.Maintenance. Costs nothing at start-up. -->
                <TabItem Header="{DynamicResource Str_Tab_Maintenance}" Content="{Binding Maintenance}">
                    <TabItem.ContentTemplate>
                        <DataTemplate>
                            <views:MaintenancePanelView/>
                        </DataTemplate>
                    </TabItem.ContentTemplate>
                </TabItem>
```
- Update the comment above the TabControl. It says "SelectedIndex is bound to Settings.IsOpen"; it should now say it is bound to `MainViewModel.SelectedTab` (0 Status, 1 Maintenance, 2 Settings), which keeps `Settings.IsOpen` and `Maintenance.IsActive` in step.
- Delete the converter file: `git rm StandbyAndTimer/Views/Converters/SettingsTabIndexConverter.cs`.

- [ ] **Step 3: Update App.xaml.cs**

The comment at `App.xaml.cs` near `_viewModel.Settings.IsOpen = true;` (`grep -n "Settings.IsOpen is bound" StandbyAndTimer/App.xaml.cs`) must say:
```csharp
    // Settings.IsOpen keeps MainViewModel.SelectedTab on the Settings tab (and refreshes the logs).
```
No code change is needed: `MainViewModel` turns `IsOpen = true` into `SelectedTab = SettingsTab`.

- [ ] **Step 4: Add the strings**

Add to `Strings.en-US.xaml` (one block, before the closing `</ResourceDictionary>`):
```xml
    <!-- ═══════════ Maintenance tab (3.0.0) ═══════════ -->
    <sys:String x:Key="Str_Tab_Maintenance">Maintenance</sys:String>
    <sys:String x:Key="Str_Maint_CleanupSection">CLEANUP</sys:String>
    <sys:String x:Key="Str_Maint_WindowsSection">WINDOWS</sys:String>
    <sys:String x:Key="Str_Clean_nvidia-shader">NVIDIA shader cache</sys:String>
    <sys:String x:Key="Str_Clean_amd-shader">AMD shader cache</sys:String>
    <sys:String x:Key="Str_Clean_user-temp">Temporary files (user)</sys:String>
    <sys:String x:Key="Str_Clean_windows-temp">Windows temporary files</sys:String>
    <sys:String x:Key="Str_Clean_windows-update">Windows Update leftovers</sys:String>
    <sys:String x:Key="Str_Clean_recycle-bin">Recycle Bin</sys:String>
    <sys:String x:Key="Str_Clean_downloads">Downloads</sys:String>
    <sys:String x:Key="Str_CleanTip_nvidia-shader">Shaders NVIDIA drivers compiled for your games. Clean once after a driver update or if a game stutters because of a broken cache; games rebuild them and can stutter on first launch.</sys:String>
    <sys:String x:Key="Str_CleanTip_amd-shader">Shaders AMD drivers compiled for your games. Clean once after a driver update or if a game stutters because of a broken cache; games rebuild them and can stutter on first launch.</sys:String>
    <sys:String x:Key="Str_CleanTip_user-temp">Your temp folder. Files changed in the last 24 hours and files in use are left alone.</sys:String>
    <sys:String x:Key="Str_CleanTip_windows-temp">Windows' temp folder. Files changed in the last 24 hours and files in use are left alone.</sys:String>
    <sys:String x:Key="Str_CleanTip_windows-update">Update files Windows already downloaded and installed. Safe to clean; Windows downloads again what it still needs.</sys:String>
    <sys:String x:Key="Str_CleanTip_recycle-bin">Empties the Recycle Bin on every drive. This cannot be undone.</sys:String>
    <sys:String x:Key="Str_CleanTip_downloads">Your own files. Choose what to send to the Recycle Bin; nothing is deleted for good.</sys:String>
    <sys:String x:Key="Str_Maint_Scanning">scanning…</sys:String>
    <sys:String x:Key="Str_Maint_NotFound">not found</sys:String>
    <sys:String x:Key="Str_Maint_ReadError">could not read</sys:String>
    <sys:String x:Key="Str_Maint_BlockedGame">Close the game first: shader caches are in use while it runs.</sys:String>
    <sys:String x:Key="Str_Maint_Cleaning">cleaning…</sys:String>
    <sys:String x:Key="Str_Maint_ResultFreed">{0} freed</sys:String>
    <sys:String x:Key="Str_Maint_ResultSkipped">{0} skipped</sys:String>
    <sys:String x:Key="Str_Maint_ResultFailed">failed: {0}</sys:String>
    <sys:String x:Key="Str_Maint_CleanSelected">Clean selected ({0})</sys:String>
    <sys:String x:Key="Str_Maint_FreedSoFar">Freed so far: {0}  ›</sys:String>
    <sys:String x:Key="Str_Maint_HistoryTip">Click to see the maintenance history</sys:String>
    <sys:String x:Key="Str_Maint_Choose">Choose…</sys:String>
    <sys:String x:Key="Str_Maint_Rescan">Rescan</sys:String>
    <sys:String x:Key="Str_Maint_PowerPlan">Power plan</sys:String>
    <sys:String x:Key="Str_Maint_PowerUnknown">unknown</sys:String>
    <sys:String x:Key="Str_Maint_Change">Change</sys:String>
    <sys:String x:Key="Str_Maint_PowerTip">The active Windows power plan. Change opens Windows' Power Options.</sys:String>
    <sys:String x:Key="Str_Maint_VirtualMemory">Virtual memory</sys:String>
    <sys:String x:Key="Str_Maint_PageFileFormat">{0} {1} · {2} · {3} in use</sys:String>
    <sys:String x:Key="Str_Maint_SystemManaged">system managed</sys:String>
    <sys:String x:Key="Str_Maint_Custom">custom size</sys:String>
    <sys:String x:Key="Str_Maint_PageFileOff">off</sys:String>
    <sys:String x:Key="Str_Maint_Adjust">Adjust</sys:String>
    <sys:String x:Key="Str_Maint_AdjustTip">Opens Performance Options. There: Advanced → Virtual memory → Change.</sys:String>
    <sys:String x:Key="Str_Maint_OptimizeDrives">Optimize drives</sys:String>
    <sys:String x:Key="Str_Maint_OptimizeDrivesTip">Opens Windows' Optimize Drives: defragments HDDs, sends TRIM to SSDs.</sys:String>
    <sys:String x:Key="Str_Maint_DiskCleanup">Disk Cleanup</sys:String>
    <sys:String x:Key="Str_Maint_DiskCleanupTip">Opens Windows' Disk Cleanup, with more categories.</sys:String>
    <sys:String x:Key="Str_Maint_Repair">Repair system files</sys:String>
    <sys:String x:Key="Str_Maint_RepairConfirm">Takes 10–30 min. Press again</sys:String>
    <sys:String x:Key="Str_Maint_RepairTip">Runs DISM, then SFC, in a console window. Only needed if Windows misbehaves; DISM needs internet.</sys:String>
    <sys:String x:Key="Str_Status_CleanDone">Cleanup done: {0} freed</sys:String>
    <sys:String x:Key="Str_Status_ToolFailed">Could not open {0}; details in the log</sys:String>
```

Add to `Strings.tr-TR.xaml`:
```xml
    <!-- ═══════════ Bakım sekmesi (3.0.0) ═══════════ -->
    <sys:String x:Key="Str_Tab_Maintenance">Bakım</sys:String>
    <sys:String x:Key="Str_Maint_CleanupSection">TEMİZLİK</sys:String>
    <sys:String x:Key="Str_Maint_WindowsSection">WINDOWS</sys:String>
    <sys:String x:Key="Str_Clean_nvidia-shader">NVIDIA shader önbelleği</sys:String>
    <sys:String x:Key="Str_Clean_amd-shader">AMD shader önbelleği</sys:String>
    <sys:String x:Key="Str_Clean_user-temp">Geçici dosyalar (kullanıcı)</sys:String>
    <sys:String x:Key="Str_Clean_windows-temp">Windows geçici dosyaları</sys:String>
    <sys:String x:Key="Str_Clean_windows-update">Windows Update artıkları</sys:String>
    <sys:String x:Key="Str_Clean_recycle-bin">Geri Dönüşüm Kutusu</sys:String>
    <sys:String x:Key="Str_Clean_downloads">İndirilenler</sys:String>
    <sys:String x:Key="Str_CleanTip_nvidia-shader">NVIDIA sürücüsünün oyunların için derlediği shader'lar. Sürücü güncellemesinden sonra ya da bozuk önbellek yüzünden takılma varsa bir kez temizle; oyunlar yeniden derler ve ilk açılışta takılabilir.</sys:String>
    <sys:String x:Key="Str_CleanTip_amd-shader">AMD sürücüsünün oyunların için derlediği shader'lar. Sürücü güncellemesinden sonra ya da bozuk önbellek yüzünden takılma varsa bir kez temizle; oyunlar yeniden derler ve ilk açılışta takılabilir.</sys:String>
    <sys:String x:Key="Str_CleanTip_user-temp">Senin geçici dosya klasörün. Son 24 saatte değişen ve kullanımdaki dosyalara dokunulmaz.</sys:String>
    <sys:String x:Key="Str_CleanTip_windows-temp">Windows'un geçici dosya klasörü. Son 24 saatte değişen ve kullanımdaki dosyalara dokunulmaz.</sys:String>
    <sys:String x:Key="Str_CleanTip_windows-update">Windows'un indirip kurduğu güncelleme dosyaları. Temizlemek güvenli; gerekirse Windows yeniden indirir.</sys:String>
    <sys:String x:Key="Str_CleanTip_recycle-bin">Tüm sürücülerdeki Geri Dönüşüm Kutusu'nu boşaltır. Geri alınamaz.</sys:String>
    <sys:String x:Key="Str_CleanTip_downloads">Senin dosyaların. Geri Dönüşüm Kutusu'na gidecekleri sen seçersin; hiçbir şey kalıcı silinmez.</sys:String>
    <sys:String x:Key="Str_Maint_Scanning">taranıyor…</sys:String>
    <sys:String x:Key="Str_Maint_NotFound">bulunamadı</sys:String>
    <sys:String x:Key="Str_Maint_ReadError">okunamadı</sys:String>
    <sys:String x:Key="Str_Maint_BlockedGame">Önce oyunu kapat: oyun açıkken shader önbelleği kullanımda.</sys:String>
    <sys:String x:Key="Str_Maint_Cleaning">temizleniyor…</sys:String>
    <sys:String x:Key="Str_Maint_ResultFreed">{0} boşaltıldı</sys:String>
    <sys:String x:Key="Str_Maint_ResultSkipped">{0} atlandı</sys:String>
    <sys:String x:Key="Str_Maint_ResultFailed">başarısız: {0}</sys:String>
    <sys:String x:Key="Str_Maint_CleanSelected">Seçilenleri temizle ({0})</sys:String>
    <sys:String x:Key="Str_Maint_FreedSoFar">Şimdiye kadar boşaltılan: {0}  ›</sys:String>
    <sys:String x:Key="Str_Maint_HistoryTip">Bakım geçmişini görmek için tıkla</sys:String>
    <sys:String x:Key="Str_Maint_Choose">Seç…</sys:String>
    <sys:String x:Key="Str_Maint_Rescan">Yenile</sys:String>
    <sys:String x:Key="Str_Maint_PowerPlan">Güç planı</sys:String>
    <sys:String x:Key="Str_Maint_PowerUnknown">bilinmiyor</sys:String>
    <sys:String x:Key="Str_Maint_Change">Değiştir</sys:String>
    <sys:String x:Key="Str_Maint_PowerTip">Etkin Windows güç planı. Değiştir, Windows'un Güç Seçenekleri'ni açar.</sys:String>
    <sys:String x:Key="Str_Maint_VirtualMemory">Sanal bellek</sys:String>
    <sys:String x:Key="Str_Maint_PageFileFormat">{0} {1} · {2} · {3} kullanımda</sys:String>
    <sys:String x:Key="Str_Maint_SystemManaged">sistem yönetimli</sys:String>
    <sys:String x:Key="Str_Maint_Custom">özel boyut</sys:String>
    <sys:String x:Key="Str_Maint_PageFileOff">kapalı</sys:String>
    <sys:String x:Key="Str_Maint_Adjust">Ayarla</sys:String>
    <sys:String x:Key="Str_Maint_AdjustTip">Performans Seçenekleri açılır. Orada: Gelişmiş → Sanal bellek → Değiştir.</sys:String>
    <sys:String x:Key="Str_Maint_OptimizeDrives">Sürücüleri iyileştir</sys:String>
    <sys:String x:Key="Str_Maint_OptimizeDrivesTip">Windows'un Sürücüleri İyileştir aracını açar: HDD'yi birleştirir, SSD'ye TRIM gönderir.</sys:String>
    <sys:String x:Key="Str_Maint_DiskCleanup">Disk Temizleme</sys:String>
    <sys:String x:Key="Str_Maint_DiskCleanupTip">Windows'un Disk Temizleme aracını açar; daha fazla kategori var.</sys:String>
    <sys:String x:Key="Str_Maint_Repair">Sistem dosyalarını onar</sys:String>
    <sys:String x:Key="Str_Maint_RepairConfirm">10–30 dk sürer. Tekrar bas</sys:String>
    <sys:String x:Key="Str_Maint_RepairTip">Bir konsol penceresinde önce DISM, sonra SFC çalışır. Sadece Windows sorun çıkarıyorsa gerekir; DISM internet ister.</sys:String>
    <sys:String x:Key="Str_Status_CleanDone">Temizlik bitti: {0} boşaltıldı</sys:String>
    <sys:String x:Key="Str_Status_ToolFailed">{0} açılamadı; ayrıntı log'da</sys:String>
```

- [ ] **Step 5: Build, test, render**

Run: `dotnet build StandbyAndTimer/StandbyAndTimer.csproj -c Release -warnaserror`. Expected: 0 warnings.
Run: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release`. Expected: all pass.
Run: `dotnet run --project tools/UiRender -c Release`.
Expected at this point: one FAIL, `IsOpen=true → SelectedIndex=2, expected 1`. Task 13 fixes UiRender; that FAIL is the only one allowed now. Check it with `grep FAIL`.

- [ ] **Step 6: Commit**

```bash
git add -A StandbyAndTimer/Views/MaintenancePanelView.xaml StandbyAndTimer/Views/MaintenancePanelView.xaml.cs StandbyAndTimer/MainWindow.xaml StandbyAndTimer/App.xaml.cs StandbyAndTimer/Views/Strings StandbyAndTimer/Views/Converters
git commit -m "feat(maintenance): Maintenance tab page between Status and Settings"
```

---

### Task 11: Downloads dialog and Maintenance history dialog

**Files:**
- Create: `StandbyAndTimer/Views/Overlays/DownloadsOverlay.xaml(.cs)`, `StandbyAndTimer/Views/Overlays/CleanupHistoryOverlay.xaml(.cs)`
- Modify: `StandbyAndTimer/MainWindow.xaml` (overlay list, near line 109)
- Modify: both strings files

**Interfaces:**
- Consumes: from `MaintenanceViewModel`: `IsDownloadsVisible`, `DownloadsDialog`, `CloseDownloadsCommand`, `IsHistoryVisible`, the `Stats*` texts, `ByTargetText`, `History`, `ResetHistoryCommand`, `IsResetArmed`, `CloseHistoryCommand`. From `DownloadsViewModel`: `Items`, `Message`, `SelectedText`, `IsLoading`, `SelectAllCommand`, `SelectNoneCommand`, `MoveCommand`. The row properties (`Icon`, `Name`, `SizeText`, `DateText`, `IsChecked`).

- [ ] **Step 1: Code-behind for both dialogs**

`StandbyAndTimer/Views/Overlays/DownloadsOverlay.xaml.cs`:
```csharp
using System.Windows.Controls;
using System.Windows.Input;

namespace StandbyAndTimer.Views.Overlays;

public partial class DownloadsOverlay : UserControl
{
    public DownloadsOverlay() => InitializeComponent();

    // Clicks inside the dialog must not reach the overlay's close binding.
    private void DialogMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
```
`StandbyAndTimer/Views/Overlays/CleanupHistoryOverlay.xaml.cs`: the same file with the class name `CleanupHistoryOverlay`.

- [ ] **Step 2: DownloadsOverlay.xaml**

```xml
<UserControl x:Class="StandbyAndTimer.Views.Overlays.DownloadsOverlay"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             FontFamily="{StaticResource AppFont}" FontSize="13">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    </UserControl.Resources>

    <!-- Downloads picker (DataContext = MaintenanceViewModel). Largest first;
         ticked items go to the Recycle Bin. The list is virtualised and exists
         only while the dialog is open. The dim overlay closes it. -->
    <Border Background="{DynamicResource ModalOverlayBrush}"
            Visibility="{Binding IsDownloadsVisible, Converter={StaticResource BoolToVis}}">
        <Border.InputBindings>
            <MouseBinding MouseAction="LeftClick" Command="{Binding CloseDownloadsCommand}"/>
        </Border.InputBindings>
        <Border HorizontalAlignment="Center" VerticalAlignment="Center"
                Background="{DynamicResource Surface2Bg}" BorderBrush="{DynamicResource BorderColor}"
                BorderThickness="1" CornerRadius="{StaticResource RadiusLarge}"
                Padding="22,18,22,16" Width="560" Height="440"
                MouseLeftButtonDown="DialogMouseDown"
                DataContext="{Binding DownloadsDialog}">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <StackPanel Grid.Row="0" Margin="0,0,0,10">
                    <StackPanel Orientation="Horizontal">
                        <Ellipse Width="9" Height="9" Fill="{DynamicResource AccentPrimary}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                        <TextBlock Text="{DynamicResource Str_Dl_Title}" FontSize="14" FontWeight="SemiBold" Foreground="{DynamicResource AppFg}"/>
                    </StackPanel>
                    <TextBlock Text="{DynamicResource Str_Dl_Subtitle}" FontSize="12" Foreground="{DynamicResource AppFgDim}" Margin="17,0,0,0"/>
                </StackPanel>

                <Grid Grid.Row="1" Margin="0,0,0,6">
                    <StackPanel Orientation="Horizontal">
                        <Button Content="{DynamicResource Str_Dl_All}"  Command="{Binding SelectAllCommand}"  Style="{StaticResource GhostButtonStyle}" Height="26" MinWidth="80" Margin="0,0,6,0"/>
                        <Button Content="{DynamicResource Str_Dl_None}" Command="{Binding SelectNoneCommand}" Style="{StaticResource GhostButtonStyle}" Height="26" MinWidth="80"/>
                    </StackPanel>
                    <TextBlock Text="{Binding SelectedText}" FontSize="12" Foreground="{DynamicResource AppFgDim}"
                               HorizontalAlignment="Right" VerticalAlignment="Center"/>
                </Grid>

                <Border Grid.Row="2" Style="{StaticResource SettingsGroupStyle}" Margin="0" Padding="6,4">
                    <Grid>
                        <TextBlock Text="{Binding Message}" FontSize="12" Foreground="{DynamicResource AppFgDim}"
                                   HorizontalAlignment="Center" VerticalAlignment="Center" TextWrapping="Wrap"/>
                        <ListBox ItemsSource="{Binding Items}"
                                 Background="Transparent" BorderThickness="0"
                                 VirtualizingPanel.IsVirtualizing="True"
                                 VirtualizingPanel.VirtualizationMode="Recycling"
                                 ScrollViewer.HorizontalScrollBarVisibility="Disabled"
                                 HorizontalContentAlignment="Stretch">
                            <ListBox.ItemTemplate>
                                <DataTemplate>
                                    <Grid Margin="2,2">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="Auto"/>
                                            <ColumnDefinition Width="20"/>
                                            <ColumnDefinition Width="*"/>
                                            <ColumnDefinition Width="90"/>
                                            <ColumnDefinition Width="80"/>
                                        </Grid.ColumnDefinitions>
                                        <CheckBox IsChecked="{Binding IsChecked}" Style="{StaticResource DarkCheckBoxStyle}" VerticalAlignment="Center"/>
                                        <TextBlock Grid.Column="1" Text="{Binding Icon}" FontSize="11" VerticalAlignment="Center" Margin="4,0,0,0"/>
                                        <TextBlock Grid.Column="2" Text="{Binding Name}" FontSize="12" TextTrimming="CharacterEllipsis"
                                                   ToolTip="{Binding Entry.Path}" VerticalAlignment="Center"/>
                                        <TextBlock Grid.Column="3" Text="{Binding DateText}" FontSize="11" Foreground="{DynamicResource AppFgDim}"
                                                   HorizontalAlignment="Right" VerticalAlignment="Center"/>
                                        <TextBlock Grid.Column="4" Text="{Binding SizeText}" FontSize="12" FontWeight="SemiBold"
                                                   Foreground="{DynamicResource AccentText}" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                                    </Grid>
                                </DataTemplate>
                            </ListBox.ItemTemplate>
                        </ListBox>
                    </Grid>
                </Border>

                <Grid Grid.Row="3" Margin="0,12,0,0">
                    <Button Content="{DynamicResource Str_Dl_Close}"
                            Command="{Binding DataContext.CloseDownloadsCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                            Style="{StaticResource GhostButtonStyle}" HorizontalAlignment="Left" MinWidth="80" Height="30"/>
                    <Button Content="{DynamicResource Str_Dl_Move}" Command="{Binding MoveCommand}"
                            Style="{StaticResource DarkButtonStyle}" HorizontalAlignment="Right" MinWidth="160" Height="30"/>
                </Grid>
            </Grid>
        </Border>
    </Border>
</UserControl>
```
> The inner dialog `Border`'s `DataContext` is `DownloadsDialog`. The Close button therefore reaches back to `MaintenanceViewModel` through `RelativeSource AncestorType=UserControl`, whose DataContext is the Maintenance VM.
> If the ListBox draws the default selection highlight, remove it with `ItemContainerStyle="{StaticResource ...}"`. Use the same ListBoxItem style the game list uses: `grep -n "GameListBoxStyle" -A30 StandbyAndTimer/Views/Styles/Theme.xaml` shows which ItemContainerStyle it sets.

- [ ] **Step 3: CleanupHistoryOverlay.xaml**

```xml
<UserControl x:Class="StandbyAndTimer.Views.Overlays.CleanupHistoryOverlay"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             FontFamily="{StaticResource AppFont}" FontSize="13">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
        <Style x:Key="TileStyle" TargetType="Border" BasedOn="{StaticResource SettingsGroupStyle}">
            <Setter Property="Margin"  Value="0"/>
            <Setter Property="Padding" Value="12,8"/>
        </Style>
        <Style x:Key="TileLabel" TargetType="TextBlock">
            <Setter Property="FontSize"   Value="10"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Foreground" Value="{DynamicResource AppFgDim}"/>
        </Style>
        <Style x:Key="TileValue" TargetType="TextBlock">
            <Setter Property="FontSize"   Value="18"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Foreground" Value="{DynamicResource AccentText}"/>
            <Setter Property="Margin"     Value="0,2,0,0"/>
        </Style>
        <Style x:Key="TileCount" TargetType="TextBlock">
            <Setter Property="FontSize"   Value="11"/>
            <Setter Property="Foreground" Value="{DynamicResource AppFgDim}"/>
        </Style>
    </UserControl.Resources>

    <!-- Maintenance history (DataContext = MaintenanceViewModel): totals for
         today / 7 days / all time, per item, and the last 20 cleanups. Kept in
         cleanup-history.json. The dim overlay closes it; clicks inside do not. -->
    <Border Background="{DynamicResource ModalOverlayBrush}"
            Visibility="{Binding IsHistoryVisible, Converter={StaticResource BoolToVis}}">
        <Border.InputBindings>
            <MouseBinding MouseAction="LeftClick" Command="{Binding CloseHistoryCommand}"/>
        </Border.InputBindings>
        <Border HorizontalAlignment="Center" VerticalAlignment="Center"
                Background="{DynamicResource Surface2Bg}" BorderBrush="{DynamicResource BorderColor}"
                BorderThickness="1" CornerRadius="{StaticResource RadiusLarge}"
                Padding="22,18,22,16" Width="560" MaxHeight="476"
                MouseLeftButtonDown="DialogMouseDown">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <StackPanel Grid.Row="0" Margin="0,0,0,12">
                    <StackPanel Orientation="Horizontal">
                        <Ellipse Width="9" Height="9" Fill="{DynamicResource AccentPrimary}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                        <TextBlock Text="{DynamicResource Str_MHist_Title}" FontSize="14" FontWeight="SemiBold" Foreground="{DynamicResource AppFg}"/>
                    </StackPanel>
                    <TextBlock Text="{DynamicResource Str_MHist_Subtitle}" FontSize="12" Foreground="{DynamicResource AppFgDim}" Margin="17,0,0,0"/>
                </StackPanel>

                <Grid Grid.Row="1">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <Border Grid.Column="0" Style="{StaticResource TileStyle}">
                        <StackPanel>
                            <TextBlock Text="{DynamicResource Str_History_Today}" Style="{StaticResource TileLabel}"/>
                            <TextBlock Text="{Binding StatsTodayFreed}" Style="{StaticResource TileValue}"/>
                            <TextBlock Text="{Binding StatsTodayCount}" Style="{StaticResource TileCount}"/>
                        </StackPanel>
                    </Border>
                    <Border Grid.Column="2" Style="{StaticResource TileStyle}">
                        <StackPanel>
                            <TextBlock Text="{DynamicResource Str_History_Week}" Style="{StaticResource TileLabel}"/>
                            <TextBlock Text="{Binding StatsWeekFreed}" Style="{StaticResource TileValue}"/>
                            <TextBlock Text="{Binding StatsWeekCount}" Style="{StaticResource TileCount}"/>
                        </StackPanel>
                    </Border>
                    <Border Grid.Column="4" Style="{StaticResource TileStyle}">
                        <StackPanel>
                            <TextBlock Text="{DynamicResource Str_History_AllTime}" Style="{StaticResource TileLabel}"/>
                            <TextBlock Text="{Binding StatsAllFreed}" Style="{StaticResource TileValue}"/>
                            <TextBlock Text="{Binding StatsAllCount}" Style="{StaticResource TileCount}"/>
                        </StackPanel>
                    </Border>
                </Grid>

                <TextBlock Grid.Row="2" Margin="2,10,0,0" Text="{Binding ByTargetText}" FontSize="11"
                           Foreground="{DynamicResource AppFgDim}" TextWrapping="Wrap"/>

                <TextBlock Grid.Row="3" Text="{DynamicResource Str_MHist_Recent}" Style="{StaticResource PageSectionLabelStyle}" Margin="2,12,0,6"/>

                <Border Grid.Row="4" Style="{StaticResource SettingsGroupStyle}" Margin="0" Padding="12,6">
                    <Grid>
                        <TextBlock Text="{DynamicResource Str_MHist_Empty}" FontSize="12" Foreground="{DynamicResource AppFgDim}"
                                   HorizontalAlignment="Center" VerticalAlignment="Center" Margin="0,10">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock">
                                    <Setter Property="Visibility" Value="Collapsed"/>
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding History.Count}" Value="0">
                                            <Setter Property="Visibility" Value="Visible"/>
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                        <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Focusable="False">
                            <ItemsControl ItemsSource="{Binding History}" Focusable="False">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <Grid Margin="0,3">
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="96"/>
                                                <ColumnDefinition Width="*"/>
                                                <ColumnDefinition Width="Auto"/>
                                            </Grid.ColumnDefinitions>
                                            <TextBlock Text="{Binding Time}" FontSize="12" Foreground="{DynamicResource AppFgDim}"/>
                                            <TextBlock Grid.Column="1" Text="{Binding What}" FontSize="12" TextTrimming="CharacterEllipsis"/>
                                            <TextBlock Grid.Column="2" Text="{Binding Freed}" FontSize="12" FontWeight="SemiBold"
                                                       Foreground="{DynamicResource AccentText}" Margin="10,0,0,0"/>
                                        </Grid>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                        </ScrollViewer>
                    </Grid>
                </Border>

                <Grid Grid.Row="5" Margin="0,14,0,0">
                    <Button Command="{Binding ResetHistoryCommand}" HorizontalAlignment="Left" MinWidth="120" Height="30">
                        <Button.Style>
                            <Style TargetType="Button" BasedOn="{StaticResource GhostButtonStyle}">
                                <Setter Property="Content" Value="{DynamicResource Str_History_Reset}"/>
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding IsResetArmed}" Value="True">
                                        <Setter Property="Content" Value="{DynamicResource Str_History_ResetConfirm}"/>
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Button.Style>
                    </Button>
                    <Button Content="{DynamicResource Str_History_Close}" Command="{Binding CloseHistoryCommand}"
                            Style="{StaticResource DarkButtonStyle}" HorizontalAlignment="Right" MinWidth="80" Height="30"/>
                </Grid>
            </Grid>
        </Border>
    </Border>
</UserControl>
```

- [ ] **Step 4: Add both dialogs to MainWindow**

After the `<overlays:PurgeHistoryOverlay .../>` line:
```xml
            <overlays:DownloadsOverlay      DataContext="{Binding Maintenance}" Grid.Row="0" Grid.RowSpan="3" Panel.ZIndex="18"/>
            <overlays:CleanupHistoryOverlay DataContext="{Binding Maintenance}" Grid.Row="0" Grid.RowSpan="3" Panel.ZIndex="18"/>
```

- [ ] **Step 5: Add the dialog strings**

`Strings.en-US.xaml`:
```xml
    <sys:String x:Key="Str_Dl_Title">Downloads</sys:String>
    <sys:String x:Key="Str_Dl_Subtitle">Chosen items go to the Recycle Bin, where you can restore them</sys:String>
    <sys:String x:Key="Str_Dl_All">Select all</sys:String>
    <sys:String x:Key="Str_Dl_None">Select none</sys:String>
    <sys:String x:Key="Str_Dl_Selected">{0} selected · {1}</sys:String>
    <sys:String x:Key="Str_Dl_Move">Move to Recycle Bin</sys:String>
    <sys:String x:Key="Str_Dl_Loading">Reading the folder…</sys:String>
    <sys:String x:Key="Str_Dl_Empty">Nothing in Downloads.</sys:String>
    <sys:String x:Key="Str_Dl_NotFound">Downloads folder not found.</sys:String>
    <sys:String x:Key="Str_Dl_Moved">{0} moved to the Recycle Bin</sys:String>
    <sys:String x:Key="Str_Dl_Failed">{0} items could not be moved</sys:String>
    <sys:String x:Key="Str_Dl_Close">Close</sys:String>
    <sys:String x:Key="Str_MHist_Title">Maintenance history</sys:String>
    <sys:String x:Key="Str_MHist_Subtitle">What the Maintenance tab cleaned; kept across restarts</sys:String>
    <sys:String x:Key="Str_MHist_Count">{0} cleanups</sys:String>
    <sys:String x:Key="Str_MHist_Recent">LAST 20</sys:String>
    <sys:String x:Key="Str_MHist_Empty">Nothing cleaned yet.</sys:String>
```
`Strings.tr-TR.xaml`:
```xml
    <sys:String x:Key="Str_Dl_Title">İndirilenler</sys:String>
    <sys:String x:Key="Str_Dl_Subtitle">Seçtiklerin Geri Dönüşüm Kutusu'na gider, oradan geri alabilirsin</sys:String>
    <sys:String x:Key="Str_Dl_All">Tümünü seç</sys:String>
    <sys:String x:Key="Str_Dl_None">Hiçbirini seçme</sys:String>
    <sys:String x:Key="Str_Dl_Selected">{0} seçili · {1}</sys:String>
    <sys:String x:Key="Str_Dl_Move">Geri Dönüşüm Kutusu'na taşı</sys:String>
    <sys:String x:Key="Str_Dl_Loading">Klasör okunuyor…</sys:String>
    <sys:String x:Key="Str_Dl_Empty">İndirilenler boş.</sys:String>
    <sys:String x:Key="Str_Dl_NotFound">İndirilenler klasörü bulunamadı.</sys:String>
    <sys:String x:Key="Str_Dl_Moved">{0} Geri Dönüşüm Kutusu'na taşındı</sys:String>
    <sys:String x:Key="Str_Dl_Failed">{0} öğe taşınamadı</sys:String>
    <sys:String x:Key="Str_Dl_Close">Kapat</sys:String>
    <sys:String x:Key="Str_MHist_Title">Bakım geçmişi</sys:String>
    <sys:String x:Key="Str_MHist_Subtitle">Bakım sekmesinin ne temizlediği; yeniden başlatınca da kalır</sys:String>
    <sys:String x:Key="Str_MHist_Count">{0} temizlik</sys:String>
    <sys:String x:Key="Str_MHist_Recent">SON 20</sys:String>
    <sys:String x:Key="Str_MHist_Empty">Henüz bir şey temizlenmedi.</sys:String>
```

- [ ] **Step 6: Build and commit**

Run: `dotnet build StandbyAndTimer/StandbyAndTimer.csproj -c Release -warnaserror`. Expected: 0 warnings.
```bash
git add StandbyAndTimer/Views/Overlays/DownloadsOverlay.xaml StandbyAndTimer/Views/Overlays/DownloadsOverlay.xaml.cs StandbyAndTimer/Views/Overlays/CleanupHistoryOverlay.xaml StandbyAndTimer/Views/Overlays/CleanupHistoryOverlay.xaml.cs StandbyAndTimer/MainWindow.xaml StandbyAndTimer/Views/Strings
git commit -m "feat(maintenance): Downloads picker and maintenance history dialogs"
```

---

### Task 12: Two-tab usage guide

**Files:**
- Modify: `StandbyAndTimer/Views/Overlays/AboutOverlay.xaml` (rows 116–147, the guide `ScrollViewer`)
- Modify: both strings files

**Interfaces:**
- Consumes: the existing `HelpSectionStyle`, `HelpTextStyle` and `HelpBulletStyle` in AboutOverlay; `SegmentTabControlStyle` and `SegmentTabItemStyle` from Theme.xaml.

- [ ] **Step 1: Wrap the guide in a TabControl**

Replace the `<!-- Guide -->` `<ScrollViewer Grid.Row="1" ...> ... </ScrollViewer>` block with:
```xml
                <!-- Guide: General (the original guide) | Maintenance. -->
                <TabControl Grid.Row="1" Margin="0,4,14,0"
                            Style="{StaticResource SegmentTabControlStyle}"
                            ItemContainerStyle="{StaticResource SegmentTabItemStyle}">
                    <TabItem Header="{DynamicResource Str_About_TabGeneral}">
                        <!-- PASTE HERE the original <ScrollViewer ...> ... </ScrollViewer> block unchanged,
                             removing only its Grid.Row="1" attribute. -->
                    </TabItem>
                    <TabItem Header="{DynamicResource Str_About_TabMaintenance}">
                        <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Focusable="False">
                            <StackPanel Margin="0,0,14,0">
                                <TextBlock Text="{DynamicResource Str_HelpM_Intro}" Style="{StaticResource HelpTextStyle}"
                                           Foreground="{DynamicResource AppFgDim}" Margin="0,4,0,0"/>

                                <TextBlock Text="{DynamicResource Str_HelpM_CleanTitle}" Style="{StaticResource HelpSectionStyle}"/>
                                <ContentControl Content="{DynamicResource Str_HelpM_Shader}"    Style="{StaticResource HelpBulletStyle}"/>
                                <ContentControl Content="{DynamicResource Str_HelpM_Temp}"      Style="{StaticResource HelpBulletStyle}"/>
                                <ContentControl Content="{DynamicResource Str_HelpM_Update}"    Style="{StaticResource HelpBulletStyle}"/>
                                <ContentControl Content="{DynamicResource Str_HelpM_Bin}"       Style="{StaticResource HelpBulletStyle}"/>
                                <ContentControl Content="{DynamicResource Str_HelpM_Downloads}" Style="{StaticResource HelpBulletStyle}"/>

                                <TextBlock Text="{DynamicResource Str_HelpM_WindowsTitle}" Style="{StaticResource HelpSectionStyle}"/>
                                <ContentControl Content="{DynamicResource Str_HelpM_Power}"       Style="{StaticResource HelpBulletStyle}"/>
                                <ContentControl Content="{DynamicResource Str_HelpM_Optimize}"    Style="{StaticResource HelpBulletStyle}"/>
                                <ContentControl Content="{DynamicResource Str_HelpM_DiskCleanup}" Style="{StaticResource HelpBulletStyle}"/>
                                <ContentControl Content="{DynamicResource Str_HelpM_Repair}"      Style="{StaticResource HelpBulletStyle}"/>

                                <TextBlock Text="{DynamicResource Str_HelpM_NoteTitle}" Style="{StaticResource HelpSectionStyle}"/>
                                <ContentControl Content="{DynamicResource Str_HelpM_Safety}"  Style="{StaticResource HelpBulletStyle}"/>
                                <ContentControl Content="{DynamicResource Str_HelpM_Account}" Style="{StaticResource HelpBulletStyle}"/>
                            </StackPanel>
                        </ScrollViewer>
                    </TabItem>
                </TabControl>
```
The original block is in the file already; move it into the first `TabItem`. Do not retype it.

- [ ] **Step 2: Guide strings**

`Strings.en-US.xaml`:
```xml
    <sys:String x:Key="Str_About_TabGeneral">General</sys:String>
    <sys:String x:Key="Str_About_TabMaintenance">Maintenance</sys:String>
    <sys:String x:Key="Str_HelpM_Intro">The Maintenance tab cleans what costs disk space or causes stutter and opens Windows' own tools. Nothing runs by itself: you choose what to clean and when.</sys:String>
    <sys:String x:Key="Str_HelpM_CleanTitle">CLEANUP</sys:String>
    <sys:String x:Key="Str_HelpM_Shader">Shader caches (NVIDIA, AMD): clean them once after a driver update, or when a game stutters because of a broken cache. Cleaning them regularly is wrong: every game rebuilds its shaders and stutters on its first launch. They cannot be cleaned while a listed game runs.</sys:String>
    <sys:String x:Key="Str_HelpM_Temp">Temporary files: safe to clean now and then. Files changed in the last 24 hours and files in use are left alone, so running installers and programs keep working.</sys:String>
    <sys:String x:Key="Str_HelpM_Update">Windows Update leftovers: update files that are already installed. Safe to clean; Windows downloads again whatever it still needs.</sys:String>
    <sys:String x:Key="Str_HelpM_Bin">Recycle Bin: emptying it is final. Restore what you need first.</sys:String>
    <sys:String x:Key="Str_HelpM_Downloads">Downloads: your own files. Choose… lists them largest first; what you tick goes to the Recycle Bin, where you can restore it.</sys:String>
    <sys:String x:Key="Str_HelpM_WindowsTitle">WINDOWS</sys:String>
    <sys:String x:Key="Str_HelpM_Power">Power plan and virtual memory show what Windows uses now. Change and Adjust open Windows' own pages; the app changes nothing there.</sys:String>
    <sys:String x:Key="Str_HelpM_Optimize">Optimize drives: Windows' tool. It defragments hard disks and sends TRIM to SSDs (SSDs are never defragmented), so it is safe for both.</sys:String>
    <sys:String x:Key="Str_HelpM_DiskCleanup">Disk Cleanup: Windows' own cleaner, with more categories (for example old Windows installations).</sys:String>
    <sys:String x:Key="Str_HelpM_Repair">Repair system files: runs DISM, then SFC. Only needed if Windows misbehaves (crashes, broken system apps); it takes 10–30 minutes and DISM needs internet. There is no registry repair: registry cleaners do not make Windows faster and can break it.</sys:String>
    <sys:String x:Key="Str_HelpM_NoteTitle">GOOD TO KNOW</sys:String>
    <sys:String x:Key="Str_HelpM_Safety">Cleaning never leaves the listed folders and never follows links out of them; the folders themselves are kept. "Freed so far" opens the history of what was cleaned.</sys:String>
    <sys:String x:Key="Str_HelpM_Account">The app runs as administrator, so it cleans the temp and Downloads folders of the account that approved UAC. On an administrator account those are your own folders.</sys:String>
```
`Strings.tr-TR.xaml`:
```xml
    <sys:String x:Key="Str_About_TabGeneral">Genel</sys:String>
    <sys:String x:Key="Str_About_TabMaintenance">Bakım</sys:String>
    <sys:String x:Key="Str_HelpM_Intro">Bakım sekmesi disk alanı kaplayan ya da takılmaya yol açan şeyleri temizler ve Windows'un kendi araçlarını açar. Hiçbir şey kendiliğinden çalışmaz: neyi ne zaman temizleyeceğini sen seçersin.</sys:String>
    <sys:String x:Key="Str_HelpM_CleanTitle">TEMİZLİK</sys:String>
    <sys:String x:Key="Str_HelpM_Shader">Shader önbellekleri (NVIDIA, AMD): sürücü güncellemesinden sonra ya da bozuk önbellek yüzünden bir oyun takılıyorsa bir kez temizle. Düzenli temizlemek yanlış: her oyun shader'larını yeniden derler ve ilk açılışta takılır. Listedeki bir oyun açıkken temizlenemez.</sys:String>
    <sys:String x:Key="Str_HelpM_Temp">Geçici dosyalar: ara sıra temizlemek güvenli. Son 24 saatte değişen ve kullanımdaki dosyalara dokunulmaz, böylece çalışan kurulumlar ve programlar bozulmaz.</sys:String>
    <sys:String x:Key="Str_HelpM_Update">Windows Update artıkları: zaten kurulmuş güncelleme dosyaları. Temizlemek güvenli; Windows gerekeni yeniden indirir.</sys:String>
    <sys:String x:Key="Str_HelpM_Bin">Geri Dönüşüm Kutusu: boşaltmak kalıcıdır. Gerekenleri önce geri al.</sys:String>
    <sys:String x:Key="Str_HelpM_Downloads">İndirilenler: senin dosyaların. Seç… en büyükten başlayarak listeler; tiklediklerin Geri Dönüşüm Kutusu'na gider, oradan geri alabilirsin.</sys:String>
    <sys:String x:Key="Str_HelpM_WindowsTitle">WINDOWS</sys:String>
    <sys:String x:Key="Str_HelpM_Power">Güç planı ve sanal bellek, Windows'un şu an kullandığını gösterir. Değiştir ve Ayarla Windows'un kendi sayfalarını açar; uygulama orada bir şey değiştirmez.</sys:String>
    <sys:String x:Key="Str_HelpM_Optimize">Sürücüleri iyileştir: Windows'un aracı. Hard diski birleştirir, SSD'ye TRIM gönderir (SSD asla birleştirilmez); ikisi için de güvenli.</sys:String>
    <sys:String x:Key="Str_HelpM_DiskCleanup">Disk Temizleme: Windows'un kendi temizleyicisi, daha fazla kategoriyle (örneğin eski Windows kurulumları).</sys:String>
    <sys:String x:Key="Str_HelpM_Repair">Sistem dosyalarını onar: önce DISM, sonra SFC çalışır. Sadece Windows sorun çıkarıyorsa gerekir (çökmeler, bozuk sistem uygulamaları); 10–30 dakika sürer ve DISM internet ister. Kayıt defteri onarımı yok: kayıt defteri temizleyicileri Windows'u hızlandırmaz, bozabilir.</sys:String>
    <sys:String x:Key="Str_HelpM_NoteTitle">BİLMEKTE FAYDA VAR</sys:String>
    <sys:String x:Key="Str_HelpM_Safety">Temizlik listedeki klasörlerin dışına çıkmaz, bağlantıları takip edip dışarı gitmez; klasörlerin kendisi silinmez. "Şimdiye kadar boşaltılan" yazısı neyin temizlendiğini gösteren geçmişi açar.</sys:String>
    <sys:String x:Key="Str_HelpM_Account">Uygulama yönetici olarak çalıştığı için UAC'yi onaylayan hesabın temp ve İndirilenler klasörlerini temizler. Yönetici hesabında bunlar senin kendi klasörlerindir.</sys:String>
```

- [ ] **Step 3: Build and commit**

Run: `dotnet build StandbyAndTimer/StandbyAndTimer.csproj -c Release -warnaserror`. Expected: 0 warnings.
```bash
git add StandbyAndTimer/Views/Overlays/AboutOverlay.xaml StandbyAndTimer/Views/Strings
git commit -m "feat(maintenance): usage guide with General and Maintenance tabs"
```

---

### Task 13: UiRender, renders and checks for the new screens

**Files:**
- Modify: `tools/UiRender/Program.cs`

**Interfaces:**
- Consumes: `MaintenanceViewModel` (its constructor and commands), `MainViewModel` (`maintenance:` parameter, `SelectedTab`), and the existing UiRender helpers `Pump()`, `Save(root, path)`, `CheckLayout(root, tag)`, `CheckToolTips(root, tag)`, `Walk(...)`, `Log(...)`, `Null<T>()`.

- [ ] **Step 1: Add fakes to UiRender**

Add next to `FakeGlobalTimer` in `tools/UiRender/Program.cs`:
```csharp
// Maintenance rows with fixed sizes: one not found, one blocked by a game.
public sealed class FakeCleanupTarget(string id, CleanupScan scan, bool blocked = false) : ICleanupTarget
{
    public string Id => id;
    public bool BlockedWhileGameRuns => blocked;
    public Task<CleanupScan> ScanAsync(CancellationToken ct) => Task.FromResult(scan);
    public Task<CleanupResult> CleanAsync(CancellationToken ct) => Task.FromResult(new CleanupResult(scan.Bytes, scan.Files, 2));
}

public sealed class FakeSystemInfo : ISystemInfoService
{
    public PowerPlanInfo? GetPowerPlan() => new(Guid.Empty, "Ultimate Performance");
    public IReadOnlyList<PageFileInfo> GetPageFiles() => [new("C:", 4096, 1210, true)];
}

// InMemoryCleanupStatsStore is internal to the app; UiRender cannot see internals.
public sealed class MemCleanupStore : ICleanupStatsStore
{
    private CleanupStats _stats = new();
    public CleanupStats Load() => _stats;
    public void Save(CleanupStats stats) => _stats = stats;
}

public sealed class FakeDownloads : IDownloadsService
{
    private static readonly DownloadEntry[] Entries =
    [
        new(@"C:\Users\x\Downloads\Win11_24H2.iso", "Win11_24H2.iso", false, 5_800L << 20, new DateTime(2026, 9, 3)),
        new(@"C:\Users\x\Downloads\SteamSetup.exe", "SteamSetup.exe", false, 2L << 20, new DateTime(2026, 10, 1)),
        new(@"C:\Users\x\Downloads\screenshots", "screenshots", true, 640L << 20, new DateTime(2026, 10, 12)),
        new(@"C:\Users\x\Downloads\driver_566.36.exe", "driver_566.36.exe", false, 712L << 20, new DateTime(2026, 8, 20)),
    ];
    public string? FolderPath => @"C:\Users\x\Downloads";
    public Task<CleanupScan> ScanAsync(CancellationToken ct) => Task.FromResult(new CleanupScan(true, Entries.Sum(e => e.Bytes), Entries.Length));
    public Task<IReadOnlyList<DownloadEntry>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DownloadEntry>>(Entries);
    public Task<IReadOnlyList<DownloadEntry>> MoveToRecycleBinAsync(IReadOnlyList<DownloadEntry> entries) => Task.FromResult<IReadOnlyList<DownloadEntry>>([]);
}
```
Add these usings at the top if missing: `StandbyAndTimer.Core.Interfaces` and `StandbyAndTimer.Core.Models` (both are already there).

- [ ] **Step 2: Construct the Maintenance VM and pass it in**

Replace the `var vm = new MainViewModel(...)` construction (line ~342) with:
```csharp
        var maintenance = new MaintenanceViewModel(
        [
            new FakeCleanupTarget("nvidia-shader", new CleanupScan(true, 1_200L << 20, 3400), blocked: true),
            new FakeCleanupTarget("amd-shader", CleanupScan.NotFound, blocked: true),
            new FakeCleanupTarget("user-temp", new CleanupScan(true, 845L << 20, 1200)),
            new FakeCleanupTarget("windows-temp", new CleanupScan(true, 120L << 20, 300)),
            new FakeCleanupTarget("windows-update", new CleanupScan(true, 2_300L << 20, 40)),
            new FakeCleanupTarget("recycle-bin", new CleanupScan(true, 310L << 20, 25)),
        ], new MemCleanupStore(), new FakeSystemInfo(), Null<ISystemToolLauncher>(), new FakeDownloads(), loc);
        var vm = new MainViewModel(Null<ITimerResolutionService>(), Null<IStandbyPurgeService>(),
                                   Null<IMemoryMonitorService>(), Null<ISettingsService>(), loc,
                                   Null<IGameDetectionService>(), settingsVm, systemVm, maintenance: maintenance);
        // UiRender has no InternalsVisibleTo (only StandbyAndTimer.Tests does), so the
        // internal members are reached by reflection, as the purge history sample already is.
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        maintenance.Initialize(null);
        typeof(MaintenanceViewModel).GetProperty("AnyGameRunning", Any)!.SetValue(maintenance, (Func<bool>)(() => true));   // NVIDIA row: blocked
        typeof(MaintenanceViewModel).GetProperty("Now", Any)!.SetValue(maintenance, (Func<DateTime>)(() => new DateTime(2026, 10, 16, 22, 0, 0)));
        var cleanupStats = (CleanupStats)typeof(MaintenanceViewModel).GetProperty("Stats", Any)!.GetValue(maintenance)!;
        foreach (var (at, id, mb, skipped) in new[] {
            (new DateTime(2026, 10, 9, 18, 0, 0), "windows-update", 2_100L, 0),
            (new DateTime(2026, 10, 16, 20, 5, 0), "user-temp", 812L, 14),
            (new DateTime(2026, 10, 16, 21, 40, 0), "nvidia-shader", 1_150L, 0) })
            cleanupStats.Add(new CleanupRecord(at, id, mb << 20, skipped));
        // RefreshAsync is internal: one helper for the renders below.
        void RefreshMaintenance() =>
            ((Task)typeof(MaintenanceViewModel).GetMethod("RefreshAsync", Any)!.Invoke(maintenance, [false])!).GetAwaiter().GetResult();
```
> `CleanupStats.Add` and `CleanupStats` itself are public, so only the property getters need reflection. The fake targets complete synchronously, so `GetAwaiter().GetResult()` on the UI thread cannot deadlock.

- [ ] **Step 3: Fix the Settings tab index check**

Change (line ~801):
```csharp
            if (tabs.SelectedIndex != 1) { failures++; Log($"  FAIL IsOpen=true → SelectedIndex={tabs.SelectedIndex}, expected 1"); }
```
to:
```csharp
            if (tabs.SelectedIndex != MainViewModel.SettingsTab) { failures++; Log($"  FAIL IsOpen=true → SelectedIndex={tabs.SelectedIndex}, expected {MainViewModel.SettingsTab}"); }
```

- [ ] **Step 4: Render the Maintenance tab and its dialogs**

Right after the Settings page block (after `Save(root, Path.Combine(outDir, $"{tag}-2-settings.png"));` and its `CheckLayout`), add:
```csharp
            // Maintenance tab: lazily built page, rows from the fake targets.
            vm.SelectedTab = MainViewModel.MaintenanceTab;
            Pump();
            RefreshMaintenance();
            Pump();
            Save(root, Path.Combine(outDir, $"{tag}-6-maintenance.png"));
            failures += CheckLayout(root, tag + " maintenance");
            failures += CheckToolTips(root, tag + " maintenance");
            {
                int rows = Walk(root).OfType<StandbyAndTimer.Views.MaintenancePanelView>().Count();
                bool ok = rows == 1;
                if (!ok) failures++;
                Log($"  {(ok ? "OK  " : "FAIL")} [{tag}] maintenance page built once on selection ({rows})");
            }
            maintenance.OpenDownloadsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            maintenance.DownloadsDialog!.Items[0].IsChecked = true;
            Pump();
            Save(root, Path.Combine(outDir, $"{tag}-6-downloads.png"));
            failures += CheckLayout(root, tag + " downloads");
            maintenance.CloseDownloadsCommand.Execute(null);
            maintenance.ShowHistoryCommand.Execute(null);
            Pump();
            Save(root, Path.Combine(outDir, $"{tag}-6-mhistory.png"));
            failures += CheckLayout(root, tag + " maintenance history");
            maintenance.CloseHistoryCommand.Execute(null);
            vm.SelectedTab = MainViewModel.StatusTab;
            Pump();
```
> The fake targets complete synchronously, so `GetAwaiter().GetResult()` on the UI thread does not deadlock. Every await in them is already complete.

In the overlay loop that saves `-4-about...`, add a render of the guide's Maintenance tab after the existing about render. Find it with `grep -n '"-4-about' tools/UiRender/Program.cs`, then add after `Save(...about...)`:
```csharp
                var guideTabs = Walk(root).OfType<StandbyAndTimer.Views.Overlays.AboutOverlay>().Single();
                var tc = Walk(guideTabs).OfType<TabControl>().Single();
                tc.SelectedIndex = 1; Pump();
                Save(root, Path.Combine(outDir, $"{tag}-4-about-maintenance.png"));
                failures += CheckLayout(root, $"{tag} about maintenance");
                tc.SelectedIndex = 0; Pump();
```

- [ ] **Step 5: Docs screenshots**

In the `--docs` block's page loop (the array with `("settings", ...)`, `("history", ...)`, `("system", ...)`, `("guide", ...)`), add a first entry:
```csharp
                        ("maintenance", v => { vm.SelectedTab = v ? MainViewModel.MaintenanceTab : MainViewModel.StatusTab; if (v) RefreshMaintenance(); }),
```
This writes `docs/screenshot-maintenance.png`. Task 14 adds it to the README.

- [ ] **Step 6: Run UiRender and look at the renders**

Run: `dotnet run --project tools/UiRender -c Release`
Expected: `RESULT: all binding checks passed` and no `FAIL` lines (`... | grep FAIL` prints nothing).
Open and check by eye:
- `ui-render-out/Dark-tr-TR-6-maintenance.png` and `White-en-US-6-maintenance.png`:
  - 6 rows plus Downloads, all fitting;
  - the NVIDIA row dimmed (blocked) and AMD "not found";
  - the clean button reading "(3,2 GB)" / "(3.2 GB)";
  - the right box showing "Ultimate Performance" and the page file line.
- `Dark-en-US-6-downloads.png`: largest first, one row ticked.
- `Black-tr-TR-6-mhistory.png`.
- `Dark-tr-TR-4-about-maintenance.png`.

- [ ] **Step 7: Commit**

```bash
git add tools/UiRender/Program.cs
git commit -m "test(uirender): render and check the Maintenance tab, dialogs and guide"
```

---

### Task 14: Real-machine checks, measurements, beta release

**Files:**
- Modify: `StandbyAndTimer/StandbyAndTimer.csproj` (`<Version>3.0.0-beta1</Version>`)
- Modify: `README.md` (add `docs/screenshot-maintenance.png` to the gallery, and one line each in the English and Turkish "What it does" / "Ne işe yarar" lists)
- Modify: `CLAUDE.md` (add a `Services/Maintenance/` line under the Architecture tree, and `MaintenanceViewModel` under ViewModels)

The steps that need administrator rights or UAC go through the user, as in earlier releases. Ask before each UAC prompt.

- [ ] **Step 1: Full verification**

Run:
- `dotnet build StandbyAndTimer/StandbyAndTimer.csproj -c Release -warnaserror`. Expected: 0 warnings.
- `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release`. Expected: all pass.
- `dotnet run --project tools/UiRender -c Release -- --docs`. Expected: no FAIL; `docs/screenshot-maintenance.png` is written and refreshed.

- [ ] **Step 2: Real cleanup on the user's PC (elevated build from a scratch copy)**

1. Publish to a scratch folder: `dotnet publish StandbyAndTimer/StandbyAndTimer.csproj -p:PublishProfile=win-x64-single -p:PublishDir=<scratchpad>/m30/`.
2. Ask the user, then start it elevated.
3. Open the Maintenance tab. Note each row's size. Compare `user-temp` with the size of `%TEMP%`: files changed in the last 24 h are excluded, so the app's number must be lower or equal.
4. Tick only `user-temp`, then Clean. Compare the "freed" figure with the `%TEMP%` size before and after.
5. Check that the history shows one entry.
6. Open each tool button once: Optimize drives, Disk Cleanup, Change (power plan), Adjust (virtual memory). Repair: press once (armed), then leave it.
7. Downloads: create `Downloads\sat-test.txt`, choose it, Move. Check that it is in the Recycle Bin, restore it, then delete it.

- [ ] **Step 3: Measure against 2.5.2 (release gate)**

- **Tray RAM and CPU:** 2.5.2 (installed) against the scratch 3.0.0 build, both started with `-hidden`, alternating twice: 45 s to settle, then 60 s measured.
  - Recorded: private MB, the Task Manager "Memory" figure (`WorkingSetPrivate`), threads and CPU %.
  - **Pass:** within ±3 MB and ±0.3 % CPU of 2.5.2.
- **Start-up and open-from-tray:** the PrintWindow method, 3 runs each.
  - **Pass:** "drawn" within ±40 ms of 2.5.2, and 0 empty samples.
- If either fails, stop, find the cause and fix it before going on.

- [ ] **Step 4: Version, docs, PR**

1. Set `<Version>3.0.0-beta1</Version>`. Update the README gallery and lists, and CLAUDE.md.
2. Commit:
   ```
   chore: 3.0.0-beta1, README and CLAUDE.md for the Maintenance tab

   Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
   ```
3. Push and open a PR. The body summarises the tab, the safety rules, the measurements and the checks, and ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
4. Wait for CI.

- [ ] **Step 5: Ask the user, then release the beta**

Ask: merge and publish `v3.0.0-beta1` as a pre-release, and install it on the PC?

On yes:
1. Squash-merge the PR.
2. Run `.\build-installer.ps1 -Version 3.0.0-beta1`.
3. Run `gh release create v3.0.0-beta1 --prerelease --target main`, attaching the setup and its `.sha256`, with the SHA256 line in the notes (format as v2.5.0-beta3).
4. Install it on the PC, comparing the settings and both history files before and after.
5. Put the setup on the desktop and remove older setups there (the user's standing preference).

The final `3.0.0` comes later, after the user has tested the beta.
