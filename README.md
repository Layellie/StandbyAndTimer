# StandbyAndTimer

A Windows desktop utility for reducing input latency and managing system memory — designed for gamers and power users.

> Complete rewrite of the original WinForms version (now archived at the [`winforms-archive`](https://github.com/Layellie/StandbyAndTimer/tree/winforms-archive) branch).

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
.\build-installer.ps1 -Version 1.0.0
```

Output: `installer/dist/StandbyAndTimer_Setup_1.0.0.exe` (~72 MB self-contained).

## License

Copyright © LAYE77IE
