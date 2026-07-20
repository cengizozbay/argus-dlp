# Argus Agent — İNDİRME PAKETİ kurulum scripti (self-contained; hedefte .NET GEREKMEZ).
# Gerçek GİZLİ WINDOWS SERVICE olarak kurar (LocalSystem) + tamper koruması.
# Bu dosya argus-agent.zip içindedir; "agent" klasörü (self-contained exe) yanında olmalı.
#
# Kullanım (YÖNETİCİ PowerShell):
#   powershell -ExecutionPolicy Bypass -File install.ps1 -ServerUrl "http://SUNUCU:5099" -TenantKey "tk_..."
#
# NOT (KVKK): Personel izleme, çalışanlara aydınlatma/politika bildirimi gerektirir.

param(
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$TenantKey,
    [string[]]$WatchFolders,
    [switch]$AllowInsecureTls
)
$ErrorActionPreference = "Stop"
$svc = "ArgusAgent"
$src = "$PSScriptRoot\agent"       # paketin kendi self-contained agent klasörü

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $admin) { Write-Host "YÖNETİCİ PowerShell'de çalıştırın." -ForegroundColor Red; exit 1 }
if (-not (Test-Path "$src\Argus.Agent.exe")) { Write-Host "HATA: $src\Argus.Agent.exe yok (paketi çıkardığın klasörde çalıştır)." -ForegroundColor Red; exit 1 }

# Eski servis/görev/süreç temizle
sc.exe stop $svc *> $null
sc.exe delete $svc *> $null
try { Unregister-ScheduledTask -TaskName $svc -Confirm:$false -ErrorAction SilentlyContinue } catch {}
Get-Process Argus.Agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1200

# Program Files'a kur (kullanıcı yazamaz/silemez)
$installDir = "$env:ProgramFiles\Argus\Agent"
New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item "$src\*" $installDir -Recurse -Force
$exe = "$installDir\Argus.Agent.exe"

# Config → LocalSystem profili
$sysData = "$env:windir\System32\config\systemprofile\AppData\Local\Argus"
New-Item -ItemType Directory -Force $sysData | Out-Null
if (-not $WatchFolders) { $WatchFolders = @("$env:SystemDrive\Users") }
@{ ServerUrl = $ServerUrl; TenantKey = $TenantKey; SendSeconds = 15; FileMonitor = "auto";
   WatchFolders = $WatchFolders; AllowInsecureTls = [bool]$AllowInsecureTls } |
    ConvertTo-Json | Set-Content "$sysData\config.json" -Encoding utf8

# Servis: LocalSystem, otomatik, "--service"
New-Service -Name $svc -BinaryPathName "`"$exe`" --service" -DisplayName "Argus Endpoint Agent" `
    -StartupType Automatic -Description "Argus endpoint activity monitoring" | Out-Null

# TAMPER: çökünce yeniden başlat + standart kullanıcı durduramaz/silemez
sc.exe failure $svc reset= 86400 actions= restart/1000/restart/1000/restart/1000 *> $null
sc.exe failureflag $svc 1 *> $null
sc.exe sdset $svc "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)" *> $null

Start-Service $svc
Write-Host "Argus agent SERVICE kuruldu ve başladı (self-contained, gizli, tamper korumalı)." -ForegroundColor Green
Write-Host "  Sunucu : $ServerUrl"
Write-Host "Tarayıcı eklentisi (gerçek URL/gizli mod) için: install-webhost.ps1 (README)."
