# Argus FILESERVER agent - paylasimda KIM ne sildi/degistirdi (gercek kullanici adiyla).
# Fileserver'in KENDISINDE, YONETICI PowerShell'de calistir. Windows Guvenlik gunlugunu (Event 4663/4660) okur.
# ASCII-only (Turkce Windows PowerShell 5.1 kodlama sorunu olmasin diye).
#
# Kullanim:
#   install-fileserver.ps1 -ServerUrl "http://192.168.2.247:5099" -TenantKey "demo-tenant-key-001" -AuditFolders "E:\Arhan"

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
if (-not $admin) { Write-Host "YONETICI PowerShell'de calistirin." -ForegroundColor Red; exit 1 }
if (-not (Test-Path "$src\Argus.Agent.exe")) { Write-Host "HATA: $src\Argus.Agent.exe yok." -ForegroundColor Red; exit 1 }

# Eski servis/surec temizle (servisi SIL -> tamper de gider, exe kilidi acilir)
sc.exe stop $svc *> $null
sc.exe delete $svc *> $null
Get-Process Argus.Agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# Program Files'a kur
$installDir = "$env:ProgramFiles\Argus\Agent"
New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item "$src\*" $installDir -Recurse -Force
$exe = "$installDir\Argus.Agent.exe"

# Config -> ProgramData
$cfgDir = "$env:ProgramData\Argus"
New-Item -ItemType Directory -Force $cfgDir | Out-Null
@{ ServerUrl = $ServerUrl; TenantKey = $TenantKey; SendSeconds = 15; AllowInsecureTls = [bool]$AllowInsecureTls } |
    ConvertTo-Json | Set-Content "$cfgDir\config.json" -Encoding utf8

# ---- Windows DENETIMI ac ----
# 1) Denetim politikasi: "File System" alt-kategorisi = Basari. Dil-bagimsiz olsun diye GUID.
$fsGuid = "{0CCE921D-69AE-11D9-BED3-505054503030}"
auditpol /set /subcategory:"$fsGuid" /success:enable | Out-Null
if ($LASTEXITCODE -eq 0) { Write-Host "Denetim politikasi acildi (File System > Basari)." -ForegroundColor DarkGray }
else { Write-Host "UYARI: auditpol basarisiz (kod $LASTEXITCODE)." -ForegroundColor Yellow }

# 2) SACL: her paylasim klasorune "Everyone -> Sil/Yaz" denetim kurali
foreach ($f in $AuditFolders) {
    if (-not (Test-Path $f)) { Write-Host "  UYARI: klasor yok, atlandi: $f" -ForegroundColor Yellow; continue }
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

# ---- Servis: fileserver modu (SYSTEM, dogrudan denetim) ----
New-Service -Name $svc -BinaryPathName "`"$exe`" --service --fileserver" -DisplayName "Argus Endpoint Agent" `
    -StartupType Automatic -Description "Argus file server audit" | Out-Null
sc.exe failure $svc reset= 86400 actions= restart/1000/restart/1000/restart/1000 *> $null
sc.exe failureflag $svc 1 *> $null
sc.exe sdset $svc "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)" *> $null
Start-Service $svc

Write-Host "Argus FILESERVER agent kuruldu ve basladi." -ForegroundColor Green
Write-Host ("  Sunucu    : {0}" -f $ServerUrl)
Write-Host ("  Denetlenen: {0}" -f ($AuditFolders -join ', '))
Write-Host "Bundan sonra bu klasorlerde kim ne silerse/degistirirse panele GERCEK kullanici adiyla duser." -ForegroundColor Green
