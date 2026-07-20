# Argus agent İNDİRME PAKETİ üretir → backend\wwwroot\download\argus-agent.zip
# Panelde "Agent Paketini İndir" bu dosyayı sunar. Sunucu/agent kodu değişince yeniden çalıştır.
#
# Kullanım:  powershell -ExecutionPolicy Bypass -File build-agent-package.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

$stageRoot = Join-Path $env:TEMP "argus-pkg"
$stage = Join-Path $stageRoot "argus-agent"
Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\agent", "$stage\extension", "$stage\webhost" | Out-Null

# SELF-CONTAINED tek-exe publish → hedef makinede .NET GEREKMEZ (GPO için).
Write-Host "Agent + webhost self-contained yayınlanıyor (runtime gömülü, biraz sürer)..." -ForegroundColor Cyan
$scArgs = @("-c", "Release", "-r", "win-x64", "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-v", "quiet")
dotnet publish "$root\agent\Argus.Agent.csproj"   @scArgs -o "$stage\agent"   | Out-Null
dotnet publish "$root\webhost\Argus.WebHost.csproj" @scArgs -o "$stage\webhost" | Out-Null
Remove-Item "$stage\agent\*.pdb", "$stage\webhost\*.pdb" -Force -ErrorAction SilentlyContinue

Copy-Item "$root\extension\*" "$stage\extension" -Recurse -Force
Copy-Item "$root\deploy\pkg-install.ps1"             "$stage\install.ps1"        -Force
Copy-Item "$root\deploy\install-webhost.ps1"         "$stage\install-webhost.ps1" -Force

@"
ARGUS ENDPOINT AGENT — KURULUM PAKETİ
=====================================

1) Bu ZIP'i hedef Windows makineye kopyalayıp klasöre çıkarın.

2) YÖNETİCİ PowerShell açın, bu klasöre gelin ve çalıştırın:

   powershell -ExecutionPolicy Bypass -File install.ps1 -ServerUrl "SUNUCU_ADRESI" -TenantKey "FIRMA_ANAHTARI"

   (Sunucu adresi ve firma anahtarını Argus panelindeki "Agent Yönetimi" ekranından alın.)

3) Agent gizli bir WINDOWS SERVICE (LocalSystem) olarak kurulur, açılışta otomatik başlar.
   Self-contained: hedef makinede .NET GEREKMEZ. Tamper korumalı: standart kullanıcı
   durduramaz/silemez, öldürülürse geri gelir. Panelin "Agent Yönetimi" ekranından
   uzaktan durdurulur/kaldırılır.

GERÇEK URL / GİZLİ MOD (tarayıcı eklentisi) — opsiyonel:
   - Chrome/Edge'de chrome://extensions > Geliştirici modu > "Paketlenmemiş öğe yükle" > extension klasörü.
   - Uzantı ID'sini alın, sonra: install-webhost.ps1 -ExtensionId "ID"
   - Üretim/toplu dağıtımda eklenti Web Store/GPO policy ile zorunlu kurulur.

KVKK: Personel izleme, çalışanlara aydınlatma metni + politika onayı gerektirir.
"@ | Set-Content "$stage\README.txt" -Encoding utf8

$out = "$root\backend\wwwroot\download"
New-Item -ItemType Directory -Force $out | Out-Null
$zip = Join-Path $out "argus-agent.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $stage -DestinationPath $zip -Force

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Paket hazır: $zip  ($mb MB)" -ForegroundColor Green
Write-Host "Panelde indirilebilir: /download/argus-agent.zip" -ForegroundColor DarkGray
