# Maintenance tab (3.0.0): design

Date: 2026-10-16 · Status: approved in conversation, waiting for spec review · Target: `3.0.0-beta1`, then `3.0.0`

## 1. Goal

A third tab, **Maintenance** ("Bakım"), between Status and Settings. It is a small
maintenance corner for gamers that does two things:

- Cleans the caches and leftovers that cost disk space or cause stutter (shader
  caches, temp files, Windows Update leftovers, Recycle Bin) and lets the user pick
  files from Downloads to move to the Recycle Bin.
- Shows the active power plan and the virtual memory (page file), and opens
  Windows' own tools: drive optimisation, Disk Cleanup, and system file repair.
  The app does not reimplement any of these tools.

It must not make the app heavier. Tray RAM and CPU, start-up time and the "no empty
window" behaviour must measure the same as in 2.5.2 (see §8).

Out of scope: browser caches, scheduled or automatic cleaning, registry cleaners
(no real benefit and they can break Windows, so SFC/DISM stand in for "repair"),
and changing the power plan from inside the app.

## 2. Screen

Tabs: **Status · Maintenance · Settings**. The page uses the same boxed layout as
Settings (`SettingsGroupStyle`) and must fit the fixed 760×520 window. There is no
ScrollViewer, and UiRender fails on any clipping.

### Left box: CLEANUP

One row per target: check box, name, size (or state), plus a tooltip with what it
does and its risk.

```
☐ NVIDIA shader cache          1.2 GB
☐ AMD shader cache             not found      (dimmed, disabled)
☑ Temporary files (user)       845 MB
☑ Windows temporary files      120 MB
☑ Windows Update leftovers     2.3 GB
☐ Recycle Bin                  310 MB
  Downloads                    4.8 GB  [Choose…]
          [ Clean selected (3.3 GB) ]
  Freed so far: 12.4 GB  ›     (opens the cleanup history)
```

- **Default ticks:** user temp, Windows temp and Update leftovers are on. Both
  shader caches and the Recycle Bin are off. The shader tooltip says that games
  rebuild their shaders afterwards and can stutter on first launch.
- **Persistence:** tick choices are saved (`CleanupSelection`, see §6).
- **Downloads** has no tick. **Choose…** opens the Downloads dialog (§4.7).
- **After a clean,** each cleaned row shows its result for this session, for
  example "1.1 GB freed · 12 in use, skipped". Then the sizes are scanned again.
- **Blocked rows:** a row that cannot be cleaned right now is disabled and its
  tooltip says why. For example, the shader rows are blocked while a listed game
  is running.

### Right box: WINDOWS

```
Power plan      High performance                         [Change]
Virtual memory  C: 4.0 GB · system managed · 1.2 GB in use [Adjust]
────────────
[ Optimize drives ]           → dfrgui.exe
[ Disk Cleanup ]              → cleanmgr.exe
[ Repair system files ]       → console: DISM /RestoreHealth, then sfc /scannow
```

- **Change** opens Power Options (`control.exe /name Microsoft.PowerOptions`).
- **Adjust** opens Performance Options (`SystemPropertiesPerformance.exe`). Windows
  offers no direct way into the Virtual memory page, so the user goes to Advanced →
  Change; the tooltip says so.
- **Repair system files** first asks for confirmation (10–30 min, DISM needs
  internet), then opens a visible `cmd.exe /k` window. The app is elevated, so the
  console is too.
- **Several page files:** one line per page file, joined with " · ".

### When work happens

- **Lazy content:** the tab's content is not created until the tab is first
  selected, so start-up stays as fast as in 2.5.2.
- **Scan:** selecting the tab starts one scan on a background thread. Rows show
  "scanning…" until their own result arrives.
- **Cancel:** leaving the tab cancels a scan that is still running.
- **No background work:** there are no timers. Nothing runs while the tab is not
  shown.

## 3. Usage guide (the ? button)

The About / usage guide overlay gets two segment tabs, with the same style as the
main tabs:

- **General:** today's guide, unchanged: timer, memory purge, Game Mode, hotkeys.
- **Maintenance:** one entry per row and tool. Each entry says what it removes or
  does, when it is right, and when it is not. Planned content:
  - **Shader caches:** clean once after a driver update, or when a game stutters
    because of a broken cache. Cleaning them regularly is wrong, because every
    game rebuilds its shaders and stutters on first launch.
  - **Temporary files:** safe to clean now and then. Files changed in the last 24
    hours are left alone.
  - **Windows Update leftovers:** downloaded update files that are already
    installed. Safe to clean; Windows downloads again whatever it still needs.
  - **Recycle Bin:** emptying it is final.
  - **Downloads:** your own files. What you choose goes to the Recycle Bin, where
    you can restore it.
  - **Optimize drives:** HDDs are defragmented. SSDs get TRIM, not
    defragmentation, so it is safe for both.
  - **Disk Cleanup:** Windows' own cleaner, with more categories.
  - **Repair system files:** only needed if Windows is misbehaving (crashes,
    broken system apps). It takes long. There is no registry repair, and registry
    cleaners do not help.
  - **Elevation note:** the app runs as administrator, so it cleans the temp and
    Downloads folders of the account that approved UAC. For an administrator
    account that is the user's own folders.

