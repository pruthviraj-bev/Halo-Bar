; Halo Bar — Inno Setup installer (V1)
; Builds a single Setup.exe that installs the app files and the Windows App SDK
; runtime (1.8, x64) silently on clean machines.
;
; Requires: ISCC.exe (Inno Setup 6) and the published app in publish\.
;
;   ISCC.exe "Installer\HaloBar.iss"
;
; Output: Installer\Output\HaloBar-Setup-1.0.0.exe

#define MyAppName "Halo Bar"
#define MyAppVersion "1.0.0"
#define MyAppExeName "DynamicIsland.exe"
#define MyAppPublisher "pruthviraj-bev"
#define MyAppURL "https://github.com/pruthviraj-bev/Halo-Bar"

; Paths are relative to this .iss file (the repo root when built from there).
#ifndef SourceDir
  #define SourceDir "..\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"
#endif

[Setup]
AppId={{E5D9F2C1-6A2B-4C8D-9E3A-1B2C3D4E5F6A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=HaloBar-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\logo\halo-bar.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The app is a WinUI 3 unpackaged app — no MSIX registration needed.
CreateAppDir=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Windows App SDK 1.8 runtime installer (bundled, run during [Run]).
Source: "runtime\WindowsAppRuntimeInstall-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
; Self-contained published app (includes the .NET 10 runtime).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

; NOTE: no desktop shortcut on purpose — Halo Bar is a taskbar/dock widget, not
; a classic app, so only the Start-menu entry is created below.

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
; Install the Windows App SDK runtime first. --quiet suppresses the UI; the
; installer idempotently skips already-present runtime versions.
Filename: "{tmp}\WindowsAppRuntimeInstall-x64.exe"; Parameters: "--quiet"; StatusMsg: "Installing Windows App SDK runtime..."; Flags: waituntilterminated runhidden
; Launch the app unless the user unchecked the run box.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

; NOTE: "Start with Windows" is NOT registered here — the app owns that toggle
; (AppSettings.ApplyStartWithWindows writes HKCU\...\Run "HaloBar" itself), so
; the installer never fights it.

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\DynamicIsland\logs"