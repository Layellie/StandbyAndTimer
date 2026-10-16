# Maintenance additions (3.0.0) Implementation Plan

> Executed inline with superpowers:executing-plans, test first in every task.

**Goal:** the additions of spec §9: five new cleanup rows (all always visible, no hiding: the user's decision), four Windows rows (HAGS, background recording, memory integrity, Game Mode).

**Spec:** `docs/superpowers/specs/2026-10-16-maintenance-tab-design.md` §9 (and §4 for the deletion rules, which do not change).

## Global Constraints

Same as `2026-10-16-maintenance-tab.md`:
- All P/Invoke in `NativeMethods.cs`; no new packages; every string in both languages.
- Persisted ids never renamed; new ids: `directx-shader`, `steam-shader`, `driver-leftovers`, `delivery-optimization`, `crash-dumps`.
- No background work: everything runs on tab selection, Rescan or Clean.
- Fixed 760×520 window, no page ScrollViewer; UiRender passes in 4 themes × 2 languages.
- Strict build is `dotnet build StandbyAndTimer/StandbyAndTimer.csproj -c Release -warnaserror --no-incremental` (an incremental build after `dotnet test` hides analyzer warnings).
- Tests: `dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release`. UiRender: `dotnet run --project tools/UiRender -c Release`.

## Review Focus

1. Steam's `libraryfolders.vdf` is user-editable text: a malformed file or odd paths give no roots outside `steamapps\shadercache` of each library (Task 1 test `Steam_libraries_from_libraryfolders_vdf`).
2. The Delivery Optimization clean never deletes files itself; a failing cmdlet is an error on the row (Task 2 tests).

---

### Task 1: New folder targets

**Files:** `Core/Models/Cleanup.cs`, `Services/Maintenance/FolderCleanupTarget.cs`; test `Maintenance/FolderCleanupTargetTests.cs`.

- `FolderCleanupTarget` gains a constructor taking `Func<IReadOnlyList<string>> roots` (read at each scan / clean, so a new Steam library is found).
- `CleanupTargetIds`: the five new ids.
- `CleanupTargets`: `DirectXShader()`, `SteamShader()`, `DriverLeftovers()`, `CrashDumps()`. `internal static IReadOnlyList<string> SteamShaderRoots(string? steamPath, string? libraryFoldersVdf)` parses `"path"  "D:\\SteamLibrary"` lines (unescape `\\`), always includes the Steam folder itself, returns `<lib>\steamapps\shadercache`, distinct, only rooted paths.
- Tests: Steam parsing (normal file, escaped paths, malformed lines, null inputs); roots read at scan time; ids, locations and the blocked flag of each factory.

### Task 2: Delivery Optimization target

**Files:** `Services/Maintenance/DeliveryOptimizationTarget.cs`, `SystemToolLauncher.cs` (shared System32 path helper not needed); test `Maintenance/DeliveryOptimizationTargetTests.cs`.

- `internal sealed class DeliveryOptimizationTarget(string cacheFolder, Func<bool> deleteCache) : ICleanupTarget`, public parameterless constructor uses the real folder and runs `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -Command Delete-DeliveryOptimizationCache -Force` (no window, 60 s timeout, exit code 0 = ok).
- Scan = `SafeFolderCleaner.Scan` of the folder (unreadable = not found). Clean = scan, run the cmdlet (false → `InvalidOperationException`, the row shows the error), scan again, freed = before − after (never negative), skipped = files left.
- Tests: freed from before/after with a fake runner that empties a temp folder; failing runner throws; missing folder → not found and the runner is not called.

### Task 3: Windows rows

**Files:** `Core/Models/SystemInfo.cs` (`GamingInfo` record, `SystemTool` members appended), `Core/Interfaces/ISystemInfoService.cs` (`GamingInfo GetGamingInfo()`), `Services/Maintenance/SystemInfoService.cs`, `SystemToolLauncher.cs`, `MaintenanceViewModel.cs`; tests `SystemInfoTests.cs`, `MaintenanceViewModelTests.cs`.

- `public sealed record GamingInfo(bool? Hags, bool BackgroundRecording, bool MemoryIntegrity, bool GameMode)`; `Hags` null = Windows default.
- Pure helpers from raw registry values: `Hags(object? hwSchMode)`, `BackgroundRecording(object? appCapture, object? historical)`, `MemoryIntegrity(object? enabled)`, `GameMode(object? autoGameMode)`.
- `SystemTool` appended: `GraphicsSettings, CaptureSettings, CoreIsolation, GameModeSettings` → `%SystemRoot%\explorer.exe` with the fixed URI.
- View model: `HagsText`, `RecordingText`, `MemoryIntegrityText`, `GameModeText` set in `RefreshInfo`.
- Tests: each helper's mapping (on/off/missing/odd types); `StartInfoFor` for the new tools; texts after a refresh with a fake info service.

### Task 4: View, strings, guide, DI, UiRender

**Files:** `MaintenancePanelView.xaml`, both strings files, `AboutOverlay.xaml`, `AppBootstrapper.cs`, `tools/UiRender/Program.cs`.

- DI registers the targets in the §9.1 order.
- Rows: margin 2.5 px (2 px if UiRender reports anything outside its card; if 2 px is not enough either, stop and ask the user).
- Windows card: four one-line rows (name · state · small Open button) under Virtual memory; tools stay at the bottom.
- Strings: row names and tips, Windows rows, guide bullets (Maintenance tab of the guide).
- UiRender: the fake targets are all eleven rows; renders checked by eye.
- Verify (build, tests, UiRender, `--docs`), commit, rebuild the installer for the user.