## 4. Cleanup targets and safety rules

### 4.1 Contract

```csharp
public interface ICleanupTarget
{
    string Id { get; }                                    // stable, persisted: "nvidia-shader", ...
    bool   IsBlocked(out string? reasonKey);             // e.g. a listed game is running
    Task<CleanupScan>   ScanAsync(CancellationToken ct);
    Task<CleanupResult> CleanAsync(CancellationToken ct);
}

public sealed record CleanupScan(bool Found, long Bytes, int Files);
public sealed record CleanupResult(long FreedBytes, int Deleted, int Skipped);
```

**Ids are persisted and never renamed:** `nvidia-shader`, `amd-shader`,
`user-temp`, `windows-temp`, `windows-update`, `recycle-bin`; `downloads` is used only in the cleanup history (§4.7).

### 4.2 Targets

| Id | Locations | Extra rule |
|---|---|---|
| `nvidia-shader` | `%LOCALAPPDATA%\NVIDIA\DXCache`, `%LOCALAPPDATA%\NVIDIA\GLCache`, `%USERPROFILE%\AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache`, `%ProgramData%\NVIDIA Corporation\NV_Cache` | Blocked while a listed game runs |
| `amd-shader` | `%LOCALAPPDATA%\AMD\DxCache`, `%LOCALAPPDATA%\AMD\DxcCache`, `%LOCALAPPDATA%\AMD\VkCache`, `%LOCALAPPDATA%\AMD\GLCache` | Blocked while a listed game runs |
| `user-temp` | `Path.GetTempPath()` | 24-hour rule |
| `windows-temp` | `%SystemRoot%\Temp` | 24-hour rule |
| `windows-update` | `%SystemRoot%\SoftwareDistribution\Download` | — |
| `recycle-bin` | All drives, through `SHQueryRecycleBin` / `SHEmptyRecycleBin` (no UI, no sound) | — |

A target with none of its locations present reports `Found = false`, and its row
shows "not found".

### 4.3 `SafeFolderCleaner`: the only code that deletes files

Every folder target goes through it. Rules:

1. **Never leaves the root.** It does not enumerate into reparse points (junctions,
   symlinks, mount points). A reparse point inside the root is removed as a link
   only; its target is never touched.
2. **Files in use or denied are skipped and counted** (`Skipped`); cleaning goes on.
   No exception escapes a target.
3. **24-hour rule** (temp targets only): files with `LastWriteTimeUtc` in the last
   24 hours are kept and counted neither as freed nor as skipped.
4. **The root folder itself is never deleted.** Empty sub-folders left after
   cleaning are removed, deepest first, under the same rules.
5. **Accurate result:** `FreedBytes` is the sum of the files actually deleted.

The scan uses the same enumeration rules, so the size shown is what a clean would
remove, minus the files that turn out to be in use.

### 4.4 Errors

- **Scan errors:** a target whose scan throws (unexpectedly, since the cleaner
  catches per file) shows "could not read". The other rows are unaffected.
- **Clean errors:** a failure while cleaning one target is logged, and its row
  shows the error. The remaining selected targets still run.

### 4.5 Cleanup history

`cleanup-history.json` sits next to the log. It is written through a temp file and
then moved, as `purge-history.json` is, and a damaged file starts a fresh history.

It keeps:
- per-target all-time totals: count and bytes;
- the last 20 entries (time, target id, freed, skipped);
- per-day totals for 60 days, which give the Today and Last 7 days figures.

The history dialog has the same layout as the purge history: three total tiles,
the per-target breakdown, the last 20 entries, and a two-press Reset.

### 4.6 Concurrency

- **One scan at a time:** a new scan cancels the previous one.
- **During a clean:** Clean and Scan are disabled while cleaning runs, and the tab
  stays usable.
- **No overlap:** cleaning never runs at the same time as a scan.

### 4.7 Downloads dialog

- **Folder:** the Downloads folder comes from `SHGetKnownFolderPath(FOLDERID_Downloads)`,
  so a moved Downloads folder is found too.
