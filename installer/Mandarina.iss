; Mandarina installer (Inno Setup 6). Build it with tools/mandarina/package-mandarina.ps1, which publishes the app and runs
;   ISCC.exe /DAppVersion=0.1.0 /DPublishDir=<published folder, launcher already named Mandarina.exe> /O<output> installer\Mandarina.iss
;
; It does what tools/mandarina/install-mandarina.ps1 does, for the current user and without administrator rights:
;  - copies the program to %LOCALAPPDATA%\Programs\Mandarina and adds a Start menu shortcut;
;  - optionally puts the "mandarina" command line on the user's PATH (the "cli" folder);
;  - closes a running Mandarina first ("Mandarina.exe --quit") and starts it at the end.
; The program registers "start with Windows" and the "Convert with Mandarina" menu entry itself (Settings -> General); the
; uninstaller runs "Mandarina.exe --uninstall-cleanup" to remove them (and the engines it downloaded for this user).
; The setup is not code-signed: see "Unsigned downloads" in the README.

#ifndef AppVersion
  #error AppVersion is not defined: build with tools/mandarina/package-mandarina.ps1
#endif
#ifndef PublishDir
  #error PublishDir is not defined: build with tools/mandarina/package-mandarina.ps1
#endif

#define AppName "Mandarina"
#define AppExe "Mandarina.exe"

[Setup]
; Never change: Windows finds the installed Mandarina by this id.
AppId={{6F0B2C7E-4A1D-4E58-9B63-2D7A5C1E8F40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Ignacio Insua
AppPublisherURL=https://github.com/ignFT37/Mandarina
AppSupportURL=https://github.com/ignFT37/Mandarina/issues
AppUpdatesURL=https://github.com/ignFT37/Mandarina/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
SetupIconFile=..\src\Filee.App\Assets\Icons\filee.ico
UninstallDisplayIcon={app}\{#AppExe}
OutputBaseFilename=Mandarina-{#AppVersion}-win-x64-setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
CloseApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
en.AddCli=Add the "mandarina" command line to the PATH (open a new terminal afterwards)
es.AddCli=Añadir la línea de comandos "mandarina" al PATH (abre una terminal nueva después)
en.LaunchApp=Start Mandarina
es.LaunchApp=Abrir Mandarina

[Tasks]
Name: "cli"; Description: "{cm:AddCli}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Convert and tweak files by dragging them"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-cleanup"; RunOnceId: "Cleanup"; Flags: runhidden waituntilterminated

[Code]
const
  EnvKey = 'Environment';

function CliDir: string;
begin
  Result := ExpandConstant('{app}\cli');
end;

// True when the user's PATH already lists Dir (compared whole, ignoring case).
function PathContains(Dir: string): Boolean;
var
  Paths: string;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Paths) then
    Paths := '';
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0;
end;

procedure AddToPath(Dir: string);
var
  Paths: string;
begin
  if PathContains(Dir) then
    exit;
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Paths) then
    Paths := '';
  if (Paths <> '') and (Paths[Length(Paths)] <> ';') then
    Paths := Paths + ';';
  RegWriteExpandStringValue(HKCU, EnvKey, 'Path', Paths + Dir);
end;

procedure RemoveFromPath(Dir: string);
var
  Paths: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Paths) then
    exit;
  Paths := ';' + Paths + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Paths));
  if P = 0 then
    exit;
  Delete(Paths, P, Length(Dir) + 1);
  Paths := Copy(Paths, 2, Length(Paths) - 2);
  RegWriteExpandStringValue(HKCU, EnvKey, 'Path', Paths);
end;

// A running Mandarina lives in the tray and keeps its files open: ask it to exit before they are replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: string;
  Code: Integer;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('cli') then
    AddToPath(CliDir);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(CliDir);
end;
