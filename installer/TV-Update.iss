#define MyAppName "流光影院"
#ifndef MyAppVersion
  #define MyAppVersion "1.9.1"
#endif
#ifndef MyBaseVersion
  #define MyBaseVersion ""
#endif
#ifndef PublishRoot
  #define PublishRoot "..\artifacts\update-patch"
#endif
#ifndef MyAppId
  #define MyAppId "{{C5F4C5D7-89F2-4D33-A0FC-34DC04A7A60D}"
#endif
#ifndef MyAppIdRaw
  #define MyAppIdRaw "{C5F4C5D7-89F2-4D33-A0FC-34DC04A7A60D}"
#endif
#define MyAppPublisher "Liuguang Cinema Contributors"
#define MyAppURL "https://github.com/binchen6/Liuguang-Cinema"
#define MyAppExeName "TV.exe"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion} 增量更新
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\流光影院
DefaultGroupName={#MyAppName}
UsePreviousAppDir=yes
UsePreviousGroup=yes
PrivilegesRequired=lowest
Uninstallable=no
CreateUninstallRegKey=no
OutputBaseFilename=TV-Update-x64
InternalCompressLevel=none
SolidCompression=no
Compression=lzma2/fast
LZMADictionarySize=4096
LZMANumFastBytes=32
LZMAUseSeparateProcess=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\TV\Assets\App.ico
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName} (Liuguang Cinema)
VersionInfoDescription={#MyAppName}增量更新程序
VersionInfoCopyright=Copyright (c) {#MyAppPublisher}
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter=TV.exe,TV.dll,java.exe,catvod-engine.jar
RestartApplications=no
SetupLogging=no
#ifdef EnableSignTool
SignTool=signtool
#endif

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"

[Files]
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

#ifdef DeleteInclude
#include DeleteInclude
#endif

[Registry]
; 与完整安装包保持文件关联及图标路径完全同步（不设置 uninsdelete，避免破坏原卸载记录）
Root: HKCU; Subkey: "Software\Classes\TV.Video"; ValueType: string; ValueData: "流光影院视频"; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\TV.Video\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\TV.Video\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.mts\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.flv\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.f4v\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.asf\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.mpg\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.mpeg\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.vob\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.3gp\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.3g2\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.rm\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.rmvb\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget
Root: HKCU; Subkey: "Software\Classes\.ogv\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Check: RegisteredInstallTarget

[Icons]
; 仅重写已存在的开始菜单 / 桌面快捷方式以强制刷新软件图标，绝不擅自创建用户未勾选的快捷方式
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; IconIndex: 0; Check: GroupShortcutExists
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; IconIndex: 0; Check: DesktopShortcutExists

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall

[Code]
const
  GenericWrite = $40000000;
  DeleteAccess = $00010000;
  CreateNew = 1;
  OpenExisting = 3;
  DeleteOnClose = $04000000;
  AccessDenied = 5;
  SHCNE_UPDATEITEM = $00002000;
  SHCNE_ASSOCCHANGED = $08000000;
  SHCNF_IDLIST = $0000;
  SHCNF_PATHW = $0005;
  SHCNF_FLUSH = $1000;

function CreateFileW(const FileName: String; DesiredAccess, ShareMode: Cardinal;
  SecurityAttributes: THandle; CreationDisposition, FlagsAndAttributes: Cardinal;
  TemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';

function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

procedure SHChangeNotify(EventId: LongInt; Flags: LongWord; Item1, Item2: LongWord);
  external 'SHChangeNotify@shell32.dll stdcall';

procedure SHChangeNotifyPath(EventId: LongInt; Flags: LongWord; const Item1: String; Item2: LongWord);
  external 'SHChangeNotify@shell32.dll stdcall';

function RegisteredInstallTarget: Boolean;
var
  InstalledPath: String;
begin
  Result := RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppIdRaw}_is1',
    'InstallLocation', InstalledPath) and
    (CompareText(AddBackslash(InstalledPath), AddBackslash(ExpandConstant('{app}'))) = 0);
end;

function GroupShortcutExists: Boolean;
begin
  Result := RegisteredInstallTarget and FileExists(ExpandConstant('{group}\{#MyAppName}.lnk'));
end;

function DesktopShortcutExists: Boolean;
begin
  Result := RegisteredInstallTarget and FileExists(ExpandConstant('{autodesktop}\{#MyAppName}.lnk'));
end;

procedure UpdateExistingUninstallMetadata;
var
  KeyPath, VersionStr, MajorPart, MinorPart, Rest: String;
  DotPos, MajorNum, MinorNum: Integer;
begin
  KeyPath := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppIdRaw}_is1';
  if not RegisteredInstallTarget then exit;

  VersionStr := '{#MyAppVersion}';
  RegWriteStringValue(HKCU, KeyPath, 'DisplayName', '{#MyAppName} ' + VersionStr);
  RegWriteStringValue(HKCU, KeyPath, 'DisplayVersion', VersionStr);
  RegWriteStringValue(HKCU, KeyPath, 'DisplayIcon', ExpandConstant('{app}\{#MyAppExeName}'));
  RegWriteStringValue(HKCU, KeyPath, 'InstallDate', GetDateTimeString('yyyymmdd', #0, #0));

  DotPos := Pos('.', VersionStr);
  if DotPos > 1 then
  begin
    MajorPart := Copy(VersionStr, 1, DotPos - 1);
    Rest := Copy(VersionStr, DotPos + 1, Length(VersionStr));
    DotPos := Pos('.', Rest);
    if DotPos > 1 then MinorPart := Copy(Rest, 1, DotPos - 1) else MinorPart := Rest;
    MajorNum := StrToIntDef(MajorPart, -1);
    MinorNum := StrToIntDef(MinorPart, -1);
    if MajorNum >= 0 then
    begin
      RegWriteDWordValue(HKCU, KeyPath, 'MajorVersion', MajorNum);
      RegWriteDWordValue(HKCU, KeyPath, 'VersionMajor', MajorNum);
    end;
    if MinorNum >= 0 then
    begin
      RegWriteDWordValue(HKCU, KeyPath, 'MinorVersion', MinorNum);
      RegWriteDWordValue(HKCU, KeyPath, 'VersionMinor', MinorNum);
    end;
  end;
end;

procedure RefreshShellIcons;
var
  ExePath, IcoPath, GroupLink, DesktopLink, TaskbarLink: String;
begin
  ExePath := ExpandConstant('{app}\{#MyAppExeName}');
  IcoPath := ExpandConstant('{app}\Assets\App.ico');
  GroupLink := ExpandConstant('{group}\{#MyAppName}.lnk');
  DesktopLink := ExpandConstant('{autodesktop}\{#MyAppName}.lnk');
  TaskbarLink := ExpandConstant('{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\{#MyAppName}.lnk');

  if FileExists(ExePath) then
    SHChangeNotifyPath(SHCNE_UPDATEITEM, SHCNF_PATHW or SHCNF_FLUSH, ExePath, 0);
  if FileExists(IcoPath) then
    SHChangeNotifyPath(SHCNE_UPDATEITEM, SHCNF_PATHW or SHCNF_FLUSH, IcoPath, 0);
  if FileExists(GroupLink) then
    SHChangeNotifyPath(SHCNE_UPDATEITEM, SHCNF_PATHW or SHCNF_FLUSH, GroupLink, 0);
  if FileExists(DesktopLink) then
    SHChangeNotifyPath(SHCNE_UPDATEITEM, SHCNF_PATHW or SHCNF_FLUSH, DesktopLink, 0);
  if FileExists(TaskbarLink) then
    SHChangeNotifyPath(SHCNE_UPDATEITEM, SHCNF_PATHW or SHCNF_FLUSH, TaskbarLink, 0);

  SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST or SHCNF_FLUSH, 0, 0);
end;

procedure WaitForAppExecutableUnlock;
var
  ExePath: String;
  Handle: THandle;
  Attempt: Integer;
begin
  ExePath := ExpandConstant('{app}\{#MyAppExeName}');
  if not FileExists(ExePath) then exit;
  for Attempt := 1 to 50 do
  begin
    Handle := CreateFileW(ExePath, GenericWrite, 0, 0, OpenExisting, 0, 0);
    if Handle <> THandle(-1) then
    begin
      CloseHandle(Handle);
      exit;
    end;
    Sleep(160);
  end;
end;

function CheckInstallDirectory: String;
var
  Directory, ProbePath, InstalledVersion: String;
  Handle: THandle;
  ErrorCode: Integer;
begin
  Result := '';
  Directory := ExpandConstant('{app}');
  if not GetVersionNumbersString(AddBackslash(Directory) + 'TV.dll', InstalledVersion) then
  begin
    Result := '此目录未安装流光影院。请使用完整安装包。';
    exit;
  end;
  if ('{#MyBaseVersion}' <> '') and (InstalledVersion <> '{#MyBaseVersion}.0') and
    (InstalledVersion <> '{#MyAppVersion}.0') then
  begin
    Result := '此增量包适用于流光影院 {#MyBaseVersion}。当前版本是 ' + InstalledVersion + '。请使用完整安装包。';
    exit;
  end;

  ProbePath := AddBackslash(Directory) + 'tv-update-check-' + ExtractFileName(ExpandConstant('{tmp}'));
  Handle := CreateFileW(ProbePath, GenericWrite or DeleteAccess, 0, 0, CreateNew, DeleteOnClose, 0);
  if Handle <> THandle(-1) then
  begin
    CloseHandle(Handle);
    exit;
  end;

  ErrorCode := DLLGetLastError;
  Result := '无法写入安装目录“' + Directory + '”。' + #13#10 +
    'Windows 错误 ' + IntToStr(ErrorCode) + '（' + SysErrorMessage(ErrorCode) + '）';
  if ErrorCode = AccessDenied then
    Result := Result + #13#10 + '如果更新程序在受限环境中运行，请从 Windows 资源管理器重新打开更新包，或确保安装目录可写。';
  Log(Result);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  WaitForAppExecutableUnlock;
  Result := CheckInstallDirectory;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    UpdateExistingUninstallMetadata;
    RefreshShellIcons;
  end;
end;
