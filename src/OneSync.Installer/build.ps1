#requires -Version 5.1
<#
.SYNOPSIS
    Builds OneSync.msi end-to-end (publish + stage + WiX).

.DESCRIPTION
    1. dotnet publish OneSync.csproj in Release (self-contained single-file win-x64)
    2. Copy the published artefacts into stage/ alongside the pre-built
       OneSyncShellOverlay.dll, OneDriveFolder.ico, and License.rtf
    3. Run `wix build` with -arch x64 so the MSI installs to the 64-bit
       Program Files (not Program Files (x86) — WiX v6 defaults to x86)

    Run from any working directory; paths are resolved relative to the
    script. Requires the WiX v6 CLI (`wix`) and the .NET SDK on PATH.

.EXAMPLE
    .\build.ps1
    Produces OneSync.msi in this folder.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$repoRoot     = Resolve-Path (Join-Path $installerDir '..\..')
$srcProject   = Join-Path $repoRoot 'src\OneSync\OneSync.csproj'
$publishDir   = Join-Path $repoRoot 'src\OneSync\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
$stageDir     = Join-Path $installerDir 'stage'
$wxs          = Join-Path $installerDir 'OneSync.wxs'
$msi          = Join-Path $installerDir 'OneSync.msi'
$bundleWxs    = Join-Path $installerDir 'OneSync.Bundle.wxs'
$distDir      = Join-Path $repoRoot 'dist'
$bundleExe    = Join-Path $distDir 'OneSyncSetup.exe'

Write-Host "[1/4] Publishing OneSync (Release, win-x64, self-contained)..." -ForegroundColor Cyan
dotnet publish $srcProject -c Release -r win-x64 --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

Write-Host "[2/4] Staging artefacts..." -ForegroundColor Cyan
if (-not (Test-Path $stageDir)) { New-Item -ItemType Directory -Path $stageDir | Out-Null }

# Publisher outputs (.NET single-file + content files) copied from publish/.
$payload = @('OneSync.exe', 'config.json', 'config.template.json', 'README.md', 'icon.ico')
foreach ($f in $payload) {
    $src = Join-Path $publishDir $f
    if (-not (Test-Path $src)) { throw "Expected publish output missing: $src" }
    Copy-Item $src $stageDir -Force
}

# Source-controlled artefacts copied from the installer folder.
foreach ($f in @('License.rtf', 'OneDriveFolder.ico', 'logo.png')) {
    $src = Join-Path $installerDir $f
    if (-not (Test-Path $src)) { throw "Source artefact missing: $src" }
    Copy-Item $src $stageDir -Force
}

# Native ShellOverlay artefact must be built separately (C++/MSBuild project)
# and dropped into stage/ — we can't rebuild it from this script's prerequisites.
if (-not (Test-Path (Join-Path $stageDir 'OneSyncShellOverlay.dll'))) {
    throw "Pre-built artefact missing from stage/: OneSyncShellOverlay.dll (build src/OneSync.ShellOverlay separately first)"
}

# ensure-dokan.cmd: copy from installer dir or create a minimal one if missing.
$ensureDokan = Join-Path $stageDir 'ensure-dokan.cmd'
if (-not (Test-Path $ensureDokan)) {
    $src = Join-Path $installerDir 'ensure-dokan.cmd'
    if (Test-Path $src) { Copy-Item $src $stageDir -Force }
}

Write-Host "[3/4] Building MSI with WiX (-arch x64)..." -ForegroundColor Cyan
wix build $wxs -arch x64 -d "SourceDir=$stageDir" -ext WixToolset.UI.wixext -out $msi | Out-Host
if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }

$msiInfo = Get-Item $msi
Write-Host "  Built: $($msiInfo.FullName) ($([math]::Round($msiInfo.Length/1MB, 2)) MB)" -ForegroundColor DarkGray

# Bundle: chains Dokan_x64.msi + OneSync.msi into one bootstrapper. SourceDir
# points at the repository root so the bundle's MsiPackage references can
# resolve both dist/Dokan_x64.msi and src/OneSync.Installer/OneSync.msi.
Write-Host "[4/4] Building Burn bundle (OneSyncSetup.exe)..." -ForegroundColor Cyan
$dokanMsi = Join-Path $distDir 'Dokan_x64.msi'
if (-not (Test-Path $dokanMsi)) { throw "Bundle dependency missing: $dokanMsi (expected at dist/Dokan_x64.msi)" }
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }

wix build $bundleWxs -arch x64 -d "SourceDir=$repoRoot" -ext WixToolset.BootstrapperApplications.wixext -out $bundleExe | Out-Host
if ($LASTEXITCODE -ne 0) { throw "wix bundle build failed (exit $LASTEXITCODE)" }

$bundleInfo = Get-Item $bundleExe
Write-Host ""
Write-Host "Built MSI:    $($msiInfo.FullName) ($([math]::Round($msiInfo.Length/1MB, 2)) MB)" -ForegroundColor Green
Write-Host "Built bundle: $($bundleInfo.FullName) ($([math]::Round($bundleInfo.Length/1MB, 2)) MB)" -ForegroundColor Green
