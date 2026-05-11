# Simple ICO creator - creates a multi-resolution ICO from PNG
Add-Type -AssemblyName System.Drawing

$pngPath = "C:\Dev\AppTunnel\AppTunnel\icon-256.png"
$icoPath = "C:\Dev\AppTunnel\AppTunnel\app.ico"

# Load the PNG
$png = [System.Drawing.Image]::FromFile($pngPath)

# Create different sizes
$sizes = @(256, 128, 64, 48, 32, 16)
$icons = @()

foreach ($size in $sizes) {
    $resized = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($resized)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($png, 0, 0, $size, $size)
    $graphics.Dispose()
    
    # Convert to icon
    $ms = New-Object System.IO.MemoryStream
    $resized.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $icons += ,@($size, $ms.ToArray())
    $resized.Dispose()
    $ms.Dispose()
}

$png.Dispose()

# Write ICO file manually
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICO header
$bw.Write([uint16]0)      # Reserved
$bw.Write([uint16]1)      # Type: 1 = ICO
$bw.Write([uint16]$icons.Count)  # Number of images

# Image directory
$offset = 6 + ($icons.Count * 16)  # Header + directory entries
foreach ($icon in $icons) {
    $size = $icon[0]
    $data = $icon[1]
    
    $bw.Write([byte]($size % 256))  # Width (0 = 256)
    $bw.Write([byte]($size % 256))  # Height
    $bw.Write([byte]0)      # Colors
    $bw.Write([byte]0)      # Reserved
    $bw.Write([uint16]1)    # Color planes
    $bw.Write([uint16]32)   # Bits per pixel
    $bw.Write([uint32]$data.Length)  # Size of image data
    $bw.Write([uint32]$offset)       # Offset to image data
    
    $offset += $data.Length
}

# Write image data
foreach ($icon in $icons) {
    $bw.Write($icon[1])
}

$bw.Close()
$fs.Close()

Write-Host "ICO file created successfully: $icoPath"
Write-Host "Sizes included: $($sizes -join ', ')"
