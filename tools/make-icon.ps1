<#
.SYNOPSIS
    Zeichnet das App-Symbol von NexusSSH und schreibt es als .ico (mehrere Größen) und .png.

.DESCRIPTION
    Das Symbol ist hier beschrieben statt als Bilddatei gepflegt: ein dunkles, abgerundetes Quadrat mit dem
    Prompt eines Terminals (Winkel und Cursor). Wer es ändern will, ändert dieses Skript und lässt es laufen:

        ./tools/make-icon.ps1

    Braucht nur Windows (System.Drawing), kein weiteres Werkzeug. Die Ergebnisse sind eingecheckt.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\frontend\NexusSSH.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconImage([int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        # Alle Maße in Anteilen der Kantenlänge, damit jede Größe gleich aussieht.
        $s = [single]$Size
        $inset = $s * 0.04
        $side = $s - (2 * $inset)
        $radius = $s * 0.22

        $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $d = $radius * 2
        $shape.AddArc($inset, $inset, $d, $d, 180, 90)
        $shape.AddArc($inset + $side - $d, $inset, $d, $d, 270, 90)
        $shape.AddArc($inset + $side - $d, $inset + $side - $d, $d, $d, 0, 90)
        $shape.AddArc($inset, $inset + $side - $d, $d, $d, 90, 90)
        $shape.CloseFigure()

        $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.PointF]::new(0, 0),
            [System.Drawing.PointF]::new(0, $s),
            [System.Drawing.Color]::FromArgb(255, 38, 44, 58),
            [System.Drawing.Color]::FromArgb(255, 14, 17, 24))
        $graphics.FillPath($background, $shape)

        # Winkel des Prompts in Akzentblau …
        $stroke = [Math]::Max($s * 0.105, 1.6)
        $accent = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 96, 165, 250), $stroke)
        $accent.StartCap = $accent.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $accent.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawLines($accent, [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new($s * 0.25, $s * 0.31),
                [System.Drawing.PointF]::new($s * 0.47, $s * 0.50),
                [System.Drawing.PointF]::new($s * 0.25, $s * 0.69)))

        # … und der Cursor daneben in Weiß.
        $cursor = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 240, 243, 248), $stroke)
        $cursor.StartCap = $cursor.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($cursor, $s * 0.56, $s * 0.69, $s * 0.76, $s * 0.69)

        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $stream.ToArray()
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) { , (New-IconImage $size) }

# .ico: Kopf, je Bild ein Eintrag (16 Byte), dann die Bilder als PNG.
$ico = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($ico)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $writer.Write([byte]($sizes[$i] % 256))   # 256 steht als 0
    $writer.Write([byte]($sizes[$i] % 256))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image) }
$writer.Flush()

$target = (Resolve-Path $OutputDirectory).Path
[System.IO.File]::WriteAllBytes((Join-Path $target 'NexusSSH.ico'), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $target 'NexusSSH.png'), (New-IconImage 64))
Write-Host "NexusSSH.ico ($($sizes -join ', ')) und NexusSSH.png geschrieben nach $target"
