$ErrorActionPreference = 'SilentlyContinue'

$processes = Get-Process -Name 'BerTool.Server' -ErrorAction SilentlyContinue
if ($processes) {
  $processes | Stop-Process -Force
}
