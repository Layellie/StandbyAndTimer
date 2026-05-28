# StandbyAndTimer
<img width="427" height="492" alt="Ekran görüntüsü 2026-05-28 162308" src="https://github.com/user-attachments/assets/054c5969-250b-4528-8990-29c3d3f72ca9" />
<img width="431" height="493" alt="Ekran görüntüsü 2026-05-28 162300" src="https://github.com/user-attachments/assets/f77404e5-8893-4e7f-bc02-250927dc11ed" />
A Windows desktop utility for reducing input latency and managing system memory — designed for gamers and power users.

> **v2.0.0** — complete rewrite on WPF (.NET 10). The original WinForms version is preserved as [`v1.0.0`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v1.0.0) on the [`winforms-archive`](https://github.com/Layellie/StandbyAndTimer/tree/winforms-archive) branch.

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
.\build-installer.ps1 -Version 2.0.0
```

Output: `installer/dist/StandbyAndTimer_Setup_2.0.0.exe` (~72 MB self-contained).

## Version history

| Tag | Stack | Notes |
|---|---|---|
| [`v2.0.0`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v2.0.0) | WPF (.NET 10) | Current release — full MVVM rewrite, in-app update checker, self-contained installer |
| [`v1.0.0`](https://github.com/Layellie/StandbyAndTimer/releases/tag/v1.0.0) | WinForms (.NET Framework) | Original — archived on the [`winforms-archive`](https://github.com/Layellie/StandbyAndTimer/tree/winforms-archive) branch |

## License

Copyright © LAYE77IE
