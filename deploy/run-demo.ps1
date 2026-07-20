# Argus DEMO — sunucu + agent'ı bu makinede çalıştırır ve paneli açar.
# Kullanım:  powershell -ExecutionPolicy Bypass -File run-demo.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

Write-Host "Argus derleniyor..." -ForegroundColor Cyan
dotnet build "$root\backend\Argus.Server.csproj" -c Release -v quiet | Out-Null
dotnet build "$root\agent\Argus.Agent.csproj"   -c Release -v quiet | Out-Null

$srvExe = "$root\backend\bin\Release\net8.0\Argus.Server.exe"
$agExe  = "$root\agent\bin\Release\net8.0-windows\Argus.Agent.exe"

# Agent'ı yerel sunucuya bağla (Masaüstü + Belgeler izlenir)
$agData = "$env:LOCALAPPDATA\Argus"
New-Item -ItemType Directory -Force $agData | Out-Null
@{ ServerUrl = "http://localhost:5099"; TenantKey = "demo-tenant-key-001"; SendSeconds = 10 } |
    ConvertTo-Json | Set-Content "$agData\config.json" -Encoding utf8

Write-Host "Sunucu başlatılıyor..." -ForegroundColor Cyan
Start-Process $srvExe
Start-Sleep -Seconds 3
Write-Host "Agent başlatılıyor..." -ForegroundColor Cyan
Start-Process $agExe

Start-Process "http://localhost:5099"
Write-Host ""
Write-Host "  Panel:      http://localhost:5099" -ForegroundColor Green
Write-Host "  Tenant Key: demo-tenant-key-001" -ForegroundColor Green
Write-Host ""
Write-Host "Durdurmak için:  Get-Process Argus.Server,Argus.Agent | Stop-Process" -ForegroundColor DarkGray
