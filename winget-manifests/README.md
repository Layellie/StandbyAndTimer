# winget manifests

Manifests for the [Microsoft Windows Package Manager](https://learn.microsoft.com/windows/package-manager/) (`winget`).
Submitted to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) once per release.

## Files

- `Layellie.StandbyAndTimer.yaml` — root version manifest.
- `Layellie.StandbyAndTimer.installer.yaml` — installer URL, SHA256, install type.
- `Layellie.StandbyAndTimer.locale.en-US.yaml` — display metadata.

## Release procedure

1. Bump `PackageVersion` in all three files to the new tag.
2. After publishing the GitHub release, compute the installer SHA256:
   ```powershell
   (Get-FileHash installer\dist\StandbyAndTimer_Setup_X.Y.Z.exe -Algorithm SHA256).Hash
   ```
3. Paste that hash into `Layellie.StandbyAndTimer.installer.yaml` under `InstallerSha256`.
4. Update `InstallerUrl` and `ReleaseDate`.
5. Validate locally:
   ```powershell
   winget validate winget-manifests
   ```
6. Open a PR to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) under `manifests/l/Layellie/StandbyAndTimer/X.Y.Z/`.

Once merged, users can install via:
```powershell
winget install Layellie.StandbyAndTimer
```
