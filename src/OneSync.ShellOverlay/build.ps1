# Compiles OneSyncShellOverlay.dll using MSVC + Windows SDK directly.
# Output goes to bin\OneSyncShellOverlay.dll.
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'

$projDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$binDir = Join-Path $projDir "bin"
$objDir = Join-Path $projDir "obj"
New-Item -ItemType Directory -Path $binDir, $objDir -Force | Out-Null

# Locate MSVC and Windows SDK
$msvcRoot = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC"
$msvcVer = (Get-ChildItem $msvcRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$msvcBin = Join-Path $msvcRoot "$msvcVer\bin\Hostx64\x64"

$sdkRoot = "C:\Program Files (x86)\Windows Kits\10"
$sdkVer = (Get-ChildItem (Join-Path $sdkRoot "Include") -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$sdkBin = Join-Path $sdkRoot "bin\$sdkVer\x64"

Write-Host "Toolchain:"
Write-Host "  MSVC: $msvcVer"
Write-Host "  SDK : $sdkVer"
Write-Host ""

# Build INCLUDE and LIB paths
$inc = @(
    "$msvcRoot\$msvcVer\include",
    "$sdkRoot\Include\$sdkVer\ucrt",
    "$sdkRoot\Include\$sdkVer\shared",
    "$sdkRoot\Include\$sdkVer\um",
    "$sdkRoot\Include\$sdkVer\winrt"
)
$lib = @(
    "$msvcRoot\$msvcVer\lib\x64",
    "$sdkRoot\Lib\$sdkVer\ucrt\x64",
    "$sdkRoot\Lib\$sdkVer\um\x64"
)
$env:INCLUDE = ($inc -join ";")
$env:LIB = ($lib -join ";")

$rc = Join-Path $sdkBin "rc.exe"
$cl = Join-Path $msvcBin "cl.exe"
$link = Join-Path $msvcBin "link.exe"

foreach ($exe in @($rc, $cl, $link)) {
    if (-not (Test-Path $exe)) { throw "Missing required tool: $exe" }
}

Push-Location $projDir
try {
    # Compile the .rc to a .res
    Write-Host "[1/3] rc.exe resource.rc"
    & $rc /nologo /fo "$objDir\resource.res" resource.rc
    if ($LASTEXITCODE -ne 0) { throw "rc.exe failed" }

    # Compile .cpp -> .obj
    $cflags = @(
        "/c", "/nologo", "/EHsc", "/W3", "/MD", "/std:c++17",
        "/Fo:$objDir\\",
        "/D", "WIN32",
        "/D", "_WINDOWS",
        "/D", "UNICODE",
        "/D", "_UNICODE",
        "/D", "_USRDLL"
    )
    if ($Configuration -eq "Release") {
        $cflags += @("/O2", "/D", "NDEBUG")
    } else {
        $cflags += @("/Od", "/Zi", "/D", "_DEBUG")
    }

    Write-Host "[2/3] cl.exe Overlay.cpp DllMain.cpp Thumbnail.cpp"
    & $cl @cflags Overlay.cpp DllMain.cpp Thumbnail.cpp
    if ($LASTEXITCODE -ne 0) { throw "cl.exe failed" }

    # Link to DLL
    $lflags = @(
        "/nologo",
        "/DLL",
        "/MACHINE:X64",
        "/SUBSYSTEM:WINDOWS",
        "/DEF:OneSync.ShellOverlay.def",
        "/OUT:$binDir\OneSyncShellOverlay.dll",
        "$objDir\Overlay.obj",
        "$objDir\DllMain.obj",
        "$objDir\Thumbnail.obj",
        "$objDir\resource.res",
        "shlwapi.lib",
        "ole32.lib",
        "advapi32.lib",
        "user32.lib",
        "kernel32.lib",
        "shell32.lib",
        "gdi32.lib",
        "windowscodecs.lib"
    )
    Write-Host "[3/3] link.exe -> bin\OneSyncShellOverlay.dll"
    & $link @lflags
    if ($LASTEXITCODE -ne 0) { throw "link.exe failed" }

    Write-Host ""
    Write-Host "Build succeeded:"
    Get-Item "$binDir\OneSyncShellOverlay.dll" | Format-List Name, Length, LastWriteTime
} finally {
    Pop-Location
}
