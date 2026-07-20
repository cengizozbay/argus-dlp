# Argus agent'ı bu makineye SESSİZ (arka planda, oturum açılışta otomatik) kurar.
# Agent'ı sabit bir klasöre kopyalar, config yazar ve gizli bir Zamanlanmış Görev oluşturur.
#
# Kullanım:
#   powershell -ExecutionPolicy Bypass -File install-agent.ps1 -ServerUrl "http://SUNUCU:5099" -TenantKey "musteri-anahtari"
#
# Kaldırmak için: uninstall-agent.ps1
#
# NOT (KVKK): Personel izleme, çalışanlara aydınlatma/politika bildirimi gerektirir.
# Bu kurulumu yapmadan önce yasal yükümlülüklerin yerine getirildiğinden emin olun.

param(
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$TenantKey,
    [string[]]$WatchFolders
)
$ErrorActionPreference = "Stop"
$taskName = "ArgusAgent"
$root = Split-Path $PSScriptRoot -Parent
$src  = "$root\agent\bin\Release\net8.0-windows"

if (-not (Test-Path "$src\Argus.Agent.exe")) {
    Write-Host "Agent derleniyor..." -ForegroundColor Cyan
    dotnet build "$root\agent\Argus.Agent.csproj" -c Release -v quiet | Out-Null
}

# Sabit kurulum klasörü
$agentHome = "$env:LOCALAPPDATA\Argus"
$bin = "$agentHome\bin"
New-Item -ItemType Directory -Force $bin | Out-Null

# Yeniden kurulum/güncelleme: çalışan eski agent DLL'i kilitler → önce durdur (dosya kilidini bırak).
try { Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue } catch {}
Get-Process Argus.Agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 900

Copy-Item "$src\*" $bin -Recurse -Force

# İzlenecek klasörler (belirtilmezse Masaüstü + Belgeler + ortak paylaşım)
if (-not $WatchFolders) {
    $WatchFolders = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('MyDocuments'))
}
@{ ServerUrl = $ServerUrl; TenantKey = $TenantKey; SendSeconds = 15; WatchFolders = $WatchFolders } |
    ConvertTo-Json | Set-Content "$agentHome\config.json" -Encoding utf8

# Gizli, oturum açılışında çalışan Zamanlanmış Görev
$exe = "$bin\Argus.Agent.exe"
try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue } catch {}
$action   = New-ScheduledTaskAction -Execute $exe
# Oturum açılışta + her 3 dakikada bir tetikle (öldürülürse en geç 3 dk içinde geri gelir).
$trigger  = New-ScheduledTaskTrigger -AtLogOn
$trigger.Repetition = (New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes 3) -RepetitionDuration (New-TimeSpan -Days 3650)).Repetition
# Zaten çalışıyorsa yeni örnek başlatma; durursa/çökerse yeniden başlat (tamper - temel).
$settings = New-ScheduledTaskSettingsSet -Hidden -StartWhenAvailable -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
    -Description "Argus Endpoint Agent" | Out-Null
Start-ScheduledTask -TaskName $taskName

Write-Host "Argus agent kuruldu ve başlatıldı." -ForegroundColor Green
Write-Host "  Sunucu : $ServerUrl"
Write-Host "  İzlenen: $($WatchFolders -join ', ')"
Write-Host "  Görev  : $taskName (gizli, oturum açılışta otomatik)"
