#define MyAppName "流光影院"
#ifndef MyAppVersion
  #define MyAppVersion "1.1.9"
#endif
#ifndef MyAppId
  #define MyAppId "{{C5F4C5D7-89F2-4D33-A0FC-34DC04A7A60D}"
#endif
#ifndef PublishRoot
  #define PublishRoot "..\artifacts\publish"
#endif
#define MyAppPublisher "TV"
#define MyAppExeName "TV.exe"
#ifndef UninstallRegKey
  #define UninstallRegKey "yes"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\流光影院
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
PrivilegesRequired=lowest
CreateUninstallRegKey={#UninstallRegKey}
OutputBaseFilename=TV-Setup-x64
Compression=lzma2/normal
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\TV\Assets\App.ico
VersionInfoVersion={#MyAppVersion}.0
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter=TV.exe,TV.dll,java.exe,catvod-engine.jar
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "..\artifacts\prerequisites\vc_redist.x64.exe"; Flags: dontcopy
Source: "..\artifacts\prerequisites\MicrosoftEdgeWebView2Setup.exe"; Flags: dontcopy solidbreak
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs solidbreak

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
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
const
  GenericWrite = $40000000;
  DeleteAccess = $00010000;
  CreateNew = 1;
  DeleteOnClose = $04000000;
  AccessDenied = 5;

function CreateFileW(const FileName: String; DesiredAccess, ShareMode: Cardinal;
  SecurityAttributes: THandle; CreationDisposition, FlagsAndAttributes: Cardinal;
  TemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';

function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

procedure SHChangeNotify(EventId: LongInt; Flags: LongWord; Item1, Item2: LongWord);
  external 'SHChangeNotify@shell32.dll stdcall';

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then SHChangeNotify($08000000, 0, 0, 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then SHChangeNotify($08000000, 0, 0, 0);
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

function VCRedistNeeded: Boolean;
var
  Installed: Cardinal;
begin
  Result := not RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) or (Installed <> 1);
end;

function WebView2Present(): Boolean;
var
  ClientKey, Version: String;
begin
  ClientKey := 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  Result := (RegQueryStringValue(HKLM32, ClientKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, ClientKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := CheckInstallDirectory;
  if Result <> '' then exit;
  if VCRedistNeeded then
  begin
    ExtractTemporaryFile('vc_redist.x64.exe');
    if not ShellExec('runas', ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    begin
      Result := '无法启动 Visual C++ 运行库安装程序。';
      exit;
    end;
    if Code = 3010 then
    begin
      NeedsRestart := True;
      Result := 'Visual C++ 运行库需要重启 Windows。';
      exit;
    end;
    if VCRedistNeeded then
    begin
      Result := 'Visual C++ 运行库未安装成功（' + IntToStr(Code) + '）。';
      exit;
    end;
  end;
  if not WebView2Present() then
  begin
    ExtractTemporaryFile('MicrosoftEdgeWebView2Setup.exe');
    if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebView2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Result := '无法启动 WebView2 安装程序。'
    else if not WebView2Present() then
      Result := 'WebView2 安装未完成（' + IntToStr(Code) + '）。请检查网络连接。';
  end;
end;
