//! Nachrichten zwischen App und Kern, als JSON.
//!
//! Die Gegenstelle in C# ist `NexusSSH.Core/Backend/CoreProtocol.cs` – beide Seiten gemeinsam ändern und
//! [`crate::ABI_VERSION`] erhöhen, wenn eine alte App die neue Form nicht mehr verstünde.
//!
//! - **Anfragen** (`Request`) beantwortet der Kern sofort (`Response`). Was dauert – eine Sitzung aufbauen –
//!   wird nur angestoßen; der Fortschritt kommt als Ereignis.
//! - **Ereignisse** (`Event`) schickt der Kern von sich aus.
//! - Terminal-Daten sind kein JSON, sie laufen als rohe Bytes daneben.

use serde::{Deserialize, Serialize};

use crate::profiles::Profile;
use crate::sshconfig::ConfigHost;

/// Anfrage der App.
#[derive(Debug, Deserialize)]
#[serde(tag = "op")]
pub enum Request {
    /// Alle Profile.
    #[serde(rename = "profiles.list")]
    ProfilesList,
    /// Profil anlegen (leere `id`) oder ersetzen.
    #[serde(rename = "profiles.save")]
    ProfilesSave {
        /// Das Profil. In einer Box, damit die größte Anfrage nicht alle anderen aufbläht.
        profile: Box<Profile>,
    },
    /// Profil samt gespeicherter Geheimnisse entfernen.
    #[serde(rename = "profiles.delete")]
    ProfilesDelete {
        /// Kennung des Profils.
        id: String,
    },
    /// Gespeichertes Passwort und gespeicherte Passphrase eines Profils vergessen; das Profil bleibt.
    #[serde(rename = "secrets.forget")]
    SecretsForget {
        /// Kennung des Profils.
        id: String,
    },
    /// Sitzung zum Server eines Profils aufbauen. Die Nummer vergibt die App.
    #[serde(rename = "session.open")]
    SessionOpen {
        /// Nummer der Sitzung.
        session: u64,
        /// Kennung des Profils.
        profile_id: String,
        /// Breite des Terminals in Zeichen.
        cols: u32,
        /// Höhe des Terminals in Zeilen.
        rows: u32,
        /// Eine andere Sitzung zum selben Profil, deren stehende Verbindung mitbenutzt werden soll – ohne neue
        /// Anmeldung. Geht das nicht, entsteht eine eigene Verbindung.
        #[serde(default)]
        share: Option<u64>,
        /// Den Aufbau dieser Verbindung ausführlich melden (`session.trace`): was ausgehandelt wurde und welche
        /// Anmeldung versucht wird. Zum Fehlersuchen, auf Wunsch des Benutzers – nie mit Geheimnissen.
        #[serde(default)]
        trace: bool,
        /// Bei einem Profil mit Mosh: die Vorhersage beim Tippen – `adaptive` (wie bei `mosh` selbst, auch ohne
        /// Angabe), `always` oder `never`.
        #[serde(default)]
        mosh_predict: Option<String>,
    },
    /// Die SSH-Verbindung unter einem Mosh-Terminal jetzt neu aufbauen, statt auf den nächsten Versuch des Kerns zu
    /// warten (nach `session.link_lost`). Andere Sitzungen überhören es.
    #[serde(rename = "session.relink")]
    SessionRelink {
        /// Nummer der Sitzung.
        session: u64,
    },
    /// Ausgabe des Terminals anhalten oder weiterlaufen lassen.
    #[serde(rename = "session.flow")]
    SessionFlow {
        /// Nummer der Sitzung.
        session: u64,
        /// Anhalten (`true`) oder weiter.
        paused: bool,
    },
    /// Neue Größe des Terminals.
    #[serde(rename = "session.resize")]
    SessionResize {
        /// Nummer der Sitzung.
        session: u64,
        /// Breite in Zeichen.
        cols: u32,
        /// Höhe in Zeilen.
        rows: u32,
    },
    /// Sitzung beenden, in welchem Zustand sie auch ist.
    #[serde(rename = "session.close")]
    SessionClose {
        /// Nummer der Sitzung.
        session: u64,
    },
    /// Verzeichnis auflisten. Das Ergebnis kommt als `sftp.result` mit derselben `request`-Nummer.
    #[serde(rename = "sftp.list")]
    SftpList {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage, von der App vergeben.
        request: u64,
        /// Verzeichnis auf dem Server.
        path: String,
    },
    /// Verzeichnis anlegen.
    #[serde(rename = "sftp.mkdir")]
    SftpMkdir {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Neues Verzeichnis.
        path: String,
    },
    /// Leere Datei anlegen, ohne eine vorhandene zu überschreiben.
    #[serde(rename = "sftp.touch")]
    SftpTouch {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Neue Datei.
        path: String,
    },
    /// Symbolischen Link anlegen, ohne etwas Vorhandenes zu ersetzen.
    #[serde(rename = "sftp.symlink")]
    SftpSymlink {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Der neue Link.
        path: String,
        /// Worauf er zeigt; muss es geben.
        to: String,
    },
    /// Fragen, wohin ein einzelner Link zeigt. Das Ergebnis kommt als `sftp.result` mit `target`: der Text im Link,
    /// nicht aufgelöst. Zeigt der Pfad auf etwas, das kein Link ist, scheitert die Anfrage.
    #[serde(rename = "sftp.readlink")]
    SftpReadlink {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Der Link.
        path: String,
    },
    /// Umbenennen oder verschieben, ohne ein vorhandenes Ziel zu überschreiben.
    #[serde(rename = "sftp.rename")]
    SftpRename {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Bisheriger Pfad.
        from: String,
        /// Neuer Pfad.
        to: String,
    },
    /// Zugriffsrechte setzen (wie `chmod`).
    #[serde(rename = "sftp.chmod")]
    SftpChmod {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Datei oder Verzeichnis.
        path: String,
        /// Die Rechte als Zahl, z. B. `0o644` = 420. Gesetzt werden nur die unteren zwölf Bits.
        mode: u32,
        /// Bei einem Verzeichnis auch alles darin (wie `chmod -R`). Verzeichnisse bekommen dabei das Recht zum
        /// Betreten, wo die Zahl das Lesen erlaubt – sonst wäre nach `644` kein Ordner mehr zu öffnen. Links
        /// werden nicht verfolgt.
        #[serde(default)]
        recursive: bool,
    },
    /// Besitzer und Gruppe setzen (wie `chown`), als Name oder Nummer. Was fehlt, bleibt, wie es ist.
    #[serde(rename = "sftp.chown")]
    SftpChown {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Datei oder Verzeichnis.
        path: String,
        /// Neuer Besitzer.
        #[serde(default)]
        owner: Option<String>,
        /// Neue Gruppe.
        #[serde(default)]
        group: Option<String>,
        /// Bei einem Verzeichnis auch alles darin (wie `chown -R`); Links werden nicht verfolgt.
        #[serde(default)]
        recursive: bool,
    },
    /// Den SFTP-Kanal der Sitzung neu öffnen – wenn der alte nicht mehr antwortet (ein hängendes Netzlaufwerk auf dem
    /// Server), die Verbindung selbst aber steht. Das Ergebnis kommt als `sftp.result`.
    #[serde(rename = "sftp.reopen")]
    SftpReopen {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
    },
    /// Was ein Download von `path` anlegen würde: jedes Verzeichnis und jede Datei mit ihrem Ort auf dem Server und
    /// dem Namen, den sie auf diesem Rechner bekäme. Das Ergebnis kommt als `sftp.result` mit `tree` – für alle,
    /// die den Inhalt eines Ordners kennen müssen, bevor er geladen ist (Ziehen in ein anderes Programm).
    #[serde(rename = "sftp.tree")]
    SftpTree {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Datei oder Verzeichnis auf dem Server.
        path: String,
        /// Muster, die ausgelassen werden (`crate::excludes`); ohne Angabe nichts.
        #[serde(default)]
        exclude: Option<Vec<String>>,
        /// Statt des Baums das zurückgeben, was die Muster auslassen würden – jeweils der oberste Eintrag.
        #[serde(default)]
        excluded: bool,
    },
    /// Unter einem Verzeichnis nach Einträgen suchen, deren Name einen Text enthält. Das Ergebnis kommt als
    /// `sftp.result` mit `tree` (dort ohne lokalen Namen) – höchstens `sftp::FIND_HITS` Treffer.
    #[serde(rename = "sftp.find")]
    SftpFind {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Verzeichnis, unter dem gesucht wird.
        path: String,
        /// Text, den der Name enthalten muss; Groß- und Kleinschreibung zählen nicht.
        name: String,
    },
    /// Einträge eines Verzeichnisses dort zu einem Archiv packen (`tar`, gzip). Braucht ein Konto, das Befehle
    /// ausführen darf; ein vorhandenes Archiv wird nicht überschrieben.
    #[serde(rename = "sftp.pack")]
    SftpPack {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Verzeichnis, in dem die Einträge liegen und das Archiv entsteht.
        directory: String,
        /// Namen der Einträge in diesem Verzeichnis.
        names: Vec<String>,
        /// Name des Archivs.
        archive: String,
    },
    /// Ein Archiv in sein Verzeichnis entpacken (`tar` bzw. `unzip`), ohne Vorhandenes zu überschreiben.
    #[serde(rename = "sftp.unpack")]
    SftpUnpack {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Verzeichnis, in dem das Archiv liegt.
        directory: String,
        /// Name des Archivs.
        archive: String,
    },
    /// Wie viel Platz ein Verzeichnis samt Inhalt belegt (`du`). Das Ergebnis kommt als `sftp.result` mit `size`.
    #[serde(rename = "sftp.size")]
    SftpSize {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Das Verzeichnis, als absoluter Pfad.
        path: String,
    },
    /// Was direkt unter einem Verzeichnis wie viel Platz belegt (`du`). Das Ergebnis kommt als `sftp.result` mit
    /// `usage`; das Verzeichnis selbst steht mit darin.
    #[serde(rename = "sftp.usage")]
    SftpUsage {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Das Verzeichnis, als absoluter Pfad.
        path: String,
    },
    /// SHA-256 von Dateien auf dem Server. Das Ergebnis kommt als `sftp.result` mit `sums` – je Datei eine, in
    /// der Reihenfolge der Anfrage, `null` wo es keine gab.
    #[serde(rename = "sftp.checksum")]
    SftpChecksum {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Die Dateien, als absolute Pfade.
        paths: Vec<String>,
    },
    /// Einträge auf dem Server selbst kopieren oder verschieben (`cp`, `mv`) – ohne den Umweg über diesen Rechner.
    #[serde(rename = "sftp.copy")]
    SftpCopy {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Quelle und Ziel je Eintrag, beides volle Pfade.
        pairs: Vec<CopyPair>,
        /// Verschieben statt kopieren.
        #[serde(default, rename = "move")]
        moving: bool,
    },
    /// Den Dateibrowser der Sitzung auf root umstellen: Der Kern startet den SFTP-Server des Servers über `sudo`
    /// und benutzt fortan diesen Kanal. Ohne `password` nur, wenn `sudo` keines verlangt – verlangt es eines,
    /// scheitert die Anfrage mit `sudo_password`, und die App fragt den Benutzer. `sftp.reopen` stellt zurück.
    #[serde(rename = "sftp.elevate")]
    SftpElevate {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Das Passwort des Benutzers für `sudo`; es wird nicht gespeichert.
        #[serde(default)]
        password: Option<String>,
    },
    /// Ein Stück einer Datei lesen, ohne sie zu übertragen – für Vorschau und Editor. Das Ergebnis kommt als
    /// `sftp.result` mit `data` (Base64) und `size` (Größe der ganzen Datei).
    #[serde(rename = "sftp.read")]
    SftpRead {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Die Datei.
        path: String,
        /// Ab hier, in Bytes.
        #[serde(default)]
        offset: u64,
        /// Höchstens so viele Bytes; der Kern liefert nie mehr als 1 MiB auf einmal.
        length: u64,
    },
    /// Eine kleine Datei ganz neu schreiben (Editor): anlegen oder leeren, schreiben. Höchstens 4 MiB.
    #[serde(rename = "sftp.write")]
    SftpWrite {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Die Datei.
        path: String,
        /// Der neue Inhalt als Base64.
        data: String,
    },
    /// Eine Datei auf dem Server mitlesen (`tail -f`): Was an ihrem Ende dazukommt, kommt als `follow.data`, bis
    /// `follow.stop` es beendet oder der Befehl von selbst endet (`follow.end`). Braucht ein Konto, das Befehle
    /// ausführen darf.
    #[serde(rename = "follow.start")]
    FollowStart {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer des Mitlesens, von der App vergeben.
        follow: u64,
        /// Die Datei, als absoluter Pfad.
        path: String,
    },
    /// Beendet ein Mitlesen. Unbekannte Nummern sind kein Fehler: Es kann schon von selbst geendet haben.
    #[serde(rename = "follow.stop")]
    FollowStop {
        /// Nummer des Mitlesens.
        follow: u64,
    },
    /// Die letzten Zeilen eines Protokolls: ohne `path` das Journal von systemd (`journalctl`), mit `path` eine
    /// Textdatei wie die unter `/var/log`. Das Ergebnis kommt als `sftp.result` mit `logs` (älteste zuerst) und
    /// beim Journal mit `limited`, wenn das Konto nur seine eigenen Einträge sieht. Kein `journalctl` am Server:
    /// Fehler `no_journal`.
    #[serde(rename = "logs.read")]
    LogsRead {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Absoluter Pfad einer Datei am Server; fehlt für das Journal.
        #[serde(default)]
        path: Option<String>,
        /// Nur Journal: wie weit zurück (`15m`, `1h`, `6h`, `1d`, `7d`); fehlt für „alles“.
        #[serde(default)]
        since: Option<crate::logs::Since>,
        /// Nur Journal: diese Schwere und alles Ernstere, 0 (Notfall) bis 7 (Fehlersuche).
        #[serde(default)]
        priority: Option<u8>,
        /// Nur Journal: Name einer Einheit (`nginx.service`).
        #[serde(default)]
        unit: Option<String>,
        /// So viele Zeilen höchstens (bis 2000); fehlt für 500.
        #[serde(default)]
        lines: Option<u32>,
    },
    /// Die Namen der Programme im Suchpfad des Kontos, für das Ergänzen beim Tippen. Ein fester Befehl; das
    /// Ergebnis kommt als `sftp.result` mit `commands`.
    #[serde(rename = "commands.list")]
    CommandsList {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
    },
    /// Laufende Sitzungen von tmux und screen auf dem Server. Das Ergebnis kommt als `sftp.result` mit `mux`.
    #[serde(rename = "mux.list")]
    MuxList {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
    },
    /// Führt der Server für dieses Konto Befehle des Kerns aus? Ein Befehl ohne Wirkung; `sftp.result` ist `ok`
    /// oder nennt, warum nicht (`no_posix_shell`, `no_exec`). Die App blendet danach aus, was Befehle bräuchte.
    #[serde(rename = "commands.probe")]
    CommandsProbe {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
    },
    /// Die Anmelde-Shell des Kontos, ein fester Befehl; `sftp.result` nennt sie in `line` (`/bin/bash`, unter Windows
    /// `windows`). Die App entscheidet danach, ob sich ein Befehl in die Shell tippen lässt.
    #[serde(rename = "commands.shell")]
    CommandsShell {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
    },
    /// Führt Text der App als Befehl aus – der einzige Weg, auf dem eine Befehlszeile von dort kommt. Die App
    /// schickt sie nur für das Werkzeug `run_command` des MCP-Servers und erst, wenn der Benutzer genau diesen Text
    /// freigegeben hat. Das Ergebnis kommt als `sftp.result` mit `run`.
    #[serde(rename = "exec.run")]
    ExecRun {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Der Befehl, wie er am Server laufen soll.
        command: String,
        /// So viele Sekunden darf er laufen, 1 bis 300.
        seconds: u64,
    },
    /// Ein Schlüsselpaar in `~/.ssh` anlegen; die Antwort nennt `path` und `public_key`.
    #[serde(rename = "key.generate")]
    KeyGenerate {
        /// Dateiname des privaten Schlüssels in `~/.ssh`.
        name: String,
        /// Passphrase für den privaten Schlüssel; ohne sie bleibt er unverschlüsselt.
        #[serde(default)]
        passphrase: Option<String>,
        /// Kommentar am öffentlichen Schlüssel.
        #[serde(default)]
        comment: String,
    },
    /// Die Schlüsseldateien in `~/.ssh` und die genannten dazu; die Antwort nennt sie in `keys`. Gelesen wird
    /// ohne Passphrase.
    #[serde(rename = "key.list")]
    KeyList {
        /// Weitere Dateien (ganze Pfade) – Schlüssel aus Profilen, die nicht in `~/.ssh` liegen.
        #[serde(default)]
        paths: Vec<String>,
    },
    /// Die Passphrase einer Schlüsseldatei ändern, setzen oder entfernen. Das Format der Datei bleibt.
    #[serde(rename = "key.passphrase")]
    KeyPassphrase {
        /// Die Schlüsseldatei.
        path: String,
        /// Die bisherige Passphrase; fehlt bei einem unverschlüsselten Schlüssel.
        #[serde(default)]
        passphrase: Option<String>,
        /// Die neue; ohne sie liegt der Schlüssel danach unverschlüsselt da.
        #[serde(default)]
        new_passphrase: Option<String>,
    },
    /// Eine Schlüsseldatei in das andere Format schreiben, als neue Datei daneben; die Antwort nennt `path`.
    #[serde(rename = "key.convert")]
    KeyConvert {
        /// Die Schlüsseldatei.
        path: String,
        /// Ihre Passphrase; sie schützt auch die neue Datei.
        #[serde(default)]
        passphrase: Option<String>,
        /// Das Format der neuen Datei: `openssh` oder `ppk`.
        format: crate::keyfiles::Format,
        /// Dateiname der neuen Datei im selben Ordner.
        name: String,
    },
    /// Einen öffentlichen Schlüssel in `~/.ssh/authorized_keys` des Servers eintragen.
    #[serde(rename = "key.install")]
    KeyInstall {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Die Zeile des öffentlichen Schlüssels.
        public_key: String,
    },
    /// Datei oder Verzeichnis samt Inhalt löschen.
    #[serde(rename = "sftp.delete")]
    SftpDelete {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Anfrage.
        request: u64,
        /// Was gelöscht wird.
        path: String,
    },
    /// Transfer in die Warteschlange stellen. Der Stand kommt als `transfer.state`.
    #[serde(rename = "transfer.start")]
    TransferStart {
        /// Nummer der Sitzung, über deren Verbindung der Transfer läuft.
        session: u64,
        /// Nummer des Transfers, von der App vergeben.
        transfer: u64,
        /// Richtung.
        direction: Direction,
        /// Upload: lokale Datei oder lokales Verzeichnis. Download: lokales Zielverzeichnis.
        local: String,
        /// Upload: Zielverzeichnis auf dem Server. Download: Datei oder Verzeichnis dort.
        remote: String,
        /// Upload: Name im Zielverzeichnis, wenn er nicht der lokale sein soll – etwa, wenn eine zum Bearbeiten
        /// geholte Datei zurückgeht, die hier anders heißen musste (`transfers::local_name`). Von Server zu Server
        /// ebenso: der Name am Ziel, wenn er nicht der der Quelle sein soll.
        #[serde(default)]
        name: Option<String>,
        /// Bei einem Ordner: nur diese Dateien übertragen, als Pfade unterhalb des Ordners der Quelle mit `/`
        /// getrennt (Abgleich). Beim Download nennt `name` dann den lokalen Ordner, in dem sie landen.
        #[serde(default)]
        only: Option<Vec<String>>,
        /// Bei einem Ordner: Muster für Namen und Pfade, die ausgelassen werden (`.git`, `*.tmp`, `build/*.map`;
        /// siehe `crate::excludes`). Die App fragt vorher, was davon trotzdem mit soll, und nennt hier den Rest.
        #[serde(default)]
        exclude: Option<Vec<String>>,
        /// Von Server zu Server (`relay`): die Sitzung des Ziels. `remote` ist dann die Quelle auf dem Server von
        /// `session`, `local` das Zielverzeichnis auf dem Server dieser Sitzung.
        #[serde(default)]
        target_session: Option<u64>,
        /// Mehr Bytes überträgt dieser Transfer nicht: Wird die Grenze überschritten, scheitert er mit `too_large`.
        /// Für Downloads, deren Größe die App vorher an der Liste geprüft hat (Bearbeiten, Ansehen, Vergleichen) –
        /// die Liste nennt, was der Server behauptet, geladen wird sonst, bis er aufhört.
        #[serde(default)]
        limit: Option<u64>,
        /// Ein neuer Versuch: so viele Bytes hatte der abgebrochene schon übertragen. Nur mit dieser Angabe baut der
        /// Kern auf dem auf, was am Ziel liegt, statt von vorn zu beginnen. Beim Upload einer einzelnen Datei geht
        /// es an dieser Stelle weiter, wenn der Server dort hat, was in der lokalen Datei steht
        /// (`transfers::resume_point`) – die App nennt die Zahl dann nur, wenn die lokale Datei seither unverändert
        /// ist. Bei einem Ordner und beim Download zählt, was der Kern selbst vom Versuch davor weiß
        /// (`transfers::Trail`): Vollständiges wird übersprungen, Angefangenes fortgesetzt, jeweils nur, solange
        /// die Quelle dieselbe ist.
        #[serde(default)]
        resume: Option<u64>,
        /// Was mit Dateien geschieht, die es am Ziel schon gibt: ohne Angabe werden sie überschrieben; sonst bleibt
        /// stehen, was die Regel nennt ([`Keep`]) – für jede Datei einzeln, auch tief in einem Ordner.
        #[serde(default)]
        keep: Option<Keep>,
    },
    /// Wohin ein Download schriebe: Die Antwort nennt in `path` die lokale Datei bzw. den Ordner, der entstünde –
    /// für die Frage „überschreiben?“ und für alle, die die Datei danach brauchen. Den Namen bildet der Kern
    /// (`transfers::local_name`); taugt er nicht, lautet die Antwort `unsafe_name`.
    #[serde(rename = "transfer.target")]
    TransferTarget {
        /// Lokales Zielverzeichnis.
        local: String,
        /// Datei oder Verzeichnis auf dem Server.
        remote: String,
    },
    /// Transfer abbrechen, ob er wartet oder läuft.
    #[serde(rename = "transfer.cancel")]
    TransferCancel {
        /// Nummer des Transfers.
        transfer: u64,
    },
    /// Einen Transfer anhalten, ob er wartet oder läuft: Er endet wie bei einer abgerissenen Verbindung und
    /// behält, womit ein neuer Versuch weitermachen kann – dasselbe wie `transfer.suspend`, nur für einen.
    #[serde(rename = "transfer.pause")]
    TransferPause {
        /// Nummer des Transfers.
        transfer: u64,
    },
    /// Einen wartenden Transfer in der Warteschlange vor einen anderen wartenden stellen – ohne `before` ans
    /// Ende. Wartet einer der beiden nicht mehr, bleibt die Reihe, wie sie ist (`not_waiting`).
    #[serde(rename = "transfer.move")]
    TransferMove {
        /// Nummer des Transfers, der umzieht.
        transfer: u64,
        /// Nummer des Transfers, vor dem er danach steht.
        #[serde(default)]
        before: Option<u64>,
    },
    /// Die Transfers, die die App noch zeigt und noch einmal versuchen könnte. Was der Kern von anderen noch weiß,
    /// vergisst er – samt der Teil-Dateien, die nur ihretwegen liegen geblieben sind (`transfers::Trails`). Die
    /// App schickt die Liste beim Start und wenn der Benutzer Zeilen aus ihr entfernt.
    #[serde(rename = "transfer.retain")]
    TransferRetain {
        /// Die Transfers, benannt wie in `transfer.start`.
        jobs: Vec<TransferJob>,
    },
    /// Die App wird beendet: Alle Transfers enden wie bei einer abgerissenen Verbindung und behalten, womit es
    /// nach dem nächsten Start weitergehen kann – auch wenn die App danach ihre Sitzungen schließt, was sonst ein
    /// Abbruch wäre. Die Transfers melden ihr Ende wie immer selbst.
    #[serde(rename = "transfer.suspend")]
    TransferSuspend,
    /// Wie oft ein Lebenszeichen an jeden Server geht und nach wie vielen unbeantworteten die Verbindung als
    /// abgerissen gilt – für Profile, die nichts Eigenes nennen. Gilt für Verbindungen, die danach entstehen.
    #[serde(rename = "keepalive.set")]
    KeepaliveSet {
        /// Sekunden zwischen zwei Lebenszeichen; 0 = keine.
        interval: u32,
        /// So viele dürfen unbeantwortet bleiben.
        max: u32,
    },
    /// Das Display des X-Servers dieses Rechners, zu dem X11-Verbindungen durchgereicht werden (0 = Port 6000).
    /// Gilt für Verbindungen, die danach aufgebaut werden.
    #[serde(rename = "x11.display")]
    X11Display {
        /// Nummer des Displays, 0 bis 63.
        display: u16,
    },
    /// Startet oder hält einen Tunnel des Profils dieser Sitzung an (`on`). Er läuft über die Verbindung der
    /// Sitzung; wie es ausging, meldet `tunnel.state`.
    #[serde(rename = "tunnel.set")]
    TunnelSet {
        /// Nummer der Sitzung, über deren Verbindung der Tunnel läuft.
        session: u64,
        /// Kennung des Tunnels im Profil.
        tunnel: String,
        /// Starten (`true`) oder anhalten.
        on: bool,
    },
    /// Verbindungstest für ein Profil, wie es gerade im Dialog steht (gespeichert oder nicht): Name auflösen, Port
    /// öffnen, Host-Schlüssel und Anmeldeverfahren ansehen – ohne sich anzumelden, ohne etwas zu merken. Das
    /// Ergebnis kommt als `probe.result`.
    #[serde(rename = "probe")]
    Probe {
        /// Nummer der Anfrage; steht im Ergebnis wieder.
        request: u64,
        /// Das Profil. Wird geprüft wie beim Speichern.
        profile: Box<Profile>,
    },
    /// Entsperrte Schlüssel (mit Passphrase) im Speicher behalten, bis der Kern endet – dann fragt nur die erste
    /// Verbindung nach der Passphrase. `remember: false` stellt es ab und vergisst sofort alle; ohne `remember`
    /// werden nur die gemerkten vergessen.
    #[serde(rename = "keys.unlocked")]
    KeysUnlocked {
        /// Ein- oder ausschalten; fehlt die Angabe, bleibt es, wie es ist.
        #[serde(default)]
        remember: Option<bool>,
    },
    /// Die Shells dieses Rechners für ein lokales Terminal (`shells` in der Antwort). Unter Windows fragt der Kern
    /// dafür `wsl.exe` nach den Distributionen – die Antwort kann bis zu drei Sekunden dauern.
    #[serde(rename = "local.shells")]
    LocalShells,
    /// Obergrenze für die Bandbreite aller Transfers zusammen, in Bytes je Sekunde; 0 = keine. Gilt sofort.
    #[serde(rename = "transfer.limit")]
    TransferLimit {
        /// Bytes je Sekunde.
        bytes_per_second: u64,
    },
    /// Auslastung des Servers melden (`monitor.stats`) oder damit aufhören.
    #[serde(rename = "monitor.set")]
    MonitorSet {
        /// Nummer der Sitzung.
        session: u64,
        /// Ein oder aus.
        enabled: bool,
    },
    /// Die Server, denen in der eigenen `known_hosts` vertraut wird.
    #[serde(rename = "hostkeys.list")]
    HostKeysList,
    /// Einen Server aus der eigenen `known_hosts` vergessen: Beim nächsten Verbinden wird wieder gefragt.
    #[serde(rename = "hostkeys.forget")]
    HostKeysForget {
        /// Der Eintrag, wie `hostkeys.list` ihn nennt.
        name: String,
        /// Bei einer Zertifizierungsstelle: der Fingerabdruck ihres Schlüssels – `name` sind dann ihre Muster.
        #[serde(default)]
        authority: Option<String>,
    },
    /// Eine Zertifizierungsstelle für Host-Zertifikate in die eigene `known_hosts` eintragen (`@cert-authority`):
    /// Server, auf die `patterns` passt und die ein Zertifikat dieser Stelle zeigen, gelten danach ohne Rückfrage
    /// als vertraut. Nur auf ausdrücklichen Wunsch des Benutzers.
    #[serde(rename = "hostkeys.authority")]
    HostKeysAuthority {
        /// Muster der Server, durch Komma getrennt (`*.example.org`).
        patterns: String,
        /// Öffentlicher Schlüssel der Stelle als Zeile (`ssh-ed25519 AAAA…`).
        key: String,
    },
    /// Server aus `~/.ssh/config` auflisten.
    #[serde(rename = "sshconfig.read")]
    SshConfigRead,
    /// Server aus `~/.ssh/config` als Profile anlegen.
    #[serde(rename = "sshconfig.import")]
    SshConfigImport {
        /// Die Aliasse (`Host …`), die übernommen werden.
        names: Vec<String>,
        /// Die Profile bleiben mit ihrem Eintrag verknüpft: Ihre Angaben werden bei jedem Start neu aus der Datei
        /// gelesen (`sshconfig.refresh`).
        #[serde(default)]
        linked: bool,
    },
    /// Liest die Angaben der verknüpften Profile neu aus `~/.ssh/config`. Die Antwort nennt in `imported`, wie viele
    /// sich geändert haben.
    #[serde(rename = "sshconfig.refresh")]
    SshConfigRefresh,
    /// Antwort des Benutzers auf eine Rückfrage (`prompt.*`-Ereignis).
    #[serde(rename = "prompt.answer")]
    PromptAnswer {
        /// Nummer der Rückfrage.
        prompt: u64,
        /// Die Antwort.
        #[serde(flatten)]
        answer: Answer,
    },
}

