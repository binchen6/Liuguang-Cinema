#define MyAppName "流光影院"
#ifndef MyAppVersion
  #define MyAppVersion "1.8.1"
#endif
#ifndef PublishRoot
  #define PublishRoot "..\artifacts\update-patch"
#endif
#ifndef MyAppId
  #define MyAppId "{{C5F4C5D7-89F2-4D33-A0FC-34DC04A7A60D}"
#endif
#define MyAppPublisher "Liuguang Cinema Contributors"
#define MyAppURL "https://github.com/binchen6/Liuguang-Cinema"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion} 增量更新
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\流光影院
UsePreviousAppDir=yes
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
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{app}\TV.exe"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall
