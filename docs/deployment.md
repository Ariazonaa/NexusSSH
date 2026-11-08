# Verteilung im Betrieb

Für alle, die NexusSSH auf vielen Rechnern einrichten: wie das Setup ohne Fenster läuft.

## Still installieren

Das Setup (`NexusSSH-Setup-<Version>.exe`, Inno Setup) läuft ohne Fenster und ohne Rückfragen:

```powershell
NexusSSH-Setup-0.1.1.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER
```

| Schalter | Wirkung |
|---|---|
| `/VERYSILENT` | kein Fenster, auch kein Fortschritt (`/SILENT` zeigt den Fortschritt) |
| `/SUPPRESSMSGBOXES` | keine Meldungen; jede Frage wird mit der Vorgabe beantwortet |
| `/NORESTART` | kein Neustart von Windows |
| `/CURRENTUSER` | für den angemeldeten Benutzer, ohne Adminrechte – nach `%LOCALAPPDATA%\Programs\NexusSSH` |
| `/ALLUSERS` | für alle Benutzer, braucht Adminrechte – nach `%ProgramFiles%\NexusSSH` |
| `/DIR="C:\Pfad"` | ein anderer Zielordner |
| `/MERGETASKS="desktopicon"` | mit Verknüpfung auf dem Desktop (ohne den Schalter: keine) |
| `/LOG="C:\Pfad\setup.log"` | ein Protokoll der Installation |

Ohne Fenster startet das Setup die App am Ende nicht. Der Rückgabewert ist 0, wenn alles gelang. Ein Upgrade ist
dasselbe Kommando mit dem neueren Setup; eine laufende App wird dabei geschlossen.

Deinstallieren, ebenso still – das Programm dafür liegt im Zielordner:

```powershell
& "$env:LOCALAPPDATA\Programs\NexusSSH\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Dabei bleiben die Daten des Benutzers stehen: Profile, Host-Schlüssel und Einstellungen unter
`%LOCALAPPDATA%\NexusSSH` und gespeicherte Passwörter in der Windows-Anmeldeinformationsverwaltung.

Für Windows auf ARM gibt es ein eigenes Setup (`…-arm64.exe`) mit denselben Schaltern.

Voraussetzungen: Windows 10 2004 oder neuer und die WebView2-Runtime (bei Windows 11 dabei).

## Wo die Daten liegen

Wo die Daten liegen, lässt sich auch von außen bestimmen: `NEXUSSSH_DATA_DIR` nennt den Datenordner,
`NEXUSSSH_SSH_DIR` den Ordner, den die App anstelle von `~/.ssh` liest.