/// Antwort auf eine Rückfrage. Fehlt alles, gilt sie als abgebrochen.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(default)]
pub struct Answer {
    /// Host-Schlüssel: vertrauen und verbinden.
    pub accept: bool,
    /// Geheimnis: die eingegebenen Werte, einer je Feld.
    pub values: Option<Vec<String>>,
    /// Geheimnis: im Schlüsselbund speichern.
    pub save: bool,
}

/// Antwort des Kerns auf eine Anfrage.
#[derive(Debug, Default, Serialize)]
pub struct Response {
    /// Ob die Anfrage ausgeführt wurde.
    pub ok: bool,
    /// Fehlerart, maschinenlesbar.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<&'static str>,
    /// Einzelheiten zum Fehler, englisch, für das Protokoll.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
    /// `profiles.list`
    #[serde(skip_serializing_if = "Option::is_none")]
    pub profiles: Option<Vec<ProfileInfo>>,
    /// `profiles.save`
    #[serde(skip_serializing_if = "Option::is_none")]
    pub profile: Option<Profile>,
    /// `sshconfig.read`: die Server der Konfiguration.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hosts: Option<Vec<ConfigHost>>,
    /// `sshconfig.read`: Bedingungen hinter `Match`, die der Kern nicht prüft (`exec` …) – ihre Blöcke gelten nie.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unchecked: Option<Vec<String>>,
    /// `sshconfig.read`: die gelesene Datei. `transfer.target`: das lokale Ziel des Downloads.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    /// `key.generate`: die Zeile des öffentlichen Schlüssels.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub public_key: Option<String>,
    /// `key.list`
    #[serde(skip_serializing_if = "Option::is_none")]
    pub keys: Option<Vec<crate::keyfiles::KeyFile>>,
    /// `hostkeys.list`
    #[serde(skip_serializing_if = "Option::is_none")]
    pub host_keys: Option<Vec<crate::hostkeys::KnownHost>>,
    /// `sshconfig.import`: Zahl der angelegten Profile (samt Sprung-Servern).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub imported: Option<usize>,
    /// `local.shells`
    #[serde(skip_serializing_if = "Option::is_none")]
    pub shells: Option<Vec<LocalShell>>,
}

