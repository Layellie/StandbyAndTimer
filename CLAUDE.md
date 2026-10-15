# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**StandbyAndTimer** is a Windows-only WPF desktop application targeting **.NET 10** (`net10.0-windows`). It is a system optimization utility that:

- Locks the system timer to **0.5 ms** via `NtSetTimerResolution` to reduce input latency.
- Monitors and automatically purges the Windows **Standby memory list** via `NtSetSystemInformation(80)`.
- Sets **High CPU priority + full CPU affinity** on user-configured game processes.
- Runs in the **system tray** and can optionally launch at Windows startup via Task Scheduler.

Requires **Administrator** privileges (declared in `app.manifest`).

## Build & Run

```powershell
# Restore + build (Debug)
dotnet build StandbyAndTimer/StandbyAndTimer.csproj

# Run (must be run as Administrator for P/Invoke calls to work)
dotnet run --project StandbyAndTimer/StandbyAndTimer.csproj

# Release build
dotnet build StandbyAndTimer/StandbyAndTimer.csproj -c Release

# Unit tests
dotnet test StandbyAndTimer.Tests/StandbyAndTimer.Tests.csproj -c Release

# UI renders + view checks after XAML/style/string changes (PNGs in ui-render-out/)
dotnet run --project tools/UiRender -c Release

# Redraw the app icon (every size 16-256 px, vector) into StandbyAndTimer/app_icon.ico
dotnet run --project tools/IconGen -c Release -- square StandbyAndTimer/app_icon.ico ui-render-out
```

The solution file uses the `.slnx` format (`StandbyAndTimer.slnx`).

## Architecture

Layered MVVM. **All P/Invoke lives in `Services/Native/NativeMethods.cs`**; nothing else declares `DllImport`. Registry access (HKCU settings, the HKLM GlobalTimer switch) goes through `Microsoft.Win32` in the services.

```
Core/                       ← contracts, no platform code
  Interfaces/               ← one interface per service (ITimerResolutionService, IStandbyPurgeService,
                              IMemoryMonitorService, IProcessOptimizationService, IGameDetectionService,
                              ISettingsService, IUpdateService, IThemeService, ILocalizationService,
                              IHotkeyService, ITrayIconService, IIdleMonitor, IGlobalTimerSetting, ...)
  Models/                   ← AppSettings, GameEntry, MemorySnapshot (record), Theme, TimerLockMode, ...

Services/                   ← implementations; the only layer that touches Win32
  Native/NativeMethods.cs   ← every DllImport (ntdll, kernel32, user32, avrt, advapi32)
  TimerResolutionService    ← NtSetTimerResolution + 50 ms watchdog thread (MMCSS) that re-asserts it
  MemoryMonitorService      ← 1 s PeriodicTimer loop: snapshot, running listed games, idle time,
                              auto purge, game boost; a failing tick is logged, the loop goes on
  StandbyPurgeService       ← NtSetSystemInformation(80), full or light (low-priority) purge
  ProcessOptimizationService← High priority / all cores / EcoQoS opt-out for running boost games
  GameDetectionService      ← 3 s poll: fullscreen foreground exe → auto-added game
  SettingsService           ← HKCU\SOFTWARE\StandbyAndTimer R/W; never saves after a failed load
  GlobalTimerSetting        ← HKLM ...\kernel\GlobalTimerResolutionRequests (Windows 11, read at boot)
  UpdateService             ← GitHub releases/latest, SHA-256 verified download to {app}\updates
  Tray/                     ← WinForms NotifyIcon + themed ContextMenuStrip (TrayMenuPalette)
  ThemeService / LocalizationService ← swap ResourceDictionary slot 1 (palette) / slot 2 (strings)
  Maintenance/              ← Maintenance tab: ICleanupTarget rows (folder targets, Recycle Bin),
                              SafeFolderCleaner (the only code that deletes files: never follows
                              links, skips files in use), Downloads, power plan / page files,
                              Windows tool launcher, cleanup-history.json

Infrastructure/AppBootstrapper.cs ← Microsoft.Extensions.DependencyInjection, all singletons

ViewModels/
  MainViewModel             ← status page, timer lock / idle-off logic, game list; owns persistence
  SettingsViewModel         ← Settings tab (also export / import, updates, hotkey rebinding)
  SystemViewModel           ← System settings panel behind the Administrator badge (settings that
                              change Windows) + the one-time GlobalTimer question
  MaintenanceViewModel      ← Maintenance tab: scans only while the tab is shown, clean selected,
                              history, Windows tools; DownloadsViewModel lives while its dialog is open

Views/
  Styles/Theme.xaml         ← control styles; Palette.{Dark,Black,Light,White}.xaml hold the colours
  Strings/Strings.{en-US,tr-TR}.xaml ← every UI string, both languages
  Cards/, Overlays/         ← status-page cards, title bar, modal dialogs (About guide, wizard,
                              System settings, close confirmation)

App.xaml.cs                 ← single instance, DI, tray, hotkeys, notifications, startup cleanup
```

## Key Design Decisions

- Settings persist to the registry after each user action (TextBoxes debounced) and in `OnExit`. `InitScope` sets the `_isInitializing` guard so applying loaded settings never saves.
- **User settings must survive updates.** New settings default to the old behaviour; existing registry value names and stored enum names are never renamed (new enum members go at the end). `SettingsServiceTests` include a value-for-value 2.4.0 key that must load and save back unchanged; extend it when the format changes.
- Machine-specific state (System settings) is not taken from a settings import.
- `PerformanceCounter`s are created lazily on the monitor thread (their constructor blocked start-up for seconds on the UI thread).
- Game processes are matched by exe name; only PIDs are kept, `Process` objects are disposed at once.
- The single-file bundle is **not compressed**: a compressed bundle is unpacked into private memory at every start (220 vs 143 MB measured). Inno Setup compresses the installer.
- Settings-page layout is fixed (760x520, no ScrollViewer): run UiRender after any XAML change; it fails on anything clipped or outside its card.

## NuGet Packages

| Package | Purpose |
|---|---|
| `CommunityToolkit.Mvvm 8.x` | Source-generated `[ObservableProperty]` / `[RelayCommand]` |
| `Microsoft.Extensions.DependencyInjection 10.x` | DI container (no hosting overhead) |

The tray uses WinForms' `NotifyIcon` (`UseWindowsForms`, aliased as `WinForms` in the one file that needs it); there is no tray package.

## Platform Notes

- Everything needs Administrator (`app.manifest`); without elevation the native calls fail silently.
- Since Windows 10 2004 a timer resolution request reaches only processes that request one themselves; games that never ask stay at 15.6 ms unless GlobalTimerResolutionRequests is on (Windows 11, takes effect after a restart). Windows 11 can also ignore requests of hidden windows: the app opts itself and boosted games out of that throttling.
- An active timer keeps Windows from sleeping (`SetThreadExecutionState`); "Turn the timer off when idle" exists for that.
- Release flow: pre-release beta first, then the final release; see the release notes of earlier versions for the format (SHA256 line + `.sha256` asset).
