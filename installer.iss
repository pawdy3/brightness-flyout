; BrightFlyout installer - framework-dependent payload + bootstraps missing
; prerequisites (.NET Desktop Runtime 10, Windows App SDK 1.8) with a UAC prompt
; only when something is actually missing.
; Build: ISCC.exe installer.iss /DAppVersion=1.0.3 /DFdPublishDir=bin\fd-publish

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef FdPublishDir
  #define FdPublishDir "bin\fd-publish"
#endif

[Setup]
AppName=BrightFlyout
AppVersion={#AppVersion}
AppPublisher=pawdy3
AppId={{3F6B2A11-9C4E-4A5D-8E2F-1C7D9B0A6E55}
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
Source: "{#FdPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\BrightFlyout"; Filename: "{app}\BrightFlyout.exe"
Name: "{group}\Uninstall BrightFlyout"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "BrightFlyout"; ValueData: """{app}\BrightFlyout.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\BrightFlyout.exe"; Description: "Launch BrightFlyout"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetRuntimeUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  WasdkRuntimeUrl = 'https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe';

function IsDotNetDesktop10Installed(): Boolean;
var
  names: TArrayOfString;
  i: Integer;
begin
  Result := False;
  if RegGetSubkeyNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', names) then
    for i := 0 to GetArrayLength(names) - 1 do
      if Copy(names[i], 1, 3) = '10.' then
      begin
        Result := True;
        exit;
      end;
end;

function IsWasdk18Installed(): Boolean;
var
  resultCode: Integer;
  checkFile: String;
  content: AnsiString;
begin
  Result := False;
  checkFile := ExpandConstant('{tmp}\bf-wasdk-check.txt');
  Exec('powershell.exe',
    '-NoProfile -NonInteractive -Command "if (Get-AppxPackage -Name Microsoft.WindowsAppRuntime.1.8*) { ''yes'' } | Out-File -FilePath ''' + checkFile + ''' -Encoding ascii"',
    '', SW_HIDE, ewWaitUntilTerminated, resultCode);
  if LoadStringFromFile(checkFile, content) then
    Result := Pos('yes', Lowercase(String(content))) > 0;
end;

function NoDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure DownloadAndInstall(const name, url, fileName, args: String);
var
  path: String;
  resultCode: Integer;
  launched: Boolean;
begin
  try
    DownloadTemporaryFile(url, fileName, '', @NoDownloadProgress);
  except
    MsgBox('Could not download ' + name + '. Please install it manually from:' #13#10 + url,
      mbError, MB_OK);
    exit;
  end;

  path := ExpandConstant('{tmp}\' + fileName);
  // Dependency installers require elevation; 'runas' triggers a UAC prompt.
  launched := ShellExec('runas', path, args, '', SW_HIDE, ewWaitUntilTerminated, resultCode);
  if not launched then
    MsgBox('Could not start the ' + name + ' installer.', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not IsDotNetDesktop10Installed() then
      DownloadAndInstall('.NET Desktop Runtime 10', DotNetRuntimeUrl,
        'windowsdesktop-runtime-win-x64.exe', '/install /quiet /norestart');
    if not IsWasdk18Installed() then
      DownloadAndInstall('Windows App SDK 1.8 Runtime', WasdkRuntimeUrl,
        'windowsappruntimeinstall-x64.exe', '--quiet --force');
  end;
end;
