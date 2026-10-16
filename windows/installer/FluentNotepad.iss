; ============================================================
; FluentNotepad — Inno Setup 6 script
;
; App version: keep in sync with <Version> in
; source/FluentNotepad.WinUI.csproj
;
; Build:
;   1. dotnet publish source\FluentNotepad.WinUI.csproj -c Release -p:Platform=x64
;   2. iscc installer\FluentNotepad.iss
;      (from windows/; requires Inno Setup 6)
; Output: installer\dist\FluentNotepad-Setup-<version>-win-x64.exe
; ============================================================

#define MyAppName "FluentNotepad"
#define MyAppVersion "2.6.0"
#define MyAppExeName "FluentNotepad.exe"
#define PublishDir "..\source\bin\Release\net8.0-windows10.0.26100.0\win-x64\publish"
#define IconFile "..\source\Assets\AppIcon.ico"

[Setup]
AppId={{E6B42724-3658-48FB-B777-E8C687EBB0DC}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={commonpf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=.\dist
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}-win-x64
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Per-machine install under C:\Program Files: requires the UAC prompt.
; {commonpf} is explicit: it can never fall back to the per-user folder (AppData)
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
; WebView2 evergreen required (preinstalled on Windows 11)

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "fileassoc"; Description: "Associate .md files with FluentNotepad"; GroupDescription: "File associations:"; Flags: unchecked

[Files]
; Publish self-contained (includes the .NET runtime and Windows App SDK)
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Machine-level .md association (HKLM): matches the Program Files install
Root: HKLM; Subkey: "Software\Classes\.md"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.md"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.md"; ValueType: string; ValueName: ""; ValueData: "File Markdown"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.md\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.md\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc

[Run]
; runasoriginaluser: without it, after an admin install the app would start elevated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch FluentNotepad"; Flags: nowait postinstall skipifsilent runasoriginaluser
