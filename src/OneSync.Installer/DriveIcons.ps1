# DriveIcons.ps1 - OneSync drive-icon registry helpers.
#
# Dot-sourced by Install-OneSync.ps1, Uninstall-OneSync.ps1,
# and Test-DriveIcons.ps1. Defines functions and three module-level variables
# only - NO #requires and NO load-time side effects - so dot-sourcing is safe.
#
# Windows resolves a drive's icon from:
#   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\<L>\DefaultIcon
# A standard-user process cannot write that hive, so these helpers run from the
# elevated install/uninstall scripts. The original DefaultIcon of every letter
# we touch is saved under the backup key so the uninstaller can restore it; the
# sentinel '__NONE__' records "this letter had no DefaultIcon before us".
#
# The registry roots are parameters (defaulting to the real HKLM locations) so
# the logic can be unit-tested against a throwaway HKCU hive without admin.

$OneSyncDriveIconsRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons'
$OneSyncBackupRoot     = 'HKLM:\SOFTWARE\OneSync\OneSync\OriginalDriveIcons'
$OneSyncNoOriginalIcon = '__NONE__'

function Get-OneSyncConfiguredDriveLetters {
    # Returns the validated, upper-cased, de-duplicated drive letters from
    # config.json. Returns @() if the file is missing or unreadable.
    param([Parameter(Mandatory)][string]$ConfigPath)

    if (-not (Test-Path -LiteralPath $ConfigPath)) { return @() }
    try {
        $cfg = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
    } catch {
        return @()
    }
    $letters = New-Object System.Collections.Generic.List[string]
    foreach ($d in @($cfg.drives)) {
        $l = ([string]$d.letter).Trim().ToUpperInvariant()
        if ($l -match '^[A-Z]$' -and -not $letters.Contains($l)) { $letters.Add($l) }
    }
    return $letters.ToArray()
}

function Restore-OneSyncDriveIcon {
    # Restores ONE letter's DefaultIcon from the backup, then drops that letter's
    # backup entry. A sentinel value means the letter had no DefaultIcon
    # originally, so the DefaultIcon (and the now-empty <L> key) is removed.
    param(
        [Parameter(Mandatory)][string]$Letter,
        [string]$DriveIconsRoot = $OneSyncDriveIconsRoot,
        [string]$BackupRoot     = $OneSyncBackupRoot
    )

    if (-not (Test-Path -LiteralPath $BackupRoot)) { return }
    $backup = Get-ItemProperty -LiteralPath $BackupRoot -ErrorAction SilentlyContinue
    if (-not $backup -or -not ($backup.PSObject.Properties.Name -contains $Letter)) { return }

    $original  = [string]$backup.$Letter
    $letterKey = Join-Path $DriveIconsRoot $Letter
    $iconKey   = Join-Path $letterKey 'DefaultIcon'

    if ($original -eq $OneSyncNoOriginalIcon) {
        if (Test-Path -LiteralPath $iconKey) { Remove-Item -LiteralPath $iconKey -Recurse -Force }
        if (Test-Path -LiteralPath $letterKey) {
            $hasSubkeys = @(Get-ChildItem -LiteralPath $letterKey -ErrorAction SilentlyContinue).Count -gt 0
            $hasValues  = @((Get-Item -LiteralPath $letterKey).Property).Count -gt 0
            if (-not $hasSubkeys -and -not $hasValues) { Remove-Item -LiteralPath $letterKey -Force }
        }
    } else {
        if (-not (Test-Path -LiteralPath $iconKey)) { New-Item -Path $iconKey -Force | Out-Null }
        Set-ItemProperty -LiteralPath $iconKey -Name '(default)' -Value $original
    }

    Remove-ItemProperty -LiteralPath $BackupRoot -Name $Letter -Force -ErrorAction SilentlyContinue
}

function Restore-OneSyncDriveIcons {
    # Restores every letter recorded in the backup key, then removes the backup
    # key. Safe to call when there is nothing to restore.
    param(
        [string]$DriveIconsRoot = $OneSyncDriveIconsRoot,
        [string]$BackupRoot     = $OneSyncBackupRoot
    )

    if (-not (Test-Path -LiteralPath $BackupRoot)) { return }
    $backup  = Get-ItemProperty -LiteralPath $BackupRoot -ErrorAction SilentlyContinue
    $letters = @()
    if ($backup) { $letters = $backup.PSObject.Properties.Name | Where-Object { $_ -notlike 'PS*' } }
    foreach ($l in $letters) {
        Restore-OneSyncDriveIcon -Letter $l -DriveIconsRoot $DriveIconsRoot -BackupRoot $BackupRoot
    }
    Remove-Item -LiteralPath $BackupRoot -Recurse -Force -ErrorAction SilentlyContinue
}

function Set-OneSyncDriveIcons {
    # Applies $IconPath as the DefaultIcon for every drive letter in config.json,
    # backing up each letter's original DefaultIcon first (once). Then reconciles:
    # any letter still in the backup but no longer in config is restored.
    param(
        [Parameter(Mandatory)][string]$ConfigPath,
        [Parameter(Mandatory)][string]$IconPath,
        [string]$DriveIconsRoot = $OneSyncDriveIconsRoot,
        [string]$BackupRoot     = $OneSyncBackupRoot
    )

    $letters = @(Get-OneSyncConfiguredDriveLetters -ConfigPath $ConfigPath)
    if (-not (Test-Path -LiteralPath $BackupRoot)) { New-Item -Path $BackupRoot -Force | Out-Null }

    foreach ($letter in $letters) {
        $letterKey = Join-Path $DriveIconsRoot $letter
        $iconKey   = Join-Path $letterKey 'DefaultIcon'

        # Back up the original DefaultIcon - only the first time we touch <L>.
        $backup = Get-ItemProperty -LiteralPath $BackupRoot -ErrorAction SilentlyContinue
        if (-not ($backup -and ($backup.PSObject.Properties.Name -contains $letter))) {
            $existing = $null
            if (Test-Path -LiteralPath $iconKey) {
                $existing = (Get-ItemProperty -LiteralPath $iconKey -ErrorAction SilentlyContinue).'(default)'
            }
            $store = if ([string]::IsNullOrEmpty($existing)) { $OneSyncNoOriginalIcon } else { [string]$existing }
            New-ItemProperty -LiteralPath $BackupRoot -Name $letter -Value $store -PropertyType String -Force | Out-Null
        }

        # Apply our icon.
        if (-not (Test-Path -LiteralPath $iconKey)) { New-Item -Path $iconKey -Force | Out-Null }
        Set-ItemProperty -LiteralPath $iconKey -Name '(default)' -Value $IconPath
    }

    # Reconcile: restore letters that are backed up but no longer configured.
    $backup = Get-ItemProperty -LiteralPath $BackupRoot -ErrorAction SilentlyContinue
    if ($backup) {
        $backedUp = $backup.PSObject.Properties.Name | Where-Object { $_ -notlike 'PS*' }
        foreach ($bl in $backedUp) {
            if ($letters -notcontains $bl) {
                Restore-OneSyncDriveIcon -Letter $bl -DriveIconsRoot $DriveIconsRoot -BackupRoot $BackupRoot
            }
        }
    }
}
