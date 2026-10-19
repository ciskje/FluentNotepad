; ============================================================
; FluentNotepad — Inno Setup 6 script
;
; App version: single source is <Version> in
; source/FluentNotepad.WinUI.csproj — #define MyAppVersion below
; derives from it at build time (never bump for docs/refactor/build).
;
; Build: see windows/AGENTS.md (Release + installer).
; Output: installer\dist\FluentNotepad-Setup-<version>-win-x64.exe
; ============================================================

#define MyAppName "FluentNotepad"
#define MyAppVersion "2.9.1"
#define MyAppExeName "FluentNotepad.exe"
#define PublishDir "..\source\bin\Release\net8.0-windows10.0.26100.0\win-x64\publish"
#define IconFile "..\source\Assets\AppIcon.ico"

[Setup]
AppId={{E6B42724-3658-48FB-B777-E8C687EBB0DC}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={commonpf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=.\dist
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}-win-x64
SetupIconFile={#IconFile}
Compression=lzma2/ultra64
ArchitecturesAllowed=x64compatible
; Per-machine install under C:\Program Files: requires the UAC prompt.
; {commonpf} is explicit: it can never fall back to the per-user folder (AppData)
; Install in 64-bit mode so {commonpf} is the real Program Files, not (x86).
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
; WebView2 evergreen required (preinstalled on Windows 11)

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "fileassoc"; Description: "Associate .md files with FluentNotepad"; GroupDescription: "File associations:"; Flags: unchecked
Name: "fileassoc_json"; Description: "Associate .json/.jsonc files with FluentNotepad"; GroupDescription: "File associations:"; Flags: unchecked
Name: "fileassoc_xml"; Description: "Associate .xml files with FluentNotepad"; GroupDescription: "File associations:"; Flags: unchecked

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

; Optional .json/.jsonc association (unchecked by default: no hijacking).
Root: HKLM; Subkey: "Software\Classes\.json"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.json"; Flags: uninsdeletevalue; Tasks: fileassoc_json
Root: HKLM; Subkey: "Software\Classes\.jsonc"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.json"; Flags: uninsdeletevalue; Tasks: fileassoc_json
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.json"; ValueType: string; ValueName: ""; ValueData: "File JSON"; Flags: uninsdeletekey; Tasks: fileassoc_json
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.json\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc_json
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.json\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc_json

; Optional .xml association (unchecked by default: no hijacking).
Root: HKLM; Subkey: "Software\Classes\.xml"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.xml"; Flags: uninsdeletevalue; Tasks: fileassoc_xml
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.xml"; ValueType: string; ValueName: ""; ValueData: "File XML"; Flags: uninsdeletekey; Tasks: fileassoc_xml
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.xml\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc_xml
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.xml\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc_xml

[Run]
; runasoriginaluser: without it, after an admin install the app would start elevated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch FluentNotepad"; Flags: nowait postinstall skipifsilent runasoriginaluser
