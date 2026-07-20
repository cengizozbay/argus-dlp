# Argus sunucusunu HTTP + HTTPS birlikte başlatır (isteyen HTTP, isteyen HTTPS bağlanır).
# Sertifika yoksa self-signed üretir (TEST). Üretimde gerçek sertifika verin.
#
# Kullanım:  powershell -ExecutionPolicy Bypass -File start-server-https.ps1 [-HttpPort 5099] [-HttpsPort 5443]

param(
    [int]$HttpPort = 5099,
    [int]$HttpsPort = 5443,
    [string]$CertPath,
    [string]$CertPass = "argus"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$srvExe = "$root\backend\bin\Release\net8.0\Argus.Server.exe"
if (-not $CertPath) { $CertPath = "$root\argus-cert.pfx" }

if (-not (Test-Path $srvExe)) {
    Write-Host "Sunucu derleniyor..." -ForegroundColor Cyan
    dotnet build "$root\backend\Argus.Server.csproj" -c Release -v quiet | Out-Null
}
if (-not (Test-Path $CertPath)) {
    Write-Host "Sertifika yok, self-signed üretiliyor (TEST)..." -ForegroundColor Yellow
    & "$PSScriptRoot\make-cert.ps1" -Password $CertPass -OutFile $CertPath | Out-Null
}

# Firewall (admin gerekebilir)
foreach ($p in @($HttpPort, $HttpsPort)) {
    try {
        if (-not (Get-NetFirewallRule -DisplayName "Argus $p" -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName "Argus $p" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $p -Profile Any -ErrorAction Stop | Out-Null
        }
    } catch { Write-Host "! Firewall portu açılamadı ($p) — gerekirse elle açın." -ForegroundColor Yellow }
}

$ips = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    Select-Object -ExpandProperty IPAddress)

Write-Host ""
Write-Host "  ARGUS SUNUCUSU  ·  HTTP:$HttpPort  +  HTTPS:$HttpsPort" -ForegroundColor Green
Write-Host "  Panel (HTTPS) : https://localhost:$HttpsPort" -ForegroundColor Cyan
foreach ($ip in $ips) { Write-Host "     ağdan      : https://${ip}:$HttpsPort" }
Write-Host "  Panel (HTTP)  : http://localhost:$HttpPort" -ForegroundColor DarkGray
Write-Host "  Self-signed sertifikada tarayıcı 'güvenli değil' uyarısı verir (Gelişmiş > Devam)." -ForegroundColor DarkGray
Write-Host ""

$env:ARGUS_URLS = "http://0.0.0.0:$HttpPort;https://0.0.0.0:$HttpsPort"
$env:ARGUS_CERT_PATH = $CertPath
$env:ARGUS_CERT_PASS = $CertPass
& $srvExe