- **List:** a virtualised list of the folder's top-level entries (files and
  folders) with name, size (folders summed with the same enumeration rules) and
  date, sorted by size, largest first. Each entry has a check box. Above the list
  sit Select all / none and the total of the ticks.
- **Action:** **Move selected to Recycle Bin** sends each entry through
  `Microsoft.VisualBasic.FileIO.FileSystem.Delete*` with
  `RecycleOption.SendToRecycleBin`. That is part of .NET, so no new package. Entries
  that fail are reported by count.
- **Memory:** the list exists only while the dialog is open and is dropped when it
  closes.
- **History:** moves to the Recycle Bin are recorded in the cleanup history as
  target `downloads`.

## 5. Windows box: information and tools

- **`ISystemInfoService.GetPowerPlan()`:** `PowerGetActiveScheme` and
  `PowerReadFriendlyName` (powrprof.dll), freed with `LocalFree`. On failure it
  returns null, and the row shows "unknown".
- **`ISystemInfoService.GetPageFiles()`:** `NtQuerySystemInformation(SystemPageFileInformation = 18)`
  gives the name, total, in-use and peak size per page file.
  - "System managed" comes from `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PagingFiles`:
    the entry is `?:\pagefile.sys`, or has the sizes `0 0`.
  - No page file at all shows "off".
- **`ISystemToolLauncher`:** starts `dfrgui.exe`, `cleanmgr.exe`, `control.exe /name Microsoft.PowerOptions`,
  `SystemPropertiesPerformance.exe`, and the repair console, all from
  `%SystemRoot%\System32` with fixed arguments (no user input in command lines). A
  start failure is logged and shown in the status line.
- **Refresh:** both info rows are read each time the tab is selected. This is cheap,
  under 5 ms.

## 6. Architecture and files

The design follows the existing layering. All P/Invoke lives in
`Services/Native/NativeMethods.cs`.

**Core:**
- Interfaces: `ICleanupTarget`, `ICleanupStatsStore`, `ISystemInfoService`,
  `ISystemToolLauncher`, `IDownloadsService`.
- Models: `CleanupScan`, `CleanupResult`, `CleanupRecord`, `CleanupStats`,
  `PowerPlanInfo`, `PageFileInfo`, `DownloadEntry`.

**`Services/Maintenance/`:**
- `SafeFolderCleaner`, the deletion engine (§4.3).
- `FolderCleanupTarget`, the base for the folder targets. It holds the locations,
  the optional 24-hour rule and the optional "blocked while a game runs" rule.
- The concrete folder targets: `NvidiaShaderTarget`, `AmdShaderTarget`,
  `UserTempTarget`, `WindowsTempTarget`, `WindowsUpdateTarget`.
- `RecycleBinTarget`.
- `DownloadsService`.
- `SystemInfoService`.
- `SystemToolLauncher`.
- `CleanupStatsStore`, with an in-memory version for tests.

**NativeMethods additions:**
- `SHQueryRecycleBin`, `SHEmptyRecycleBin`;
- `SHGetKnownFolderPath`, plus `CoTaskMemFree`;
- `PowerGetActiveScheme`, `PowerReadFriendlyName`, `LocalFree`;
- the `SYSTEM_PAGEFILE_INFORMATION` layout for the existing `NtQuerySystemInformation`.

**ViewModels:**
- **`MaintenanceViewModel`:**
  - rows (one per target), with tick, size text, state and result text;
  - `ScanCommand` (also run when the tab is selected), `CleanSelectedCommand`;
  - the selected total;
  - the history totals and the history dialog;
  - the power plan and page file text;
  - the tool commands.
- **`DownloadsViewModel`:** created when the dialog opens and released when it
  closes.

**Views:**
- `Views/MaintenancePanelView.xaml`, created on first selection.
- `Views/Overlays/DownloadsOverlay.xaml` and `Views/Overlays/CleanupHistoryOverlay.xaml`.
- `AboutOverlay`, changed to two tabs.
- All strings in `Strings.en-US.xaml` and `Strings.tr-TR.xaml`.

**DI:** `AppBootstrapper` registers the new services (singletons) and
`MaintenanceViewModel`. The targets are registered as `IEnumerable<ICleanupTarget>`.
"Is a listed game running" comes from the existing `IMemoryMonitorService` /
`RunningGames` state, without a new poll.

**Settings:**
- **One new value:** `CleanupSelection`, the comma-separated target ids. When it
  is missing, the defaults from §2 apply.
- **Nothing is renamed.** The 2.4.0 value-for-value upgrade test is extended with a
  2.5.2 key that must load and save back unchanged.
