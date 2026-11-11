#ifndef PublishDir
  #error PublishDir must be supplied by the build script
#endif
#ifndef PlatformToolsDir
  #error PlatformToolsDir must be supplied by the build script
#endif
#ifndef DotnetDir
  #error DotnetDir must be supplied by the build script
#endif

[Setup]
AppId={{6D8E1F99-42C9-45D7-93F8-0D2BE4605F01}
AppName=ScreenKey Bridge
AppVersion=0.3.0
AppPublisher=Deanx270
AppPublisherURL=https://github.com/Deanx270/screenkey-bridge
SetupIconFile=..\windows\ScreenKeyBridge\screenkey.ico
UninstallDisplayIcon={app}\ScreenKeyBridge.exe
DefaultDirName={localappdata}\Programs\ScreenKey Bridge
DefaultGroupName=ScreenKey Bridge
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=ScreenKeyBridge.User.{username}
OutputBaseFilename=ScreenKeyBridge-Windows-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "{#DotnetDir}\LICENSE.txt"; DestDir: "{app}\licenses\dotnet"; Flags: ignoreversion
Source: "{#DotnetDir}\ThirdPartyNotices.txt"; DestDir: "{app}\licenses\dotnet"; Flags: ignoreversion
Source: "{#PublishDir}\ScreenKeyBridge.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\edge-extension\*"; DestDir: "{app}\edge-extension"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PlatformToolsDir}\adb.exe"; DestDir: "{app}\platform-tools"; Flags: ignoreversion
Source: "{#PlatformToolsDir}\AdbWinApi.dll"; DestDir: "{app}\platform-tools"; Flags: ignoreversion
Source: "{#PlatformToolsDir}\AdbWinUsbApi.dll"; DestDir: "{app}\platform-tools"; Flags: ignoreversion
Source: "{#PlatformToolsDir}\libwinpthread-1.dll"; DestDir: "{app}\platform-tools"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PlatformToolsDir}\NOTICE.txt"; DestDir: "{app}\platform-tools"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\ScreenKey Bridge"; Filename: "{app}\ScreenKeyBridge.exe"

[Run]
Filename: "{app}\ScreenKeyBridge.exe"; Description: "Launch ScreenKey Bridge"; Flags: nowait postinstall skipifsilent
