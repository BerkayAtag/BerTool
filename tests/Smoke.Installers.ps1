# BerTool kurulum duman testi. Temiz ve yönetici yetkili bir Windows'ta
# (GitHub Actions windows-latest) iki setup dosyasını sessiz kurar, Programlar ve
# Özellikler kaydını (yayıncı, sürüm, simge), çalışan sunucuyu, ajan raporunu ve
# kaldırmayı doğrular. Sonuç dist\<sürüm>\installation-verification.json dosyasına yazılır.
param(
    [string]$Version = '1.4.0',
    [string]$ExpectedPublisher = "Berkay Ata$([char]0x011F)"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist\$Version"
$serverSetup = Join-Path $dist "BerTool.Server.Setup-$Version.exe"
$clientSetup = Join-Path $dist "BerTool.Client.Setup-$Version.exe"
$results = New-Object System.Collections.Generic.List[object]

function Assert($condition, [string]$message) {
    if (-not $condition) { throw "DOĞRULAMA BAŞARISIZ: $message" }
    Write-Host "OK  $message"
}

function Get-UninstallEntry([string]$hive, [string]$displayName) {
    Get-ChildItem "${hive}:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" -ErrorAction SilentlyContinue |
        Get-ItemProperty | Where-Object { $_.DisplayName -eq $displayName } | Select-Object -First 1
}

function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    throw "Zaman aşımı: $what"
}

function Invoke-Setup([string]$path, [string[]]$arguments) {
    # Start-Process -Wait sunucu gibi kalıcı alt süreçleri de bekler; yalnızca
    # kurulum sürecinin kendisi beklenir.
    $process = Start-Process -FilePath $path -ArgumentList $arguments -PassThru
    $process.WaitForExit()
    return $process.ExitCode
}

function Test-Entry($entry, [string]$component) {
    Assert ($null -ne $entry) "$component Programlar ve Özellikler kaydı var"
    Assert ($entry.Publisher -eq $ExpectedPublisher) "$component yayıncı '$($entry.Publisher)'"
    Assert ($entry.DisplayVersion -eq $Version) "$component sürüm $($entry.DisplayVersion)"
    Assert ($entry.DisplayIcon -like '*BerTool.ico') "$component simgesi BerTool.ico"
    Assert (Test-Path -LiteralPath $entry.DisplayIcon) "$component simge dosyası mevcut"
    $results.Add([pscustomobject]@{
        Component = $component
        DisplayName = $entry.DisplayName
        Publisher = $entry.Publisher
        Version = $entry.DisplayVersion
        DisplayIcon = $entry.DisplayIcon
    })
}

Assert (Test-Path $serverSetup) "sunucu setup dosyası bulundu"
Assert (Test-Path $clientSetup) "istemci setup dosyası bulundu"

# --- Sunucu ---
$exit = Invoke-Setup $serverSetup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$env:TEMP\bertool-server-setup.log")
Assert ($exit -eq 0) "sunucu kurulumu çıkış kodu 0"
Test-Entry (Get-UninstallEntry 'HKCU' 'BerTool Sunucu') 'Server'

Wait-Until { try { (Invoke-WebRequest 'http://127.0.0.1:5081/healthz' -UseBasicParsing).StatusCode -eq 200 } catch { $false } } 60 'sunucu /healthz' | Out-Null
Write-Host 'OK  sunucu http://127.0.0.1:5081 üzerinde çalışıyor'

$credentials = Join-Path $env:LOCALAPPDATA 'BerToolServer\data\bootstrap-credentials.txt'
Assert (Test-Path $credentials) "ilk giriş bilgileri dosyası oluştu"
$apiKeyLine = Get-Content -LiteralPath $credentials -Encoding UTF8 | Where-Object { $_ -match 'API anahtar' } | Select-Object -First 1
$apiKey = ($apiKeyLine -split ':\s*', 2)[1].Trim()
Assert ($apiKey.Length -ge 32) "ajan API anahtarı okundu"

# --- İstemci ---
$exit = Invoke-Setup $clientSetup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$env:TEMP\bertool-client-setup.log", '/SERVERURL=http://127.0.0.1:5081', "/APIKEY=$apiKey")
Assert ($exit -eq 0) "istemci kurulumu çıkış kodu 0"
$clientEntry = Get-UninstallEntry 'HKLM' 'BerTool'
Test-Entry $clientEntry 'Client'

$agentExe = Join-Path $clientEntry.InstallLocation 'BerTool.TrayAgent.exe'
Assert (Test-Path $agentExe) "ajan Program Files altına kuruldu"
Assert ((Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run').BerTool -like '*BerTool.TrayAgent.exe*') "Windows açılışında başlatma kaydı"
Assert ($null -ne (Get-ScheduledTask -TaskName 'BerTool BitLocker Cache' -ErrorAction SilentlyContinue)) "BitLocker cache görevi oluşturuldu"
Assert (Test-Path "$env:ProgramData\BerTool\agent-settings.json") "ajan sunucu ayarı yazıldı"
Assert (Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\BerTool.lnk") "Başlat menüsü kısayolu"

$report = Start-Process -FilePath $agentExe -ArgumentList '--report-once' -PassThru
$report.WaitForExit()
Assert ($report.ExitCode -eq 0) "ajan tek seferlik raporu sunucuya gönderdi"

$agentLog = Join-Path $env:LOCALAPPDATA 'BerTool\Logs\tray-agent.log'
if (Test-Path $agentLog) {
    $log = Get-Content -LiteralPath $agentLog -Raw -Encoding UTF8
    Assert (-not ($log -match 'GetEncryptionMethod')) "BitLocker WMI çağrısı hatasız"
}

# --- Kaldırma ---
& (Join-Path $clientEntry.InstallLocation 'unins000.exe') /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
Wait-Until { $null -eq (Get-UninstallEntry 'HKLM' 'BerTool') } 120 'istemci kaldırma' | Out-Null
Assert ($null -eq (Get-ScheduledTask -TaskName 'BerTool BitLocker Cache' -ErrorAction SilentlyContinue)) "BitLocker görevi kaldırıldı"
Assert (Test-Path "$env:ProgramData\BerTool\agent-settings.json") "ajan ayarı kaldırmada korundu"

$serverEntry = Get-UninstallEntry 'HKCU' 'BerTool Sunucu'
& (Join-Path $serverEntry.InstallLocation 'unins000.exe') /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
Wait-Until { $null -eq (Get-UninstallEntry 'HKCU' 'BerTool Sunucu') } 120 'sunucu kaldırma' | Out-Null
Assert ($null -eq (Get-Process -Name 'BerTool.Server' -ErrorAction SilentlyContinue)) "sunucu süreci durduruldu"
Assert (Test-Path (Join-Path $env:LOCALAPPDATA 'BerToolServer\data\server.json')) "sunucu verisi kaldırmada korundu"

$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dist 'installation-verification.json') -Encoding UTF8
Write-Host 'Tüm kurulum doğrulamaları başarılı.'