- **Imports:** a settings import does take `CleanupSelection`, because it is a user
  preference and not machine state.

## 7. Testing

**Unit tests** (temp folders only, never real user folders):

- **`SafeFolderCleaner`:**
  - a junction inside the root pointing outside: the outside file survives and the
    junction is gone;
  - a file held open is skipped and counted;
  - the 24-hour rule;
  - the root survives and empty sub-folders are removed;
  - `FreedBytes` equals the deleted sizes;
  - the scan equals the clean on an unlocked tree.
- **Targets:**
  - all locations missing gives `Found = false`;
  - the shader targets are blocked while a game runs and unblocked otherwise.
- **`MaintenanceViewModel`** (fake targets):
  - scan results reach the rows;
  - only ticked targets are cleaned;
  - a throwing target does not stop the others and shows its error;
  - leaving the tab cancels the scan;
  - ticks are saved and loaded;
  - the selected total adds up;
  - history totals are updated after a clean.
- **`CleanupStatsStore`:** round trip, a damaged file gives a fresh history,
  pruning to 20 entries and 60 days.
- **`DownloadsViewModel`** (fake service): the list is sorted, select all / none
  works, only ticked entries are moved, failures are counted.
- **Settings:** the 2.5.2 key round trip.

**UiRender** (4 themes × 2 languages):
- the Maintenance tab, including "not found" and blocked rows;
- the Downloads dialog, the cleanup history and the two-tab guide;
- the layout and tooltip-contrast checks.

**On the real PC:**
- a scan, then a small real clean, compared with the folder sizes before and after;
- each Windows tool button opened once;
- Downloads: one test file moved to the Recycle Bin and restored.

## 8. Acceptance and release

The release is blocked unless all of these hold:

- tests and `-warnaserror` pass;
- UiRender passes;
- **tray RAM and CPU** match 2.5.2 (same method as before: 45 s settle, 60 s
  measurement, both versions twice, alternating);
- **start-up and open-from-tray** match 2.5.2, with no empty window (PrintWindow
  method, 3 runs each);
- user settings are kept on update from 2.5.2 (compared before and after
  installing).

**Release steps:**
1. `3.0.0-beta1` ships as a GitHub pre-release.
2. The user tests it; fixes follow as `beta2…` as needed.
3. On the user's approval, `3.0.0` ships as the normal release, with the README and
   screenshots updated (`UiRender --docs` adds the Maintenance tab).

## 9. Additions after the first install (decided 2026-10-16)

The user installed the first build, asked for layout fixes (done: cards line up
with the other pages, Windows' tools at the bottom of their card, Downloads as
its own section) and chose these additions.

### 9.1 New cleanup rows

New ids, persisted, never renamed; appended after the existing ones.

| Id | Locations | Rules |
|---|---|---|
| `directx-shader` | `%LOCALAPPDATA%\D3DSCache` | Blocked while a listed game runs; not ticked by default |
| `steam-shader` | `steamapps\shadercache` of every Steam library (`HKCU\Software\Valve\Steam\SteamPath`, `steamapps\libraryfolders.vdf`), read at each scan | Blocked while a listed game runs; not ticked by default |
| `driver-leftovers` | `C:\NVIDIA`, `C:\AMD` (system drive) — extracted driver installers | Not ticked by default |
| `delivery-optimization` | `%SystemRoot%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache` (readable elevated) | Cleaned with Windows' own `Delete-DeliveryOptimizationCache -Force` (powershell.exe from System32, fixed arguments), not by deleting files; freed = size before − size after. Not ticked by default |
| `crash-dumps` | `%LOCALAPPDATA%\CrashDumps`, `%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive`, `…\ReportQueue`, `%ProgramData%\Microsoft\Windows\WER\ReportArchive`, `…\ReportQueue` | Not ticked by default |

Row order: NVIDIA, AMD, DirectX, Steam shader caches; driver leftovers; user
temp, Windows temp, Windows Update, Delivery Optimization; crash dumps; Recycle
Bin.

A saved `CleanupSelection` keeps its meaning: the new ids are unticked for
existing users; fresh installs get the §2 defaults.

### 9.2 Every row stays visible

The user decided against hiding rows that are not on this PC: every row is
always shown, a target with no folders shows "not found" as before. To fit
eleven rows in the fixed window the row spacing goes from 3 px to 2.5 px
(2 px if 2.5 does not fit); UiRender checks it.

### 9.3 New Windows rows

One line each: name, state, and a button that opens Windows' own page. The app
changes nothing itself. Read each time the tab is selected (registry, under a
millisecond).

