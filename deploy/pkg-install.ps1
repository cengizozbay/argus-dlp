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

# Config → makine-geneli ProgramData (gözcü servis + kullanıcı-oturumu ajanı ORTAK okur).
$cfgDir = "$env:ProgramData\Argus"
New-Item -ItemType Directory -Force $cfgDir | Out-Null
$cfg = @{ ServerUrl = $ServerUrl; TenantKey = $TenantKey; SendSeconds = 15; FileMonitor = "auto";
          AllowInsecureTls = [bool]$AllowInsecureTls }
# WatchFolders verilmezse config'e KOYMA → ajan kullanıcı oturumunda gerçek kullanıcının Masaüstü/Belgeler'ini seçer.
if ($WatchFolders) { $cfg.WatchFolders = $WatchFolders }
$cfg | ConvertTo-Json | Set-Content "$cfgDir\config.json" -Encoding utf8

# Kaldırma sinyali klasörü: kullanıcı-oturumu ajanı (yetkisiz) buraya "uninstall" işareti bırakır,
# SYSTEM servisi görüp gerçek kaldırmayı yapar. Yalnız BU alt klasör Users'a yazılabilir; config admin-only.
$sig = "$cfgDir\signal"
New-Item -ItemType Directory -Force $sig | Out-Null
icacls $sig /grant "*S-1-5-11:(OI)(CI)M" *> $null   # S-1-5-11 = Authenticated Users → Modify

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

# ===== TARAYICI EKLENTİSİ — gerçek URL + gizli mod (otomatik) =====
# Sabit uzantı ID'si (CRX imza anahtarından türer; build-extension.ps1 ile aynı).
$extId = "nchclcmnnndflmefplbjbggdoekihkin"
$updateUrl = "$($ServerUrl.TrimEnd('/'))/ext/update.xml"

# 1) webhost köprüsü (native messaging host) — makine geneli (HKLM), Chrome + Edge.
$whSrc = "$PSScriptRoot\webhost"
if (Test-Path "$whSrc\Argus.WebHost.exe") {
    $whDir = "$installDir\webhost"
    New-Item -ItemType Directory -Force $whDir | Out-Null
    Copy-Item "$whSrc\*" $whDir -Recurse -Force
    $hostName = "com.argus.webhost"
    $manifest = "$whDir\$hostName.json"
    @{ name = $hostName; description = "Argus Web Host"; path = "$whDir\Argus.WebHost.exe"; type = "stdio";
       allowed_origins = @("chrome-extension://$extId/") } | ConvertTo-Json | Set-Content $manifest -Encoding utf8
    foreach ($base in @("HKLM:\Software\Google\Chrome\NativeMessagingHosts",
                        "HKLM:\Software\Microsoft\Edge\NativeMessagingHosts")) {
        New-Item -Path "$base\$hostName" -Force | Out-Null
        Set-ItemProperty -Path "$base\$hostName" -Name "(default)" -Value $manifest
    }
    Write-Host "  Tarayıcı köprüsü kuruldu (native host)." -ForegroundColor DarkGray
}

# 2) Eklentiyi ZORUNLU kur (HKLM policy) — domain makinede Chrome/Edge sunucudan otomatik kurar.
foreach ($base in @("HKLM:\Software\Policies\Google\Chrome", "HKLM:\Software\Policies\Microsoft\Edge")) {
    $key = "$base\ExtensionInstallForcelist"
    New-Item -Path $key -Force | Out-Null
    $props = Get-Item $key
    $already = $false
    foreach ($p in $props.Property) { if ((Get-ItemProperty -Path $key -Name $p).$p -like "$extId;*") { $already = $true } }
    if (-not $already) {
        $idx = 1; while ($props.Property -contains "$idx") { $idx++ }
        Set-ItemProperty -Path $key -Name "$idx" -Value "$extId;$updateUrl"
    }
}
Write-Host "  Eklenti force-install policy yazıldı (Chrome+Edge). Tarayıcı kapat-aç → otomatik kurulur." -ForegroundColor DarkGray
Write-Host "Kurulum tamam. Gerçek URL için kullanıcı tarayıcısını bir kez kapatıp açmalı." -ForegroundColor Green
