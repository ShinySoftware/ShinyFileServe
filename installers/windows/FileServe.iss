; The Windows installer. Built by the release workflow, once per architecture:
;
;   iscc /DAppVersion=1.2.0 /DFileVersion=1.2.0.0 /DArch=x64 /DSourceDir=<publish dir> /DOutputDir=<dir> FileServe.iss
;
; Per-user, into %LOCALAPPDATA%\Programs\FileServe, for three reasons: nobody needs to be an
; administrator to install it; "Start at login" writes this exe's path into the HKCU Run key, so the
; path has to stay put; and the app has to be able to write to its own folder to update itself.
; The updater finds this installer by name and runs it with /VERYSILENT once the app has quit.

#ifndef AppVersion
  #error Pass /DAppVersion=<version>
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<publish output>
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppExe "ShinyFileServe.Windows.exe"

[Setup]
; Never change this: it is how an upgrade finds the copy it is replacing.
AppId={{32A68B8C-D77E-4B1B-B8BE-60FEA199F29F}
AppName=FileServe
AppVersion={#AppVersion}
AppVerName=FileServe {#AppVersion}
AppPublisher=Shiny Software
AppPublisherURL=https://github.com/ShinySoftware/ShinyFileServe
AppSupportURL=https://github.com/ShinySoftware/ShinyFileServe/issues
AppUpdatesURL=https://github.com/ShinySoftware/ShinyFileServe/releases
VersionInfoVersion={#FileVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\FileServe
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=FileServe
OutputDir={#OutputDir}
OutputBaseFilename=FileServe-{#AppVersion}-windows-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The tray app keeps running with its window closed. Restart Manager closes it for a manual
; upgrade; an update from inside the app has already quit before this runs.
CloseApplications=yes
RestartApplications=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\FileServe"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\FileServe"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Not written here - the app writes it when "Start at login" is turned on. Only removed on uninstall,
; so an uninstalled app is not started at the next sign-in.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "net.shinylib.fileserve"; ValueType: none; Flags: uninsdeletevalue

[Run]
; Without skipifsilent on purpose: an update runs this installer silently, and the app it replaced
; should come back.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,FileServe}"; Flags: nowait postinstall

[UninstallRun]
; The server keeps running behind the tray icon, which would hold its files open.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopFileServe"

; The data folder (%APPDATA%\ShinyFileServe - accounts, shares, the log) is left alone: an uninstall
; to reinstall should not take every account with it.
