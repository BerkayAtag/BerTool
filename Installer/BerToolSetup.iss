#define MyAppName "BerTool"
#define MyAppVersion "1.4.0"
#define MyAppPublisher "Berkay Atağ"
#define MyAppExeName "BerTool.TrayAgent.exe"
#define MyAppTaskName "BerTool BitLocker Cache"

[Setup]
AppId={{7AB5C8B2-9181-453C-B827-30EA3CDFDEE1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\BerTool\TrayAgent
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Tıkla-kur: klasör/grup sayfası sorulmaz; tek "Kur" onayı ve bitiş sayfası kalır.
DisableDirPage=yes
DisableWelcomePage=yes
UsePreviousAppDir=yes
OutputDir=.
OutputBaseFilename=BerToolSetup
SetupIconFile=..\src\BerTool.TrayAgent\Branding\BerToolBrand.ico
WizardSmallImageFile=..\docs\assets\setup-small.png
; Programlar ve Özellikler (appwiz.cpl) simgesi ayrı bir .ico dosyasından okunur.
UninstallDisplayIcon={app}\BerTool.ico
UninstallDisplayName={#MyAppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
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
Source: "..\src\BerTool.TrayAgent\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\src\BerTool.TrayAgent\Branding\BerToolBrand.ico"; DestDir: "{app}"; DestName: "BerTool.ico"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\BerTool"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\BerTool.ico"; Comment: "BerTool cihaz durumu (tray ajanı)"

[InstallDelete]
; Yalnizca istemciye ait kesin adlar temizlenir. "BerTool*.lnk" gibi joker
; desenler sunucunun "BerTool Sunucu.lnk" kisayolunu da siliyordu.
Type: files; Name: "{commondesktop}\BerTool.lnk"
Type: files; Name: "{userdesktop}\BerTool.lnk"
Type: files; Name: "{commonstartup}\BerTool.lnk"
Type: files; Name: "{userstartup}\BerTool.lnk"
Type: filesandordirs; Name: "{commonprograms}\BerTool"
Type: filesandordirs; Name: "{userprograms}\BerTool"

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BerTool"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--update-bitlocker-cache"; StatusMsg: "BitLocker önbelleği güncelleniyor..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Parameters: "--register-bitlocker-task"; StatusMsg: "BitLocker önbellek görevi oluşturuluyor..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "BerTool'u başlat"; Flags: nowait runasoriginaluser

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#MyAppTaskName}"" /F"; Flags: runhidden; RunOnceId: "DeleteBitLockerCacheTask"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "StopBerToolTrayAgent"

[UninstallDelete]
; {commonappdata}\BerTool altındaki agent-settings.json kullanıcı verisidir;
; kaldırma sırasında silinmez. Yalnızca kısayollar ve boş klasörler temizlenir.
Type: filesandordirs; Name: "{commonprograms}\BerTool"
Type: filesandordirs; Name: "{userprograms}\BerTool"
; Yalnizca istemciye ait kesin adlar temizlenir. "BerTool*.lnk" gibi joker
; desenler sunucunun "BerTool Sunucu.lnk" kisayolunu da siliyordu.
Type: files; Name: "{commondesktop}\BerTool.lnk"
Type: files; Name: "{userdesktop}\BerTool.lnk"
Type: files; Name: "{commonstartup}\BerTool.lnk"
Type: files; Name: "{userstartup}\BerTool.lnk"
Type: dirifempty; Name: "{commonappdata}\BerTool"
Type: dirifempty; Name: "{commonappdata}\BerToolUninstallBackup"

[Code]
procedure StopAgentProcess();
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/F /IM {#MyAppExeName}',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Eski ajan çalışıyorsa dosyaları kilitlemesini engelle.
  StopAgentProcess();
  Result := '';
end;

function TrimTrailingSlashes(const Value: string): string;
var
  ResultValue: string;
begin
  ResultValue := Trim(Value);
  while (Length(ResultValue) > 0) and (ResultValue[Length(ResultValue)] = '/') do
    Delete(ResultValue, Length(ResultValue), 1);
  Result := ResultValue;
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

function IsLoopbackHttpUrl(const Value: string): Boolean;
begin
  Result := IsLoopbackHost(ExtractHttpHost(Value));
end;

function IsInsecureLanUrl(const Value: string): Boolean;
var
  LowerValue: string;
begin
  LowerValue := Lowercase(Trim(Value));
  Result :=
    (Pos('http://', LowerValue) = 1) and
    (not IsLoopbackHttpUrl(LowerValue));
end;

function ExtractServerUrl(const Json: string): string;
var
  KeyPos: Integer;
  ColonPos: Integer;
  QuoteStart: Integer;
  QuoteEnd: Integer;
  Tail: string;
begin
  Result := '';
  KeyPos := Pos('"serverurl"', Lowercase(Json));
  if KeyPos = 0 then
    Exit;

  Tail := Copy(Json, KeyPos + Length('"serverUrl"'), Length(Json));
  ColonPos := Pos(':', Tail);
  if ColonPos = 0 then
    Exit;

  Tail := Copy(Tail, ColonPos + 1, Length(Tail));
  QuoteStart := Pos('"', Tail);
  if QuoteStart = 0 then
    Exit;

  Tail := Copy(Tail, QuoteStart + 1, Length(Tail));
  QuoteEnd := Pos('"', Tail);
  if QuoteEnd = 0 then
    Exit;

  Result := Trim(Copy(Tail, 1, QuoteEnd - 1));
end;

procedure HardenAgentSettingsFile(const SettingsPath: string);
var
  ResultCode: Integer;
  AclArguments: string;
begin
  // Ayarlar dosyası yalnızca SYSTEM ve Administrators tarafından yazılabilir,
  // standart kullanıcılar (tray ajanı) yalnızca okuyabilir olmalıdır.
  AclArguments :=
    '"' + SettingsPath + '" /inheritance:r' +
    ' /grant:r "*S-1-5-18:(F)"' +
    ' "*S-1-5-32-544:(F)"' +
    ' "*S-1-5-32-545:(R)"';

  if (not Exec(
        ExpandConstant('{sys}\icacls.exe'),
        AclArguments,
        '',
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode)) or (ResultCode <> 0) then
  begin
    MsgBox(
      'Ajan ayar dosyasının izinleri sıkılaştırılamadı. ' +
      'Dosyayı yalnızca yöneticilerin değiştirebildiğinden emin olun:' + #13#10 +
      SettingsPath,
      mbError,
      MB_OK);
  end;
end;

function GetAgentBackupDir(): string;
begin
  Result := ExpandConstant('{commonappdata}\BerToolUninstallBackup');
end;

procedure BackupAgentFile(const FileName: string);
var
  SourcePath: string;
  BackupPath: string;
begin
  SourcePath := ExpandConstant('{commonappdata}\BerTool\') + FileName;
  BackupPath := GetAgentBackupDir() + '\' + FileName;

  if not FileExists(SourcePath) then
  begin
    if FileExists(BackupPath) then
      DeleteFile(BackupPath);
    Exit;
  end;

  if not CopyFile(SourcePath, BackupPath, False) then
    Exit;

  if FileName = 'agent-settings.json' then
    HardenAgentSettingsFile(BackupPath);
end;

procedure BackupAgentData();
begin
  ForceDirectories(GetAgentBackupDir());
  BackupAgentFile('agent-settings.json');
  BackupAgentFile('bitlocker-cache.json');
end;

procedure RestoreAgentFile(const FileName: string);
var
  BackupPath: string;
  TargetPath: string;
begin
  BackupPath := GetAgentBackupDir() + '\' + FileName;
  TargetPath := ExpandConstant('{commonappdata}\BerTool\') + FileName;
  if FileExists(BackupPath) and (not FileExists(TargetPath)) then
    CopyFile(BackupPath, TargetPath, False);
end;

procedure RestoreAgentData();
var
  SettingsPath: string;
begin
  SettingsPath := ExpandConstant('{commonappdata}\BerTool\agent-settings.json');
  ForceDirectories(ExpandConstant('{commonappdata}\BerTool'));
  RestoreAgentFile('agent-settings.json');
  RestoreAgentFile('bitlocker-cache.json');

  if FileExists(SettingsPath) then
    HardenAgentSettingsFile(SettingsPath);
end;

procedure ConfigureAgentReporting();
var
  ServerUrl: string;
  ApiKey: string;
  SettingsSource: string;
  SettingsDirectory: string;
  SettingsPath: string;
  SettingsContent: string;
  SettingsLines: TArrayOfString;
  JsonLines: TArrayOfString;
  LineIndex: Integer;
  ConfiguredServerUrl: string;
begin
  ServerUrl := Trim(ExpandConstant('{param:SERVERURL|}'));
  ApiKey := Trim(ExpandConstant('{param:APIKEY|}'));
  SettingsSource := Trim(ExpandConstant('{param:AGENTSETTINGSFILE|}'));

  if (ServerUrl = '') and (ApiKey = '') and (SettingsSource = '') then
    Exit;

  SettingsDirectory := ExpandConstant('{commonappdata}\BerTool');
  SettingsPath := SettingsDirectory + '\agent-settings.json';
  ForceDirectories(SettingsDirectory);

  if SettingsSource <> '' then
  begin
    SettingsSource := ExpandConstant(SettingsSource);
    if not FileExists(SettingsSource) then
    begin
      MsgBox(
        'Belirtilen ajan ayar dosyası bulunamadı:' + #13#10 + SettingsSource,
        mbError,
        MB_OK);
      Exit;
    end;

    if not LoadStringsFromFile(SettingsSource, SettingsLines) then
    begin
      MsgBox(
        'Belirtilen ajan ayar dosyası okunamadı:' + #13#10 + SettingsSource,
        mbError,
        MB_OK);
      Exit;
    end;

    SettingsContent := '';
    for LineIndex := 0 to GetArrayLength(SettingsLines) - 1 do
      SettingsContent := SettingsContent + SettingsLines[LineIndex] + #13#10;

    ConfiguredServerUrl := ExtractServerUrl(SettingsContent);
    if (ConfiguredServerUrl <> '') and IsInsecureLanUrl(ConfiguredServerUrl) then
    begin
      MsgBox(
        'Ajan ayar dosyası yerel ağa açık bir HTTP sunucu adresi içeriyor:' + #13#10 +
        ConfiguredServerUrl + #13#10 + #13#10 +
        'Güvenlik nedeniyle uzak sunucular için HTTPS zorunludur. ' +
        'Dosyadaki serverUrl değerini HTTPS olarak güncelleyin.',
        mbError,
        MB_OK);
      Exit;
    end;

    if not CopyFile(SettingsSource, SettingsPath, False) then
    begin
      MsgBox(
        'Ajan ayar dosyası kopyalanamadı:' + #13#10 + SettingsSource,
        mbError,
        MB_OK);
      Exit;
    end;

    HardenAgentSettingsFile(SettingsPath);
    Exit;
  end;

  if (ServerUrl = '') or (ApiKey = '') then
  begin
    MsgBox(
      'Sunucu raporlaması için /SERVERURL ve /APIKEY parametreleri birlikte ' +
      'verilmelidir. Alternatif olarak /AGENTSETTINGSFILE kullanılabilir.' + #13#10 + #13#10 +
      'Ajan ayarı yazılmadı; mevcut ayar dosyası varsa korundu.',
      mbError,
      MB_OK);
    Exit;
  end;

  ServerUrl := TrimTrailingSlashes(ServerUrl);
  if (Lowercase(Copy(ServerUrl, 1, 7)) <> 'http://') and
     (Lowercase(Copy(ServerUrl, 1, 8)) <> 'https://') then
  begin
    MsgBox(
      'Sunucu adresi http:// veya https:// ile başlamalıdır:' + #13#10 + ServerUrl,
      mbError,
      MB_OK);
    Exit;
  end;

  if IsInsecureLanUrl(ServerUrl) then
  begin
    MsgBox(
      'Güvenlik nedeniyle yerel ağa açık bir HTTP sunucu adresi kullanılamaz:' +
      ServerUrl + #13#10 + #13#10 +
      'Yerel olmayan sunucular için HTTPS adresi kullanın. ' +
      'Yalnızca bu makinede çalışan sunucu için http://127.0.0.1 veya ' +
      'http://localhost kullanılabilir.',
      mbError,
      MB_OK);
    Exit;
  end;

  SetArrayLength(JsonLines, 6);
  JsonLines[0] := '{';
  JsonLines[1] := '  "enabled": true,';
  JsonLines[2] := '  "serverUrl": "' + JsonEscape(ServerUrl) + '",';
  JsonLines[3] := '  "apiKey": "' + JsonEscape(ApiKey) + '",';
  JsonLines[4] := '  "reportIntervalSeconds": 300';
  JsonLines[5] := '}';

  if not SaveStringsToUTF8File(SettingsPath, JsonLines, False) then
  begin
    MsgBox(
      'Ajan ayar dosyası yazılamadı:' + #13#10 + SettingsPath,
      mbError,
      MB_OK);
    Exit;
  end;

  HardenAgentSettingsFile(SettingsPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RestoreAgentData();
    ConfigureAgentReporting();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    // Ayar ve önbellek dosyaları kopyalanmadan önce ajanı durdur.
    StopAgentProcess();
    BackupAgentData();
  end;
end;

