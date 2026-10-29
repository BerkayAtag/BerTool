# BerTool Authenticode imzalama yardımcısı.
#
# Sertifika ortam değişkenleriyle verilir; hiçbiri yoksa imzalama atlanır:
#   BERTOOL_SIGN_PFX           .pfx dosya yolu
#   BERTOOL_SIGN_PFX_PASSWORD  .pfx parolası
#   BERTOOL_SIGN_THUMBPRINT    Sertifika deposundaki (ör. donanım anahtarı /
#                              bulut HSM sürücüsü) sertifikanın parmak izi
#   BERTOOL_SIGN_TIMESTAMP_URL Zaman damgası sunucusu (varsayılan DigiCert)
#   BERTOOL_SIGNTOOL           signtool.exe yolu (varsayılan: Windows SDK)
#
# Kullanım: Sign-BerTool.ps1 <dosya> [<dosya> ...]
# Inno Setup bu betiği SignTool olarak çağırır; kurulum ve kaldırıcı da imzalanır.
param(
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]]$Files
)

$ErrorActionPreference = 'Stop'

$pfx = $env:BERTOOL_SIGN_PFX
$thumbprint = $env:BERTOOL_SIGN_THUMBPRINT
if ([string]::IsNullOrWhiteSpace($pfx) -and [string]::IsNullOrWhiteSpace($thumbprint)) {
    Write-Host 'Kod imzalama sertifikası tanımlı değil; imzalama atlandı.'
    exit 0
}

$signtool = $env:BERTOOL_SIGNTOOL
if ([string]::IsNullOrWhiteSpace($signtool)) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } |
        Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $signtool -or -not (Test-Path -LiteralPath $signtool)) {
    throw 'signtool.exe bulunamadı. Windows SDK kurun veya BERTOOL_SIGNTOOL değişkenini tanımlayın.'
}

$timestamp = $env:BERTOOL_SIGN_TIMESTAMP_URL
if ([string]::IsNullOrWhiteSpace($timestamp)) {
    $timestamp = 'http://timestamp.digicert.com'
}

$arguments = @('sign', '/fd', 'SHA256', '/tr', $timestamp, '/td', 'SHA256', '/d', 'BerTool')
if (-not [string]::IsNullOrWhiteSpace($pfx)) {
    if (-not (Test-Path -LiteralPath $pfx)) { throw "Sertifika dosyası bulunamadı: $pfx" }
    $arguments += @('/f', $pfx)
    if (-not [string]::IsNullOrEmpty($env:BERTOOL_SIGN_PFX_PASSWORD)) {
        $arguments += @('/p', $env:BERTOOL_SIGN_PFX_PASSWORD)
    }
}
else {
    $arguments += @('/sha1', $thumbprint.Replace(' ', ''))
}

foreach ($file in $Files) {
    if (-not (Test-Path -LiteralPath $file)) { throw "İmzalanacak dosya bulunamadı: $file" }

    # Zaman damgası sunucusu geçici olarak yanıt vermeyebilir; birkaç kez dene.
    $signed = $false
    for ($attempt = 1; $attempt -le 3 -and -not $signed; $attempt++) {
        & $signtool @arguments $file | Out-Host
        if ($LASTEXITCODE -eq 0) {
            $signed = $true
        }
        elseif ($attempt -lt 3) {
            Start-Sleep -Seconds (5 * $attempt)
        }
    }

    if (-not $signed) { throw "İmzalama başarısız: $file" }
    Write-Host "İmzalandı: $file"
}
