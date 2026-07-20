# Argus tarayıcı uzantısını Chrome + Edge'e ZORUNLU KUR (force-install) — kendi sunucumuzdan barındırarak.
# Tek makinede test için: YÖNETİCİ PowerShell'de çalıştır. Sahada: aynı registry anahtarlarını GPO ile dağıt.
# Önce: build-extension.ps1 çalıştırılmış + sunucu (yeniden derlenip) çalışıyor olmalı.
#
# Kullanım:  powershell -ExecutionPolicy Bypass -File install-extension-policy.ps1 -ServerUrl "http://SUNUCU:5099"

param([Parameter(Mandatory = $true)][string]$ServerUrl)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $admin) { Write-Host "YÖNETİCİ PowerShell'de çalıştırın (HKLM policy)." -ForegroundColor Red; exit 1 }

$idFile = "$root\backend\wwwroot\ext\extid.txt"
if (-not (Test-Path $idFile)) { Write-Host "Önce build-extension.ps1 çalıştırın (extid.txt yok)." -ForegroundColor Red; exit 1 }
$id = (Get-Content $idFile -Raw).Trim()
$updateUrl = "$($ServerUrl.TrimEnd('/'))/ext/update.xml"
$val = "$id;$updateUrl"

foreach ($base in @("HKLM:\Software\Policies\Google\Chrome", "HKLM:\Software\Policies\Microsoft\Edge")) {
    $key = "$base\ExtensionInstallForcelist"
    New-Item -Path $key -Force | Out-Null
    # Sıradaki boş index'i bul (varolanları ezme)
    $existing = (Get-Item $key).Property
    $idx = 1; while ($existing -contains "$idx") { $idx++ }
    Set-ItemProperty -Path $key -Name "$idx" -Value $val
}

Write-Host "Uzantı force-install policy'si yazıldı." -ForegroundColor Green
Write-Host "  Extension ID : $id"
Write-Host "  Update URL   : $updateUrl"
Write-Host ""
Write-Host "Chrome/Edge'i kapat-aç → uzantı otomatik kurulur (kullanıcı kaldıramaz)."
Write-Host "GPO ile sahada: Bilgisayar Yapılandırması > Yönetim Şablonları > Google Chrome >"
Write-Host "  'Belirtilen uzantıların otomatik yüklenmesini yapılandır' = $val" -ForegroundColor DarkGray
Write-Host "(Edge için aynısı Microsoft Edge şablonunda.)" -ForegroundColor DarkGray
