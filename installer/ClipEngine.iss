; Clip Engine's Windows installer (Inno Setup 6). The Build workflow compiles it from the published app in dist\:
;   iscc /DAppVersion=0.13.0 /DSourceDir=..\dist installer\ClipEngine.iss
; It installs for the current Windows account only (no admin prompt) into %LOCALAPPDATA%\Programs\Clip Engine, so the
; app's own updater can still swap its files in place. Settings and clips live elsewhere and are kept on uninstall.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist"
#endif

[Setup]
; Never change the AppId: Windows uses it to know an install is the same app (upgrades, Apps & features).
AppId={{6F1B8E2A-4C7D-4E59-9A3B-CE1A2D7F5B10}
AppName=Clip Engine
AppVersion={#AppVersion}
AppVerName=Clip Engine {#AppVersion}
AppPublisher=Clip Engine
AppPublisherURL=https://mochiboy9.github.io/Clip_Engine/
AppSupportURL=https://github.com/MochiBoy9/Clip_Engine/issues
AppUpdatesURL=https://mochiboy9.github.io/Clip_Engine/
DefaultDirName={autopf}\Clip Engine
DefaultGroupName=Clip Engine
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.18362
SetupIconFile=..\ui\ClipEngine.Desktop\Assets\ClipEngine.ico
UninstallDisplayIcon={app}\ClipEngine.exe
UninstallDisplayName=Clip Engine
OutputDir=..\installer-out
OutputBaseFilename=Clip_Engine-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Put Clip Engine on the desktop"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Clip Engine"; Filename: "{app}\ClipEngine.exe"
Name: "{autodesktop}\Clip Engine"; Filename: "{app}\ClipEngine.exe"; Tasks: desktopicon

[Registry]
; clipengine:// links (squad invites). The app also registers these on each start; this lets the uninstaller remove them.
Root: HKCU; Subkey: "Software\Classes\clipengine"; ValueType: string; ValueName: ""; ValueData: "URL:Clip Engine"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\clipengine"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\clipengine\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\ClipEngine.exe"",0"
Root: HKCU; Subkey: "Software\Classes\clipengine\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ClipEngine.exe"" ""%1"""

[Run]
Filename: "{app}\ClipEngine.exe"; Description: "Start Clip Engine"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM ClipEngine.exe /F & taskkill /IM clip_engine.exe /F"; Flags: runhidden; RunOnceId: "StopEngine"

[UninstallDelete]
; Files the in-app updater moved aside (*.old) or added after install.
Type: filesandordirs; Name: "{app}"
