$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = Join-Path $PSScriptRoot 'assets'
$source = [Drawing.Bitmap]::new((Join-Path $assetRoot 'owl-minecraft-master.png'))
try {
    $left = $source.Width; $top = $source.Height; $right = 0; $bottom = 0
    for ($y = 0; $y -lt $source.Height; $y++) {
        for ($x = 0; $x -lt $source.Width; $x++) {
            if ($source.GetPixel($x, $y).A -gt 8) {
                $left = [Math]::Min($left, $x); $right = [Math]::Max($right, $x)
                $top = [Math]::Min($top, $y); $bottom = [Math]::Max($bottom, $y)
            }
        }
    }
    $crop = [Drawing.Rectangle]::new($left, $top, ($right - $left + 1), ($bottom - $top + 1))
    function New-OwlBitmap([int]$width, [int]$height, [int]$padding) {
        $bitmap = [Drawing.Bitmap]::new($width, $height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale = [Math]::Min(($width - 2 * $padding) / $crop.Width, ($height - 2 * $padding) / $crop.Height)
            $drawWidth = [int][Math]::Round($crop.Width * $scale)
            $drawHeight = [int][Math]::Round($crop.Height * $scale)
            $destination = [Drawing.Rectangle]::new([int](($width - $drawWidth) / 2), [int](($height - $drawHeight) / 2), $drawWidth, $drawHeight)
            $graphics.DrawImage($source, $destination, $crop, [Drawing.GraphicsUnit]::Pixel)
        } finally { $graphics.Dispose() }
        return ,$bitmap
    }
    $mark = New-OwlBitmap 400 300 4
    try { $mark.Save((Join-Path $assetRoot 'owl.png'), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $mark.Dispose() }
    # ICO directory with a real PNG image for each Windows icon size.
    $images = [Collections.Generic.List[byte[]]]::new()
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    foreach ($size in $sizes) {
        $bitmap = New-OwlBitmap $size $size ([Math]::Max(0, [int]($size / 64)))
        $buffer = [IO.MemoryStream]::new()
        try { $bitmap.Save($buffer, [Drawing.Imaging.ImageFormat]::Png); $images.Add($buffer.ToArray()) }
        finally { $bitmap.Dispose(); $buffer.Dispose() }
    }
    $stream = [IO.File]::Create((Join-Path $assetRoot 'owl.ico'))
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($bytes in $images) { $writer.Write($bytes) }
    } finally { $writer.Dispose(); $stream.Dispose() }
    Write-Output "Prepared owl.png and owl.ico (16–256 px)."
} finally { $source.Dispose() }
