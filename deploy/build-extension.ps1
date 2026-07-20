# Argus tarayıcı uzantısını CRX olarak paketler (Chrome ile), SABİT extension ID hesaplar,
# CRX'i sunucunun barındıracağı klasöre koyar. Sonra install-extension-policy.ps1 ile GPO/force-install.
# Web Store'a GEREK YOK — kendi sunucumuz barındırır (kurumsal self-hosted senaryo).
#
# Kullanım:  powershell -ExecutionPolicy Bypass -File build-extension.ps1

param([string]$Chrome)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$extDir = "$root\extension"
$pem = "$PSScriptRoot\argus-ext.pem"        # SABİT imzalama anahtarı (ID bunun için stabil kalır — SAKLA, git'e koyma)
$outDir = "$root\backend\wwwroot\ext"
New-Item -ItemType Directory -Force $outDir | Out-Null

if (-not $Chrome) {
    foreach ($c in @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
                     "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe")) {
        if (Test-Path $c) { $Chrome = $c; break }
    }
}
if (-not $Chrome -or -not (Test-Path $Chrome)) { Write-Host "chrome.exe bulunamadı. -Chrome ile yol verin." -ForegroundColor Red; exit 1 }

# Eski çıktıları temizle
Remove-Item "$root\extension.crx" -Force -ErrorAction SilentlyContinue

# Paketle (anahtar varsa yeniden kullan → ID stabil)
if (Test-Path $pem) {
    & $Chrome "--pack-extension=$extDir" "--pack-extension-key=$pem" --no-message-box | Out-Null
} else {
    & $Chrome "--pack-extension=$extDir" --no-message-box | Out-Null
    Start-Sleep -Seconds 2
    if (Test-Path "$root\extension.pem") { Move-Item "$root\extension.pem" $pem -Force }
}

# CRX oluşana kadar bekle (Chrome async)
$crx = "$root\extension.crx"
for ($i = 0; $i -lt 15 -and -not (Test-Path $crx); $i++) { Start-Sleep -Milliseconds 500 }
if (-not (Test-Path $crx)) { Write-Host "CRX üretilemedi (Chrome paketleme başarısız)." -ForegroundColor Red; exit 1 }
Copy-Item $crx "$outDir\argus-ext.crx" -Force

# Extension ID = CRX3 header'ındaki crx_id (Chrome hesaplar) → a-p eşlemesi. (PS 5.1 uyumlu, kriptosuz.)
$bytes = [System.IO.File]::ReadAllBytes("$outDir\argus-ext.crx")
$hsize = [BitConverter]::ToUInt32($bytes, 8)
$header = $bytes[12..(12 + $hsize - 1)]
$id = $null
for ($i = 0; $i -lt $header.Length - 3; $i++) {
    # SignedData alanı (field 10000) tag = 0x82 0xF1 0x04
    if ($header[$i] -eq 0x82 -and $header[$i + 1] -eq 0xF1 -and $header[$i + 2] -eq 0x04) {
        $j = $i + 3
        $shift = 0; $null = 0                       # SignedData uzunluğu (varint) — atla
        while ($true) { $b = $header[$j]; $j++; if (($b -band 0x80) -eq 0) { break }; $shift += 7 }
        if ($header[$j] -eq 0x0A) {                  # SignedData.crx_id: field 1, len-delim
            $j++; $clen = $header[$j]; $j++
            $crxid = $header[$j..($j + $clen - 1)]
            $sb = New-Object System.Text.StringBuilder
            foreach ($b in $crxid) { [void]$sb.Append([char](97 + ($b -shr 4))); [void]$sb.Append([char](97 + ($b -band 15))) }
            $id = $sb.ToString()
            break
        }
    }
}
if (-not $id) { Write-Host "Extension ID CRX'ten okunamadı." -ForegroundColor Red; exit 1 }
$ver = (Get-Content "$extDir\manifest.json" -Raw | ConvertFrom-Json).version

Set-Content "$outDir\extid.txt"  $id  -NoNewline -Encoding ascii
Set-Content "$outDir\extver.txt" $ver -NoNewline -Encoding ascii

# --- Firefox XPI (imzasız) — ESR'de force-install; normal Firefox'ta AMO imzası gerekir ---
$ffSrc = "$root\extension-firefox"
if (Test-Path "$ffSrc\manifest.json") {
    $ffStage = Join-Path $env:TEMP "argus-ff"
    Remove-Item $ffStage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $ffStage | Out-Null
    Copy-Item "$ffSrc\manifest.json" $ffStage -Force
    Copy-Item "$extDir\background.js"  $ffStage -Force       # background.js tek kaynak (Chrome ile aynı)
    $xpiTmp = "$outDir\argus-ext.zip"
    Remove-Item $xpiTmp, "$outDir\argus-ext.xpi" -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path "$ffStage\*" -DestinationPath $xpiTmp -Force
    Move-Item $xpiTmp "$outDir\argus-ext.xpi" -Force
}

Write-Host "Uzantı paketlendi (self-hosted)." -ForegroundColor Green
Write-Host "  Extension ID : $id  (Chrome/Edge)"
Write-Host "  Firefox XPI  : $outDir\argus-ext.xpi  (gecko id: argus@argus.local)"
Write-Host "  Sürüm        : $ver"
Write-Host "  CRX          : $outDir\argus-ext.crx"
Write-Host "  Update XML   : sunucudan /ext/update.xml"
Write-Host ""
Write-Host "Sonraki: sunucuyu (yeniden) derleyip başlat; sonra force-install:"
Write-Host "  install-extension-policy.ps1 -ServerUrl `"http://SUNUCU:5099`"" -ForegroundColor DarkGray
Write-Host "Webhost köprüsü için de bu ID: install-webhost.ps1 -ExtensionId $id" -ForegroundColor DarkGray
