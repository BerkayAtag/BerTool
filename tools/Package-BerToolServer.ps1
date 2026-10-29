<#
.SYNOPSIS
    BerTool sunucu klasörünü taşınabilir bir ZIP paketine dönüştürür.

.DESCRIPTION
    Compress-Archive, Windows PowerShell 5.1 üzerinde ZIP girdilerini ters bölü
    (\) ile yazabilir. Bu durum Linux/macOS unzip araçlarında ve bazı dağıtım
    sistemlerinde klasör yapısının bozulmasına yol açar. Bu betik tüm girdileri
    standart ileri bölü (/) ayırıcısıyla yazar ve paket içinde zorunlu dosyaların
    bulunduğunu doğrular.

.PARAMETER SourceDirectory
    Paketlenecek yayınlanmış sunucu klasörü.

.PARAMETER DestinationPath
    Oluşturulacak ZIP dosyasının yolu.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,

    [Parameter(Mandatory = $true)]
    [string]$DestinationPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression | Out-Null

if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
    throw "Sunucu klasörü bulunamadı: $SourceDirectory"
}

$source = (Resolve-Path -LiteralPath $SourceDirectory).Path.TrimEnd('\', '/')
$destination = [System.IO.Path]::GetFullPath($DestinationPath)
$destinationDirectory = [System.IO.Path]::GetDirectoryName($destination)

if ($destinationDirectory -and -not (Test-Path -LiteralPath $destinationDirectory)) {
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
}

if (Test-Path -LiteralPath $destination) {
    Remove-Item -LiteralPath $destination -Force
}

$files = @(Get-ChildItem -LiteralPath $source -Recurse -File | Sort-Object FullName)
if ($files.Count -eq 0) {
    throw "Paketlenecek dosya bulunamadı: $source"
}

$stream = [System.IO.File]::Open(
    $destination,
    [System.IO.FileMode]::CreateNew,
    [System.IO.FileAccess]::ReadWrite,
    [System.IO.FileShare]::None)

try {
    $archive = New-Object System.IO.Compression.ZipArchive(
        $stream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $true)

    try {
        $baseLength = $source.Length + 1

        foreach ($file in $files) {
            $relativePath = $file.FullName.Substring($baseLength).Replace('\', '/')
            $entry = $archive.CreateEntry(
                $relativePath,
                [System.IO.Compression.CompressionLevel]::Optimal)

            $entry.LastWriteTime = $file.LastWriteTime

            $entryStream = $entry.Open()
            try {
                $fileStream = [System.IO.File]::OpenRead($file.FullName)
                try {
                    $fileStream.CopyTo($entryStream)
                }
                finally {
                    $fileStream.Dispose()
                }
            }
            finally {
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $stream.Dispose()
}

# Paket bütünlüğünü doğrula: yönetici konsolu ve sunucu çalıştırılabilir dosyası
# olmadan paket dağıtıma çıkmamalıdır.
$readStream = [System.IO.File]::OpenRead($destination)
try {
    $verifyArchive = New-Object System.IO.Compression.ZipArchive(
        $readStream,
        [System.IO.Compression.ZipArchiveMode]::Read,
        $true)

    try {
        $entryNames = @($verifyArchive.Entries | ForEach-Object { $_.FullName })

        foreach ($required in @('BerTool.Server.exe', 'wwwroot/index.html', 'appsettings.json')) {
            if ($entryNames -notcontains $required) {
                throw "ZIP doğrulama hatası: '$required' pakette bulunamadı."
            }
        }

        $invalidSeparators = @($entryNames | Where-Object { $_.Contains('\') })
        if ($invalidSeparators.Count -gt 0) {
            throw "ZIP doğrulama hatası: $($invalidSeparators.Count) girdi ters bölü içeriyor."
        }
    }
    finally {
        $verifyArchive.Dispose()
    }
}
finally {
    $readStream.Dispose()
}

$sizeMb = [Math]::Round((Get-Item -LiteralPath $destination).Length / 1MB, 1)
Write-Host "Sunucu ZIP olusturuldu: $destination ($($files.Count) dosya, $sizeMb MB)"
