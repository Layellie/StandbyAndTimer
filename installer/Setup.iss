; StandbyAndTimer — Inno Setup installer script
;
; Prerequisites:
;   1) Publish first:
;        dotnet publish StandbyAndTimer\StandbyAndTimer.csproj ^
;          -p:PublishProfile=win-x64-single
;   2) Install Inno Setup 6:  https://jrsoftware.org/isdl.php
;   3) Compile this script:
;        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\Setup.iss
;
; Output: installer\dist\StandbyAndTimer_Setup_<version>.exe

#define AppName        "StandbyAndTimer"
#ifndef AppVersion
  #define AppVersion   "1.0.0"
#endif
#define AppPublisher   "LAYE77IE"
#define AppExeName     "StandbyAndTimer.exe"
#define AutoStartTask  "StandbyAndTimer_AutoStart"
#define SourceDir      "..\StandbyAndTimer\bin\publish\win-x64"

[Setup]
AppId={{A8F3D2B4-1E5C-4A6F-9D2B-8E7C3F5D1A20}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/Layellie/StandbyAndTimer
AppSupportURL=https://github.com/Layellie/StandbyAndTimer/issues
AppUpdatesURL=https://github.com/Layellie/StandbyAndTimer/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=dist
OutputBaseFilename=StandbyAndTimer_Setup_{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
SetupIconFile=..\StandbyAndTimer\app_icon.ico
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon";  Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart";    Description: "Launch on Windows startup (recommended)"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; If the publish output ever produces additional files (debug pdb, etc), add them here.

[Registry]
; Mark the per-user settings hive for deletion on uninstall. `dontcreatekey` keeps
; install from creating an empty key; the app writes its own values at runtime.
Root: HKCU; Subkey: "SOFTWARE\StandbyAndTimer"; Flags: dontcreatekey uninsdeletekey

[Icons]
Name: "{group}\{#AppName}";                Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";          Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser

; Register a Task Scheduler "logon" entry if user opted in. Uses the same
; task name the app's AutoStartService writes to, so the in-app toggle and the
; installer toggle stay consistent (no duplicate logon entries).
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/Create /F /SC ONLOGON /RL HIGHEST /TN ""{#AutoStartTask}"" /TR ""\""{app}\{#AppExeName}\"" -hidden"""; \
  Flags: runhidden; Tasks: autostart

[UninstallRun]
; Stop the running app (tray icon) before removing files. /T includes any child
; processes; redirecting to NUL keeps the uninstall log clean if not running.
Filename: "{sys}\taskkill.exe"; \
  Parameters: "/F /IM {#AppExeName} /T"; \
  Flags: runhidden; RunOnceId: "KillRunningApp"
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/Delete /F /TN ""{#AutoStartTask}"""; \
  Flags: runhidden; RunOnceId: "DeleteAutoStartTask"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
