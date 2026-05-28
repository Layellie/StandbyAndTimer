# Build & Package

## 1. Publish single-file binary
```powershell
dotnet publish StandbyAndTimer\StandbyAndTimer.csproj -p:PublishProfile=win-x64-single
```
Output: `StandbyAndTimer\bin\publish\win-x64\StandbyAndTimer.exe`

## 2. Build installer (optional)
Install [Inno Setup 6](https://jrsoftware.org/isdl.php), then:
```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\Setup.iss
```
Output: `installer\dist\StandbyAndTimer_Setup_<version>.exe`
