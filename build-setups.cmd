@echo off
setlocal EnableExtensions EnableDelayedExpansion

cd /d "%~dp0"

echo ==========================================
echo  BerTool setup dosyalari uretiliyor
echo ==========================================
echo.

rem Tam yol: NoDefaultCurrentDirectoryInExePath ayarli ortamlarda da calisir.
call "%~dp0build-installer.cmd" --with-server
if errorlevel 1 (
  echo.
  echo HATA: Setup dosyalari uretilemedi.
  exit /b 1
)

rem Surum numarasini Inno Setup betiginden oku.
set "APP_VERSION="
for /f "tokens=3" %%V in ('findstr /b /c:"#define MyAppVersion" "Installer\BerToolSetup.iss"') do set "APP_VERSION=%%~V"

if not defined APP_VERSION (
  echo HATA: Uygulama surumu okunamadi.
  exit /b 1
)

set "DIST_DIR=dist\%APP_VERSION%"
if exist "%DIST_DIR%" rmdir /s /q "%DIST_DIR%"
mkdir "%DIST_DIR%"

copy /y "Installer\BerToolSetup.exe" "%DIST_DIR%\BerTool.Client.Setup-%APP_VERSION%.exe" >nul
if errorlevel 1 (
  echo HATA: Istemci setup dosyasi kopyalanamadi.
  exit /b 1
)

copy /y "Installer\BerToolServerSetup.exe" "%DIST_DIR%\BerTool.Server.Setup-%APP_VERSION%.exe" >nul
if errorlevel 1 (
  echo HATA: Sunucu setup dosyasi kopyalanamadi.
  exit /b 1
)

copy /y "Installer\SETUP-KURULUM.txt" "%DIST_DIR%\KURULUM.txt" >nul
if errorlevel 1 (
  echo HATA: Kurulum yonergesi kopyalanamadi.
  exit /b 1
)

rem Teslim edilen dosyalarin dogrulanabilmesi icin SHA-256 ozetlerini yaz.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$dir = '%DIST_DIR%'; Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object { $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); '{0}  {1}' -f $hash, $_.Name } | Set-Content -LiteralPath (Join-Path $dir 'SHA256SUMS.txt') -Encoding ASCII"
if errorlevel 1 (
  echo UYARI: SHA-256 dosyasi olusturulamadi.
)

echo.
echo ==========================================
echo  SETUP DOSYALARI HAZIR
echo ==========================================
echo Istemci : "%DIST_DIR%\BerTool.Client.Setup-%APP_VERSION%.exe"
echo Sunucu  : "%DIST_DIR%\BerTool.Server.Setup-%APP_VERSION%.exe"
echo Kurulum : "%DIST_DIR%\KURULUM.txt"
echo Ozet    : "%DIST_DIR%\SHA256SUMS.txt"
echo.
echo Istemci kurulumu (yonetici cmd):
echo   "%DIST_DIR%\BerTool.Client.Setup-%APP_VERSION%.exe" /SERVERURL="https://sunucu-adresi" /APIKEY="AJAN-API-ANAHTARI"
echo.
echo Sunucu kurulumu:
echo   "%DIST_DIR%\BerTool.Server.Setup-%APP_VERSION%.exe" /PORT=5081 /BIND=127.0.0.1
echo   Uretim/LAN icin HTTPS adresi verin; ayrintilar KURULUM.txt ve README.md icinde.
echo.

exit /b 0
