<#
    Genera un certificado HTTPS autofirmado válido para "localhost" y la IP actual de tu red
    local, y lo deja en Data/dev-cert.pfx (+ Data/dev-cert.cer para instalar en el móvil).

    Ejecútalo de nuevo si tu IP local cambia (por ejemplo, al reconectarte al Wi-Fi) y la app
    deja de abrir en el móvil.

    Uso: desde esta carpeta (SmartAgenda), en PowerShell:
        .\scripts\generar-certificado-lan.ps1
#>

$ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object {
    $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '127.0.0.1'
} | Select-Object -First 1 -ExpandProperty IPAddress)

if (-not $ip) {
    Write-Error "No se encontró ninguna IP de red local. ¿Estás conectado a una red?"
    exit 1
}

Write-Output "IP local detectada: $ip"

$certDir = Join-Path $PSScriptRoot "..\Data"
New-Item -ItemType Directory -Force -Path $certDir | Out-Null

$existente = Get-ChildItem "Cert:\CurrentUser\My" | Where-Object { $_.FriendlyName -eq "SmartAgenda Dev Cert" }
$existente | Remove-Item -Force -ErrorAction SilentlyContinue

$cert = New-SelfSignedCertificate `
    -Subject "CN=SmartAgenda" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears(2) `
    -KeyAlgorithm RSA -KeyLength 2048 `
    -KeyUsage DigitalSignature, KeyEncipherment `
    -Type SSLServerAuthentication `
    -FriendlyName "SmartAgenda Dev Cert" `
    -TextExtension @(
        "2.5.29.17={text}DNS=localhost&IPAddress=127.0.0.1&IPAddress=$ip",
        "2.5.29.37={text}1.3.6.1.5.5.7.3.1"
    )

# Contraseña aleatoria generada en cada ejecución (nunca fija en el código fuente): se guarda
# en Data/dev-cert.pwd, un archivo que .gitignore excluye igual que el propio certificado.
$pfxPasswordTexto = [guid]::NewGuid().ToString("N") + [guid]::NewGuid().ToString("N")
Set-Content -Path (Join-Path $certDir "dev-cert.pwd") -Value $pfxPasswordTexto -NoNewline
$pfxPassword = ConvertTo-SecureString -String $pfxPasswordTexto -Force -AsPlainText
Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath (Join-Path $certDir "dev-cert.pfx") -Password $pfxPassword | Out-Null
Export-Certificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath (Join-Path $certDir "dev-cert.cer") | Out-Null

Write-Output ""
Write-Output "Listo. Certificado creado para: localhost, 127.0.0.1, $ip"
Write-Output "Reinicia la app (dotnet run) y abre desde el móvil: https://${ip}:7094"
Write-Output "Instala Data\dev-cert.cer como certificado de confianza en tu móvil (ver README)."
