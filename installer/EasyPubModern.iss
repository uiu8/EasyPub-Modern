#define AppName "EasyPub Modern"
#define AppVersion "1.1.1"
#define AppPublisher "uiu8"
#define AppExeName "EasyPub.Desktop.exe"
#define PublishDir "..\outputs\EasyPubModern-v1.1.1-win-x64"

[Setup]
AppId={{8F564AA1-31F4-4EA5-BFF7-BBD76C35B5F4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/uiu8/EasyPub-Modern
AppSupportURL=https://github.com/uiu8/EasyPub-Modern/issues
AppUpdatesURL=https://github.com/uiu8/EasyPub-Modern/releases/latest
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={code:GetDefaultInstallDir}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\outputs
OutputBaseFilename=EasyPubModern-Setup-v{#AppVersion}-x64
SetupIconFile=..\src\EasyPub.Desktop\Assets\EasyPubModern.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=115
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
ShowLanguageDialog=auto

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function GetDefaultInstallDir(Param: String): String;
begin
  if DirExists('D:\software') then
    Result := 'D:\software\EasyPub Modern'
  else
    Result := ExpandConstant('{localappdata}\Programs\EasyPub Modern');
end;
