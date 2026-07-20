# Argus HTTPS için self-signed (kendinden imzalı) sertifika üretir — TEST/DEV amaçlı.
# Üretimde gerçek bir sertifika kullanın (Let's Encrypt / kurumsal CA) ya da reverse proxy (Nginx/Caddy).
#
# Kullanım:  powershell -ExecutionPolicy Bypass -File make-cert.ps1 [-Dns localhost] [-Password argus]

param(
    [string]$Dns = "localhost",
    [string]$Password = "argus",
    [string]$OutFile
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutFile) { $OutFile = "$root\argus-cert.pfx" }

$names = @($Dns)
$hn = [System.Net.Dns]::GetHostName()
if ($hn -and $names -notcontains $hn) { $names += $hn }

$cert = New-SelfSignedCertificate -DnsName $names -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears(5) -KeyExportPolicy Exportable -KeyUsage DigitalSignature, KeyEncipherment

$pw = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $OutFile -Password $pw | Out-Null
Remove-Item ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -Force -ErrorAction SilentlyContinue

Write-Host "Sertifika üretildi:" -ForegroundColor Green
Write-Host "  Dosya  : $OutFile"
Write-Host "  Parola : $Password"
Write-Host "  DNS    : $($names -join ', ')"
Write-Host ""
Write-Host "HTTPS başlatmak için:  start-server-https.ps1" -ForegroundColor DarkGray
