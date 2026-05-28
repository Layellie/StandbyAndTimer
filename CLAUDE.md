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
```

The solution file uses the `.slnx` format (`StandbyAndTimer.slnx`).

## Architecture

The project follows a layered MVVM architecture. **All Windows API calls are isolated in `Services/Native/NativeMethods.cs`** — nothing else in the codebase calls P/Invoke directly.

```
Core/                     ← Pure contracts; zero platform dependencies
  Interfaces/             ← ITimerResolutionService, IStandbyPurgeService,
                            IMemoryMonitorService, IProcessOptimizationService,
                            IAutoStartService, ISettingsService
  Models/                 ← MemorySnapshot (record), AppSettings, GameEntry

Services/                 ← Concrete implementations; only layer that touches Win32
  Native/NativeMethods.cs ← ALL DllImport declarations (ntdll, kernel32, avrt, advapi32)
  TimerResolutionService  ← Activate/Deactivate; IDisposable restores timer on exit
  StandbyPurgeService     ← PurgeAsync + PurgeSucceeded event
  MemoryMonitorService    ← PeriodicTimer loop (1 s); owns PerformanceCounter lifecycle
  ProcessOptimizationService ← Tracks PIDs as HashSet<int>; disposes Process handles inline
  AutoStartService        ← schtasks.exe wrapper
  SettingsService         ← HKCU\SOFTWARE\StandbyAndTimer registry R/W

Infrastructure/
  AppBootstrapper.cs      ← Microsoft.Extensions.DependencyInjection wiring (all singletons)

ViewModels/
  MainViewModel.cs        ← CommunityToolkit.Mvvm [ObservableProperty]/[RelayCommand];
                            orchestrates all services; dispatches snapshot events to UI thread

Views/
  Styles/Theme.xaml       ← Black/yellow palette + all Control styles
  MainWindow.xaml         ← Pure data-binding; zero code-behind logic
  MainWindow.xaml.cs      ← Only: DataContext = viewModel; OnClosing → Hide() (tray)

App.xaml / App.xaml.cs   ← ShutdownMode=OnExplicitShutdown; builds DI container;
                            creates H.NotifyIcon tray icon programmatically
```

## Key Design Decisions

- **`PeriodicTimer`** (not `Thread.Sleep`) drives the 1-second monitor loop — cleanly cancelled via `CancellationToken`.
- **`IMemoryMonitorService`** exposes `StandbyLimitMb`, `FreeLimitMb`, `GameModeEnabled`, `GamePaths` as writable properties — the ViewModel updates these live without restarting the loop.
- **`IStandbyPurgeService.PurgeSucceeded`** event lets the ViewModel increment `PurgeCount` for both manual and automatic purges without polling.
- Settings are persisted to the registry in `OnExit` **and** after each user action — not on every keystroke. The `_isInitializing` guard prevents spurious saves during `ApplySettings`.
- `Process` objects in `ProcessOptimizationService` are disposed immediately with `using` — only PIDs (`HashSet<int>`) are retained between ticks.

## NuGet Packages

| Package | Purpose |
|---|---|
| `CommunityToolkit.Mvvm 8.x` | Source-generated `[ObservableProperty]` / `[RelayCommand]` |
| `Microsoft.Extensions.DependencyInjection 10.x` | DI container (no hosting overhead) |
| `H.NotifyIcon.Wpf 2.x` | WPF-native system tray (no WinForms dependency) |

## Platform Notes

- All native calls require Administrator; running without UAC elevation will silently return failure codes.
- `NtSetTimerResolution` should be called once (`Activate`) and restored on exit (`Deactivate` / `Dispose`) — not on every loop tick as in the original WinForms version.
- `PerformanceCounter("Memory", "Standby Cache Reserve Bytes")` is expensive to construct; it is created once in `MemoryMonitorService` and disposed with the service.
