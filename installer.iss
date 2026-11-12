; BrightFlyout installer - packages the self-contained single-file exe.
; Build: ISCC.exe installer.iss /DAppVersion=1.0.2 /DPublishDir=bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish"
#endif

[Setup]
AppName=BrightFlyout
AppVersion={#AppVersion}
AppPublisher=pawdy3
AppId={{3F6B2A11-9C4E-4A5D-8E2F-1C7D9B0A6E55}
; Per-user install: no admin required, no UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\BrightFlyout
DefaultGroupName=BrightFlyout
OutputDir=installer-out
OutputBaseFilename=BrightFlyoutSetup-{#AppVersion}
UninstallDisplayIcon={app}\BrightFlyout.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Run BrightFlyout when Windows starts"; GroupDescription: "Additional tasks:"

[Files]
Source: "{#PublishDir}\BrightFlyout.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\BrightFlyout"; Filename: "{app}\BrightFlyout.exe"
Name: "{group}\Uninstall BrightFlyout"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "BrightFlyout"; ValueData: """{app}\BrightFlyout.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\BrightFlyout.exe"; Description: "Launch BrightFlyout"; Flags: nowait postinstall skipifsilent
