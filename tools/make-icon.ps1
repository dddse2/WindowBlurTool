<#
.SYNOPSIS
    由 BlurTool/Assets/AppIcon.png 生成多尺寸的 BlurTool/Assets/AppIcon.ico。

.DESCRIPTION
    使用圆角遮罩裁出透明四角，并输出 256/128/64/48/32/24/16 七个尺寸，
    ICO 内以 PNG 形式存放（Windows Vista 及以上支持）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$Source = "",
    [string]$Target = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Source)) { $Source = Join-Path $root "BlurTool\Assets\AppIcon.png" }
if ([string]::IsNullOrWhiteSpace($Target)) { $Target = Join-Path $root "BlurTool\Assets\AppIcon.ico" }

if (-not (Test-Path $Source)) { throw "Source image not found: $Source" }

$sizes = 256, 128, 64, 48, 32, 24, 16
$image = [System.Drawing.Image]::FromFile((Resolve-Path $Source).Path)
$frames = @()

foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    # 圆角遮罩：半径按比例，四角透明。
    $radius = [int]($size * 0.22)
    $diameter = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $path.AddArc($size - $diameter, 0, $diameter, $diameter, 270, 90)
    $path.AddArc($size - $diameter, $size - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc(0, $size - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    $graphics.SetClip($path)
    $graphics.DrawImage($image, 0, 0, $size, $size)

    $graphics.ResetClip()
    $graphics.Dispose()
    $path.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += [pscustomobject]@{ Size = $size; Data = $stream.ToArray() }

    $stream.Dispose()
    $bitmap.Dispose()
}

$image.Dispose()

$output = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($output)

$writer.Write([UInt16]0)                 # reserved
$writer.Write([UInt16]1)                 # type: icon
$writer.Write([UInt16]$frames.Count)     # image count

$offset = 6 + 16 * $frames.Count

foreach ($frame in $frames) {
    $dimension = 0
    if ($frame.Size -lt 256) { $dimension = $frame.Size }

    $writer.Write([Byte]$dimension)      # width (0 = 256)
    $writer.Write([Byte]$dimension)      # height
    $writer.Write([Byte]0)               # palette size
    $writer.Write([Byte]0)               # reserved
    $writer.Write([UInt16]1)             # color planes
    $writer.Write([UInt16]32)            # bits per pixel
    $writer.Write([UInt32]$frame.Data.Length)
    $writer.Write([UInt32]$offset)

    $offset += $frame.Data.Length
}

foreach ($frame in $frames) { $writer.Write($frame.Data) }

$writer.Flush()
[System.IO.File]::WriteAllBytes($Target, $output.ToArray())

$writer.Dispose()
$output.Dispose()

Write-Host ("Icon written: {0} ({1} bytes)" -f $Target, (Get-Item $Target).Length) -ForegroundColor Green
