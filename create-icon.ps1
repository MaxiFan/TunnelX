# Create a simple Tx icon using .NET Drawing
Add-Type -AssemblyName System.Drawing

# Create 256x256 bitmap
$bitmap = New-Object System.Drawing.Bitmap 256, 256
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

# Draw rounded rectangle background
$bgBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(30, 136, 229))
$rect = New-Object System.Drawing.Rectangle 0, 0, 256, 256
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$radius = 40
$path.AddArc($rect.Left, $rect.Top, $radius, $radius, 180, 90)
$path.AddArc($rect.Right - $radius, $rect.Top, $radius, $radius, 270, 90)
$path.AddArc($rect.Right - $radius, $rect.Bottom - $radius, $radius, $radius, 0, 90)
$path.AddArc($rect.Left, $rect.Bottom - $radius, $radius, $radius, 90, 90)
$path.CloseFigure()
$graphics.FillPath($bgBrush, $path)

# Draw "Tx" text
$font = New-Object System.Drawing.Font("Segoe UI", 120, [System.Drawing.FontStyle]::Bold)
$textBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$format = New-Object System.Drawing.StringFormat
$format.Alignment = [System.Drawing.StringAlignment]::Center
$format.LineAlignment = [System.Drawing.StringAlignment]::Center
$graphics.DrawString("Tx", $font, $textBrush, 128, 128, $format)

# Save as PNG first
$pngPath = "C:\Dev\AppTunnel\AppTunnel\icon-256.png"
$bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "PNG icon created: $pngPath"

# Cleanup
$graphics.Dispose()
$bitmap.Dispose()
$font.Dispose()
$textBrush.Dispose()
$bgBrush.Dispose()

Write-Host "Now converting PNG to ICO..."

# Convert PNG to ICO using ImageMagick if available, otherwise use online tool
if (Get-Command magick -ErrorAction SilentlyContinue) {
    magick convert $pngPath -define icon:auto-resize=256,128,96,64,48,32,16 "C:\Dev\AppTunnel\AppTunnel\app.ico"
    Write-Host "ICO created successfully!"
} else {
    Write-Host "ImageMagick not found. Creating basic ICO..."
    # Simple ICO creation without ImageMagick
    $ico = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
    $stream = [System.IO.File]::Create("C:\Dev\AppTunnel\AppTunnel\app.ico")
    $ico.Save($stream)
    $stream.Close()
    Write-Host "Basic ICO created at C:\Dev\AppTunnel\AppTunnel\app.ico"
}
