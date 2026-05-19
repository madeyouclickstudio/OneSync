#requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs OneSync and its prerequisite (Dokan 2.x driver).

.DESCRIPTION
    Performs a silent end-to-end install:
      1. Verifies Dokan 2.x is present; if not, installs it from the bundled MSI
      2. Installs OneSync MSI
      3. Optionally starts the app for the current user
      4. Applies the configured drives' custom icons

    Designed to be deployed via Intune, SCCM, or PDQ Deploy.

.PARAMETER InstallerDir
    Directory containing OneSync.msi and (optionally) Dokan_x64.msi.
    Defaults to the directory of this script.

.PARAMETER SkipDokan
    Skip the Dokan installation check. Use only if Dokan is already deployed
    via another mechanism.

.PARAMETER StartAfterInstall
    Launch OneSync.exe immediately for the current user after install.

.EXAMPLE
    .\Install-OneSync.ps1
    Installs Dokan (if needed) then OneSync, silently.

.EXAMPLE
    .\Install-OneSync.ps1 -StartAfterInstall
    Installs and then launches the app for the current user.
#>
[CmdletBinding()]
param(
    [string]$InstallerDir,
    [switch]$SkipDokan,
    [switch]$StartAfterInstall
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot / $MyInvocation.MyCommand.Path are NOT reliably populated when a
# [CmdletBinding()] script's param-block default expressions are evaluated - so
# resolve the installer directory here in the body, where they are reliable.
if (-not $InstallerDir) { $InstallerDir = $PSScriptRoot }

function Write-Step([string]$msg) { Write-Host "[OneSync] $msg" -ForegroundColor Cyan }
function Write-OK([string]$msg)   { Write-Host "  OK  $msg" -ForegroundColor Green }
function Write-Skip([string]$msg) { Write-Host "  SKIP $msg" -ForegroundColor Yellow }
function Write-Fail([string]$msg) { Write-Host "  FAIL $msg" -ForegroundColor Red }

# --- 1. Verify / install Dokan -----------------------------------------------

if (-not $SkipDokan) {
    Write-Step "Checking for Dokan 2.x driver"

    $dokanCtl = "C:\Program Files\Dokan\DokanLibrary-2.1.0\dokanctl.exe"

    # What actually matters is the kernel DRIVER SERVICE, not just the library
    # files. The library can be present while the 'dokan2' service is missing
    # (some uninstall/upgrade cycles, or a library install that never registered
    # the driver). In that state drive mounting fails with "Can't install the
    # Dokan driver" - and 'dokanctl /v' STILL prints a version string, so its
    # output is not a reliable test. The service's existence is.
    function Test-DokanDriverService {
        return $null -ne (Get-Service -Name 'dokan2' -ErrorAction SilentlyContinue)
    }

    if (Test-DokanDriverService) {
        Write-OK "Dokan driver service is registered"
    }
    elseif (Test-Path $dokanCtl) {
        # Library present but driver service missing - register it. 'dokanctl /i d'
        # installs and starts the dokan2 service from the existing dokan2.sys.
        Write-Step "Dokan library found but driver service not registered - registering it"
        $dp = Start-Process -FilePath $dokanCtl -ArgumentList '/i','d' -Wait -PassThru -NoNewWindow
        Start-Sleep -Seconds 2
        if (Test-DokanDriverService) {
            Write-OK "Dokan driver service registered and started"
        } else {
            Write-Fail "dokanctl /i d did not register the Dokan driver service (exit $($dp.ExitCode)). H: mapping will not work."
            exit 2
        }
    }
    else {
        # Dokan not installed at all - install it from the bundled MSI.
        $dokanMsi = Join-Path $InstallerDir 'Dokan_x64.msi'
        if (-not (Test-Path $dokanMsi)) {
            Write-Fail "Dokan is not installed and Dokan_x64.msi is not bundled in $InstallerDir."
            Write-Fail "H: mapping cannot work without it. Bundle Dokan_x64.msi alongside this script, pre-deploy Dokan 2.x, or re-run with -SkipDokan if Dokan is genuinely managed separately."
            exit 2
        }
        Write-Step "Installing Dokan from $dokanMsi"
        $logFile = Join-Path $env:TEMP "OneSync-Dokan-Install.log"
        $proc = Start-Process msiexec.exe -ArgumentList "/i `"$dokanMsi`" /quiet /norestart /l*v `"$logFile`"" -Wait -PassThru
        if ($proc.ExitCode -ne 0) {
            Write-Fail "Dokan install failed with exit code $($proc.ExitCode). See $logFile"
            exit 2
        }
        Write-OK "Dokan installed (log: $logFile)"
        # Make sure the driver service actually came up - the MSI normally does
        # this, but verify and register it ourselves if it didn't.
        if (-not (Test-DokanDriverService) -and (Test-Path $dokanCtl)) {
            Write-Step "Registering Dokan driver service"
            Start-Process -FilePath $dokanCtl -ArgumentList '/i','d' -Wait -PassThru -NoNewWindow | Out-Null
            Start-Sleep -Seconds 2
        }
        if (Test-DokanDriverService) {
            Write-OK "Dokan driver service registered and started"
        } else {
            Write-Fail "Dokan MSI installed but the driver service is still not registered. H: mapping will not work."
            exit 2
        }
    }
}

# --- 2. Install OneSync -------------------------------------------

Write-Step "Installing OneSync"
$msi = Join-Path $InstallerDir 'OneSync.msi'
if (-not (Test-Path $msi)) {
    Write-Fail "Cannot find $msi"
    exit 3
}

$logFile = Join-Path $env:TEMP "OneSync-Install.log"
$proc = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /quiet /norestart /l*v `"$logFile`"" -Wait -PassThru
if ($proc.ExitCode -ne 0 -and $proc.ExitCode -ne 3010) {
    Write-Fail "OneSync install failed with exit code $($proc.ExitCode). See $logFile"
    exit $proc.ExitCode
}
Write-OK "OneSync installed (log: $logFile)"

# --- 3. Start now (optional) -------------------------------------------------

if ($StartAfterInstall) {
    Write-Step "Launching OneSync for current user"
    Start-Process 'C:\Program Files\OneSync\OneSync.exe'
    Write-OK "Started"
}

# --- 4. Apply drive icons ----------------------------------------------------
# Sets HKLM DriveIcons\<L>\DefaultIcon for every drive in the installed
# config.json, backing up the originals so uninstall can restore them. Cosmetic
# only - a failure here must never fail the install.

Write-Step "Applying drive icons"
$driveIconsModule = Join-Path $PSScriptRoot 'DriveIcons.ps1'
$installedConfig  = 'C:\Program Files\OneSync\config.json'
$driveIcon        = 'C:\Program Files\OneSync\OneDriveFolder.ico'
if (-not (Test-Path $driveIconsModule)) {
    Write-Skip "DriveIcons.ps1 not found next to this script - skipping drive icons"
}
elseif (-not (Test-Path $installedConfig)) {
    Write-Skip "config.json not found at $installedConfig - skipping drive icons"
}
elseif (-not (Test-Path $driveIcon)) {
    Write-Skip "OneDriveFolder.ico not found at $driveIcon - skipping drive icons"
}
else {
    try {
        . $driveIconsModule
        Set-OneSyncDriveIcons -ConfigPath $installedConfig -IconPath $driveIcon
        Write-OK "Drive icons applied for configured drives"
    } catch {
        Write-Fail "Could not apply drive icons: $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "Installation complete." -ForegroundColor Green
Write-Host "The app will start automatically at the next user logon (via the Startup folder shortcut)." -ForegroundColor Green
