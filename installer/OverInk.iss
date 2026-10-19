; Inno Setup script for OverInk. Built by scripts/publish.ps1 (and the release workflow):
;   iscc /DAppVersion=1.2.3 /DSourceDir=..\artifacts\publish installer\OverInk.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

[Setup]
; Never change AppId: it is how upgrades and the uninstaller find the existing install.
AppId={{15B47256-D1E1-4A5D-AA7A-7215E2C445A1}
AppName=OverInk
AppVersion={#AppVersion}
AppPublisher=OverInk
AppPublisherURL=https://github.com/hickmanz/overink
AppSupportURL=https://github.com/hickmanz/overink/issues
AppUpdatesURL=https://github.com/hickmanz/overink/releases
DefaultDirName={autopf}\OverInk
DefaultGroupName=OverInk
DisableProgramGroupPage=yes
; Per-user install by default (no admin prompt); the wizard offers "all users" via a dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=OverInk-Setup-{#AppVersion}
SetupIconFile=..\src\OverInk\Assets\app.ico
UninstallDisplayIcon={app}\OverInk.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Matches the single-instance mutex in App.xaml.cs so setup closes a running copy before upgrading.
AppMutex=OverInk_SingleInstance_2026
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\OverInk"; Filename: "{app}\OverInk.exe"
Name: "{autodesktop}\OverInk"; Filename: "{app}\OverInk.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OverInk.exe"; Description: "Launch OverInk"; Flags: nowait postinstall skipifsilent
