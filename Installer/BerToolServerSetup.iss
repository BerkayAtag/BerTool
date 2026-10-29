#define MyAppName "BerTool Sunucu"
#define MyAppVersion "1.4.0"
#define MyAppPublisher "Berkay Atağ"
#define MyAppServerExe "BerTool.Server.exe"
#define MyAppBaseDir "{localappdata}\BerToolServer"

[Setup]
AppId={{9D3B4E21-6C7A-4F58-9A1D-2E5B8C7F4A10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\BerToolServer\app
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Tıkla-kur: klasör/grup sayfası sorulmaz; tek "Kur" onayı ve bitiş sayfası kalır.
DisableDirPage=yes
DisableWelcomePage=yes
UsePreviousAppDir=yes
OutputDir=.
OutputBaseFilename=BerToolServerSetup
SetupIconFile=..\src\BerTool.TrayAgent\Branding\BerToolBrand.ico
WizardSmallImageFile=..\docs\assets\setup-small.png
; Programlar ve Özellikler (appwiz.cpl) simgesi ayrı bir .ico dosyasından okunur.
UninstallDisplayIcon={app}\BerTool.ico
UninstallDisplayName={#MyAppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UsedUserAreasWarning=no
CloseApplications=no
RestartApplications=no
VersionInfoVersion=1.4.0.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Kurulum
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Files]
Source: "..\artifacts\BerTool.Server\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\BerTool.Server\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "..\src\BerTool.TrayAgent\Branding\BerToolBrand.ico"; DestDir: "{app}"; DestName: "BerTool.ico"; Flags: ignoreversion
Source: "ServerSupport\start-server.ps1"; DestDir: "{localappdata}\BerToolServer"; Flags: ignoreversion
Source: "ServerSupport\stop-server.ps1"; DestDir: "{localappdata}\BerToolServer"; Flags: ignoreversion

[Dirs]
Name: "{code:GetDataDir}"
Name: "{localappdata}\BerToolServer\logs"

[Icons]
Name: "{group}\BerTool Sunucu Konsolu"; Filename: "{code:GetConsoleUrl}"; IconFilename: "{app}\BerTool.ico"; Comment: "Yönetim konsolunu tarayıcıda açar"
Name: "{group}\BerTool Sunucu Başlat"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{localappdata}\BerToolServer\start-server.ps1"""; WorkingDir: "{localappdata}\BerToolServer"; IconFilename: "{app}\BerTool.ico"; Comment: "Sunucuyu arka planda başlatır"
Name: "{group}\BerTool Sunucu Durdur"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{localappdata}\BerToolServer\stop-server.ps1"""; WorkingDir: "{localappdata}\BerToolServer"; IconFilename: "{app}\BerTool.ico"; Comment: "Sunucuyu durdurur"
Name: "{group}\Sunucu Veri Klasörü"; Filename: "{code:GetDataDir}"; Comment: "Veri, günlük ve ilk kurulum bilgileri"
Name: "{group}\BerTool Sunucu Kaldır"; Filename: "{uninstallexe}"
Name: "{userstartup}\BerTool Sunucu"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{localappdata}\BerToolServer\start-server.ps1"""; WorkingDir: "{localappdata}\BerToolServer"; IconFilename: "{app}\BerTool.ico"; Comment: "BerTool sunucusunu oturum açılışında başlatır"

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{localappdata}\BerToolServer\start-server.ps1"""; StatusMsg: "BerTool sunucusu başlatılıyor..."; Flags: runhidden waituntilterminated
Filename: "{code:GetConsoleUrl}"; Description: "BerTool yönetim konsolunu aç"; Flags: postinstall nowait skipifsilent shellexec
; İlk kurulumda üretilen yönetici şifresi ve ajan API anahtarı. Dosya yalnızca
; ilk kurulumda oluşur; yoksa seçenek gösterilmez.
Filename: "{win}\notepad.exe"; Parameters: """{code:GetCredentialsFile}"""; Description: "İlk giriş bilgilerini göster (yönetici şifresi ve ajan API anahtarı)"; Flags: postinstall nowait skipifsilent; Check: CredentialsFileExists

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppServerExe}"; Flags: runhidden; RunOnceId: "StopBerToolServer"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""BerTool Sunucu Konsolu"" /F"; Flags: runhidden; RunOnceId: "DeleteLegacyServerTask"

[UninstallDelete]
; appsettings.json, appsettings.Production.json ve .bak kaldırma sırasında
; config-backup klasörüne yedeklenir; yeniden kurulumda geri yüklenir.
; Oturum açılışında sunucuyu başlatan kısayol yalnızca bu kuruluma aittir.
Type: files; Name: "{userstartup}\BerTool Sunucu.lnk"
Type: files; Name: "{localappdata}\BerToolServer\start-server.ps1"
Type: files; Name: "{localappdata}\BerToolServer\stop-server.ps1"
Type: files; Name: "{localappdata}\BerToolServer\server-location.txt"
Type: dirifempty; Name: "{app}\wwwroot"
Type: dirifempty; Name: "{app}"
Type: dirifempty; Name: "{localappdata}\BerToolServer\logs"
Type: dirifempty; Name: "{localappdata}\BerToolServer\config-backup"
Type: dirifempty; Name: "{localappdata}\BerToolServer"

[Code]
var
  Urls: string;
  DataDir: string;
  UrlsExplicit: Boolean;
  ForceConfig: Boolean;
  HadExistingMainConfig: Boolean;

function GetParamValue(const Name: string): string;
begin
  Result := Trim(ExpandConstant('{param:' + Name + '|}'));
end;

function IsTruthy(const Value: string): Boolean;
begin
  Result :=
    (Value = '1') or
    (CompareText(Value, 'true') = 0) or
    (CompareText(Value, 'yes') = 0) or
    (CompareText(Value, 'on') = 0);
end;

function IsValidUrl(const Value: string): Boolean;
var
  LowerValue: string;
begin
  LowerValue := Lowercase(Value);
  Result :=
    (Pos('http://', LowerValue) = 1) or
    (Pos('https://', LowerValue) = 1);
end;

function ExtractHttpHost(const Value: string): string;
var
  LowerValue: string;
  Tail: string;
  CutPos: Integer;
  Index: Integer;
begin
  Result := '';
  LowerValue := Lowercase(Trim(Value));

  if Pos('http://', LowerValue) <> 1 then
    Exit;

  Tail := Copy(LowerValue, 8, Length(LowerValue));

  // IPv6 adresleri köşeli parantez içinde gelir; bağlantı noktası ayrımı
  // köşeli parantezden sonra yapılır.
  if Length(Tail) > 0 then
  begin
    if Tail[1] = '[' then
    begin
      CutPos := Pos(']', Tail);
      if CutPos > 0 then
        Result := Copy(Tail, 1, CutPos)
      else
        Result := Tail;
      Exit;
    end;
  end;

  CutPos := Length(Tail) + 1;
  Index := 1;
  while Index <= Length(Tail) do
  begin
    if (Tail[Index] = '/') or (Tail[Index] = ':') or
       (Tail[Index] = '?') or (Tail[Index] = '#') then
    begin
      CutPos := Index;
      Index := Length(Tail) + 1;
    end
    else
      Index := Index + 1;
  end;

  Result := Copy(Tail, 1, CutPos - 1);
end;

function IsLoopbackHost(const Host: string): Boolean;
begin
  Result :=
    (Host = 'localhost') or
    (Host = '127.0.0.1') or
    (Host = '[::1]');
end;

function IsInsecureLanUrl(const Value: string): Boolean;
var
  LowerValue: string;
begin
  LowerValue := Lowercase(Trim(Value));

  if Pos('http://', LowerValue) <> 1 then
  begin
    Result := False;
    Exit;
  end;

  Result := not IsLoopbackHost(ExtractHttpHost(LowerValue));
end;

function IsLoopbackBindAddress(const Value: string): Boolean;
var
  LowerValue: string;
begin
  LowerValue := Lowercase(Trim(Value));
  Result :=
    (LowerValue = 'localhost') or
    (LowerValue = '::1') or
    (LowerValue = '[::1]') or
    (LowerValue = '127.0.0.1');
end;

function JsonEscape(const Value: string): string;
var
  Index: Integer;
  Character: Char;
begin
  Result := '';
  for Index := 1 to Length(Value) do
  begin
    Character := Value[Index];
    if Character = '"' then
      Result := Result + '\"'
    else if Character = '\' then
      Result := Result + '\\'
    else if Character = #13 then
      Result := Result + '\r'
    else if Character = #10 then
      Result := Result + '\n'
    else if Ord(Character) < 32 then
      Result := Result + '\u' + Format('%.4x', [Ord(Character)])
    else
      Result := Result + Character;
  end;
end;

procedure ParseParameters();
var
  BindAddress: string;
  PortValue: string;
  UrlsValue: string;
begin
  UrlsValue := GetParamValue('URLS');
  BindAddress := GetParamValue('BIND');
  PortValue := GetParamValue('PORT');

  UrlsExplicit :=
    (UrlsValue <> '') or
    (BindAddress <> '') or
    (PortValue <> '');

  ForceConfig := IsTruthy(GetParamValue('FORCECONFIG'));

  if BindAddress = '' then
    BindAddress := '127.0.0.1';
  if PortValue = '' then
    PortValue := '5081';

  if UrlsValue <> '' then
    Urls := UrlsValue
  else if IsLoopbackBindAddress(BindAddress) then
    // Yerel kurulumda HTTP yeterlidir; trafik makineden dışarı çıkmaz.
    Urls := 'http://' + BindAddress + ':' + PortValue
  else
    // Yerel ağa açık kurulumlarda varsayılan olarak HTTPS kullanılır.
    // Sertifika appsettings.Production.json içinde tanımlanmalıdır.
    Urls := 'https://' + BindAddress + ':' + PortValue;

  if GetParamValue('DATADIR') <> '' then
    DataDir := ExpandConstant(GetParamValue('DATADIR'))
  else
    DataDir := ExpandConstant('{localappdata}\BerToolServer\data');
end;

function GetDataDir(Param: string): string;
begin
  Result := DataDir;
end;

function GetConsoleUrl(Param: string): string;
begin
  Result := Urls;
end;

function GetCredentialsFile(Param: string): string;
begin
  Result := AddBackslash(DataDir) + 'bootstrap-credentials.txt';
end;

function CredentialsFileExists(): Boolean;
begin
  Result := FileExists(GetCredentialsFile(''));
end;

function ReadUrlFromConfig(const FileName: string): string;
var
  Lines: TArrayOfString;
  Index: Integer;
  Line: string;
  LowerLine: string;
  KeyPos: Integer;
  Tail: string;
  ColonPos: Integer;
  QuoteStart: Integer;
  QuoteEnd: Integer;
begin
  Result := '';
  if not FileExists(FileName) then
    Exit;
  if not LoadStringsFromFile(FileName, Lines) then
    Exit;

  for Index := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[Index];
    LowerLine := Lowercase(Line);
    KeyPos := Pos('"urls"', LowerLine);
    if KeyPos > 0 then
    begin
      Tail := Copy(Line, KeyPos + 6, Length(Line));
      ColonPos := Pos(':', Tail);
      if ColonPos > 0 then
      begin
        Tail := Copy(Tail, ColonPos + 1, Length(Tail));
        QuoteStart := Pos('"', Tail);
        if QuoteStart > 0 then
        begin
          Tail := Copy(Tail, QuoteStart + 1, Length(Tail));
          QuoteEnd := Pos('"', Tail);
          if QuoteEnd > 0 then
          begin
            Result := Trim(Copy(Tail, 1, QuoteEnd - 1));
            Exit;
          end;
        end;
      end;
    end;
  end;
end;

procedure ApplyConfiguredUrl();
var
  ExistingUrl: string;
begin
  if UrlsExplicit or ForceConfig then
    Exit;

  ExistingUrl := ReadUrlFromConfig(ExpandConstant('{app}\appsettings.Production.json'));
  if ExistingUrl = '' then
    ExistingUrl := ReadUrlFromConfig(ExpandConstant('{app}\appsettings.json'));

  if IsValidUrl(ExistingUrl) then
    Urls := ExistingUrl;
end;

function GetConfigBackupDir(): string;
begin
  Result := ExpandConstant('{localappdata}\BerToolServer\config-backup');
end;

function BackupConfigFile(const FileName: string): Boolean;
var
  SourcePath: string;
  BackupPath: string;
begin
  Result := True;
  SourcePath := ExpandConstant('{app}\') + FileName;
  if not FileExists(SourcePath) then
    Exit;

  BackupPath := GetConfigBackupDir() + '\' + FileName;
  Result := CopyFile(SourcePath, BackupPath, False);
end;

function BackupServerConfig(): Boolean;
begin
  ForceDirectories(GetConfigBackupDir());
  Result := BackupConfigFile('appsettings.json');
  if not BackupConfigFile('appsettings.Production.json') then
    Result := False;
  if not BackupConfigFile('appsettings.Production.json.bak') then
    Result := False;
end;

procedure DeleteConfigFile(const FileName: string);
var
  FilePath: string;
begin
  FilePath := ExpandConstant('{app}\') + FileName;
  if FileExists(FilePath) then
    DeleteFile(FilePath);
end;

function RestoreConfigFile(const FileName: string): Boolean;
var
  BackupPath: string;
  TargetPath: string;
begin
  Result := True;
  BackupPath := GetConfigBackupDir() + '\' + FileName;
  TargetPath := ExpandConstant('{app}\') + FileName;

  if not FileExists(BackupPath) then
    Exit;
  if FileExists(TargetPath) then
    Exit;

  // PrepareToInstall, dosyalar kopyalanmadan once calisir; hedef klasor
  // henuz olusmamis olabilir. Klasoru olusturmadan CopyFile sessizce
  // basarisiz olur ve kullanici yapilandirmasi kaybolur.
  ForceDirectories(ExpandConstant('{app}'));
  Result := CopyFile(BackupPath, TargetPath, False);
end;

function RestoreServerConfig(): Boolean;
begin
  Result := RestoreConfigFile('appsettings.json');
  if not RestoreConfigFile('appsettings.Production.json') then
    Result := False;
  // .bak dosyasi kritik degildir; geri yuklenemezse kurulumu durdurma.
  RestoreConfigFile('appsettings.Production.json.bak');
end;

procedure StopServerProcess();
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/F /IM {#MyAppServerExe}',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';

  // Kaldirma sirasinda yedeklenen sunucu ayarlari varsa geri yukle.
  // Acikca yeni yapilandirma istenmisse eski yedegi geri yukleme.
  if not ForceConfig then
  begin
    if not RestoreServerConfig() then
    begin
      Result := 'Sunucu ayarlari yedekten geri yuklenemedi. ' +
        'Kurulum durduruldu; yedek ve veri dosyalari korunuyor. ' +
        'Sorunu giderdikten sonra kurulumu yeniden calistirin.';
      Exit;
    end;
  end;

  // Kurulumdan once calisan sunucuyu durdur; dosyalar kilitli kalmasin.
  StopServerProcess();

  // appsettings.json mevcut kurulumdan geliyorsa korunur; bu durumda
  // kurulum parametreleri yalnizca acikca verildiyse uygulanir.
  HadExistingMainConfig := FileExists(ExpandConstant('{app}\appsettings.json'));

  // Konsol kisayolu ve kurulum sonrasi tarayici, mevcut yapilandirmadaki
  // gercek adresi kullanmali; varsayilan 127.0.0.1:5081 degil.
  ApplyConfiguredUrl();

  if not IsValidUrl(Urls) then
  begin
    Result :=
      'Sunucu adresi http:// veya https:// ile başlamalıdır:' + #13#10 + Urls;
    Exit;
  end;

  if ((not HadExistingMainConfig) or UrlsExplicit) and
     IsInsecureLanUrl(Urls) then
  begin
    // Etkileşimsiz kurulumda güvenli olmayan LAN adresi hiç kabul edilmez.
    if WizardSilent then
    begin
      Result :=
        'Güvenlik nedeniyle yerel ağa açık HTTP adresi kullanılamaz. ' +
        'Lütfen HTTPS adresi veya yerel (127.0.0.1 / localhost) adres kullanın: ' +
        Urls;
      Exit;
    end;

    if MsgBox(
      'Uyarı: Yerel ağa açık bir HTTP adresi seçildi.' + #13#10 + #13#10 +
      Urls + #13#10 + #13#10 +
      'Üretim ve LAN kurulumlarında HTTPS kullanılmalıdır. Devam edilsin mi?',
      mbConfirmation,
      MB_YESNO) = IDNO then
    begin
      Result := 'Kurulum kullanıcı tarafından iptal edildi.';
      Exit;
    end;
  end;
end;

procedure WriteProductionConfig();
var
  ConfigPath: string;
  Lines: TArrayOfString;
begin
  ConfigPath := ExpandConstant('{app}\appsettings.Production.json');

  if FileExists(ConfigPath) and (not ForceConfig) then
    Exit;

  // Mevcut kurulumda appsettings.json zaten adres ve veri dizinini
  // tanimliyorsa, kurulum parametresi verilmedikce dosyaya dokunma.
  if HadExistingMainConfig and (not UrlsExplicit) and (not ForceConfig) then
    Exit;

  if FileExists(ConfigPath) then
    CopyFile(ConfigPath, ConfigPath + '.bak', False);

  if HadExistingMainConfig and (not ForceConfig) then
  begin
    // Mevcut appsettings.json veri dizinini ve diğer ayarları korur;
    // yalnızca acikca verilen adresi gecersiz kil.
    SetArrayLength(Lines, 3);
    Lines[0] := '{';
    Lines[1] := '  "Urls": "' + JsonEscape(Urls) + '"';
    Lines[2] := '}';
  end
  else
  begin
    SetArrayLength(Lines, 6);
    Lines[0] := '{';
    Lines[1] := '  "Urls": "' + JsonEscape(Urls) + '",';
    Lines[2] := '  "BerTool": {';
    Lines[3] := '    "DataDirectory": "' + JsonEscape(DataDir) + '"';
    Lines[4] := '  }';
    Lines[5] := '}';
  end;

  SaveStringsToUTF8File(ConfigPath, Lines, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  LocationPath: string;
  LocationLines: TArrayOfString;
begin
  // [Run] bölümündeki "sunucuyu başlat" girdisi ssPostInstall'dan önce
  // çalışır. Yapılandırma ssPostInstall'da yazılırsa temiz kurulumda sunucu
  // appsettings.json varsayılanlarıyla (5080 portu, app\data) başlıyordu.
  // Bu yüzden adres ve veri dizini dosyalar kopyalanmadan önce yazılır.
  if CurStep = ssInstall then
  begin
    ForceDirectories(ExpandConstant('{app}'));
    ForceDirectories(ExpandConstant('{localappdata}\BerToolServer'));
    LocationPath := ExpandConstant('{localappdata}\BerToolServer\server-location.txt');
    SetArrayLength(LocationLines, 1);
    LocationLines[0] := ExpandConstant('{app}');
    SaveStringsToUTF8File(LocationPath, LocationLines, False);

    WriteProductionConfig();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    // Ayarlar kopyalanmadan ve silinmeden once sunucuyu durdur.
    StopServerProcess();

    // Ayarlar guvenli sekilde yedeklendiyse uygulama klasorunu temizle.
    // Yedekleme basarisizsa dosyalar yerinde birakilir; veri kaybi olmaz.
    if BackupServerConfig() then
    begin
      DeleteConfigFile('appsettings.json');
      DeleteConfigFile('appsettings.Production.json');
      DeleteConfigFile('appsettings.Production.json.bak');
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  ParseParameters();
  Result := True;
end;
