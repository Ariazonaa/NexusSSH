; Inno-Setup-Skript für NexusSSH.
; Erzeugt ein setup.exe, das die self-contained AOT-App je Benutzer installiert (kein Admin, kein UAC).
; Aufruf über tools/make-release.ps1; die Variablen kommen per /D von ISCC:
;   ISCC /DAppVersion=0.5.0 /DSourceDir="...\release-stage\app" /DRepoDir="X:\src\NexusSSH" /DOutputDir="...\dist" installer\NexusSSH.iss
; Fallback-Werte, damit sich das Skript auch direkt in der Inno-IDE öffnen lässt:
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\..\AppData\Local\NexusSSH.build\publish\app"
#endif
#ifndef RepoDir
  #define RepoDir ".."
#endif
#ifndef OutputDir
  #define OutputDir "dist"
#endif

; Für welche Prozessoren die Dateien in SourceDir gebaut sind: x64 (läuft auch auf ARM, übersetzt) oder arm64.
#ifndef Arch
  #define Arch "x64"
#endif
#if Arch == "arm64"
  #define Allowed "arm64"
  #define Suffix "-arm64"
#else
  #define Allowed "x64compatible"
  #define Suffix ""
#endif

#define AppName "NexusSSH"
#define AppExe "NexusSSH.exe"

[Setup]
; Stabile AppId (nie ändern – daran erkennt Windows Upgrades und Deinstallation)
AppId={{7C4E2B1A-93D5-4F60-A8E7-2B6D0C9F1E34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
; Installation je Benutzer ohne Adminrechte -> {autopf} = %LOCALAPPDATA%\Programs
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\NexusSSH
DefaultGroupName=NexusSSH
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
SetupIconFile={#RepoDir}\frontend\NexusSSH.App\Assets\NexusSSH.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed={#Allowed}
ArchitecturesInstallIn64BitMode={#Allowed}
OutputDir={#OutputDir}
OutputBaseFilename=NexusSSH-Setup-{#AppVersion}{#Suffix}
; Eine laufende App vor dem Überschreiben schließen (bei Upgrades)
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.DesktopIcon=Create a desktop shortcut
de.DesktopIcon=Desktop-Verknüpfung anlegen
en.Shortcuts=Shortcuts:
de.Shortcuts=Verknüpfungen:
en.Uninstall=Uninstall {#AppName}
de.Uninstall={#AppName} deinstallieren
en.RunNow=Start {#AppName} now
de.RunNow={#AppName} jetzt starten
en.SshLinks=Open ssh:// links with {#AppName} (asks before connecting)
de.SshLinks=ssh://-Links mit {#AppName} öffnen (fragt vor dem Verbinden)

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Shortcuts}"; Flags: unchecked
; Von Haus aus aus: Ein anderes Programm (PuTTY, ein Terminal) kann ssh:// schon für sich haben.
Name: "sshlinks"; Description: "{cm:SshLinks}"; GroupDescription: "{cm:Shortcuts}"; Flags: unchecked

[Registry]
; Ein Link kommt als ein Argument ("%1"); die App nimmt daraus nur Benutzer, Host und Port und fragt vor dem Verbinden.
; HKA: je Benutzer nach HKCU, bei einer Installation für alle nach HKLM – sonst landete es im Profil des Administrators.
Root: HKA; Subkey: "Software\Classes\ssh"; ValueType: string; ValueName: ""; ValueData: "URL:SSH"; Flags: uninsdeletekey; Tasks: sshlinks
Root: HKA; Subkey: "Software\Classes\ssh"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Tasks: sshlinks
Root: HKA; Subkey: "Software\Classes\ssh\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"; Tasks: sshlinks
Root: HKA; Subkey: "Software\Classes\ssh\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: sshlinks

[Files]
; Der komplette self-contained Ordner – ohne Daten, falls dort ein Testlauf welche hinterlassen hat
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "Data\*"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:Uninstall}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:RunNow}"; Flags: nowait postinstall skipifsilent

; Profile, Host-Schlüssel und Einstellungen unter %LOCALAPPDATA%\NexusSSH bleiben bei der Deinstallation stehen –
; ebenso gespeicherte Passwörter in der Windows-Anmeldeinformationsverwaltung. Wer neu installiert, findet alles wieder.
