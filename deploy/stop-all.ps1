# Argus sunucu + agent süreçlerini durdurur (izlemeyi kapatır).
Get-Process Argus.Server, Argus.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "Argus durduruldu." -ForegroundColor Green
Start-Sleep -Seconds 1
