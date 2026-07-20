# Argus uzantısını FIREFOX'a zorunlu kur (force-install) + native messaging köprüsü.
# YÖNETİCİ PowerShell gerekir. Sahada aynı policies.json + registry GPO ile dağıtılır.
#
# ⚠ İMZA GERÇEĞİ:
#   - Firefox ESR: bu script imza şartını kapatır (Preferences) → İMZASIZ XPI force-install olur. ÇALIŞIR.
#   - Normal (release) Firefox: imza şartı kapatılamaz → XPI Mozilla/AMO imzalı OLMALI.
#     `web-ext sign` (AMO API anahtarı ile) imzalı XPI üretir; onu barındır, install_url'i ona çevir.
#
# Kullanım:  powershell -ExecutionPolicy Bypass -File install-firefox-policy.ps1 -ServerUrl "http://SUNUCU:5099"

param([Parameter(Mandatory = $true)][string]$ServerUrl)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $admin) { Write-Host "YÖNETİCİ PowerShell'de çalıştırın." -ForegroundColor Red; exit 1 }

$geckoId = "argus@argus.local"
$xpiUrl = "$($ServerUrl.TrimEnd('/'))/ext/argus-ext.xpi"
$hostName = "com.argus.webhost"

# 1) Native messaging köprüsü (Firefox manifesti: allowed_extensions)
$src = "$root\webhost\bin\Release\net8.0"
if (-not (Test-Path "$src\Argus.WebHost.exe")) {
    dotnet build "$root\webhost\Argus.WebHost.csproj" -c Release -v quiet | Out-Null
}
$bin = "$env:ProgramData\Argus\webhost"
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item "$src\*" $bin -Recurse -Force
$exe = "$bin\Argus.WebHost.exe"
$ffManifest = "$bin\$hostName.firefox.json"
@{ name = $hostName; description = "Argus Web Host"; path = $exe; type = "stdio"; allowed_extensions = @($geckoId) } |
    ConvertTo-Json | Set-Content $ffManifest -Encoding utf8
$key = "HKLM:\Software\Mozilla\NativeMessagingHosts\$hostName"
New-Item -Path $key -Force | Out-Null
Set-ItemProperty -Path $key -Name "(default)" -Value $ffManifest

# 2) Firefox policies.json — force-install + (ESR) imza şartını kapat
$policies = @{
    policies = @{
        ExtensionSettings = @{
            "$geckoId" = @{ installation_mode = "force_installed"; install_url = $xpiUrl }
        }
        Preferences = @{
            "xpinstall.signatures.required" = @{ Value = $false; Status = "locked" }
        }
    }
}
$ffDir = $null
foreach ($p in @("$env:ProgramFiles\Mozilla Firefox", "${env:ProgramFiles(x86)}\Mozilla Firefox")) {
    if (Test-Path "$p\firefox.exe") { $ffDir = $p; break }
}
if ($ffDir) {
    $dist = "$ffDir\distribution"
    New-Item -ItemType Directory -Force $dist | Out-Null
    $policies | ConvertTo-Json -Depth 8 | Set-Content "$dist\policies.json" -Encoding utf8
    Write-Host "Firefox policy yazıldı: $dist\policies.json" -ForegroundColor Green
} else {
    Write-Host "Firefox kurulu değil — policies.json atlandı (GPO ile de dağıtılabilir)." -ForegroundColor Yellow
}

Write-Host "Firefox köprü + policy hazır." -ForegroundColor Green
Write-Host "  gecko id : $geckoId"
Write-Host "  XPI      : $xpiUrl"
Write-Host "  Köprü    : $exe (HKLM Mozilla NativeMessagingHosts)"
Write-Host ""
Write-Host "ESR'de: Firefox kapat-aç → uzantı otomatik kurulur (imzasız, imza şartı kapalı)." -ForegroundColor DarkGray
Write-Host "Release Firefox'ta: XPI AMO imzalı olmalı (web-ext sign) — yoksa kurulmaz." -ForegroundColor DarkGray