/// Eine Shell dieses Rechners, wie die App sie zur Wahl stellt – ohne Pfad und Argumente, die kennt nur der Kern.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct LocalShell {
    /// Kennung für das Profil (`host`): `pwsh`, `wsl:Ubuntu`, `login` …
    pub id: String,
    /// Anzeigename, englisch.
    pub name: String,
}

impl Response {
    /// Erfolg ohne Daten.
    pub fn ok() -> Self {
        Self {
            ok: true,
            ..Self::default()
        }
    }

    /// Fehler mit Art und Einzelheiten.
    pub fn error(error: &'static str, message: impl Into<String>) -> Self {
        Self {
            error: Some(error),
            message: Some(message.into()),
            ..Self::default()
        }
    }
}

/// Ein Profil in der Liste, mit dem, was nicht in `profiles.json` steht.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct ProfileInfo {
    /// Das Profil.
    #[serde(flatten)]
    pub profile: Profile,
    /// Ob im Schlüsselbund ein Passwort oder eine Passphrase zu diesem Profil liegt.
    pub saved_secret: bool,
}

/// Woran der Kern einen Transfer wiedererkennt: die Angaben aus `transfer.start`, ohne Sitzung und Nummer.
#[derive(Clone, Debug, Deserialize)]
pub struct TransferJob {
    /// Richtung.
    pub direction: Direction,
    /// Upload: lokale Datei oder lokales Verzeichnis. Download: lokales Zielverzeichnis.
    pub local: String,
    /// Upload: Zielverzeichnis auf dem Server. Download: Datei oder Verzeichnis dort.
    pub remote: String,
    /// Upload: Name im Zielverzeichnis, wenn er nicht der lokale sein soll.
    #[serde(default)]
    pub name: Option<String>,
}

