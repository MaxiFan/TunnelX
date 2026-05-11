# TunnelX - Run as Administrator
# WinDivert requires Administrator privileges to operate at kernel level

$exePath = "$PSScriptRoot\bin\Release\net8.0-windows\TunnelX.exe"

if (Test-Path $exePath) {
    Start-Process -FilePath $exePath -Verb RunAs
    Write-Host "TunnelX launched with Administrator privileges" -ForegroundColor Green
} else {
    Write-Host "Error: TunnelX.exe not found. Please build the project first." -ForegroundColor Red
    Write-Host "Expected path: $exePath" -ForegroundColor Yellow
}
