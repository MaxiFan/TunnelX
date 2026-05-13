# TunnelX - Run as Administrator
# WinDivert requires Administrator privileges to operate at kernel level.

$releaseRoot = Join-Path $PSScriptRoot "bin\Release"

$exePath = Get-ChildItem -Path $releaseRoot -Filter "TunnelX.exe" -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if ($exePath) {
    Start-Process -FilePath $exePath -Verb RunAs
    Write-Host "TunnelX launched with Administrator privileges" -ForegroundColor Green
    Write-Host "Executable: $exePath" -ForegroundColor DarkGray
}
else {
    Write-Host "Error: TunnelX.exe not found. Please build the project first." -ForegroundColor Red
    Write-Host "Searched under: $releaseRoot" -ForegroundColor Yellow
}