| Row | State from | Button opens |
|---|---|---|
| Hardware-accelerated GPU scheduling | `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode`: 2 on, 1 off, missing "Windows default" | `ms-settings:display-advancedgraphics` |
| Background recording | `HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR`: `AppCaptureEnabled` ≠ 0 and `HistoricalCaptureEnabled` = 1 → on; else off | `ms-settings:gaming-captures` |
| Memory integrity | `HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity\Enabled`: 1 on, else off | `windowsdefender://coreisolation` |
| Game Mode | `HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled`: 0 off, else on (Windows' default) | `ms-settings:gaming-gamemode` |
| Power mode (first of the rows, added after the second install) | `PowerGetEffectiveOverlayScheme` (powrprof): `ded574b5-…` best performance, `00000000-…` balanced, `961cc777-…` best power efficiency, else unknown. Only the Balanced plan has power modes: another active plan shows "not available on this plan". On a laptop it is the mode of the current power source. | `ms-settings:powersleep` |

The classic power plan row stays: Windows 11 sets the mode on top of the
plan, and the mode works only on the Balanced plan, so both are shown. The app
changes neither (user's choice: the button opens Windows' page).

The pages open through `%SystemRoot%\explorer.exe <fixed URI>`; the tooltips
say what each setting costs or protects (memory integrity is a security
feature: the tooltip says so and the app never suggests turning it off).

### 9.4 Not added

- A registry cleaner: Windows has none of its own, and third-party ones do not
  help (§1).
- Prefetch cleaning, DNS flushing, "RAM cleaners".

## 10. Second round of additions (decided 2026-10-16, after the second install)

### 10.1 Shader caches become one row

NVIDIA, AMD, DirectX and Steam shader caches are one row, **Shader caches**,
with a **Choose…** button (like Downloads) that lists the four caches with
their size and their own tick.

- The four ids stay the persisted ids (`nvidia-shader`, `amd-shader`,
  `directx-shader`, `steam-shader`); `CleanupSelection` stores the ticked ones
  as before, so earlier choices keep their meaning. No new id.
- The row's tick is three-state: all cleanable caches ticked / none / some.
  Ticking it ticks every cache that can be cleaned; unticking clears all.
- The row shows the size of the ticked caches, or of all found caches when
  none is ticked; the tooltip lists each cache's size.
- Blocked while a listed game runs (all four are).
- Epic Games has no shared shader cache like Steam, so there is no Epic entry.

### 10.2 New rows

| Id | Locations | Rules |
|---|---|---|
| `system-crash-dumps` | `%SystemRoot%\MEMORY.DMP` (a file), `%SystemRoot%\Minidump`, `%SystemRoot%\LiveKernelReports` (GPU driver resets leave dumps here) | Not ticked by default. `SafeFolderCleaner` learns file roots: a root that is a file is counted and deleted with the same rules |
| `windows-logs` | `%SystemRoot%\Logs\CBS`, `%SystemRoot%\Logs\DISM` | 24-hour rule (today's logs stay for troubleshooting); files in use skipped |
| `windows-old` | `<system drive>\Windows.old` | Cleaned by Windows' own Disk Cleanup: the app sets `StateFlags9917 = 2` on the `VolumeCaches\Previous Installations` handler, runs `cleanmgr.exe /sagerun:9917` from System32, then removes the value again; freed = size before − after. Not ticked by default; the tooltip warns that going back to the previous Windows version is no longer possible afterwards |

`driver-leftovers` also covers `%ProgramData%\NVIDIA Corporation\Downloader`
(driver packages the NVIDIA app downloaded and kept).

Order: Shader caches, driver leftovers, user temp, Windows temp, Windows
Update, Delivery Optimization, crash dumps, system crash dumps, Windows logs,
previous Windows installation, Recycle Bin: eleven rows, the most the card
holds at 2 px spacing.

### 10.3 Refresh rate on the Status page

The timer card shows the primary display's current refresh rate (for example
"144 Hz") to the right of the timer value, small and dim. It is read with
`EnumDisplaySettings(ENUM_CURRENT_SETTINGS)` at start-up and again on
`SystemEvents.DisplaySettingsChanged`; there is no polling. Unknown (0) hides
it.

### 10.4 Power mode on Windows 10

Windows 10 has the power slider only on battery-powered PCs and names its
steps differently: `961cc777-…` "Better battery", `00000000-…` "Better
performance", `ded574b5-…` "Best performance". On Windows 10 (build < 22000)
the row uses those names, and a PC without a battery shows "not available".

### 10.5 Separators line up

The divider above Downloads (left card) and the one above Windows' tools
(right card) sit at the same height: the bottom parts of both cards are one
`SharedSizeGroup` row, top-aligned.