/// Welche Dateien ein Transfer am Ziel stehen lässt, statt sie zu überschreiben. Verglichen werden Größe und
/// Änderungszeit, wie Quelle und Ziel sie nennen; fehlt eine Zeit, wird übertragen.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Keep {
    /// Was gleich groß ist und am Ziel nicht älter als die Quelle – es wurde schon einmal übertragen.
    Same,
    /// Was am Ziel nicht älter ist als die Quelle: Übertragen wird nur Neueres.
    Newer,
}

impl Keep {
    /// So viele Sekunden dürfen die Uhren auseinanderliegen (manche Dateisysteme merken sich nur gerade Sekunden).
    const SLACK: u64 = 2;

    /// Ob die Datei am Ziel bleibt, wie sie ist. Zeiten in Sekunden seit 1970.
    pub(crate) fn leaves(
        self,
        size: u64,
        modified: Option<u64>,
        there: Option<u64>,
        there_modified: Option<u64>,
    ) -> bool {
        let (Some(source), Some(target)) = (modified, there_modified) else {
            return false;
        };
        let not_newer = source <= target.saturating_add(Self::SLACK);
        match self {
            Self::Newer => not_newer,
            Self::Same => not_newer && there == Some(size),
        }
    }
}

/// Richtung eines Transfers.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Direction {
    /// Vom Rechner zum Server.
    Upload,
    /// Vom Server zum Rechner.
    Download,
    /// Von einem Server zu einem anderen, ohne die Platte dieses Rechners.
    Relay,
}

