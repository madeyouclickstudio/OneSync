# Generates three multi-resolution .ico files for the sync-state overlay handlers:
#   synced  - green,  thick white checkmark   (local + cloud are in sync)
#   syncing - amber,  white circular arrow     (transfer in progress)
#   error   - red,    thick white cross        (sync failed / not synced)
#
# Every glyph is drawn as a thick stroked path, NOT a font glyph - thin glyph
# strokes go blurry when the shell scales them, a thick stroke stays crisp.
# Each .ico packs many sizes (16-48 as 32-bit BGRA, 64-256 as PNG) so the shell
# always has a close match for whatever icon view is in use.

Add-Type -AssemblyName System.Drawing

function New-PF { param([double]$px, [double]$py) New-Object System.Drawing.PointF ([single]$px), ([single]$py) }

# Render the badge at a given square size; returns a 32bpp ARGB Bitmap.
# $Kind: 'syncing' | 'synced' | 'error'
function Render-Badge {
    param([int]$Size, [System.Drawing.Color]$FillColor, [string]$Kind)

    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Circle ~11/16 of the canvas, ~1/16 margin from the bottom-right edges.
    $margin = [Math]::Max(1, [int][Math]::Round($Size / 16.0))
    $circle = [int][Math]::Round($Size * 11.0 / 16.0)
    $x = $Size - $circle - $margin
    $y = $Size - $circle - $margin
    $inset = [Math]::Max(1, [int][Math]::Round($Size / 16.0))

    # White ring underlay, then the coloured fill inset.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillEllipse($white, (New-Object System.Drawing.Rectangle $x, $y, $circle, $circle))
    $fill = New-Object System.Drawing.SolidBrush $FillColor
    $g.FillEllipse($fill, (New-Object System.Drawing.Rectangle ($x + $inset), ($y + $inset), ($circle - 2 * $inset), ($circle - 2 * $inset)))

    $cx = $x + $circle / 2.0
    $cy = $y + $circle / 2.0
    $r  = ($circle - 2 * $inset) / 2.0    # inner (coloured) radius

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single]([Math]::Max(1.6, $r * 0.30)))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    if ($Kind -eq 'synced') {
        # Thick stroked checkmark.
        $pts = [System.Drawing.PointF[]]@(
            (New-PF ($cx - $r * 0.46) ($cy + $r * 0.06)),
            (New-PF ($cx - $r * 0.12) ($cy + $r * 0.40)),
            (New-PF ($cx + $r * 0.50) ($cy - $r * 0.34))
        )
        $g.DrawLines($pen, $pts)
    }
    elseif ($Kind -eq 'error') {
        # Thick stroked cross.
        $d = $r * 0.42
        $g.DrawLine($pen, (New-PF ($cx - $d) ($cy - $d)), (New-PF ($cx + $d) ($cy + $d)))
        $g.DrawLine($pen, (New-PF ($cx + $d) ($cy - $d)), (New-PF ($cx - $d) ($cy + $d)))
    }
    elseif ($Kind -eq 'syncing') {
        # Circular arrow: a thick arc with a filled triangular arrowhead.
        $rr = $r * 0.60
        $arcRect = New-Object System.Drawing.RectangleF ([single]($cx - $rr)), ([single]($cy - $rr)), ([single]($rr * 2)), ([single]($rr * 2))
        $startDeg = 115.0
        $sweepDeg = 250.0
        $g.DrawArc($pen, $arcRect, [single]$startDeg, [single]$sweepDeg)
        $endRad = ($startDeg + $sweepDeg) * [Math]::PI / 180.0
        $ex = $cx + $rr * [Math]::Cos($endRad)
        $ey = $cy + $rr * [Math]::Sin($endRad)
        $tvx = -[Math]::Sin($endRad); $tvy = [Math]::Cos($endRad)   # clockwise tangent
        $pvx = [Math]::Cos($endRad);  $pvy = [Math]::Sin($endRad)   # radial perpendicular
        $ahL = $r * 0.46
        $ahW = $r * 0.34
        $tri = [System.Drawing.PointF[]]@(
            (New-PF ($ex + $tvx * $ahL) ($ey + $tvy * $ahL)),
            (New-PF ($ex + $pvx * $ahW) ($ey + $pvy * $ahW)),
            (New-PF ($ex - $pvx * $ahW) ($ey - $pvy * $ahW))
        )
        $g.FillPolygon($white, $tri)
    }

    $pen.Dispose(); $white.Dispose(); $fill.Dispose(); $g.Dispose()
    return $bmp
}

