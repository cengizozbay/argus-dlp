# Argus — GPO BAŞLANGIÇ SCRİPTİ (Computer Startup Script).
# Domain'deki her makinede, açılışta SYSTEM (yönetici) olarak çalışır → sessiz kurar.
# install.ps1 + agent klasörünün olduğu PAYLAŞIM yolunu ve sunucu bilgilerini aşağıda ayarla.
#
# Kurulum: bu dosyayı bir GPO'da
#   Computer Configuration > Policies > Windows Settings > Scripts > Startup > PowerShell Scripts
# olarak ekle. (Detay: docs\GPO-DAGITIM.md)

# ==== AYARLA (3 satır) ====
$Share     = "\\DOMAIN\NETLOGON\argus-agent"      # install.ps1 + agent klasörünün olduğu paylaşım
$ServerUrl = "http://192.168.2.247:5099"          # Argus sunucu adresi (üretimde https önerilir)
$TenantKey = "demo-tenant-key-001"                # firma anahtarı (panelde Firmalar'dan)
# ==========================

$ErrorActionPreference = "SilentlyContinue"
$log = "$env:windir\Temp\argus-deploy.log"
"[{0}] baslatildi" -f (Get-Date) | Out-File $log -Append

# Zaten kuruluysa hiçbir şey yapma (her açılışta tekrar kurma)
if (Get-Service ArgusAgent -ErrorAction SilentlyContinue) {
    "[{0}] zaten kurulu, atlaniyor" -f (Get-Date) | Out-File $log -Append
    exit 0
}

if (-not (Test-Path "$Share\install.ps1")) {
    "[{0}] HATA: {1}\install.ps1 bulunamadi (paylasim/izin?)" -f (Get-Date), $Share | Out-File $log -Append
    exit 1
}

# Kur (install.ps1 = gizli servis + tamper, self-contained)
& powershell.exe -ExecutionPolicy Bypass -NonInteractive -File "$Share\install.ps1" `
    -ServerUrl $ServerUrl -TenantKey $TenantKey *>> $log

"[{0}] kurulum tamam" -f (Get-Date) | Out-File $log -Append
