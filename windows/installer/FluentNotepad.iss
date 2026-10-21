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
#define MyAppVersion "2.10.9"
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
; Control Panel / Settings entry icon: the generated unins000.exe has no icon
; of its own, so point at the app exe (which embeds AppIcon.ico).
UninstallDisplayIcon={app}\{#MyAppExeName}
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
Name: "fileassoc_yaml"; Description: "Associate .yaml/.yml files with FluentNotepad"; GroupDescription: "File associations:"; Flags: unchecked
Name: "fileassoc_toml"; Description: "Associate .toml files with FluentNotepad"; GroupDescription: "File associations:"; Flags: unchecked
Name: "fileassoc_ini"; Description: "Associate .ini/.cfg/.inf/.properties files with FluentNotepad"; GroupDescription: "File associations:"; Flags: unchecked
Name: "fileassoc_csv"; Description: "Associate .csv/.tsv files with FluentNotepad"; GroupDescription: "File associations:"; Flags: unchecked

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

; Optional .yaml/.yml association (unchecked by default: no hijacking).
Root: HKLM; Subkey: "Software\Classes\.yaml"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.yaml"; Flags: uninsdeletevalue; Tasks: fileassoc_yaml
Root: HKLM; Subkey: "Software\Classes\.yml"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.yaml"; Flags: uninsdeletevalue; Tasks: fileassoc_yaml
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.yaml"; ValueType: string; ValueName: ""; ValueData: "File YAML"; Flags: uninsdeletekey; Tasks: fileassoc_yaml
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.yaml\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc_yaml
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.yaml\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc_yaml

; Optional .toml association (unchecked by default: no hijacking).
Root: HKLM; Subkey: "Software\Classes\.toml"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.toml"; Flags: uninsdeletevalue; Tasks: fileassoc_toml
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.toml"; ValueType: string; ValueName: ""; ValueData: "File TOML"; Flags: uninsdeletekey; Tasks: fileassoc_toml
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.toml\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc_toml
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.toml\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc_toml

; Optional .ini/.cfg/.inf/.properties association (unchecked by default).
Root: HKLM; Subkey: "Software\Classes\.ini"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.ini"; Flags: uninsdeletevalue; Tasks: fileassoc_ini
Root: HKLM; Subkey: "Software\Classes\.cfg"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.ini"; Flags: uninsdeletevalue; Tasks: fileassoc_ini
Root: HKLM; Subkey: "Software\Classes\.inf"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.ini"; Flags: uninsdeletevalue; Tasks: fileassoc_ini
Root: HKLM; Subkey: "Software\Classes\.properties"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.ini"; Flags: uninsdeletevalue; Tasks: fileassoc_ini
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.ini"; ValueType: string; ValueName: ""; ValueData: "File INI"; Flags: uninsdeletekey; Tasks: fileassoc_ini
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.ini\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc_ini
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.ini\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc_ini

; Optional .csv/.tsv association (unchecked by default: no hijacking).
Root: HKLM; Subkey: "Software\Classes\.csv"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.csv"; Flags: uninsdeletevalue; Tasks: fileassoc_csv
Root: HKLM; Subkey: "Software\Classes\.tsv"; ValueType: string; ValueName: ""; ValueData: "FluentNotepad.csv"; Flags: uninsdeletevalue; Tasks: fileassoc_csv
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.csv"; ValueType: string; ValueName: ""; ValueData: "File CSV"; Flags: uninsdeletekey; Tasks: fileassoc_csv
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.csv\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc_csv
Root: HKLM; Subkey: "Software\Classes\FluentNotepad.csv\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc_csv

[Run]
; runasoriginaluser: without it, after an admin install the app would start elevated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch FluentNotepad"; Flags: nowait postinstall skipifsilent runasoriginaluser