/// Stand eines Transfers.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum TransferPhase {
    /// Wartet auf einen freien Platz.
    Queued,
    /// Daten fließen.
    Running,
    /// Alles übertragen.
    Completed,
    /// Gescheitert; `message` sagt, woran.
    Failed,
    /// Vom Benutzer abgebrochen oder mit der Sitzung beendet.
    Cancelled,
}

/// Art eines Verzeichniseintrags.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum EntryKind {
    /// Gewöhnliche Datei.
    File,
    /// Verzeichnis.
    Directory,
    /// Pipe, Gerät, Socket oder ein Link ohne Ziel – nichts, was sich öffnen oder übertragen ließe.
    Other,
}

/// Quelle und Ziel für `sftp.copy`.
#[derive(Clone, Debug, PartialEq, Eq, Deserialize)]
pub struct CopyPair {
    /// Was kopiert oder verschoben wird, als absoluter Pfad.
    pub from: String,
    /// Wie es danach heißt, als absoluter Pfad.
    pub to: String,
}

/// Ein Eintrag eines Verzeichnisses auf dem Server.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct Entry {
    /// Name ohne Pfad.
    pub name: String,
    /// Art – bei einem Link die des Ziels.
    pub kind: EntryKind,
    /// Ob der Eintrag ein symbolischer Link ist.
    pub link: bool,
    /// Größe in Bytes.
    pub size: u64,
    /// Letzte Änderung, Sekunden seit 1970, falls der Server sie nennt.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub modified: Option<u64>,
    /// Zugriffsrechte (die unteren Bits von `st_mode`, z. B. `0o644`).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub permissions: Option<u32>,
    /// Besitzer: der Name, wenn der Server ihn nennt, sonst die Nummer.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub owner: Option<String>,
    /// Gruppe, wie `owner`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub group: Option<String>,
    /// Nur bei einem Link: wohin er zeigt, so wie es im Link steht (relativ oder absolut, nicht aufgelöst). Fremder
    /// Text vom Server, auf 4096 Bytes gekürzt. Fehlt, wenn der Server es nicht nennt – und immer bei einem
    /// Dateizugriff über Befehle (ohne SFTP): Dort kostete es je Link einen Befehl; es gibt `sftp.readlink`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target: Option<String>,
    /// Nur bei einem Link: Er zeigt ins Leere – am Ende seiner Kette von Links fehlt etwas. Eine Schleife oder ein
    /// Ziel ohne Zugriff ist nicht gemeint; `kind` ist bei beidem `other`. Wo `target` fehlt, bleibt es offen.
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub dangling: bool,
}

/// Ein Eintrag aus `sftp.tree`: etwas, das ein Download anlegt.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct TreeEntry {
    /// Voller Pfad auf dem Server.
    pub remote: String,
    /// Wo der Eintrag auf diesem Rechner landet: relativ zum Zielverzeichnis, mit `/` getrennt, jeder Teil ein
    /// Name, den es hier geben kann (`transfers::local_name`).
    pub local: String,
    /// Ob es ein Verzeichnis ist. Verzeichnisse stehen vor ihrem Inhalt.
    pub directory: bool,
    /// Größe in Bytes; bei Verzeichnissen 0.
    pub size: u64,
    /// Letzte Änderung, Sekunden seit 1970, falls der Server sie nennt.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub modified: Option<u64>,
}

/// Zustand einer Sitzung.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum SessionState {
    /// TCP-Verbindung und Schlüsselaustausch laufen.
    Connecting,
    /// Der Server ist bestätigt, die Anmeldung läuft.
    Authenticating,
    /// Die Shell steht.
    Connected,
    /// Zu Ende; `reason` sagt, warum.
    Closed,
}

/// Wie es dem Terminal über Mosh geht (`session.mosh`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum MoshState {
    /// Der Server antwortet.
    Running,
    /// Seit einer Weile nichts vom Server; Mosh versucht es weiter.
    Silent,
}

/// Warum eine Sitzung zu Ende ist.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum CloseReason {
    /// Der Benutzer hat sie geschlossen.
    User,
    /// Die Shell auf dem Server ist beendet (`exit`).
    Remote,
    /// Der Server war nicht erreichbar.
    ConnectFailed,
    /// Der Benutzer hat dem Host-Schlüssel nicht vertraut.
    HostKeyRejected,
    /// Keine Anmeldemethode hatte Erfolg oder der Benutzer hat abgebrochen.
    AuthFailed,
    /// Die Verbindung ist abgerissen oder der Server hat sie beendet.
    ConnectionLost,
    /// Das Profil gibt es nicht (mehr).
    UnknownProfile,
}

/// Was der Benutzer eingeben soll.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum SecretKind {
    /// Passwort des Benutzers auf dem Server.
    Password,
    /// Passphrase eines verschlüsselten privaten Schlüssels.
    Passphrase,
    /// Fragen des Servers (keyboard-interactive, z. B. ein Einmalcode).
    KeyboardInteractive,
}

/// Ein Eingabefeld einer Rückfrage.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct SecretField {
    /// Beschriftung; bei Passwort und Passphrase leer, die App kennt den Text selbst.
    pub text: String,
    /// Ob die Eingabe sichtbar sein darf.
    pub echo: bool,
}

/// Eine Netzwerkkarte in `monitor.stats`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct MonitorInterface {
    /// Name der Karte (`eth0`) – nur Buchstaben, Ziffern, `.`, `_` und `-`.
    pub name: String,
    /// Empfangene Bytes je Sekunde.
    pub rx: u64,
    /// Gesendete Bytes je Sekunde.
    pub tx: u64,
    /// Ihre Adressen (IPv4 und IPv6), vom Kern als Adressen gelesen; ohne die nur auf der Leitung gültigen.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub addresses: Vec<String>,
}

/// Der Durchsatz einer Platte in `monitor.stats`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct MonitorIoDisk {
    /// Name des Geräts (`sda`, `nvme0n1`) – nur Buchstaben, Ziffern, `.`, `_` und `-`.
    pub name: String,
    /// Gelesene Bytes je Sekunde; mit `io_mixed` gelesene und geschriebene zusammen.
    pub read: u64,
    /// Geschriebene Bytes je Sekunde.
    pub write: u64,
    /// Anteil der Zeit mit E/A, 0–100.
    pub busy: u8,
}

/// Ein Dateisystem in `monitor.stats`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct MonitorDisk {
    /// Wo es eingehängt ist.
    pub mount: String,
    /// Gerät oder Quelle, wie `df` sie nennt.
    pub device: String,
    /// Belegte Bytes.
    pub used: u64,
    /// Bytes, die Benutzern zur Verfügung stehen (belegt + frei).
    pub total: u64,
    /// Belegte Inodes; fehlt, wenn der Server sie nicht nennt.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub inodes_used: Option<u64>,
    /// Alle Inodes.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub inodes_total: Option<u64>,
}

