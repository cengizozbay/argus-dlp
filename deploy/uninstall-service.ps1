# Argus SERVICE'i kaldırır. YÖNETİCİ PowerShell gerekir.
# Kullanım:  powershell -ExecutionPolicy Bypass -File uninstall-service.ps1

$ErrorActionPreference = "SilentlyContinue"
$svc = "ArgusAgent"
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $admin) { Write-Host "Bu script'i YÖNETİCİ PowerShell'de çalıştırın." -ForegroundColor Red; exit 1 }

sc.exe stop $svc *> $null
Start-Sleep -Milliseconds 800
sc.exe delete $svc *> $null
Get-Process Argus.Agent -ErrorAction SilentlyContinue | Stop-Process -Force

Remove-Item "$env:ProgramFiles\Argus\Agent" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$env:windir\System32\config\systemprofile\AppData\Local\Argus" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Argus servisi kaldırıldı." -ForegroundColor Green
