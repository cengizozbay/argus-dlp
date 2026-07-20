# Argus tarayıcı köprüsünü (native messaging host) kurar — Chrome + Edge.
# Uzantı önce yüklenmeli; ID'sini chrome://extensions (Geliştirici modu) sayfasından al.
#
# Kullanım:
#   powershell -ExecutionPolicy Bypass -File install-webhost.ps1 -ExtensionId "abcdef....(32 harf)"
#
# Gizli mod için: chrome://extensions > Argus Web Monitor > "Gizli modda izin ver" açık olmalı
# (kurumsalda GPO ile force-install + incognito policy).

param([Parameter(Mandatory = $true)][string]$ExtensionId)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$src = "$root\webhost\bin\Release\net8.0"

if (-not (Test-Path "$src\Argus.WebHost.exe")) {
    Write-Host "Köprü derleniyor..." -ForegroundColor Cyan
    dotnet build "$root\webhost\Argus.WebHost.csproj" -c Release -v quiet | Out-Null
}

$argusHome = "$env:LOCALAPPDATA\Argus"
$bin = "$argusHome\webhost"
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item "$src\*" $bin -Recurse -Force

$hostName = "com.argus.webhost"
$exe = "$bin\Argus.WebHost.exe"
$manifest = "$bin\$hostName.json"

@{
    name            = $hostName
    description     = "Argus Web Host"
    path            = $exe
    type            = "stdio"
    allowed_origins = @("chrome-extension://$ExtensionId/")
} | ConvertTo-Json | Set-Content $manifest -Encoding utf8

foreach ($base in @("HKCU:\Software\Google\Chrome\NativeMessagingHosts",
                    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts")) {
    $key = "$base\$hostName"
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name "(default)" -Value $manifest
}

Write-Host "Argus tarayıcı köprüsü kuruldu." -ForegroundColor Green
Write-Host "  Köprü   : $exe"
Write-Host "  Manifest: $manifest"
Write-Host "  Extension ID: $ExtensionId"
Write-Host "Tarayıcıyı yeniden başlat; uzantı köprüye bağlanacak."
