# Argus agent'ı bu makineden kaldırır (görevi ve süreci durdurur).
# Kullanım:  powershell -ExecutionPolicy Bypass -File uninstall-agent.ps1
$taskName = "ArgusAgent"

try { Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue } catch {}
try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue } catch {}
Get-Process Argus.Agent -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Argus agent kaldırıldı (görev ve süreç durduruldu)." -ForegroundColor Green
Write-Host "Config ve veriler duruyor: $env:LOCALAPPDATA\Argus  (silmek isterseniz elle kaldırın)" -ForegroundColor DarkGray
