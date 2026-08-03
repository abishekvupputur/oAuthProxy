; RavensPort installer.
;
; Why this exists at all: the Microsoft Store submission is the EXE/MSI type, and what was
; submitted before was the application itself. Running it starts a tray app; it does not install
; anything, so there was no Add or Remove Programs entry (Store policy 10.2.7), no Start menu
; shortcut and therefore no way to launch the app again after the tray menu's Exit (10.1.2.10),
; and the Store's own install step had nothing to detect (10.3.4). One missing installer, three
; findings.
;
; Per-user by design. PrivilegesRequired=lowest installs under the user's profile and writes the
; uninstall entry to HKCU, which means no elevation prompt — and a silent install that cannot
; raise UAC is a silent install that cannot fail on it. RavensPort has nothing that needs machine
; scope: its configuration lives in the user's password manager, and its session key is already
; bound to the Windows account.
;
; Build:  ISCC.exe installer\RavensPort.iss /DAppVersion=4.1.3
; The publish step must have run first — see SourceExe below.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "RavensPort"
#define AppPublisher "Abishek Narasimhan"
#define AppUrl "https://github.com/abishekvupputur/ravensPort"
#define AppExeName "RavensPort.exe"
#define SourceExe "..\src\RavensPort.App\bin\Release\net8.0-windows\publish\win-x64\RavensPort.exe"

[Setup]
; Never change AppId. It is what lets a later version recognise, and replace, an existing
; install rather than sitting beside it as a second entry.
AppId={{C47DF74F-150F-4AD3-9B12-46A8BF02BE9C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; The name the reviewer looks for in Add or Remove Programs.
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
; Spelled the pre-6.3 way on purpose. "x64compatible" is the modern name but is a hard error on
; Inno 6.2, which is still what some build images carry; "x64" merely warns on newer versions.
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

; Matches SupportedOSPlatformVersion in Directory.Build.props: Windows 10 1809.
MinVersion=10.0.17763

OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\RavensPort.App\Assets\tray.ico
LicenseFile=..\LICENSE

; The payload is one already-compressed self-extracting binary, so asking LZMA to squeeze it
; again costs minutes of build time to save almost nothing.
Compression=none
SolidCompression=no

WizardStyle=modern
DisableProgramGroupPage=yes
DisableDirPage=auto

; Setup itself must not need a console or a restart; the Store runs it unattended.
RestartIfNeededByRun=no
CloseApplications=no

; Lets Setup notice a running copy and say so, rather than failing to overwrite a locked exe.
; Deliberately not CloseApplications=force: exiting RavensPort can prompt about vault changes
; that exist only in memory, and a forced kill would discard them silently.
AppMutex=RavensPort_SingleInstance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; The Start menu entry. This is the "clear method to launch the product" that was missing.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; skipifsilent, so the Store's unattended install does not leave a window on the reviewer's
; desktop — and so the install step ends when the installer does, rather than when the app is
; closed. nowait for the same reason.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
