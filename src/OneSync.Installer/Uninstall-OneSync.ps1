#requires -RunAsAdministrator
<#
.SYNOPSIS
    Silently uninstalls OneSync.

.DESCRIPTION
    1. Stops any running OneSync processes (graceful 90s shutdown if possible)
    2. Restores the original drive icons
    3. Uninstalls the MSI
    4. Optionally removes per-user local state (placeholders, auth cache, logs)
    5. Optionally uninstalls the Dokan driver

.PARAMETER PurgeUserState
    Also delete per-user state under %LOCALAPPDATA%\OneSync for ALL users.
    Without this flag, only the application is uninstalled and user data remains.

.PARAMETER UninstallDokan
    Also uninstall the Dokan driver. Off by default since other apps may use it.
#>
[CmdletBinding()]
param(
    [switch]$PurgeUserState,
    [switch]$UninstallDokan
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "[Uninstall] $msg" -ForegroundColor Cyan }
function Write-OK([string]$msg)   { Write-Host "  OK  $msg" -ForegroundColor Green }
function Write-Skip([string]$msg) { Write-Host "  SKIP $msg" -ForegroundColor Yellow }
function Write-Fail([string]$msg) { Write-Host "  FAIL $msg" -ForegroundColor Red }

# --- 1. Stop running processes (with up to 90s grace for sync flush) ---------

Write-Step "Stopping any running OneSync instances"
$procs = Get-Process OneSync -ErrorAction SilentlyContinue
if ($procs) {
    foreach ($p in $procs) {
        try {
            # WM_CLOSE attempt: the app's tray-icon Exit handler triggers graceful shutdown
            $p.CloseMainWindow() | Out-Null
        } catch { }
    }

    $deadline = (Get-Date).AddSeconds(95)
    while ((Get-Date) -lt $deadline -and (Get-Process OneSync -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds 2
    }

    $still = Get-Process OneSync -ErrorAction SilentlyContinue
    if ($still) {
        Write-Skip "Force-killing remaining processes"
        $still | Stop-Process -Force
    }
    Write-OK "All processes stopped"
} else {
    Write-OK "Nothing running"
}

# --- 2. Restore drive icons --------------------------------------------------
# Must run BEFORE the MSI uninstall: the backup key lives under the
# MSI-managed HKLM\SOFTWARE\OneSync\OneSync key.
# Cosmetic only - a failure here must never fail the uninstall.

Write-Step "Restoring original drive icons"
$driveIconsModule = Join-Path $PSScriptRoot 'DriveIcons.ps1'
if (Test-Path $driveIconsModule) {
    try {
        . $driveIconsModule
        Restore-OneSyncDriveIcons
        Write-OK "Drive icons restored"
    } catch {
        Write-Fail "Could not restore drive icons: $($_.Exception.Message)"
    }
} else {
    Write-Skip "DriveIcons.ps1 not found next to this script - skipping drive icon restore"
}

# --- 3. Uninstall via MSI ----------------------------------------------------

Write-Step "Uninstalling OneSync"
$installed = Get-WmiObject Win32_Product -Filter "Name = 'OneSync'" -ErrorAction SilentlyContinue
if ($installed) {
    $logFile = Join-Path $env:TEMP "OneSync-Uninstall.log"
    $proc = Start-Process msiexec.exe -ArgumentList "/x $($installed.IdentifyingNumber) /quiet /norestart /l*v `"$logFile`"" -Wait -PassThru
    if ($proc.ExitCode -eq 0 -or $proc.ExitCode -eq 3010) {
        Write-OK "MSI uninstall complete (log: $logFile)"
    } else {
        Write-Fail "MSI uninstall exited with $($proc.ExitCode). See $logFile"
    }
} else {
    Write-Skip "OneSync not registered in WMI - may already be uninstalled"
}

# --- 4. Optional: purge per-user state ---------------------------------------

if ($PurgeUserState) {
    Write-Step "Purging per-user state for all profiles"
    $usersFolder = $env:SystemDrive + "\Users"
    Get-ChildItem $usersFolder -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $stateDir = Join-Path $_.FullName 'AppData\Local\OneSync'
        if (Test-Path $stateDir) {
            try {
                Remove-Item $stateDir -Recurse -Force -ErrorAction Stop
                Write-OK "Removed $stateDir"
            } catch {
                Write-Fail "Could not remove $stateDir : $_"
            }
        }
    }
}

# --- 5. Optional: uninstall Dokan --------------------------------------------

if ($UninstallDokan) {
    Write-Step "Uninstalling Dokan"
    $dokan = Get-WmiObject Win32_Product -Filter "Name LIKE '%Dokan%'" -ErrorAction SilentlyContinue
    if ($dokan) {
        foreach ($d in $dokan) {
            Start-Process msiexec.exe -ArgumentList "/x $($d.IdentifyingNumber) /quiet /norestart" -Wait
            Write-OK "Uninstalled: $($d.Name)"
        }
    } else {
        Write-Skip "Dokan not found in WMI"
    }
}

Write-Host ""
Write-Host "Uninstallation complete." -ForegroundColor Green
