<#
.SYNOPSIS
Generates the multi-resolution Windows icon from CleanUpDeskIcon.png.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'src/CleanUpDesk.App/Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$source = [System.Drawing.Image]::FromFile((Join-Path $root 'CleanUpDeskIcon.png'))
$images = @()
try {
    foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = New-Object System.IO.MemoryStream
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($source, 0, 0, $size, $size)
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $images += ,@($size, $stream.ToArray())
        } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
$output = [System.IO.File]::Create((Join-Path $assets 'CleanUpDesk.ico'))
$writer = New-Object System.IO.BinaryWriter($output)
try {
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($item in $images) {
        $dimension = if ($item[0] -eq 256) { 0 } else { $item[0] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([UInt16]1); $writer.Write([UInt16]32)
        $writer.Write([UInt32]$item[1].Length); $writer.Write([UInt32]$offset)
        $offset += $item[1].Length
    }
    foreach ($item in $images) { $writer.Write([byte[]]$item[1]) }
} finally { $writer.Dispose(); $output.Dispose() }
Write-Output 'Icon generated in seven resolutions.'