/// Ereignis des Kerns.
// Die Messung (`MonitorStats`) ist weit größer als die übrigen. Ein Ereignis entsteht, wird zu JSON und fällt
// wieder – in einer Liste liegt keines, die Größe kostet also nichts.
#[allow(clippy::large_enum_variant)]
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(tag = "event")]
pub enum Event {
    /// Etwas ist beim Start schiefgegangen, der Kern läuft aber.
    #[serde(rename = "core.warning")]
    Warning {
        /// Art der Warnung.
        code: &'static str,
        /// Einzelheiten.
        message: String,
    },
    /// Die Profilliste hat sich geändert, ohne dass die App es angestoßen hat.
    #[serde(rename = "profiles.changed")]
    ProfilesChanged,
    /// Eine Sitzung hat ihren Zustand gewechselt.
    #[serde(rename = "session.state")]
    SessionState {
        /// Nummer der Sitzung.
        session: u64,
        /// Neuer Zustand.
        state: SessionState,
        /// Nur bei `closed`.
        #[serde(skip_serializing_if = "Option::is_none")]
        reason: Option<CloseReason>,
        /// Einzelheiten, englisch.
        #[serde(skip_serializing_if = "Option::is_none")]
        message: Option<String>,
        /// Rückgabewert der Shell, falls der Server ihn gemeldet hat.
        #[serde(skip_serializing_if = "Option::is_none")]
        exit_status: Option<u32>,
        /// Nur bei `closed` mit `connect_failed`: woran der Aufbau lag (`name`, `family`, `refused`, `timeout`,
        /// `unreachable`, `algorithms`, `other`) – die Oberfläche macht daraus einen Satz mit Vorschlag.
        #[serde(skip_serializing_if = "Option::is_none")]
        trouble: Option<&'static str>,
        /// Nur bei `connected`: Startverzeichnis des Dateibrowsers. Fehlt, wenn der Server kein SFTP anbietet.
        #[serde(skip_serializing_if = "Option::is_none")]
        home: Option<String>,
        /// Nur bei `connecting` und `authenticating`, wenn die Verbindung über Sprung-Server läuft: der
        /// Server, um den es gerade geht (`user@host`).
        #[serde(skip_serializing_if = "Option::is_none")]
        hop: Option<String>,
    },
    /// Auslastung des Servers, alle paar Sekunden, solange `monitor.set` sie verlangt.
    #[serde(rename = "monitor.stats")]
    MonitorStats {
        /// Nummer der Sitzung.
        session: u64,
        /// CPU-Auslastung seit der vorigen Messung, 0–100.
        cpu: u8,
        /// Belegter Arbeitsspeicher in Bytes (ohne Puffer und Cache).
        memory_used: u64,
        /// Arbeitsspeicher in Bytes.
        memory_total: u64,
        /// Zwischenspeicher (Puffer, Cache), den das System bei Bedarf hergibt; fehlt, wenn der Server nicht aufteilt.
        #[serde(skip_serializing_if = "Option::is_none")]
        memory_cache: Option<u64>,
        /// Ganz freier Arbeitsspeicher in Bytes; fehlt, wenn der Server nicht aufteilt.
        #[serde(skip_serializing_if = "Option::is_none")]
        memory_free: Option<u64>,
        /// Belegte Auslagerung in Bytes.
        swap_used: u64,
        /// Größe der Auslagerung in Bytes; 0, wenn es keine gibt oder der Server sie nicht nennt.
        swap_total: u64,
        /// Belegter Platz auf `/` in Bytes.
        disk_used: u64,
        /// Größe von `/` in Bytes; 0, wenn unbekannt.
        disk_total: u64,
        /// Name des Systems, z. B. `Ubuntu 24.04.1 LTS`.
        os: String,
        /// `uname -sr`.
        kernel: String,
        /// Laufzeit in Sekunden.
        uptime: u64,
        /// Lastdurchschnitt über 1, 5 und 15 Minuten, wie in `/proc/loadavg`.
        load: String,
        /// Empfangene Bytes je Sekunde über alle Netzwerkkarten außer `lo`.
        net_rx: u64,
        /// Gesendete Bytes je Sekunde.
        net_tx: u64,
        /// Dasselbe je Netzwerkkarte, mit ihren Adressen; leer, wenn der Server keine nennt.
        #[serde(skip_serializing_if = "Vec::is_empty")]
        interfaces: Vec<MonitorInterface>,
        /// Von allen Datenträgern zusammen gelesene Bytes je Sekunde; fehlt wie die beiden folgenden, wenn der
        /// Server den Durchsatz nicht nennt (kein `/proc/diskstats`).
        #[serde(skip_serializing_if = "Option::is_none")]
        io_read: Option<u64>,
        /// Geschriebene Bytes je Sekunde.
        #[serde(skip_serializing_if = "Option::is_none")]
        io_write: Option<u64>,
        /// Anteil der Zeit, in der der meistbeschäftigte Datenträger zu tun hatte, 0–100.
        #[serde(skip_serializing_if = "Option::is_none")]
        io_busy: Option<u8>,
        /// Der Server nennt Lesen und Schreiben nur zusammen (OpenBSD): Die Summe steht in `io_read` bzw. `read`.
        #[serde(skip_serializing_if = "std::ops::Not::not")]
        io_mixed: bool,
        /// Dasselbe je Platte; leer, wenn der Server den Durchsatz nicht nennt.
        #[serde(skip_serializing_if = "Vec::is_empty")]
        io_disks: Vec<MonitorIoDisk>,
        /// Auslastung jedes Kerns, 0–100; leer, wenn der Server sie nicht einzeln nennt.
        #[serde(skip_serializing_if = "Vec::is_empty")]
        cores: Vec<u8>,
        /// Anteil der CPU-Zeit für Programme der Benutzer, 0–100; fehlt, wenn der Server nicht aufteilt.
        #[serde(skip_serializing_if = "Option::is_none")]
        cpu_user: Option<u8>,
        /// Anteil für das System (samt Unterbrechungen).
        #[serde(skip_serializing_if = "Option::is_none")]
        cpu_system: Option<u8>,
        /// Anteil, in dem auf Platte oder Netz gewartet wurde – in `cpu` zählt er als untätig.
        #[serde(skip_serializing_if = "Option::is_none")]
        cpu_iowait: Option<u8>,
        /// Anteil, den der Wirt einer virtuellen Maschine anderen gab.
        #[serde(skip_serializing_if = "Option::is_none")]
        cpu_steal: Option<u8>,
        /// Die Dateisysteme des Servers (ohne solche, die nur im Speicher liegen).
        #[serde(skip_serializing_if = "Vec::is_empty")]
        disks: Vec<MonitorDisk>,
        /// Name des Prozessors; fehlt wie alles Folgende, wenn der Server es nicht nennt.
        #[serde(skip_serializing_if = "Option::is_none")]
        cpu_model: Option<String>,
        /// Zahl der Kerne.
        #[serde(skip_serializing_if = "Option::is_none")]
        core_count: Option<u32>,
        /// Hersteller und Modell des Rechners.
        #[serde(skip_serializing_if = "Option::is_none")]
        machine: Option<String>,
        /// `none` für eigene Hardware, sonst die Art der Virtualisierung (`kvm`, `docker` …; `vm` = unbekannt welche).
        #[serde(skip_serializing_if = "Option::is_none")]
        virtualization: Option<String>,
        /// Die Uhr des Servers bei dieser Messung, Sekunden seit 1970.
        #[serde(skip_serializing_if = "Option::is_none")]
        time: Option<u64>,
        /// Abstand der Zeitzone des Servers zu UTC in Sekunden.
        #[serde(skip_serializing_if = "Option::is_none")]
        utc_offset: Option<i32>,
        /// Kürzel der Zeitzone (`CEST`).
        #[serde(skip_serializing_if = "Option::is_none")]
        zone: Option<String>,
    },
    /// Was der Server vor der Anmeldung zeigt (`Banner` in `sshd_config`), auf druckbare Zeichen beschränkt.
    #[serde(rename = "session.banner")]
    Banner {
        /// Nummer der Sitzung.
        session: u64,
        /// Der Text.
        text: String,
    },
    /// Die Sitzung hat keine Shell – der Server hat sie abgelehnt oder sofort beendet (Konto, das nur SFTP darf) –,
    /// bleibt aber für Dateien offen. Der Zustand ist weiter `connected`; zu Ende ist sie erst mit `closed`.
    #[serde(rename = "session.trace")]
    SessionTrace {
        /// Nummer der Sitzung.
        session: u64,
        /// Eine Zeile über den Aufbau der Verbindung, englisch. Enthält Namen von Verfahren, Fingerabdrücke und
        /// Pfade von Schlüsseldateien – nie ein Passwort, eine Passphrase oder den Inhalt eines Schlüssels.
        text: String,
    },
    /// Das Terminal der Sitzung läuft über Mosh (`running`, gleich nach `connected` und wenn Mosh wieder Antworten
    /// bekommt) oder hört seit `silent_for` Sekunden nichts vom Server (`silent`, alle paar Sekunden neu gemeldet).
    /// Fehlt das Ereignis nach `connected`, läuft das Terminal über SSH.
    #[serde(rename = "session.mosh")]
    SessionMosh {
        /// Nummer der Sitzung.
        session: u64,
        /// `running` oder `silent`.
        state: MoshState,
        /// Nur bei `silent`: seit so vielen Sekunden nichts vom Server.
        #[serde(skip_serializing_if = "Option::is_none")]
        silent_for: Option<u64>,
    },
    /// Die SSH-Verbindung unter einem Mosh-Terminal ist weg (oder ein neuer Aufbau scheiterte, dann mit `message`):
    /// Das Terminal läuft weiter, Dateien, Auslastung und Tunnel nicht. Der Kern baut sie neu auf, sobald Mosh
    /// wieder Antworten bekommt – von selbst nicht mehr, nachdem eine Anmeldung scheiterte oder eine Rückfrage
    /// abgelehnt wurde; dann auf `session.relink`.
    #[serde(rename = "session.link_lost")]
    SessionLinkLost {
        /// Nummer der Sitzung.
        session: u64,
        /// Warum ein neuer Aufbau scheiterte, englisch; fehlt, wenn die Verbindung eben erst riss.
        #[serde(skip_serializing_if = "Option::is_none")]
        message: Option<String>,
    },
    /// Die SSH-Verbindung unter einem Mosh-Terminal steht wieder: Dateien, Auslastung und Tunnel gehen wieder.
    #[serde(rename = "session.linked")]
    SessionLinked {
        /// Nummer der Sitzung.
        session: u64,
        /// Startverzeichnis des Dateibrowsers; fehlt, wenn der Server keine Dateien anbietet.
        #[serde(skip_serializing_if = "Option::is_none")]
        home: Option<String>,
    },
    /// Die Sitzung steht ohne Shell: Der Server hat sie abgelehnt oder sofort beendet, Dateien gehen aber.
    #[serde(rename = "session.files_only")]
    SessionFilesOnly {
        /// Nummer der Sitzung.
        session: u64,
        /// Rückgabewert der Shell, falls es einen gab.
        #[serde(skip_serializing_if = "Option::is_none")]
        exit_status: Option<u32>,
    },
    /// Der Server liefert keine Auslastung (kein Linux, Befehle nicht erlaubt).
    #[serde(rename = "monitor.unavailable")]
    MonitorUnavailable {
        /// Nummer der Sitzung.
        session: u64,
        /// Einzelheiten, englisch.
        message: String,
        /// Der Server lieferte schon Werte und verstummte; der Kern versucht es nach einer Pause von selbst neu.
        #[serde(skip_serializing_if = "std::ops::Not::not")]
        retrying: bool,
    },
    /// Ein Tunnel steht (`listening`), wurde angehalten oder endete mit seiner Verbindung (`stopped`), oder er
    /// kam nicht zustande (`failed`, mit dem Grund).
    #[serde(rename = "tunnel.state")]
    TunnelState {
        /// Kennung des Profils.
        profile: String,
        /// Kennung des Tunnels im Profil.
        tunnel: String,
        /// `listening`, `stopped` oder `failed`.
        state: &'static str,
        /// Einzelheiten, englisch.
        #[serde(skip_serializing_if = "Option::is_none")]
        message: Option<String>,
    },
    /// Ergebnis eines Verbindungstests (`probe`). Felder fehlen, wenn der Test nicht so weit kam.
    #[serde(rename = "probe.result")]
    ProbeResult {
        /// Nummer der Anfrage.
        request: u64,
        /// Der letzte Schritt, der gelang: `resolve`, `connect`, `greeting`, `key`, `methods`; leer, wenn keiner.
        step: &'static str,
        /// Die Adressen, die der Name ergibt.
        addresses: Vec<String>,
        /// Die Adresse, die geantwortet hat.
        #[serde(skip_serializing_if = "Option::is_none")]
        address: Option<String>,
        /// Dauer des Verbindungsaufbaus in Millisekunden.
        #[serde(skip_serializing_if = "Option::is_none")]
        connect_ms: Option<u64>,
        /// Als was sich der Dienst vorstellt (`SSH-2.0-OpenSSH_9.6`), gekürzt und ohne Steuerzeichen.
        #[serde(skip_serializing_if = "Option::is_none")]
        software: Option<String>,
        /// Typ des Host-Schlüssels.
        #[serde(skip_serializing_if = "Option::is_none")]
        key_algorithm: Option<String>,
        /// Fingerabdruck des Host-Schlüssels.
        #[serde(skip_serializing_if = "Option::is_none")]
        fingerprint: Option<String>,
        /// `trusted`, `unknown` oder `changed`.
        #[serde(skip_serializing_if = "Option::is_none")]
        key_state: Option<&'static str>,
        /// Angebotene Anmeldeverfahren (`publickey`, `password`, `keyboard-interactive` …).
        methods: Vec<String>,
        /// Woran es scheiterte: die Kennungen aus `session.state` und dazu `jump` (Weg über Sprung-Server) und
        /// `not_ssh` (auf dem Port antwortet kein SSH-Dienst).
        #[serde(skip_serializing_if = "Option::is_none")]
        trouble: Option<&'static str>,
        /// Einzelheit dazu, englisch.
        #[serde(skip_serializing_if = "Option::is_none")]
        message: Option<String>,
    },
    /// Ergebnis einer `sftp.*`-Anfrage.
    #[serde(rename = "sftp.result")]
    SftpResult {
        /// Nummer der Anfrage.
        request: u64,
        /// Ob sie gelang.
        ok: bool,
        /// Nur bei `sftp.list`.
        #[serde(skip_serializing_if = "Option::is_none")]
        entries: Option<Vec<Entry>>,
        /// Nur bei `mux.list`.
        #[serde(skip_serializing_if = "Option::is_none")]
        mux: Option<Vec<crate::exec::MuxSession>>,
        /// Nur bei `commands.probe`: die Art der Shell (`posix` oder `windows`).
        #[serde(skip_serializing_if = "Option::is_none")]
        line: Option<String>,
        /// Nur bei `commands.list`: die Namen der Programme im Suchpfad.
        #[serde(skip_serializing_if = "Option::is_none")]
        commands: Option<Vec<String>>,
        /// Nur bei `exec.run`: was der Befehl hinterlassen hat.
        #[serde(skip_serializing_if = "Option::is_none")]
        run: Option<crate::exec::RunOutput>,
        /// Nur bei `logs.read`: die Zeilen, älteste zuerst.
        #[serde(skip_serializing_if = "Option::is_none")]
        logs: Option<Vec<crate::logs::LogLine>>,
        /// Nur bei `logs.read` für das Journal: Das Konto sieht nur seine eigenen Einträge.
        #[serde(skip_serializing_if = "std::ops::Not::not")]
        limited: bool,
        /// Nur bei `sftp.tree`.
        #[serde(skip_serializing_if = "Option::is_none")]
        tree: Option<Vec<TreeEntry>>,
        /// Nur bei `sftp.size`: belegter Platz in Bytes.
        #[serde(skip_serializing_if = "Option::is_none")]
        size: Option<u64>,
        /// Nur bei `sftp.usage`.
        #[serde(skip_serializing_if = "Option::is_none")]
        usage: Option<Vec<crate::exec::Usage>>,
        /// Nur bei `sftp.checksum`: SHA-256 je Datei als Hex, `null` wo es keine gab.
        #[serde(skip_serializing_if = "Option::is_none")]
        sums: Option<Vec<Option<String>>>,
        /// Nur bei `sftp.read`: das gelesene Stück als Base64; `size` nennt dann die Größe der ganzen Datei.
        #[serde(skip_serializing_if = "Option::is_none")]
        data: Option<String>,
        /// Nur bei `sftp.readlink`: wohin der Link zeigt, wie bei `entries[].target`.
        #[serde(skip_serializing_if = "Option::is_none")]
        target: Option<String>,
        /// Nur bei `sftp.find`, und nur wenn die Suche vorzeitig aufhörte: nach so vielen angesehenen Einträgen.
        /// Es kann dann weitere Treffer geben.
        #[serde(skip_serializing_if = "Option::is_none")]
        seen: Option<u64>,
        /// Fehlerart, maschinenlesbar.
        #[serde(skip_serializing_if = "Option::is_none")]
        error: Option<&'static str>,
        /// Meldung des Servers, englisch.
        #[serde(skip_serializing_if = "Option::is_none")]
        message: Option<String>,
    },
    /// Was beim Mitlesen einer Datei dazugekommen ist.
    #[serde(rename = "follow.data")]
    FollowData {
        /// Nummer des Mitlesens.
        follow: u64,
        /// Der neue Text – Ausgabe des Servers, also fremd.
        text: String,
    },
    /// Ein Mitlesen ist zu Ende: beendet, oder der Befehl endete von selbst (Datei fehlt, keine Rechte).
    #[serde(rename = "follow.end")]
    FollowEnd {
        /// Nummer des Mitlesens.
        follow: u64,
        /// Fehlerart, wenn es nicht auf Wunsch endete.
        #[serde(skip_serializing_if = "Option::is_none")]
        error: Option<&'static str>,
        /// Meldung des Servers, englisch.
        #[serde(skip_serializing_if = "Option::is_none")]
        message: Option<String>,
    },
    /// Stand eines Transfers.
    #[serde(rename = "transfer.state")]
    TransferState {
        /// Nummer des Transfers.
        transfer: u64,
        /// Stand.
        state: TransferPhase,
        /// Übertragene Bytes.
        bytes: u64,
        /// Gesamtgröße; bei Verzeichnissen erst bekannt, wenn der Baum erfasst ist.
        total: u64,
        /// Fertige Dateien (auch übersprungene, die schon am Ziel waren) – daran misst die App den Fortschritt,
        /// wenn es keine Bytes gibt (lauter leere Dateien).
        files_done: u64,
        /// Alle Dateien; wie `total` erst bekannt, wenn der Baum erfasst ist.
        files: u64,
        /// Ein neuer Versuch: so viel war vom Versuch davor schon am Ziel und ging nicht noch einmal über die
        /// Leitung. Fehlt, wenn alles von vorn übertragen wird.
        #[serde(skip_serializing_if = "Option::is_none")]
        resumed: Option<u64>,
        /// Nur bei `failed`.
        #[serde(skip_serializing_if = "Option::is_none")]
        message: Option<String>,
    },
    /// Der Benutzer soll über den Host-Schlüssel entscheiden.
    #[serde(rename = "prompt.host_key")]
    HostKeyPrompt {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Rückfrage, für `prompt.answer`.
        prompt: u64,
        /// Server.
        host: String,
        /// Port.
        port: u16,
        /// Typ des Schlüssels, z. B. `ssh-ed25519`.
        algorithm: String,
        /// Fingerabdruck, `SHA256:…`.
        fingerprint: String,
        /// `true`, wenn für den Server ein anderer Schlüssel hinterlegt ist.
        changed: bool,
        /// Bei `changed`: Datei mit dem alten Eintrag.
        #[serde(skip_serializing_if = "Option::is_none")]
        known_hosts_file: Option<String>,
        /// Bei `changed`: Zeile des alten Eintrags.
        #[serde(skip_serializing_if = "Option::is_none")]
        known_hosts_line: Option<usize>,
        /// Bei `changed`: Fingerabdruck des Schlüssels, der bisher hinterlegt ist.
        #[serde(skip_serializing_if = "Option::is_none")]
        previous_fingerprint: Option<String>,
        /// Bei `changed`: `true`, wenn der alte Eintrag in der Datei des Benutzers steht (`~/.ssh/known_hosts`) –
        /// die App ändert sie nie, der Eintrag bleibt also stehen.
        #[serde(skip_serializing_if = "Option::is_none")]
        known_hosts_foreign: Option<bool>,
    },
    /// Der Benutzer soll ein Geheimnis eingeben.
    #[serde(rename = "prompt.secret")]
    SecretPrompt {
        /// Nummer der Sitzung.
        session: u64,
        /// Nummer der Rückfrage, für `prompt.answer`.
        prompt: u64,
        /// Was gefragt ist.
        kind: SecretKind,
        /// Benutzer auf dem Server.
        user: String,
        /// Server, bei abweichendem Port mit `:port` – über Sprung-Server gibt es mehrere, auch unter einer Adresse.
        host: String,
        /// Bei `passphrase`: die Schlüsseldatei.
        #[serde(skip_serializing_if = "Option::is_none")]
        key_path: Option<String>,
        /// Bei `keyboard_interactive`: Überschrift des Servers.
        #[serde(skip_serializing_if = "Option::is_none")]
        name: Option<String>,
        /// Bei `keyboard_interactive`: Hinweistext des Servers.
        #[serde(skip_serializing_if = "Option::is_none")]
        instructions: Option<String>,
        /// Die Eingabefelder.
        fields: Vec<SecretField>,
        /// Ob „speichern“ angeboten werden kann.
        can_save: bool,
        /// Wievielter Versuch (ab 1); ab 2 war die vorige Eingabe falsch.
        attempt: u32,
    },
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn requests_are_tagged_by_op() {
        let request: Request =
            serde_json::from_str(r#"{"op":"session.open","session":7,"profile_id":"p","cols":120,"rows":30}"#).unwrap();
        assert!(matches!(
            request,
            Request::SessionOpen {
                session: 7,
                cols: 120,
                rows: 30,
                share: None,
                ..
            }
        ));

        assert!(matches!(
            serde_json::from_str::<Request>(r#"{"op":"profiles.list"}"#).unwrap(),
            Request::ProfilesList
        ));
        assert!(serde_json::from_str::<Request>(r#"{"op":"format.disk"}"#).is_err());
    }

    #[test]
    fn a_prompt_answer_without_fields_is_a_cancel() {
        let Request::PromptAnswer { prompt, answer } =
            serde_json::from_str(r#"{"op":"prompt.answer","prompt":3}"#).unwrap()
        else {
            panic!("falsche Variante");
        };
        assert_eq!(prompt, 3);
        assert!(!answer.accept);
        assert!(answer.values.is_none());
    }

    #[test]
    fn events_are_tagged_and_omit_empty_fields() {
        let json = serde_json::to_string(&Event::SessionState {
            session: 1,
            state: SessionState::Closed,
            reason: Some(CloseReason::AuthFailed),
            message: None,
            exit_status: None,
            trouble: None,
            home: None,
            hop: None,
        })
        .unwrap();
        assert_eq!(
            json,
            r#"{"event":"session.state","session":1,"state":"closed","reason":"auth_failed"}"#
        );
    }

    #[test]
    fn mosh_has_its_own_events_and_the_app_names_the_prediction() {
        let Request::SessionOpen { mosh_predict, .. } = serde_json::from_str(
            r#"{"op":"session.open","session":7,"profile_id":"p","cols":80,"rows":24,"mosh_predict":"never"}"#,
        )
        .unwrap() else {
            panic!("falsche Variante");
        };
        assert_eq!(mosh_predict.as_deref(), Some("never"));
        assert!(matches!(
            serde_json::from_str::<Request>(r#"{"op":"session.relink","session":7}"#).unwrap(),
            Request::SessionRelink { session: 7 }
        ));

        let json = |event: Event| serde_json::to_string(&event).unwrap();
        assert_eq!(
            json(Event::SessionMosh {
                session: 1,
                state: MoshState::Running,
                silent_for: None
            }),
            r#"{"event":"session.mosh","session":1,"state":"running"}"#
        );
        assert_eq!(
            json(Event::SessionMosh {
                session: 1,
                state: MoshState::Silent,
                silent_for: Some(12)
            }),
            r#"{"event":"session.mosh","session":1,"state":"silent","silent_for":12}"#
        );
        assert_eq!(
            json(Event::SessionLinkLost {
                session: 1,
                message: None
            }),
            r#"{"event":"session.link_lost","session":1}"#
        );
        assert_eq!(
            json(Event::SessionLinked {
                session: 1,
                home: Some("/home/x".into())
            }),
            r#"{"event":"session.linked","session":1,"home":"/home/x"}"#
        );
    }

    #[test]
    fn responses_carry_only_what_is_set() {
        assert_eq!(serde_json::to_string(&Response::ok()).unwrap(), r#"{"ok":true}"#);
        assert_eq!(
            serde_json::to_string(&Response::error("invalid", "host")).unwrap(),
            r#"{"ok":false,"error":"invalid","message":"host"}"#
        );
    }

    #[test]
    fn the_shells_of_this_computer_go_to_the_app_without_a_path() {
        assert!(matches!(
            serde_json::from_str::<Request>(r#"{"op":"local.shells"}"#).unwrap(),
            Request::LocalShells
        ));
        let response = Response {
            ok: true,
            shells: Some(vec![LocalShell {
                id: "wsl:Ubuntu".to_owned(),
                name: "WSL: Ubuntu".to_owned(),
            }]),
            ..Response::default()
        };
        assert_eq!(
            serde_json::to_string(&response).unwrap(),
            r#"{"ok":true,"shells":[{"id":"wsl:Ubuntu","name":"WSL: Ubuntu"}]}"#
        );
    }
}
