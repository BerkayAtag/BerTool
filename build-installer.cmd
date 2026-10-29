@echo off
setlocal EnableExtensions EnableDelayedExpansion

cd /d "%~dp0"

set "WITH_SERVER=0"
if /I "%~1"=="--with-server" set "WITH_SERVER=1"
if /I "%~1"=="-server" set "WITH_SERVER=1"

echo [1/5] Gereksinimler kontrol ediliyor...
where dotnet >nul 2>&1
if errorlevel 1 (
  echo HATA: dotnet bulunamadi. .NET 8 SDK kurulu olmali.
  exit /b 1
)

set "PROJECT=src\BerTool.TrayAgent\BerTool.TrayAgent.csproj"
set "PUBLISH_DIR=src\BerTool.TrayAgent\bin\Release\net8.0-windows\win-x64\publish"
set "SERVER_PROJECT=src\BerTool.Server\BerTool.Server.csproj"
set "SERVER_PUBLISH_DIR=artifacts\BerTool.Server"
set "ICON_GENERATOR_PROJECT=tools\BerTool.IconGenerator\BerTool.IconGenerator.csproj"
set "BRAND_ICON=src\BerTool.TrayAgent\Branding\BerToolBrand.ico"
set "INSTALLER_DIR=Installer"
set "INSTALLER_SCRIPT=Installer\BerToolSetup.iss"
set "SERVER_INSTALLER_SCRIPT=Installer\BerToolServerSetup.iss"

echo [2/5] Marka ikonu uretiliyor...
dotnet run --project "%ICON_GENERATOR_PROJECT%" -c Release -- "%BRAND_ICON%" "docs\assets\bertool-logo.png" "docs\assets\setup-small.png"
if errorlevel 1 goto :fail
if not exist "%BRAND_ICON%" (
  echo HATA: Marka ikonu olusturulamadi: "%BRAND_ICON%"
  goto :fail
)

echo [3/5] Ajan uygulamasi publish ediliyor...
dotnet restore "%PROJECT%"
if errorlevel 1 goto :fail

dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
if errorlevel 1 goto :fail

if "%WITH_SERVER%"=="1" (
  echo [4/5] Sunucu uygulamasi publish ediliyor...
  if exist "%SERVER_PUBLISH_DIR%" rmdir /s /q "%SERVER_PUBLISH_DIR%"

  dotnet publish "%SERVER_PROJECT%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o "%SERVER_PUBLISH_DIR%"
  if errorlevel 1 goto :fail
) else (
  echo [4/5] Sunucu derlemesi atlandi. Eklemek icin: build-installer.cmd --with-server
)

set "ISCC_EXE="
for %%I in (
  "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
  "%ProgramFiles%\Inno Setup 6\ISCC.exe"
) do (
  if not defined ISCC_EXE if exist %%I set "ISCC_EXE=%%~I"
)

if not defined ISCC_EXE (
  where ISCC.exe >nul 2>&1
  if not errorlevel 1 set "ISCC_EXE=ISCC.exe"
)

if not defined ISCC_EXE (
  echo UYARI: ISCC.exe bulunamadi. Kurulum EXE'leri uretilmedi.
  echo Uygulama EXE hazir: "%PUBLISH_DIR%\BerTool.TrayAgent.exe"
  exit /b 2
)

if not exist "%INSTALLER_SCRIPT%" (
  echo HATA: Inno Setup scripti bulunamadi: "%INSTALLER_SCRIPT%"
  goto :fail
)

if not exist "%PUBLISH_DIR%\BerTool.TrayAgent.exe" (
  echo HATA: Publish EXE bulunamadi: "%PUBLISH_DIR%\BerTool.TrayAgent.exe"
  goto :fail
)

if not exist "%BRAND_ICON%" (
  echo HATA: Marka ikonu bulunamadi: "%BRAND_ICON%"
  goto :fail
)

if "%WITH_SERVER%"=="1" (
  if not exist "%SERVER_INSTALLER_SCRIPT%" (
    echo HATA: Sunucu Inno Setup scripti bulunamadi: "%SERVER_INSTALLER_SCRIPT%"
    goto :fail
  )
  if not exist "%SERVER_PUBLISH_DIR%\BerTool.Server.exe" (
    echo HATA: Sunucu publish EXE bulunamadi: "%SERVER_PUBLISH_DIR%\BerTool.Server.exe"
    goto :fail
  )
)

echo [5/5] Kurulum EXE'leri derleniyor...
pushd "%INSTALLER_DIR%"

"%ISCC_EXE%" "BerToolSetup.iss"
set "ISCC_EXIT=!ERRORLEVEL!"
if not "!ISCC_EXIT!"=="0" (
  popd
  goto :fail
)

if "%WITH_SERVER%"=="1" (
  "%ISCC_EXE%" "BerToolServerSetup.iss"
  set "ISCC_EXIT=!ERRORLEVEL!"
  if not "!ISCC_EXIT!"=="0" (
    popd
    goto :fail
  )
)

popd

if not exist "%INSTALLER_DIR%\BerToolSetup.exe" (
  echo HATA: Istemci kurulum EXE'si olusmadi: "%INSTALLER_DIR%\BerToolSetup.exe"
  goto :fail
)

if "%WITH_SERVER%"=="1" (
  if not exist "%INSTALLER_DIR%\BerToolServerSetup.exe" (
    echo HATA: Sunucu kurulum EXE'si olusmadi: "%INSTALLER_DIR%\BerToolServerSetup.exe"
    goto :fail
  )
)

echo BASARILI:
echo - Uygulama EXE: "%PUBLISH_DIR%\BerTool.TrayAgent.exe"
echo - Istemci kurulum EXE: "Installer\BerToolSetup.exe"
if "%WITH_SERVER%"=="1" (
  echo - Sunucu klasoru: "%SERVER_PUBLISH_DIR%"
  echo - Sunucu kurulum EXE: "Installer\BerToolServerSetup.exe"
)
echo.
echo Ajan raporlama ayarlari icin kurulum parametreleri:
echo   BerToolSetup.exe /SERVERURL="https://bertool.example.com" /APIKEY="AJAN-API-ANAHTARI"
echo   BerToolSetup.exe /AGENTSETTINGSFILE="C:\Temp\agent-settings.json"
echo.
echo Sunucu kurulum parametreleri:
echo   BerToolServerSetup.exe /PORT=5081 /BIND=127.0.0.1
echo   BerToolServerSetup.exe /URLS="https://0.0.0.0:5443"
echo   BerToolServerSetup.exe /DATADIR="C:\BerToolData" /FORCECONFIG=1
echo.
echo Silent parametreleri:
echo   BerToolSetup.exe /SILENT
echo   BerToolSetup.exe /VERYSILENT
echo   BerToolSetup.exe /SILENT /NORESTART
echo   BerToolSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
echo   BerToolSetup.exe /VERYSILENT /LOG="C:\Temp\BerToolSetup.log"
echo   BerToolSetup.exe /VERYSILENT /SERVERURL="https://bertool.example.com" /APIKEY="AJAN-API-ANAHTARI"
exit /b 0

:fail
echo HATA: Build islemi basarisiz.
exit /b 1
