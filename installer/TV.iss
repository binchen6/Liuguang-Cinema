#define MyAppName "流光影院"
#ifndef MyAppVersion
  #define MyAppVersion "1.8.0"
#endif
#ifndef MyAppId
  #define MyAppId "{{C5F4C5D7-89F2-4D33-A0FC-34DC04A7A60D}"
#endif
#ifndef PublishRoot
  #define PublishRoot "..\artifacts\publish"
#endif
#define MyAppPublisher "Liuguang Cinema Contributors"
#define MyAppURL "https://github.com/binchen6/Liuguang-Cinema"
#define MyAppExeName "TV.exe"
#ifndef UninstallRegKey
  #define UninstallRegKey "yes"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\流光影院
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
PrivilegesRequired=lowest
CreateUninstallRegKey={#UninstallRegKey}
OutputBaseFilename=TV-Setup-x64
; Instant-launch installer tuning (non-regression enforced by scripts/Test-InstallerLaunchSpeed.ps1):
; - InternalCompressLevel=none: zero-overhead SetupLdr unpacking (<10ms)
; - SolidCompression=no: avoids whole-archive sequential scan/seek on launch
; - lzma2/fast + 4MB dictionary: fast decompression & lean download size
InternalCompressLevel=none
SolidCompression=no
Compression=lzma2/fast
LZMADictionarySize=4096
LZMANumFastBytes=32
LZMAUseSeparateProcess=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\TV\Assets\App.ico
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName} (Liuguang Cinema)
VersionInfoDescription={#MyAppName}安装程序
VersionInfoCopyright=Copyright (c) {#MyAppPublisher}
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter=TV.exe,TV.dll,java.exe,catvod-engine.jar
RestartApplications=no
SetupLogging=no
#ifdef EnableSignTool
SignTool=signtool
SignedUninstaller=yes
#endif

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; Already-compressed payloads stored without re-compression for faster install & lower CPU overhead
Source: "{#PublishRoot}\runtime\catvod-engine.jar"; DestDir: "{app}\runtime"; Flags: ignoreversion nocompression
Source: "{#PublishRoot}\runtime\java\lib\modules"; DestDir: "{app}\runtime\java\lib"; Flags: ignoreversion nocompression
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Excludes: "runtime\catvod-engine.jar,runtime\java\lib\modules"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKCU; Subkey: "Software\Classes\TV.Video"; ValueType: string; ValueData: "流光影院视频"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\TV.Video\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\TV.Video\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mts\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.flv\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.f4v\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.asf\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mpg\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mpeg\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.vob\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.3gp\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.3g2\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.rm\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.rmvb\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.ogv\OpenWithProgids"; ValueType: string; ValueName: "TV.Video"; ValueData: ""; Flags: uninsdeletevalue

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; IconIndex: 0
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; IconIndex: 0; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent unchecked

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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then RefreshShellIcons;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST or SHCNF_FLUSH, 0, 0);
end;

function CheckInstallDirectory: String;
var
  Directory, ProbePath: String;
  Handle: THandle;
  ErrorCode: Integer;
begin
  Result := '';
  Directory := ExpandConstant('{app}');
  if not DirExists(Directory) then exit;

  { Inno stages replacements in the destination directory, including the uninstaller. }
  ProbePath := AddBackslash(Directory) + 'tv-setup-check-' + ExtractFileName(ExpandConstant('{tmp}'));
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
    Result := Result + #13#10 + '如果安装程序在受限环境中运行，请从 Windows 资源管理器重新打开安装包，或选择当前用户可写的目录。';
  Log(Result);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  WaitForAppExecutableUnlock;
  Result := CheckInstallDirectory;
end;
