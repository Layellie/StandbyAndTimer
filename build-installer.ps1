<#
.SYNOPSIS
    Publishes StandbyAndTimer as a self-contained single-file binary and
    compiles the Inno Setup installer.

.PARAMETER Version
    Version string written to the assembly and the installer filename.
    Default: 2.0.0

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Version 2.0.1

.NOTES
    Requirements:
      * .NET 10 SDK on PATH
      * Inno Setup 6 (https://jrsoftware.org/isdl.php)

    Output:
      installer\dist\StandbyAndTimer_Setup_<Version>.exe
#>

[CmdletBinding()]
param(
    [string]$Version = "2.0.7"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# --- Step 1: dotnet publish (self-contained, single-file) -------------------
Write-Host ""
Write-Host "[1/2] Publishing self-contained build (Version=$Version)..." -ForegroundColor Cyan

$publishDir = Join-Path $root "StandbyAndTimer\bin\publish\win-x64"
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

$csproj = Join-Path $root "StandbyAndTimer\StandbyAndTimer.csproj"
& dotnet publish $csproj -p:PublishProfile=win-x64-single -p:Version=$Version --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$publishedExe = Join-Path $publishDir "StandbyAndTimer.exe"
if (-not (Test-Path $publishedExe)) {
    throw "Expected published binary not found: $publishedExe"
}
$sizeMb = [math]::Round((Get-Item $publishedExe).Length / 1MB, 1)
Write-Host "      Published: $publishedExe  ($sizeMb MB)"

# --- Step 2: locate ISCC.exe -----------------------------------------------
Write-Host ""
Write-Host "[2/2] Compiling Inno Setup installer..." -ForegroundColor Cyan

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command ISCC -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) {
    Write-Host ""
    Write-Host "Inno Setup 6 is not installed." -ForegroundColor Red
    Write-Host "Download and install it from:  https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    Write-Host "Then re-run this script."
    exit 1
}

# --- Step 3: compile installer ----------------------------------------------
$iss = Join-Path $root "installer\Setup.iss"
& $iscc "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE"
}

$setupExe = Join-Path $root "installer\dist\StandbyAndTimer_Setup_$Version.exe"
if (-not (Test-Path $setupExe)) {
    throw "Compilation reported success but output not found at $setupExe"
}

$setupMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
Write-Host ""
Write-Host "Installer ready:" -ForegroundColor Green
Write-Host "  $setupExe  ($setupMb MB)"

# --- Step 4: emit SHA-256 (consumed by the in-app update verifier) ---------
# The desktop UpdateService verifies downloaded installers against this hash.
# Publish the value in the GitHub release body (as "SHA256: <hex>") OR upload
# the .sha256 sibling file alongside the installer asset — either form is read
# by UpdateService.ResolveSha256Async.
$sha = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLowerInvariant()
$shaFile = "$setupExe.sha256"
"$sha  $(Split-Path $setupExe -Leaf)" | Set-Content -Path $shaFile -Encoding ascii -NoNewline

Write-Host ""
Write-Host "SHA-256:" -ForegroundColor Green
Write-Host "  $sha"
Write-Host "  (also written to $shaFile)"
Write-Host ""
Write-Host "Paste this line into the GitHub release body so the in-app update" -ForegroundColor Yellow
Write-Host "checker can verify integrity:" -ForegroundColor Yellow
Write-Host ""
Write-Host "  SHA256: $sha" -ForegroundColor White
