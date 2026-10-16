# Maintenance round 2 Implementation Plan

> Executed inline with superpowers:executing-plans, test first in every task.

**Goal:** spec §10: one Shader caches row with a chooser, system crash dumps, Windows logs, Windows.old, NVIDIA downloader in driver leftovers, the refresh rate on the timer card, Windows 10 power mode names, aligned separators.

**Spec:** `docs/superpowers/specs/2026-10-16-maintenance-tab-design.md` §10.

## Global Constraints

As in `2026-10-16-maintenance-additions.md`: P/Invoke only in `NativeMethods.cs`, persisted ids never renamed, both languages, no background work or polling, fixed window checked by UiRender (`--no-incremental` strict build).

## Review Focus

1. A file root (`MEMORY.DMP`) that is a reparse point is never followed or deleted through (Task 1).
2. Windows.old: the `StateFlags9917` value is removed again even when cleanmgr fails or times out (Task 2).
3. The Shader caches row never cleans a cache the user unticked in the chooser (Task 3).

### Task 1: File roots in SafeFolderCleaner; system dumps, Windows logs, NVIDIA downloader
- `SafeFolderCleaner.Scan/Clean`: a root that is an existing file (not a reparse point) is counted / deleted like a file inside a folder root (24-hour rule, read-only, in use skipped).
- `CleanupTargets.SystemCrashDumps()`, `WindowsLogs()` (24-hour rule); `DriverLeftovers()` gains the NVIDIA Downloader folder. New ids in `CleanupTargetIds`.
- Tests: file root scanned and deleted; a file root that is a symlink is not followed; missing file root ignored; factory locations and rules.

### Task 2: Windows.old target
- `WindowsOldTarget(string folder, Func<bool> runDiskCleanup)`; the real runner sets `StateFlags9917` (DWORD 2) under `HKLM\...\VolumeCaches\Previous Installations`, runs `%SystemRoot%\System32\cleanmgr.exe /sagerun:9917` (30 min timeout), and deletes the value in a `finally`.
- Scan = folder size; clean = before − after; runner false → error on the row; missing folder → not found, runner not called.
- Tests with a fake runner; the command line is fixed.

### Task 3: Shader caches row and chooser
- `ShaderGroupViewModel` over the four shader `CleanupRowViewModel`s: `IsChecked` (bool?, three-state), `SizeText`, `DisplayText` (results), `Tip` (each cache's size), `IsBlocked`, `CanClean`.
- `MaintenanceViewModel`: `DisplayRows` (group first, then the other rows), `Shaders`, `IsShadersVisible`, `OpenShadersCommand`, `CloseShadersCommand`. Cleaning and `SelectionText` keep working on the four child rows (no new id).
- Tests: ticking the group ticks every cleanable cache; unticking clears; partial → null; only ticked caches are cleaned; the size text rule; `SelectionText` round trip.

### Task 4: Refresh rate on the timer card
- `NativeMethods.EnumDisplaySettings` + `DEVMODE`; `IDisplayInfoService.GetRefreshRate()` (0 = unknown) in `Services/DisplayInfoService.cs`.
- `MainViewModel.RefreshRateText` ("144 Hz" or ""), set in `InitializeAsync` and on `SystemEvents.DisplaySettingsChanged` (unsubscribed in `Dispose`).
- Tests: text from a fake service; 0 → empty.

### Task 5: Windows 10 power mode names
- `PowerModeFrom` gains `isWindows10` and `hasBattery`; Windows 10 names for the three GUIDs, no battery → not available. New `PowerMode` members appended (`BetterBattery`, `BetterPerformance`).
- Tests for both systems.

### Task 6: Views, strings, guide, DI, UiRender, alignment
- Cleanup card: `DisplayRows` with two templates; shader chooser overlay (lazy like the other dialogs); rows 2 px apart.
- Both cards: bottom part in a `SharedSizeGroup="CardBottom"` row, top-aligned; `Grid.IsSharedSizeScope` on the page.
- Timer card: refresh rate to the right of the value.
- Strings (both languages), guide bullets, DI order (§10.2), UiRender fakes for eleven rows and a chooser render; UiRender check that both separators have the same Y.
- Verify, commit, rebuild the installer.
