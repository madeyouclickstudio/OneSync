# generate-app-icon.ps1
# Converts app-icon.png into a multi-resolution icon.ico
# Sizes: 16, 20, 24, 32, 40, 48 (uncompressed BMP), 64, 256 (PNG entries)
# Writes ICO binary format manually to preserve alpha channel.

Add-Type -AssemblyName System.Drawing

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$pngPath = Join-Path $scriptDir "app-icon.png"
$icoPath = Join-Path $scriptDir "..\icon.ico"

if (-not (Test-Path $pngPath)) {
    Write-Error "Source image not found: $pngPath"
    exit 1
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$pngThreshold = 48  # sizes > this use PNG encoding

# Load source image
$srcImage = [System.Drawing.Image]::FromFile($pngPath)

# Build entry data - avoid functions to prevent PowerShell byte-array unrolling
$entries = New-Object System.Collections.Generic.List[hashtable]

foreach ($size in $sizes) {
    # Resize
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bmp.SetResolution($srcImage.HorizontalResolution, $srcImage.VerticalResolution)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($srcImage, 0, 0, $size, $size)
    $g.Dispose()

    if ($size -gt $pngThreshold) {
        # PNG entry: just raw PNG bytes
        $pngMs = New-Object System.IO.MemoryStream
        $bmp.Save($pngMs, [System.Drawing.Imaging.ImageFormat]::Png)
        [byte[]]$data = $pngMs.ToArray()
        $pngMs.Close()
    } else {
        # BMP entry: BITMAPINFOHEADER + bottom-up BGRA pixels + AND mask
        $w = $bmp.Width
        $h = $bmp.Height

        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)

        # BITMAPINFOHEADER (40 bytes)
        $bw.Write([uint32]40)          # biSize
        $bw.Write([int32]$w)           # biWidth
        $bw.Write([int32]($h * 2))     # biHeight (doubled for AND mask)
        $bw.Write([uint16]1)           # biPlanes
        $bw.Write([uint16]32)          # biBitCount
        $bw.Write([uint32]0)           # biCompression (BI_RGB)
        $bw.Write([uint32]0)           # biSizeImage (can be 0 for BI_RGB)
        $bw.Write([int32]0)            # biXPelsPerMeter
        $bw.Write([int32]0)            # biYPelsPerMeter
        $bw.Write([uint32]0)           # biClrUsed
        $bw.Write([uint32]0)           # biClrImportant

        # Pixel data: bottom-up row order, BGRA
        $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
        $bmpData = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $stride = $bmpData.Stride
        $totalBytes = [Math]::Abs($stride) * $h
        $pixelBytes = New-Object byte[] $totalBytes
        [System.Runtime.InteropServices.Marshal]::Copy($bmpData.Scan0, $pixelBytes, 0, $totalBytes)
        $bmp.UnlockBits($bmpData)

        # Write rows bottom-up
        $rowSize = $w * 4
        for ($y = $h - 1; $y -ge 0; $y--) {
            $offset = $y * [Math]::Abs($stride)
            $bw.Write($pixelBytes, $offset, $rowSize)
        }

        # AND mask: 1 bit per pixel, rows padded to 4 bytes, bottom-up
        $andRowBytes = [Math]::Ceiling($w / 8.0)
        $andRowPadded = [int]([Math]::Ceiling($andRowBytes / 4.0) * 4)
        $andRow = New-Object byte[] $andRowPadded

        # All zeros = fully opaque (alpha is in the BGRA data)
        for ($y = 0; $y -lt $h; $y++) {
            $bw.Write($andRow, 0, $andRowPadded)
        }

        $bw.Flush()
        [byte[]]$data = $ms.ToArray()
        $bw.Close()
        $ms.Close()
    }

    Write-Host "  Size ${size}x${size}: $($data.Length) bytes"

    $entry = @{
        Width  = $size
        Height = $size
        Data   = $data
    }
    $entries.Add($entry)
    $bmp.Dispose()
}
$srcImage.Dispose()

# Write ICO file
$fs = New-Object System.IO.FileStream($icoPath, [System.IO.FileMode]::Create)
$bw2 = New-Object System.IO.BinaryWriter($fs)

$numEntries = $entries.Count

# ICO header (6 bytes)
$bw2.Write([uint16]0)              # reserved
$bw2.Write([uint16]1)              # type: 1 = ICO
$bw2.Write([uint16]$numEntries)    # count

# Calculate data offset: header(6) + entries(16 each)
[uint32]$dataOffset = 6 + ($numEntries * 16)

# Write directory entries (16 bytes each)
foreach ($entry in $entries) {
    $w = $entry.Width
    $h = $entry.Height
    [byte[]]$dataBytes = $entry.Data

    # Width and Height: 0 means 256
    if ($w -ge 256) { $bw2.Write([byte]0) } else { $bw2.Write([byte]$w) }
    if ($h -ge 256) { $bw2.Write([byte]0) } else { $bw2.Write([byte]$h) }

    $bw2.Write([byte]0)             # color palette count
    $bw2.Write([byte]0)             # reserved
    $bw2.Write([uint16]1)           # color planes
    $bw2.Write([uint16]32)          # bits per pixel
    $bw2.Write([uint32]$dataBytes.Length)  # data size
    $bw2.Write([uint32]$dataOffset)       # data offset

    $dataOffset += $dataBytes.Length
}

# Write image data
foreach ($entry in $entries) {
    [byte[]]$dataBytes = $entry.Data
    $bw2.Write($dataBytes)
}

$bw2.Close()
$fs.Close()

$resolvedIcoPath = (Resolve-Path $icoPath).Path
Write-Host ""
Write-Host "icon.ico generated at: $resolvedIcoPath"
Write-Host "Entries: $($entries.Count) sizes"
$icoSize = (Get-Item $icoPath).Length
Write-Host "File size: $icoSize bytes"
