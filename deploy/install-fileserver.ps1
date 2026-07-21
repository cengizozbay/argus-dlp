# Argus FİLESERVER agent — paylaşımda KİM ne sildi/değiştirdi (GERÇEK kullanıcı adıyla).
# Fileserver'ın KENDİSİNDE, YÖNETİCİ PowerShell'de çalıştır. Windows Güvenlik günlüğünü (Event 4663) okur.
# Denetimi (audit policy + SACL) otomatik açar.
#
# Kullanım:
#   install-fileserver.ps1 -ServerUrl "http://192.168.2.247:5099" -TenantKey "demo-tenant-key-001" `
#       -AuditFolders "D:\Paylasim\Arhan","E:\Ortak"
#
# -AuditFolders: denetlenecek paylaşım klasörleri (fiziksel yerel yol, UNC değil).

param(
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$TenantKey,
    [Parameter(Mandatory = $true)][string[]]$AuditFolders,
    [switch]$AllowInsecureTls
)
$ErrorActionPreference = "Stop"
$svc = "ArgusAgent"
$src = "$PSScriptRoot\agent"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $admin) { Write-Host "YÖNETİCİ PowerShell'de çalıştırın." -ForegroundColor Red; exit 1 }
if (-not (Test-Path "$src\Argus.Agent.exe")) { Write-Host "HATA: $src\Argus.Agent.exe yok." -ForegroundColor Red; exit 1 }

# Eski servis/süreç temizle
sc.exe stop $svc *> $null; sc.exe delete $svc *> $null
Get-Process Argus.Agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1200

# Program Files'a kur
$installDir = "$env:ProgramFiles\Argus\Agent"
New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item "$src\*" $installDir -Recurse -Force
$exe = "$installDir\Argus.Agent.exe"

# Config → ProgramData
$cfgDir = "$env:ProgramData\Argus"
New-Item -ItemType Directory -Force $cfgDir | Out-Null
@{ ServerUrl = $ServerUrl; TenantKey = $TenantKey; SendSeconds = 15; AllowInsecureTls = [bool]$AllowInsecureTls } |
    ConvertTo-Json | Set-Content "$cfgDir\config.json" -Encoding utf8

# ---- Windows DENETİMİ aç ----
# 1) Denetim politikası: "File System" alt-kategorisi = Başarı.
#    DİL BAĞIMSIZ olması için GUID kullan (Türkçe Windows'ta ad "Dosya Sistemi" → İngilizce ad "Parametre hatalı" verir).
$fsGuid = "{0CCE921D-69AE-11D9-BED3-505054503030}"   # Audit File System alt-kategori GUID'i (sabit)
auditpol /set /subcategory:"$fsGuid" /success:enable | Out-Null
if ($LASTEXITCODE -eq 0) { Write-Host "Denetim politikası açıldı (File System > Başarı)." -ForegroundColor DarkGray }
else { Write-Host "UYARI: auditpol başarısız (kod $LASTEXITCODE) — denetim politikasını elle aç." -ForegroundColor Yellow }

# 2) SACL: her paylaşım klasörüne "Everyone → Sil/Yaz" denetim kuralı
foreach ($f in $AuditFolders) {
    if (-not (Test-Path $f)) { Write-Host "  UYARI: klasör yok, atlandı: $f" -ForegroundColor Yellow; continue }
    try {
        $acl = Get-Acl -Path $f -Audit
        $rule = New-Object System.Security.AccessControl.FileSystemAuditRule(
            "Everyone", "Delete,DeleteSubdirectoriesAndFiles,WriteData,AppendData",
            "ContainerInherit,ObjectInherit", "None", "Success")
        $acl.AddAuditRule($rule)
        Set-Acl -Path $f -AclObject $acl
        Write-Host "  Denetim eklendi: $f" -ForegroundColor DarkGray
    } catch { Write-Host "  UYARI: SACL eklenemedi ($f): $($_.Exception.Message)" -ForegroundColor Yellow }
}

# ---- Servis: fileserver modu (SYSTEM, doğrudan denetim) ----
New-Service -Name $svc -BinaryPathName "`"$exe`" --service --fileserver" -DisplayName "Argus Endpoint Agent" `
    -StartupType Automatic -Description "Argus file server audit" | Out-Null
sc.exe failure $svc reset= 86400 actions= restart/1000/restart/1000/restart/1000 *> $null
sc.exe failureflag $svc 1 *> $null
sc.exe sdset $svc "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)" *> $null
Start-Service $svc

Write-Host "Argus FİLESERVER agent kuruldu ve başladı." -ForegroundColor Green
Write-Host "  Sunucu    : $ServerUrl"
Write-Host "  Denetlenen: $($AuditFolders -join ', ')"
Write-Host "Bundan sonra bu klasörlerde kim ne silerse/değiştirirse panele GERÇEK kullanıcı adıyla düşer." -ForegroundColor Green
