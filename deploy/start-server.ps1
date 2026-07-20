# Argus sunucusunu başlatır (gerekirse derler).
# Kullanım:  powershell -ExecutionPolicy Bypass -File start-server.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$srvExe = "$root\backend\bin\Release\net8.0\Argus.Server.exe"

if (-not (Test-Path $srvExe)) {
    Write-Host "Sunucu derleniyor..." -ForegroundColor Cyan
    dotnet build "$root\backend\Argus.Server.csproj" -c Release -v quiet | Out-Null
}

Write-Host "Argus sunucusu başlıyor -> http://localhost:5099" -ForegroundColor Green
Write-Host "Panel için tarayıcıda aç: http://localhost:5099   (Tenant Key: demo-tenant-key-001)" -ForegroundColor DarkGray
& $srvExe
