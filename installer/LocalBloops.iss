; Inno Setup script for LocalBloops. Build with installer\build-installer.ps1, which publishes the
; app and passes /DAppVersion and /DPublishDir. Per-user install: no admin rights needed.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName "LocalBloops"
#define AppExe "LocalBloops.exe"

[Setup]
; Keep AppId fixed forever: it's how upgrades find the existing install.
AppId={{CD121C75-AD40-4F2C-83FF-19485F7CCF62}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=githuian
AppPublisherURL=https://github.com/githuian/LocalBloops
AppSupportURL=https://github.com/githuian/LocalBloops/issues
AppUpdatesURL=https://github.com/githuian/LocalBloops/releases
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\LocalBloops\Assets\LocalBloops.ico
UninstallDisplayName={#AppName}
; Close a running LocalBloops (it lives in the tray) before replacing its files.
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start LocalBloops with &Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same Run value the app's own "Start with Windows" tray option uses, so the two stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopLocalBloops"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { The startup entry may have been added from the tray menu rather than by this installer. }
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
end;
