# Build & Package

The quick way does both steps and prints the installer's SHA-256:

```powershell
.\build-installer.ps1 -Version 3.0.9
```

## 1. Publish
```powershell
dotnet publish StandbyAndTimer\StandbyAndTimer.csproj -p:PublishProfile=win-x64
```
Output: `StandbyAndTimer\bin\publish\win-x64\` (the exe with its DLLs, self-contained). Empty that folder before publishing: the installer packs everything in it.

## 2. Build installer
Install [Inno Setup 6](https://jrsoftware.org/isdl.php), then:
```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\Setup.iss
```
Output: `installer\dist\StandbyAndTimer_Setup_<version>.exe`
