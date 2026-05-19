# Standalone tests for DriveIcons.ps1. No admin required: all registry work is
# done under a throwaway HKCU test hive. Run: powershell -File Test-DriveIcons.ps1
# Exit code 0 = all passed, 1 = one or more failed.

. (Join-Path $PSScriptRoot 'DriveIcons.ps1')

$TestRoot     = 'HKCU:\SOFTWARE\__OneSyncDriveIconsTest__'
$IconsRoot    = Join-Path $TestRoot 'DriveIcons'
$BackupRoot   = Join-Path $TestRoot 'Backup'
$IconPath     = 'C:\Program Files\OneSync\OneDriveFolder.ico'
$TmpConfig    = Join-Path $env:TEMP '__sdm_driveicons_test_config.json'

$script:Failures = 0
function Assert-Equal($expected, $actual, $what) {
    if ($expected -eq $actual) {
        Write-Host ("  PASS  {0}" -f $what) -ForegroundColor Green
    } else {
        Write-Host ("  FAIL  {0}`n        expected: [{1}]`n        actual:   [{2}]" -f $what,$expected,$actual) -ForegroundColor Red
        $script:Failures++
    }
}
function Reset-TestState {
    if (Test-Path $TestRoot) { Remove-Item $TestRoot -Recurse -Force }
    New-Item -Path $IconsRoot  -Force | Out-Null
    New-Item -Path $BackupRoot -Force | Out-Null
}
function Write-TestConfig([string[]]$letters) {
    $drives = $letters | ForEach-Object { @{ letter = $_; label = "Drive $_"; type = 'onedrive' } }
    @{ drives = $drives } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $TmpConfig -Encoding UTF8
}
function Get-IconValue([string]$letter) {
    $k = Join-Path $IconsRoot "$letter\DefaultIcon"
    if (-not (Test-Path $k)) { return $null }
    return (Get-ItemProperty -LiteralPath $k -ErrorAction SilentlyContinue).'(default)'
}
function Get-BackupValue([string]$letter) {
    $b = Get-ItemProperty -LiteralPath $BackupRoot -ErrorAction SilentlyContinue
    if ($b -and ($b.PSObject.Properties.Name -contains $letter)) { return [string]$b.$letter }
    return $null
}

# --- Get-OneSyncConfiguredDriveLetters ---
Reset-TestState
Write-TestConfig @('h','I','H','9','II')   # mixed case, dup, invalid
$letters = @(Get-OneSyncConfiguredDriveLetters -ConfigPath $TmpConfig)
Assert-Equal 'H,I' ($letters -join ',') 'Get-OneSyncConfiguredDriveLetters normalises case, de-dups, drops invalid'
Assert-Equal 0 (@(Get-OneSyncConfiguredDriveLetters -ConfigPath 'C:\nope\missing.json')).Count 'Get-OneSyncConfiguredDriveLetters returns empty for missing config'

# --- Restore-OneSyncDriveIcon: restores a real original value ---
Reset-TestState
New-Item -Path (Join-Path $IconsRoot 'H\DefaultIcon') -Force | Out-Null
Set-ItemProperty -LiteralPath (Join-Path $IconsRoot 'H\DefaultIcon') -Name '(default)' -Value $IconPath
New-ItemProperty -LiteralPath $BackupRoot -Name 'H' -Value 'C:\old\original.ico' -PropertyType String -Force | Out-Null
Restore-OneSyncDriveIcon -Letter 'H' -DriveIconsRoot $IconsRoot -BackupRoot $BackupRoot
Assert-Equal 'C:\old\original.ico' (Get-IconValue 'H') 'Restore-OneSyncDriveIcon restores the original DefaultIcon'
Assert-Equal $null (Get-BackupValue 'H') 'Restore-OneSyncDriveIcon drops the backup entry'

# --- Restore-OneSyncDriveIcon: sentinel means "remove what we added" ---
Reset-TestState
New-Item -Path (Join-Path $IconsRoot 'I\DefaultIcon') -Force | Out-Null
Set-ItemProperty -LiteralPath (Join-Path $IconsRoot 'I\DefaultIcon') -Name '(default)' -Value $IconPath
New-ItemProperty -LiteralPath $BackupRoot -Name 'I' -Value '__NONE__' -PropertyType String -Force | Out-Null
Restore-OneSyncDriveIcon -Letter 'I' -DriveIconsRoot $IconsRoot -BackupRoot $BackupRoot
Assert-Equal $false (Test-Path (Join-Path $IconsRoot 'I')) 'Restore-OneSyncDriveIcon (sentinel) removes the whole <L> key it created'
Assert-Equal $null (Get-BackupValue 'I') 'Restore-OneSyncDriveIcon (sentinel) drops the backup entry'

