$ErrorActionPreference = 'SilentlyContinue'

$base = $PSScriptRoot
$locationFile = Join-Path $base 'server-location.txt'
$app = Join-Path $base 'app'

if (Test-Path -LiteralPath $locationFile) {
  $configuredApp = (Get-Content -LiteralPath $locationFile -Raw).Trim()
  if ($configuredApp) { $app = $configuredApp }
}

$logs = Join-Path $base 'logs'
$exe = Join-Path $app 'BerTool.Server.exe'

if (-not (Test-Path -LiteralPath $exe)) { exit 1 }

# Ayni anda iki tetikleyici (gorev + baslangic kisayolu) calissa bile
# tek bir sunucu ornegi baslatilir.
$mutex = New-Object System.Threading.Mutex($false, 'Local\BerToolServerStart')
if (-not $mutex.WaitOne(0)) { exit 0 }

try {
  if (Get-Process -Name 'BerTool.Server' -ErrorAction SilentlyContinue) { exit 0 }

  if (-not (Test-Path -LiteralPath $logs)) {
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
  }

  $process = Start-Process -FilePath $exe `
    -WorkingDirectory $app `
    -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $logs 'server.out.log') `
    -RedirectStandardError (Join-Path $logs 'server.err.log') `
    -PassThru

  # Kurulum sonunda konsol acilmadan once sunucunun dinlemeye basladigini
  # bekle (en fazla 30 sn). Ilk calistirmada giris bilgileri dosyasi da bu
  # surede olusur.
  $deadline = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $deadline -and $process -and -not $process.HasExited) {
    if (Get-NetTCPConnection -OwningProcess $process.Id -State Listen -ErrorAction SilentlyContinue) { break }
    Start-Sleep -Milliseconds 250
  }
}
finally {
  $mutex.ReleaseMutex()
  $mutex.Dispose()
}