# Build the image-data blob for one ICONDIRENTRY.
#   size <= 48 : classic 32bpp BMP (BITMAPINFOHEADER + BGRA + all-zero AND mask)
#   size  > 48 : PNG (compact; Windows Vista+ reads PNG icon entries)
function Get-IcoEntryData {
    param([System.Drawing.Bitmap]$Bmp)
    $w = $Bmp.Width; $h = $Bmp.Height
    $ms = New-Object System.IO.MemoryStream
    if ($w -le 48) {
        $bw = New-Object System.IO.BinaryWriter $ms
        $andRow = [int]([Math]::Floor(($w + 31) / 32) * 4)
        $bw.Write([UInt32]40); $bw.Write([Int32]$w); $bw.Write([Int32]($h * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($yy = $h - 1; $yy -ge 0; $yy--) {
            for ($xx = 0; $xx -lt $w; $xx++) {
                $c = $Bmp.GetPixel($xx, $yy)
                $bw.Write([Byte]$c.B); $bw.Write([Byte]$c.G); $bw.Write([Byte]$c.R); $bw.Write([Byte]$c.A)
            }
        }
        for ($i = 0; $i -lt ($andRow * $h); $i++) { $bw.Write([Byte]0) }
        $bw.Flush()
    } else {
        $Bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $data = $ms.ToArray()
    $ms.Dispose()
    return ,$data
}

# Pack several bitmaps into one multi-resolution .ico file.
function Save-IcoMulti {
    param([System.Drawing.Bitmap[]]$Bitmaps, [string]$Path)
    $entries = foreach ($b in $Bitmaps) {
        [PSCustomObject]@{ W = $b.Width; H = $b.Height; Data = (Get-IcoEntryData -Bmp $b) }
    }
    $entries = @($entries)
    $n = $entries.Count

    $out = New-Object System.IO.MemoryStream
    $ow = New-Object System.IO.BinaryWriter $out
    $ow.Write([UInt16]0); $ow.Write([UInt16]1); $ow.Write([UInt16]$n)
    $offset = 6 + ($n * 16)
    foreach ($e in $entries) {
        $ow.Write([Byte]($e.W -band 0xFF))   # 0 means 256
        $ow.Write([Byte]($e.H -band 0xFF))
        $ow.Write([Byte]0); $ow.Write([Byte]0)
        $ow.Write([UInt16]1); $ow.Write([UInt16]32)
        $ow.Write([UInt32]$e.Data.Length)
        $ow.Write([UInt32]$offset)
        $offset += $e.Data.Length
    }
    foreach ($e in $entries) { $ow.Write($e.Data) }
    $ow.Flush()
    [System.IO.File]::WriteAllBytes($Path, $out.ToArray())
    $ow.Dispose(); $out.Dispose()
}

function New-OverlayIcon {
    param([string]$Path, [System.Drawing.Color]$FillColor, [string]$Kind)
    # Explorer/desktop view sizes are 16/32/48/96/256; the rest cover DPI scaling
    # and give the shell smooth intermediate steps.
    $sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
    $bmps = foreach ($s in $sizes) { Render-Badge -Size $s -FillColor $FillColor -Kind $Kind }
    Save-IcoMulti -Bitmaps $bmps -Path $Path
    foreach ($b in $bmps) { $b.Dispose() }
    Write-Host ("Wrote $Path  (" + ((Get-Item $Path).Length) + " bytes, sizes " + ($sizes -join '/') + ")")
}

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$iconsDir = Join-Path $dir "icons"
New-Item -ItemType Directory -Path $iconsDir -Force | Out-Null

# Syncing: transfer in progress (amber, circular arrow)
New-OverlayIcon -Path "$iconsDir\syncing.ico" -FillColor ([System.Drawing.Color]::FromArgb(0xFF, 0x98, 0x00)) -Kind 'syncing'
# Synced: local and cloud in sync (green, checkmark)
New-OverlayIcon -Path "$iconsDir\synced.ico"  -FillColor ([System.Drawing.Color]::FromArgb(0x4C, 0xAF, 0x50)) -Kind 'synced'
# Error: sync failed / not synced (red, cross)
New-OverlayIcon -Path "$iconsDir\error.ico"   -FillColor ([System.Drawing.Color]::FromArgb(0xE5, 0x39, 0x35)) -Kind 'error'

Write-Host "Done. Three multi-resolution .ico files generated under: $iconsDir"