# --- Restore-OneSyncDriveIcons: restores everything, removes backup key ---
Reset-TestState
New-Item -Path (Join-Path $IconsRoot 'H\DefaultIcon') -Force | Out-Null
Set-ItemProperty -LiteralPath (Join-Path $IconsRoot 'H\DefaultIcon') -Name '(default)' -Value $IconPath
New-Item -Path (Join-Path $IconsRoot 'J\DefaultIcon') -Force | Out-Null
Set-ItemProperty -LiteralPath (Join-Path $IconsRoot 'J\DefaultIcon') -Name '(default)' -Value $IconPath
New-ItemProperty -LiteralPath $BackupRoot -Name 'H' -Value 'C:\old\h.ico' -PropertyType String -Force | Out-Null
New-ItemProperty -LiteralPath $BackupRoot -Name 'J' -Value '__NONE__'      -PropertyType String -Force | Out-Null
Restore-OneSyncDriveIcons -DriveIconsRoot $IconsRoot -BackupRoot $BackupRoot
Assert-Equal 'C:\old\h.ico' (Get-IconValue 'H') 'Restore-OneSyncDriveIcons restores a real value'
Assert-Equal $false (Test-Path (Join-Path $IconsRoot 'J')) 'Restore-OneSyncDriveIcons honours the sentinel'
Assert-Equal $false (Test-Path $BackupRoot) 'Restore-OneSyncDriveIcons removes the backup key when done'

# --- Set-OneSyncDriveIcons: applies icon, backs up "no original" as sentinel ---
Reset-TestState
Write-TestConfig @('H','I')
Set-OneSyncDriveIcons -ConfigPath $TmpConfig -IconPath $IconPath -DriveIconsRoot $IconsRoot -BackupRoot $BackupRoot
Assert-Equal $IconPath  (Get-IconValue 'H')  'Set-OneSyncDriveIcons sets DefaultIcon for H'
Assert-Equal $IconPath  (Get-IconValue 'I')  'Set-OneSyncDriveIcons sets DefaultIcon for I'
Assert-Equal '__NONE__' (Get-BackupValue 'H') 'Set-OneSyncDriveIcons backs up "no original" as the sentinel'

# --- Set-OneSyncDriveIcons: preserves a pre-existing original in the backup ---
Reset-TestState
New-Item -Path (Join-Path $IconsRoot 'H\DefaultIcon') -Force | Out-Null
Set-ItemProperty -LiteralPath (Join-Path $IconsRoot 'H\DefaultIcon') -Name '(default)' -Value 'C:\was\here.ico'
Write-TestConfig @('H')
Set-OneSyncDriveIcons -ConfigPath $TmpConfig -IconPath $IconPath -DriveIconsRoot $IconsRoot -BackupRoot $BackupRoot
Assert-Equal $IconPath        (Get-IconValue 'H')   'Set-OneSyncDriveIcons overwrites a pre-existing icon'
Assert-Equal 'C:\was\here.ico' (Get-BackupValue 'H') 'Set-OneSyncDriveIcons backs up the pre-existing original'

# --- Set-OneSyncDriveIcons: re-run does not clobber the saved original ---
Set-OneSyncDriveIcons -ConfigPath $TmpConfig -IconPath $IconPath -DriveIconsRoot $IconsRoot -BackupRoot $BackupRoot
Assert-Equal 'C:\was\here.ico' (Get-BackupValue 'H') 'Set-OneSyncDriveIcons re-run keeps the original backup (idempotent)'

# --- Set-OneSyncDriveIcons: reconcile - a letter dropped from config is restored ---
Reset-TestState
Write-TestConfig @('H','I')
Set-OneSyncDriveIcons -ConfigPath $TmpConfig -IconPath $IconPath -DriveIconsRoot $IconsRoot -BackupRoot $BackupRoot
Write-TestConfig @('H')   # I removed from config
Set-OneSyncDriveIcons -ConfigPath $TmpConfig -IconPath $IconPath -DriveIconsRoot $IconsRoot -BackupRoot $BackupRoot
Assert-Equal $IconPath (Get-IconValue 'H') 'Set-OneSyncDriveIcons reconcile keeps still-configured H'
Assert-Equal $false (Test-Path (Join-Path $IconsRoot 'I')) 'Set-OneSyncDriveIcons reconcile restores dropped letter I'
Assert-Equal $null (Get-BackupValue 'I') 'Set-OneSyncDriveIcons reconcile drops I from the backup'

# --- Set-OneSyncDriveIcons: a letter added to config on re-run gets the icon ---
Write-TestConfig @('H','I','J')   # I added back, J newly added
Set-OneSyncDriveIcons -ConfigPath $TmpConfig -IconPath $IconPath -DriveIconsRoot $IconsRoot -BackupRoot $BackupRoot
Assert-Equal $IconPath (Get-IconValue 'I') 'Set-OneSyncDriveIcons re-run applies the icon to a re-added letter'
Assert-Equal $IconPath (Get-IconValue 'J') 'Set-OneSyncDriveIcons re-run applies the icon to a newly added letter'
Assert-Equal $IconPath (Get-IconValue 'H') 'Set-OneSyncDriveIcons re-run leaves still-configured H set'

# --- cleanup + report ---
if (Test-Path $TestRoot) { Remove-Item $TestRoot -Recurse -Force }
if (Test-Path $TmpConfig) { Remove-Item $TmpConfig -Force }
Write-Host ""
if ($script:Failures -eq 0) { Write-Host "ALL TESTS PASSED" -ForegroundColor Green; exit 0 }
else { Write-Host ("$script:Failures TEST(S) FAILED") -ForegroundColor Red; exit 1 }
