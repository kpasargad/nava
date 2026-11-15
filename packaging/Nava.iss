; Inno Setup 6.3+ / 7. Build parameters are supplied by scripts/package.ps1.
; This installer never owns the application's per-user settings or history.
#ifndef SourceDir
  #error "SourceDir must point to a clean self-contained Nava publish directory."
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef AppPublisher
  #define AppPublisher "Kourosh Soleimani"
#endif
#ifndef RepoUrl
  #define RepoUrl "https://github.com/kpasargad/nava"
#endif

[Setup]
AppId={{A73BA990-5F13-49EF-8B20-F139DFD938B4}
AppName=Nava
AppVersion={#AppVersion}
AppVerName=Nava {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
DefaultDirName={localappdata}\Programs\Nava
DefaultGroupName=Nava
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
WizardSizePercent=110
SetupIconFile={#SourceDir}\nava.ico
UninstallDisplayIcon={app}\Nava.exe
UninstallDisplayName=Nava
OutputDir={#OutputDir}
OutputBaseFilename=Nava-Setup-{#AppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
AppMutex=Local\Nava.Desktop.v1
CloseApplications=yes
CloseApplicationsFilter=Nava.exe
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; Use an official compiler-bundled Persian translation when available.
#if FileExists(AddBackslash(CompilerPath) + "Languages\Persian.isl")
Name: "persian"; MessagesFile: "compiler:Languages\Persian.isl"
#elif FileExists(AddBackslash(CompilerPath) + "Languages\Farsi.isl")
Name: "persian"; MessagesFile: "compiler:Languages\Farsi.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The packaging script supplies a whitelist-cleaned directory. These exclusions
; also protect direct ISCC builds from accidentally including mutable user data.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "Data\*,.local\*,.git\*,History\*,Audio\*,Records\*,credentials.bin,settings.json,installed.flag,*.log,*.sqlite,*.sqlite3,*.db,*.wav,*.mp3,*.m4a,*.srt,*.pdb,.env,.env.*,unins*.exe,unins*.dat"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Nava"; Filename: "{app}\Nava.exe"; WorkingDir: "{app}"; AppUserModelID: "Nava.Desktop"
Name: "{autodesktop}\Nava"; Filename: "{app}\Nava.exe"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "Nava.Desktop"

[Run]
Filename: "{app}\Nava.exe"; Description: "{cm:LaunchProgram,Nava}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
Type: files; Name: "{app}\installed.flag"

[Code]
procedure RemoveOwnedStartupEntry;
var
  RunCommand, InstalledExecutable: String;
begin
  if not RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Run', 'Nava', RunCommand) then
    Exit;
  RunCommand := Trim(RunCommand);
  InstalledExecutable := ExpandConstant('{app}\Nava.exe');
  { Only this exact installed executable belongs to this uninstaller. Preserve
    a portable copy's startup entry and all settings/history in LocalAppData. }
  if (CompareText(RunCommand, InstalledExecutable) = 0) or
    (CompareText(RunCommand, '"' + InstalledExecutable + '"') = 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Nava');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveOwnedStartupEntry;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { Portable archives do not contain this marker. Installed copies use
      LocalAppData\Nava for all settings, credentials, recordings and history. }
    if not SaveStringToFile(ExpandConstant('{app}\installed.flag'),
      'nava-installed-v1' + #13#10, False) then
      RaiseException('Nava could not finish installation. Please choose a writable folder.');
  end;
end;
