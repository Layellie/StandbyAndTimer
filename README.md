# StandbyAndTimer

<p align="center">
  <img src="docs/screenshot.png" width="60%" alt="StandbyAndTimer main window" />
</p>

A Windows desktop utility for reducing input latency and managing system memory — designed for gamers and power users.

> **v2.1.0** — Five user-visible features bundled under the new "batch fixes, don't ship per-commit" cadence:
>
> - **Global hotkeys** — `Ctrl+Alt+P` manual purge, `Ctrl+Alt+T` timer toggle.
> - **Tray single-click** toggles window visibility instead of always showing.
> - **Notification preferences** — per-balloon toggles in Settings (purge / timer / game).
> - **Idle-only auto-purge** — threshold purges wait until you've been idle, so gaming isn't interrupted.
> - **Single-instance signal** — re-launching while the app is running silently surfaces the live window.
>
> Memory card bottom strip refreshed to `Total | Free | Purges` with left / center / right symmetry. The original WinForms release is preserved as [`v1.0.0`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v1.0.0) on the [`winforms-archive`](https://github.com/Layellie/StandbyAndTimer/tree/winforms-archive) branch.

## Features

- **Timer Resolution Lock** — pins the system timer at 0.5 ms via `NtSetTimerResolution` for lower input latency
- **Standby Memory Purge** — manual and automatic cleanup of the Windows Standby memory list via `NtSetSystemInformation`
- **Game Mode** — sets High CPU priority and full affinity on user-configured game processes
- **System Tray** — runs in the background with optional launch at Windows startup
- **Dark / Light themes** with English & Turkish localization
- **Built-in update checker** against GitHub Releases (download & install from inside the app)

## Installation

1. Go to [Releases](https://github.com/Layellie/StandbyAndTimer/releases) and download the latest `StandbyAndTimer_Setup_X.Y.Z.exe`
2. Run the installer (UAC will prompt — Administrator privileges required)
3. Launch from the Start Menu

**Requirements:** Windows 10 21H1+ or Windows 11, x64, Administrator privileges. No separate .NET runtime install needed — the installer ships a self-contained .NET 10 build.

## Architecture

WPF (.NET 10) with MVVM. All Win32 P/Invoke calls are isolated in `Services/Native/NativeMethods.cs`. See [`ARCHITECTURE.md`](ARCHITECTURE.md) for the full design breakdown.

## Build from source

```powershell
# Build (Debug or Release)
dotnet build StandbyAndTimer/StandbyAndTimer.csproj -c Release

# Run (must be Administrator for the P/Invoke calls to succeed)
dotnet run --project StandbyAndTimer/StandbyAndTimer.csproj
```

To produce a redistributable installer ([Inno Setup 6](https://jrsoftware.org/isdl.php) required):

```powershell
.\build-installer.ps1 -Version 2.1.0
```

Output: `installer/dist/StandbyAndTimer_Setup_2.1.0.exe` (~72 MB self-contained).

## Install via winget

Once the package is published to the community repository:

```powershell
winget install Layellie.StandbyAndTimer
```

The manifest lives at [`winget-manifests/`](winget-manifests/) and is submitted to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) with each release.

## Version history

Full per-release notes are on the [Releases page](https://github.com/Layellie/StandbyAndTimer/releases). v2.0.8, v2.0.9, and v2.0.10 are rapid-iteration interim builds of the Settings-panel refresh and are marked pre-release on GitHub — install **v2.0.11** instead.

| Tag | Notes |
|---|---|
| [`v2.1.0`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v2.1.0) | **Current** — global hotkeys, tray single-click toggle, per-balloon notification preferences, idle-only auto-purge, single-instance signal, Memory card `Total / Free / Purges` strip with left/center/right symmetry |
| [`v2.0.11`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v2.0.11) | Settings panel no longer shows an outer vertical scrollbar; log viewer absorbs the leftover space and the version footer stays pinned |
| [`v2.0.7`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v2.0.7) | Settings panel UI polish (readable logs, refresh button no-clip, no window shift on Settings/Timer click) |
| [`v2.0.6`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v2.0.6) | First-run wizard, crash reporter, logs viewer, game auto-detect, winget manifest |
| [`v2.0.5`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v2.0.5) | Game Mode EcoQoS opt-out fix, dedicated AVRT watchdog thread for tighter 0.5 ms lock |
| [`v2.0.4`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v2.0.4) | AUTO PURGE master switch + safer defaults (0 MB thresholds) |
| [`v2.0.3`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v2.0.3) | Sub-500 ms startup, accurate Free-RAM accounting, timer auto-lock on launch |
| [`v2.0.2`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v2.0.2) | Card UI + lime accent, timer auto-start fix |
| [`v2.0.1`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v2.0.1) | P0 fixes + update integrity verification |
| [`v2.0.0`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v2.0.0) | First WPF rewrite — replaces the original WinForms build |
| [`v1.0.0`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v1.0.0) | Original WinForms (.NET Framework) — archived on the [`winforms-archive`](https://github.com/Layellie/StandbyAndTimer/tree/winforms-archive) branch |

## License

[MIT](LICENSE) — Copyright © 2026 LAYE77IE
