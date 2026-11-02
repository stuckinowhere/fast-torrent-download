#define MyAppName "fast torrent download"
#define MyAppPublisher "stuckinowhere"
#define MyAppURL "https://github.com/stuckinowhere/fast-torrent-download"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.1"
#endif
#define MyAppExeName "FastTorrentDownload.exe"
#ifndef SourceDir
  #define SourceDir "..\\artifacts\\fast-torrent-download-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\\artifacts"
#endif

[Setup]
AppId={{0F391C83-3CBB-485F-91C5-8754E80F1A9E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\\Programs\\fast torrent download
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=fast-torrent-download-{#MyAppVersion}-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\\{#MyAppExeName}
ChangesAssociations=yes
; Refuse to replace files under a running instance; the mutex name matches Program.cs.
AppMutex=Local\WASD.FastTorrentDownload

[Files]
Source: "{#SourceDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExeName}"
Name: "{autodesktop}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "magnethandler"; Description: "Open &magnet links with {#MyAppName}"; GroupDescription: "Integration:"
Name: "torrentassoc"; Description: "Open .&torrent files with {#MyAppName}"; GroupDescription: "Integration:"

; Per-user registration (PrivilegesRequired=lowest): HKA resolves to HKCU, so no elevation and no machine-wide change.
[Registry]
; The magnet key is shared with other clients: delete only our values and the key only when it is empty afterwards.
Root: HKA; Subkey: "Software\\Classes\\magnet"; ValueType: string; ValueName: ""; ValueData: "URL:Magnet link"; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: magnethandler
Root: HKA; Subkey: "Software\\Classes\\magnet"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletevalue; Tasks: magnethandler
Root: HKA; Subkey: "Software\\Classes\\magnet\\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\\{#MyAppExeName},0"; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: magnethandler
Root: HKA; Subkey: "Software\\Classes\\magnet\\shell\\open\\command"; ValueType: string; ValueName: ""; ValueData: """{app}\\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: magnethandler
Root: HKA; Subkey: "Software\\Classes\\magnet\\shell\\open"; Flags: uninsdeletekeyifempty; Tasks: magnethandler
Root: HKA; Subkey: "Software\\Classes\\magnet\\shell"; Flags: uninsdeletekeyifempty; Tasks: magnethandler
Root: HKA; Subkey: "Software\\Classes\\.torrent"; ValueType: string; ValueName: ""; ValueData: "FastTorrentDownload.Torrent"; Flags: uninsdeletevalue; Tasks: torrentassoc
Root: HKA; Subkey: "Software\\Classes\\FastTorrentDownload.Torrent"; ValueType: string; ValueName: ""; ValueData: "Torrent file"; Flags: uninsdeletekey; Tasks: torrentassoc
Root: HKA; Subkey: "Software\\Classes\\FastTorrentDownload.Torrent\\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\\{#MyAppExeName},0"; Tasks: torrentassoc
Root: HKA; Subkey: "Software\\Classes\\FastTorrentDownload.Torrent\\shell\\open\\command"; ValueType: string; ValueName: ""; ValueData: """{app}\\{#MyAppExeName}"" ""%1"""; Tasks: torrentassoc

[Run]
Filename: "{app}\\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
