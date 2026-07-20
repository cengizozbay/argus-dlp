# Argus sunucusunu AĞ ÜZERİNDEN erişilebilir başlatır (başka makinelerdeki agent'lar bağlanabilsin).
# Yerel test için start-server.ps1 (sadece localhost) yeter; sahada bunu kullan.
#
# Kullanım:  powershell -ExecutionPolicy Bypass -File start-server-lan.ps1 [-Port 5099]
#
# Yaptıkları:
#   1. Sunucuyu tüm ağ arayüzlerine bağlar (0.0.0.0) → LAN'daki agent'lar ulaşır.
#   2. Windows Güvenlik Duvarı'nda portu açar (admin isterse; yoksa uyarır).
#   3. Panel + agent bağlantı adreslerini (bu makinenin IP'si ile) ekrana yazar.

param(
    [int]$Port = 5099
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$srvExe = "$root\backend\bin\Release\net8.0\Argus.Server.exe"

if (-not (Test-Path $srvExe)) {
    Write-Host "Sunucu derleniyor..." -ForegroundColor Cyan
    dotnet build "$root\backend\Argus.Server.csproj" -c Release -v quiet | Out-Null
}

# Güvenlik duvarında portu aç (admin gerektirir; başarısızsa yalnızca uyar).
$ruleName = "Argus Server $Port"
try {
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
            -Protocol TCP -LocalPort $Port -Profile Any -ErrorAction Stop | Out-Null
        Write-Host "Güvenlik duvarı kuralı eklendi (port $Port)." -ForegroundColor DarkGray
    }
} catch {
    Write-Host "! Güvenlik duvarı portu açılamadı (admin gerekir). Gerekirse elle açın:" -ForegroundColor Yellow
    Write-Host "  New-NetFirewallRule -DisplayName '$ruleName' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port" -ForegroundColor DarkGray
}

# Bu makinenin LAN IPv4 adres(ler)i
$ips = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    Select-Object -ExpandProperty IPAddress)

Write-Host ""
Write-Host "  ARGUS SUNUCUSU (ağ modu)  ·  port $Port" -ForegroundColor Green
Write-Host "  ------------------------------------------------------------"
Write-Host "  Panel (tarayıcıdan sen gir) :" -ForegroundColor Cyan
Write-Host "     bu makinede : http://localhost:$Port"
foreach ($ip in $ips) { Write-Host "     ağdan       : http://${ip}:$Port" }
Write-Host ""
Write-Host "  Agent bu adrese bağlanır (install-agent.ps1 -ServerUrl):" -ForegroundColor Cyan
if ($ips.Count -gt 0) { Write-Host "     http://$($ips[0]):$Port" } else { Write-Host "     http://<bu-makinenin-ip>:$Port" }
Write-Host "  Tenant Key : demo-tenant-key-001" -ForegroundColor DarkGray
Write-Host "  ------------------------------------------------------------"
Write-Host ""

$env:ARGUS_URLS = "http://0.0.0.0:$Port"
& $srvExe
