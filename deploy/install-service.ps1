# Argus agent'ı GERÇEK GİZLİ WINDOWS SERVICE olarak kurar (LocalSystem) + tamper koruması.
# YÖNETİCİ PowerShell gerekir. Zamanlanmış Görev sürümünün (install-agent.ps1) production karşılığı.
#
# Kullanım:
#   powershell -ExecutionPolicy Bypass -File install-service.ps1 -ServerUrl "https://SUNUCU:5443" -TenantKey "tk_..."
#
# Tamper: servis LocalSystem çalışır; çökerse/durdurulursa otomatik yeniden başlar;
#   standart (yönetici olmayan) kullanıcı servisi DURDURAMAZ/SİLEMEZ (SDDL ile).
# Kaldırma: uninstall-service.ps1

param(
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$TenantKey,
    [string[]]$WatchFolders,
    [switch]$AllowInsecureTls
)
$ErrorActionPreference = "Stop"
$svc = "ArgusAgent"
$root = Split-Path $PSScriptRoot -Parent
$src = "$root\agent\bin\Release\net8.0-windows"

# Yönetici kontrolü
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $admin) { Write-Host "Bu script'i YÖNETİCİ PowerShell'de çalıştırın." -ForegroundColor Red; exit 1 }

if (-not (Test-Path "$src\Argus.Agent.exe")) {
    Write-Host "Agent derleniyor..." -ForegroundColor Cyan
    dotnet build "$root\agent\Argus.Agent.csproj" -c Release -v quiet | Out-Null
}

# Eski servis/görev varsa temizle
sc.exe stop $svc *> $null
sc.exe delete $svc *> $null
try { Unregister-ScheduledTask -TaskName $svc -Confirm:$false -ErrorAction SilentlyContinue } catch {}
Get-Process Argus.Agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1200

# Program Files'a kur (varsayılan ACL: kullanıcılar yazamaz/silemez → exe tamper-korumalı)
$installDir = "$env:ProgramFiles\Argus\Agent"
New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item "$src\*" $installDir -Recurse -Force
$exe = "$installDir\Argus.Agent.exe"

# Config → LocalSystem profilinin AppData\Local\Argus (agent oradan okur)
$sysData = "$env:windir\System32\config\systemprofile\AppData\Local\Argus"
New-Item -ItemType Directory -Force $sysData | Out-Null
if (-not $WatchFolders) { $WatchFolders = @("$env:SystemDrive\Users") }   # servis tüm kullanıcıları izler
@{ ServerUrl = $ServerUrl; TenantKey = $TenantKey; SendSeconds = 15; FileMonitor = "auto";
   WatchFolders = $WatchFolders; AllowInsecureTls = [bool]$AllowInsecureTls } |
    ConvertTo-Json | Set-Content "$sysData\config.json" -Encoding utf8

# Servis oluştur: LocalSystem, otomatik başlat, "--service" argümanı
New-Service -Name $svc -BinaryPathName "`"$exe`" --service" -DisplayName "Argus Endpoint Agent" `
    -StartupType Automatic -Description "Argus endpoint activity monitoring" | Out-Null

# TAMPER 1: çökerse/durursa yeniden başlat (1 sn arayla, sayaç günlük sıfırlanır)
sc.exe failure $svc reset= 86400 actions= restart/1000/restart/1000/restart/1000 *> $null
sc.exe failureflag $svc 1 *> $null

# TAMPER 2: SDDL — System+Admin tam yetki; standart/interaktif kullanıcı yalnız SORGU (durduramaz/silemez)
$sddl = "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)"
sc.exe sdset $svc $sddl *> $null

Start-Service $svc
Write-Host "Argus SERVICE kuruldu ve başladı." -ForegroundColor Green
Write-Host "  Servis : $svc (LocalSystem, otomatik, gizli)"
Write-Host "  Kurulum: $installDir"
Write-Host "  Sunucu : $ServerUrl"
Write-Host "  Tamper : çökünce yeniden başlar · standart kullanıcı durduramaz/silemez"
