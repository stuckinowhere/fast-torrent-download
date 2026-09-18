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

[Files]
Source: "{#SourceDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExeName}"
Name: "{autodesktop}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
