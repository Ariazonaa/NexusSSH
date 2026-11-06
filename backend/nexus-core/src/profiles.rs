//! Serverprofile (Spec §8) und ihre Ablage als `profiles.json`.
//!
//! Ein Profil enthält nie ein Passwort oder eine Passphrase – die liegen im [`crate::secrets::SecretStore`].

use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

/// Die Kennfarben, die ein Profil tragen kann. Die Töne dazu wählt die Oberfläche.
pub const COLORS: [&str; 6] = ["red", "orange", "yellow", "green", "blue", "violet"];

/// Mehr Tunnel trägt ein Profil nicht.
pub const MAX_TUNNELS: usize = 32;

/// Welche Art von Portweiterleitung ein [`Tunnel`] ist.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum TunnelKind {
    /// Wie `ssh -L`: Ein Port auf diesem Rechner führt zu einem Dienst, den der Server erreicht.
    #[default]
    Local,
    /// Wie `ssh -R`: Ein Port am Server führt zu einem Dienst, den dieser Rechner erreicht.
    Remote,
    /// Wie `ssh -D`: Ein Port auf diesem Rechner ist ein SOCKS-Proxy, der über den Server hinausgeht.
    Dynamic,
}

/// Eine Portweiterleitung eines Profils. Sie steht, solange eine Verbindung zu diesem Server steht.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct Tunnel {
    /// Kennung innerhalb des Profils; leer bei einem neuen Tunnel, dann vergibt [`Profile::normalized`] eine.
    pub id: String,
    /// Lokal, entfernt oder dynamisch.
    pub kind: TunnelKind,
    /// Anzeigename; leer heißt: Die App nennt den Tunnel nach seinen Ports.
    pub name: String,
    /// Der Port, an dem gelauscht wird – auf diesem Rechner (`local`, `dynamic`) oder am Server (`remote`).
    pub listen_port: u16,
    /// Wohin es geht: vom Server aus gesehen (`local`) oder von diesem Rechner aus (`remote`). Leer bei `dynamic`.
    pub target_host: String,
    /// Der Port am Ziel.
    pub target_port: u16,
    /// Nicht nur der Rechner selbst darf sich an den Port hängen, sondern das ganze Netz. Nur auf ausdrücklichen
    /// Wunsch: Jeder im Netz käme sonst an den Dienst dahinter. Bei `remote` entscheidet zudem der Server
    /// (`GatewayPorts`), ob er das zulässt.
    pub open: bool,
    /// Mit der Verbindung starten. Sonst erst auf Zuruf.
    pub auto: bool,
}

impl Tunnel {
    /// Prüft einen Tunnel und räumt ihn auf; `None`, wenn er so nicht stehen kann.
    pub(crate) fn normalized(mut self) -> Option<Self> {
        self.id = self.id.trim().to_owned();
        self.name = self.name.trim().chars().filter(|c| !c.is_control()).take(64).collect();
        self.target_host = self.target_host.trim().to_owned();
        if !self.id.is_empty()
            && (self.id.len() > 32
                || !self
                    .id
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_')))
        {
            return None;
        }

        if self.listen_port == 0 {
            return None;
        }

        if self.kind == TunnelKind::Dynamic {
            (self.target_host, self.target_port) = (String::new(), 0);
            return Some(self);
        }

        // Ein Hostname oder eine Adresse, nichts sonst: Er geht an den Server (`local`) oder an den Resolver
        // dieses Rechners (`remote`).
        let plain = !self.target_host.is_empty()
            && self.target_host.len() <= 255
            && self
                .target_host
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '-' | '_' | ':'));
        (plain && self.target_port != 0).then_some(self)
    }
}

/// Ein gespeicherter Server.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct Profile {
    /// Eindeutige Kennung; leer bei einem neuen Profil, dann vergibt [`ProfileStore::save`] eine.
    pub id: String,
    /// Anzeigename.
    pub name: String,
    /// Hostname oder IP-Adresse.
    pub host: String,
    /// TCP-Port.
    pub port: u16,
    /// Benutzername auf dem Server.
    pub user: String,
    /// Gruppe in der Serverliste.
    pub group: String,
    /// Freie Schlagworte.
    pub tags: Vec<String>,
    /// Favoriten stehen in der Suche oben.
    pub favorite: bool,
    /// Privater Schlüssel für die Anmeldung; ohne Angabe gelten die Standarddateien in `~/.ssh`.
    pub key_path: Option<String>,
    /// Schlüssel aus dem SSH-Agenten (OpenSSH-Agent, Pageant) anbieten.
    pub use_agent: bool,
    /// Kennung eines anderen Profils, über dessen Server die Verbindung läuft (Sprung-Server, wie `ProxyJump`).
    pub jump: Option<String>,
    /// Dem Server erlauben, den lokalen SSH-Agenten zu benutzen (Agent Forwarding). Nur für Server, denen man
    /// vertraut: Wer dort Root ist, kann sich solange mit den Schlüsseln des Agenten anmelden.
    pub forward_agent: bool,
    /// Grafische Programme des Servers auf dem X-Server dieses Rechners zeichnen lassen (X11-Forwarding). Wie
    /// beim Agenten nur für Server, denen man vertraut: Wer dort Root ist, erreicht solange den lokalen X-Server.
    #[serde(default)]
    pub forward_x11: bool,
    /// Kennfarbe (einer der Namen in [`COLORS`]) – damit man Produktion und Test an Tab und Kachel unterscheidet.
    pub color: Option<String>,
    /// Kennung des Farbschemas, in dem das Terminal dieses Servers erscheint (Produktion z. B. mit dunkelrotem
    /// Hintergrund). Der Kern merkt sie sich nur – die Schemata kennt allein die App; `None` = das allgemeine.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub theme: Option<String>,
    /// Kennung einer Identität der App (Benutzer und Schlüssel, die mehrere Profile teilen). Der Kern merkt sie
    /// sich nur: Verbunden wird immer mit `user`, `key_path` und `use_agent` dieses Profils, die die App aus der
    /// Identität hineinschreibt.
    #[serde(default)]
    pub identity: Option<String>,
    /// `telnet` für eine Telnet-Verbindung; ohne Angabe SSH. Telnet ist unverschlüsselt und kennt weder
    /// Anmeldung durch den Kern noch Sprung-Server, Dateien oder Auslastung.
    #[serde(default)]
    pub protocol: Option<String>,
    /// Bei `protocol: ftp`: `none` verbindet ohne TLS (Klartext). Ohne Angabe gilt FTPS (`AUTH TLS`).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ftp_tls: Option<String>,
    /// Bei `protocol: ftp`: Der Server baut die Datenverbindungen zu diesem Rechner auf (aktiver Modus), statt
    /// dass dieser Rechner sie zum Server aufbaut. Nur auf Wunsch – die Vorgabe ist passiv.
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub ftp_active: bool,
    /// Bei `protocol: ftp`: wie der Server Dateinamen schreibt, wenn nicht als UTF-8 – `latin1` (ISO-8859-1) oder
    /// `windows1252`. Ohne Angabe UTF-8. Gilt für alles, was über die Steuerverbindung geht.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ftp_encoding: Option<String>,
    /// Bei `protocol: serial` die Geschwindigkeit in Baud; `host` nennt dann die Schnittstelle (`COM3`). Ohne
    /// Angabe 9600. Übertragen wird mit 8 Datenbits, ohne Parität, mit einem Stoppbit und ohne Flusskontrolle.
    #[serde(default)]
    pub baud: Option<u32>,
    /// Bei `protocol: local` der Ordner, in dem die Shell startet; ohne Angabe der Ordner des Benutzers. `host`
    /// nennt dann die Kennung der Shell (`pwsh`, `wsl:Ubuntu`, `login` …) – Programm und Argumente kennt nur der
    /// Kern (`local`).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub local_dir: Option<String>,
    /// Kennung des Betriebssystems (`ubuntu`, `debian`, `freebsd` …), wie die App sie beim Verbinden erkannt hat –
    /// nur für das Symbol an Kachel und Kopfzeile. Der Kern merkt sie sich bloß.
    #[serde(default)]
    pub system: Option<String>,
    /// Freier Text des Benutzers zum Server (Zweck, Ansprechpartner, Wartungsfenster). Der Kern merkt ihn sich bloß.
    #[serde(default)]
    pub notes: String,
    /// Nach einem Abriss nicht von selbst neu verbinden – die App fragt das Profil, bevor sie es versucht.
    #[serde(default)]
    pub no_reconnect: bool,
    /// `ipv4` oder `ipv6`, wenn nur Adressen dieser Art versucht werden sollen; ohne Angabe beide.
    #[serde(default)]
    pub address_family: Option<String>,
    /// Wie viele Sekunden der Aufbau der Verbindung dauern darf; ohne Angabe 15.
    #[serde(default)]
    pub connect_timeout: Option<u32>,
    /// Alle wie viele Sekunden ein Lebenszeichen an den Server geht; 0 = keines. Ohne Angabe gilt, was die App
    /// allgemein nennt (`keepalive.set`).
    #[serde(default)]
    pub keepalive_interval: Option<u32>,
    /// Nach wie vielen unbeantworteten Lebenszeichen die Verbindung als abgerissen gilt; ohne Angabe wie allgemein.
    #[serde(default)]
    pub keepalive_max: Option<u32>,
    /// Als welches Terminal sich die App beim Server ausgibt (`TERM`); ohne Angabe [`DEFAULT_TERM`].
    #[serde(default)]
    pub term: Option<String>,
    /// Umgebungsvariablen für die Shell (`LANG` …). Der Server nimmt nur an, was er erlaubt (`AcceptEnv`).
    #[serde(default)]
    pub env: Vec<EnvVar>,
    /// In welchem Zeichensatz der Server spricht, wenn nicht in UTF-8 (siehe [`CHARSETS`]). Der Kern merkt es sich
    /// bloß – umgerechnet wird in der App, für jede Art von Verbindung an einer Stelle.
    #[serde(default)]
    pub charset: Option<String>,
    /// Nur den genannten Schlüssel anbieten (`IdentitiesOnly`): keine weiteren aus dem Agenten. Für Agenten und
    /// Passwort-Manager mit vielen Schlüsseln – jeder angebotene zählt beim Server als Versuch. Gilt nur, wenn
    /// das Profil einen Schlüssel nennt.
    #[serde(default)]
    pub identities_only: bool,
    /// Die Verbindung komprimieren (`zlib`) – für langsame Leitungen; auf schnellen kostet es nur Rechenzeit.
    #[serde(default)]
    pub compression: bool,
    /// Auch Verfahren anbieten, die als schwach gelten (SHA-1, CBC) – für alte Geräte, die nichts anderes
    /// sprechen. Nur auf ausdrücklichen Wunsch im Profil.
    #[serde(default)]
    pub legacy_algorithms: bool,
    /// Der Name hinter `Host` in `~/.ssh/config`, mit dem dieses Profil verknüpft ist: Adresse, Port, Benutzer und
    /// Schlüssel werden dann bei jedem Start neu aus der Datei gelesen (`sshconfig::refresh`). Ohne Angabe gilt, was
    /// im Profil steht.
    pub ssh_alias: Option<String>,
    /// Ein vorläufiges Profil: für eine Verbindung ohne gespeicherten Server („Schnellverbinden“) angelegt. Es
    /// lebt, solange die App es braucht; was beim Start des Kerns noch da ist (Absturz), fällt weg
    /// ([`ProfileStore::drop_transient`]).
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub transient: bool,
    /// Portweiterleitungen, die mit einer Verbindung zu diesem Server stehen (nur SSH).
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub tunnels: Vec<Tunnel>,
    /// Das Terminal über Mosh (UDP) führen statt über den Kanal der SSH-Verbindung – angemeldet wird weiter über
    /// SSH, Dateien, Auslastung und Tunnel bleiben dort. Nur SSH, nie über einen Sprung-Server; mit Mosh gibt es
    /// kein Agent- und kein X11-Forwarding und nur UTF-8 (wie bei `mosh` selbst).
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub mosh: bool,
    /// Die UDP-Ports, aus denen `mosh-server` einen nimmt: `60001` oder `60001:60010`. Ohne Angabe wählt er selbst.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mosh_ports: Option<String>,
    /// Letzte erfolgreiche Verbindung, Sekunden seit 1970.
    pub last_connected: Option<u64>,
}

impl Default for Profile {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            host: String::new(),
            port: 22,
            user: String::new(),
            group: String::new(),
            tags: Vec::new(),
            favorite: false,
            key_path: None,
            use_agent: true,
            jump: None,
            forward_agent: false,
            forward_x11: false,
            color: None,
            theme: None,
            identity: None,
            protocol: None,
            ftp_tls: None,
            ftp_active: false,
            ftp_encoding: None,
            baud: None,
            local_dir: None,
            system: None,
            notes: String::new(),
            no_reconnect: false,
            address_family: None,
            connect_timeout: None,
            keepalive_interval: None,
            keepalive_max: None,
            term: None,
            env: Vec::new(),
            charset: None,
            identities_only: false,
            compression: false,
            legacy_algorithms: false,
            ssh_alias: None,
            transient: false,
            tunnels: Vec::new(),
            mosh: false,
            mosh_ports: None,
            last_connected: None,
        }
    }
}

/// Liest einen Bereich von UDP-Ports für `mosh-server`: eine Zahl oder `von:bis`, je 1–65535, von nicht größer als
/// bis. Nur Ziffern – der Bereich steht später in der Befehlszeile, als Zahlen, die der Kern selbst schreibt.
pub(crate) fn mosh_port_range(text: &str) -> Option<(u16, u16)> {
    let port = |digits: &str| -> Option<u16> {
        if digits.is_empty() || digits.len() > 5 || !digits.bytes().all(|byte| byte.is_ascii_digit()) {
            return None;
        }

        digits.parse::<u16>().ok().filter(|port| *port != 0)
    };
    let (from, to) = match text.split_once(':') {
        Some((from, to)) => (port(from)?, port(to)?),
        None => (port(text)?, port(text)?),
    };
    (from <= to).then_some((from, to))
}

impl Profile {
    /// Ob das Profil über Telnet verbindet statt über SSH.
    pub fn is_telnet(&self) -> bool {
        self.protocol.as_deref() == Some("telnet")
    }

    /// Ob das Profil ein FTP-Server ist: nur Dateien, kein Terminal.
    pub fn is_ftp(&self) -> bool {
        self.protocol.as_deref() == Some("ftp")
    }

    /// Ob das FTP-Profil ausdrücklich ohne TLS verbindet – Passwort und Dateien gehen dann im Klartext.
    pub fn ftp_plain(&self) -> bool {
        self.ftp_tls.as_deref() == Some("none")
    }

    /// Ob das FTP-Profil TLS ab dem ersten Byte spricht, statt es mit `AUTH TLS` zu beginnen.
    pub fn ftp_implicit(&self) -> bool {
        self.ftp_tls.as_deref() == Some("implicit")
    }

    /// Ob das Profil eine serielle Schnittstelle dieses Rechners öffnet statt einer Verbindung im Netz.
    pub fn is_serial(&self) -> bool {
        self.protocol.as_deref() == Some("serial")
    }

    /// Ob das Profil eine Shell dieses Rechners öffnet (lokales Terminal) statt einer Verbindung.
    pub fn is_local(&self) -> bool {
        self.protocol.as_deref() == Some("local")
    }

    /// Die Ports für `mosh-server` als Zahlen; `None`, wenn das Profil keine nennt (oder keine gültigen).
    pub fn mosh_port_range(&self) -> Option<(u16, u16)> {
        self.mosh_ports.as_deref().and_then(mosh_port_range)
    }

    /// Räumt Eingaben auf und prüft sie. Der Fehler nennt das betroffene Feld.
    pub fn normalized(mut self) -> Result<Self, &'static str> {
        self.host = self.host.trim().to_owned();
        self.user = self.user.trim().to_owned();
        self.name = self.name.trim().to_owned();
        self.group = self.group.trim().to_owned();
        self.tags = self
            .tags
            .iter()
            .map(|tag| tag.trim().to_owned())
            .filter(|tag| !tag.is_empty())
            .collect();
        self.key_path = self
            .key_path
            .map(|path| path.trim().to_owned())
            .filter(|path| !path.is_empty());
        self.jump = self
            .jump
            .map(|jump| jump.trim().to_owned())
            .filter(|jump| !jump.is_empty());

        // Eine Farbe, die es nicht gibt, ist keine – so bleibt eine Datei von Hand oder aus einer neueren Version
        // lesbar, statt das Profil abzulehnen.
        self.color = self
            .color
            .map(|color| color.trim().to_lowercase())
            .filter(|color| COLORS.contains(&color.as_str()));

        // Die Kennung eines Farbschemas: Kleinbuchstaben, Ziffern und Bindestriche, wie die App sie bildet.
        self.theme = self.theme.map(|theme| theme.trim().to_lowercase()).filter(|theme| {
            !theme.is_empty()
                && theme.len() <= 64
                && theme
                    .bytes()
                    .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-')
        });

        // Eine Kennung, kein Text: Was anders aussieht, ist keine.
        self.identity = self
            .identity
            .map(|identity| identity.trim().to_owned())
            .filter(|identity| {
                !identity.is_empty()
                    && identity.len() <= 64
                    && identity
                        .chars()
                        .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_'))
            });

        // Eine kurze Kennung aus Kleinbuchstaben und Ziffern; alles andere ist keine.
        self.system = self.system.map(|system| system.trim().to_lowercase()).filter(|system| {
            !system.is_empty() && system.len() <= 24 && system.chars().all(|c| c.is_ascii_alphanumeric())
        });

        // Ein Protokoll, das es nicht gibt, ist SSH – wie bei der Farbe bleibt die Datei so lesbar. Über Telnet
        // führt kein Weg über Sprung-Server, und es gibt nichts, wofür ein Schlüssel oder der Agent gut wäre.
        self.protocol = self
            .protocol
            .map(|protocol| protocol.trim().to_lowercase())
            .filter(|protocol| matches!(protocol.as_str(), "telnet" | "serial" | "ftp" | "local"));

        // FTP ist ohne ausdrücklichen Wunsch immer mit TLS (`AUTH TLS`). Ein Wunsch ist nur „none“ (ohne) oder
        // „implicit“ (TLS ab dem ersten Byte, der alte Port 990) – alles andere ist keiner.
        self.ftp_tls = self
            .ftp_tls
            .map(|tls| tls.trim().to_lowercase())
            .filter(|tls| self.protocol.as_deref() == Some("ftp") && (tls == "none" || tls == "implicit"));
        self.ftp_active &= self.is_ftp();
        self.ftp_encoding = self
            .ftp_encoding
            .map(|encoding| encoding.trim().to_lowercase())
            .filter(|encoding| {
                self.protocol.as_deref() == Some("ftp") && (encoding == "latin1" || encoding == "windows1252")
            });
        let local = self.is_local();
        let plain = self.is_telnet() || self.is_serial() || self.is_ftp() || local;
        if plain {
            (self.jump, self.key_path, self.forward_agent, self.forward_x11) = (None, None, false, false);
            (self.compression, self.legacy_algorithms) = (false, false);
            self.tunnels.clear();
        }

        // Ein lokales Terminal meldet niemanden an: kein Benutzer, keine Identität, kein Agent. Der Startordner
        // gehört nur dorthin; er ist absolut und schlicht – ob es ihn gibt, sieht erst der Start nach.
        if local {
            self.user.clear();
            (self.identity, self.use_agent, self.identities_only) = (None, false, false);
        }

        self.local_dir = self
            .local_dir
            .map(|dir| dir.trim().to_owned())
            .filter(|dir| local && !dir.is_empty());
        if let Some(dir) = &self.local_dir
            && (dir.len() > MAX_LOCAL_DIR
                || !Path::new(dir).is_absolute()
                || dir
                    .chars()
                    .any(|c| c.is_control() || matches!(c, '\u{202a}'..='\u{202e}' | '\u{2066}'..='\u{2069}')))
        {
            return Err("local_dir");
        }

        // Die Notiz ist Text des Benutzers: Zeilenumbrüche bleiben, andere Steuerzeichen nicht, und sie hat ein Ende.
        self.notes = self
            .notes
            .replace("\r\n", "\n")
            .chars()
            .filter(|c| *c == '\n' || !c.is_control())
            .take(MAX_NOTES)
            .collect::<String>()
            .trim()
            .to_owned();

        // Eine Art von Adressen, die es nicht gibt, ist keine Einschränkung; eine Wartezeit außerhalb des
        // Sinnvollen ist die übliche. An einer seriellen Schnittstelle gibt es beides nicht.
        // Ein Alias ist ein einzelnes Wort ohne Muster; Telnet und serielle Profile stehen in keiner ssh_config.
        self.ssh_alias = self.ssh_alias.map(|alias| alias.trim().to_owned()).filter(|alias| {
            !plain
                && !alias.is_empty()
                && alias.len() <= 255
                && !alias
                    .chars()
                    .any(|c| c.is_whitespace() || c.is_control() || matches!(c, '*' | '?' | '!'))
        });

        // Ohne Netz (seriell, lokal) gibt es weder Adressen noch Wartezeit, Lebenszeichen oder `TERM` vom Profil.
        let offline = self.is_serial() || local;
        self.address_family = self
            .address_family
            .map(|family| family.trim().to_lowercase())
            .filter(|family| !offline && (family == "ipv4" || family == "ipv6"));
        self.connect_timeout = self
            .connect_timeout
            .filter(|seconds| !offline && (3..=120).contains(seconds));
        self.keepalive_interval = self
            .keepalive_interval
            .filter(|seconds| !local && keepalive_interval(*seconds));
        self.keepalive_max = self
            .keepalive_max
            .filter(|count| !local && KEEPALIVE_MAX.contains(count));

        // Als welches Terminal: nur, was ein Name von terminfo sein kann – und nur, wo es ein Terminal gibt, das
        // danach fragt (SSH, Telnet).
        let (telnet, ftp) = (self.is_telnet(), self.is_ftp());
        self.term = self
            .term
            .map(|name| name.trim().to_owned())
            .filter(|name| !offline && !ftp && terminal_name(name) && name != DEFAULT_TERM);

        // Umgebungsvariablen kennt nur SSH. Was kein Name oder kein Wert sein kann, fällt weg; ein Name gilt einmal.
        let mut env: Vec<EnvVar> = Vec::new();
        for variable in std::mem::take(&mut self.env) {
            let name = variable.name.trim().to_owned();
            if !offline
                && !telnet
                && !ftp
                && env.len() < MAX_ENV
                && env_name(&name)
                && env_value(&variable.value)
                && env.iter().all(|known| known.name != name)
            {
                env.push(EnvVar {
                    name,
                    value: variable.value,
                });
            }
        }
        self.env = env;

        // Der Zeichensatz gilt dem Terminal; ohne eines (FTP) gibt es nichts umzurechnen, und eine Shell dieses
        // Rechners spricht in der Pseudokonsole UTF-8.
        self.charset = self
            .charset
            .map(|name| name.trim().to_lowercase())
            .filter(|name| !ftp && !local && CHARSETS.contains(&name.as_str()));

        // Mosh gibt es nur für SSH. UDP geht nicht durch einen Sprung-Server – ein solches Profil wird abgelehnt,
        // statt Mosh still abzuschalten. Mosh trägt weder den Agenten noch X11, und `mosh-server` spricht nur UTF-8.
        self.mosh &= !plain;
        self.mosh_ports = self
            .mosh_ports
            .map(|ports| ports.trim().to_owned())
            .filter(|ports| !plain && !ports.is_empty());
        if self
            .mosh_ports
            .as_deref()
            .is_some_and(|ports| mosh_port_range(ports).is_none())
        {
            return Err("mosh_ports");
        }

        if self.mosh {
            if self.jump.is_some() {
                return Err("mosh");
            }

            (self.forward_agent, self.forward_x11, self.charset) = (false, false, None);
        }

        // Die Geschwindigkeit gehört zur seriellen Schnittstelle; eine, die keine ist, wird zur üblichen.
        self.baud = self
            .baud
            .filter(|baud| self.protocol.as_deref() == Some("serial") && (50..=4_000_000).contains(baud));

        // Leerzeichen und Steuerzeichen haben in einem Hostnamen nichts zu suchen; ein führendes „-“ könnte
        // später als Option missverstanden werden, wenn der Name je an ein anderes Programm geht.
        if self.host.is_empty()
            || self.host.starts_with('-')
            || self.host.chars().any(|c| c.is_whitespace() || c.is_control())
        {
            return Err("host");
        }

        // Im lokalen Terminal nennt `host` eine Shell: nur eine Kennung, wie der Kern sie vergibt – nie ein Pfad zu
        // einem beliebigen Programm oder eine Befehlszeile.
        if local && !crate::local::valid_id(&self.host) {
            return Err("host");
        }

        // Über Telnet und an einer seriellen Schnittstelle meldet sich der Benutzer im Terminal selbst an; ein Name
        // im Profil ist dort nur eine Notiz.
        if (self.user.is_empty() && !plain) || self.user.chars().any(|c| c.is_whitespace() || c.is_control()) {
            return Err("user");
        }

        if self.port == 0 && !local {
            return Err("port");
        }

        // Ein Profil kann nicht sein eigener Sprung-Server sein. Längere Schleifen findet `ProfileStore::chain`.
        if !self.id.is_empty() && self.jump.as_deref() == Some(self.id.as_str()) {
            return Err("jump");
        }

        if self.name.is_empty() {
            self.name = self.host.clone();
        }

        // Tunnel: jeder für sich geprüft, mit eigener Kennung, und kein Port zweimal an derselben Seite – der
        // zweite könnte nie lauschen.
        if self.tunnels.len() > MAX_TUNNELS {
            return Err("tunnels");
        }

        let given: Vec<String> = self.tunnels.iter().map(|tunnel| tunnel.id.trim().to_owned()).collect();
        let mut tunnels: Vec<Tunnel> = Vec::with_capacity(self.tunnels.len());
        for tunnel in std::mem::take(&mut self.tunnels) {
            let mut tunnel = tunnel.normalized().ok_or("tunnels")?;
            let here = |kind: TunnelKind| kind != TunnelKind::Remote;
            if tunnels
                .iter()
                .any(|other| other.listen_port == tunnel.listen_port && here(other.kind) == here(tunnel.kind))
                || (!tunnel.id.is_empty() && tunnels.iter().any(|other| other.id == tunnel.id))
            {
                return Err("tunnels");
            }

            if tunnel.id.is_empty() {
                tunnel.id = (1..)
                    .map(|number| format!("t{number}"))
                    .find(|id| !given.contains(id) && tunnels.iter().all(|other| &other.id != id))
                    .unwrap_or_default();
            }

            tunnels.push(tunnel);
        }

        self.tunnels = tunnels;

        Ok(self)
    }
}

/// Als was sich die App ausgibt, wenn das Profil nichts nennt.
pub const DEFAULT_TERM: &str = "xterm-256color";

/// So viele Umgebungsvariablen kann ein Profil nennen.
pub const MAX_ENV: usize = 16;

/// So lang darf der Wert einer Umgebungsvariable sein.
pub const MAX_ENV_VALUE: usize = 256;

/// Die Zeichensätze, in denen ein Server sprechen kann, wenn nicht in UTF-8 – je Byte ein Zeichen. Umgerechnet
/// wird in der App (`TerminalCharset`); der Kern merkt sich die Wahl bloß.
pub const CHARSETS: [&str; 3] = ["latin1", "latin9", "windows1252"];

/// Eine Umgebungsvariable, die der Server zu Beginn der Shell genannt bekommt.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct EnvVar {
    /// Name, etwa `LANG`.
    pub name: String,
    /// Wert, etwa `de_DE.UTF-8`.
    pub value: String,
}

/// Ob das der Name eines Terminals sein kann (`TERM`): wie die Einträge von terminfo heißen – Buchstaben, Ziffern,
/// `-`, `.`, `_`, `+`, mit einem Buchstaben oder einer Ziffer am Anfang.
pub fn terminal_name(name: &str) -> bool {
    (1..=64).contains(&name.len())
        && name.starts_with(|c: char| c.is_ascii_alphanumeric())
        && name
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '.' | '_' | '+'))
}

/// Ob das der Name einer Umgebungsvariable sein kann: Buchstaben, Ziffern und `_`, nicht mit einer Ziffer vorn.
pub fn env_name(name: &str) -> bool {
    (1..=64).contains(&name.len())
        && !name.starts_with(|c: char| c.is_ascii_digit())
        && name.chars().all(|c| c.is_ascii_alphanumeric() || c == '_')
}

/// Ob das der Wert einer Umgebungsvariable sein kann: begrenzt lang und ohne Steuerzeichen.
pub fn env_value(value: &str) -> bool {
    value.len() <= MAX_ENV_VALUE && !value.chars().any(char::is_control)
}

/// So viele Zeichen darf die Notiz eines Profils haben.
pub const MAX_NOTES: usize = 4000;

/// So lang darf der Startordner eines lokalen Terminals sein (in Bytes).
pub const MAX_LOCAL_DIR: usize = 1024;

/// Was ohne Angabe gilt: alle 30 Sekunden ein Lebenszeichen, nach drei unbeantworteten gilt die Verbindung als weg.
pub const KEEPALIVE_DEFAULT: (u32, u32) = (30, 3);

/// So viele unbeantwortete Lebenszeichen lassen sich einstellen.
pub const KEEPALIVE_MAX: std::ops::RangeInclusive<u32> = 1..=20;

/// Ob das ein Abstand für Lebenszeichen sein kann: 0 (keine) oder fünf Sekunden bis eine Stunde. Kürzer wäre
/// Last für den Server, länger hielte keine Firewall offen.
pub fn keepalive_interval(seconds: u32) -> bool {
    seconds == 0 || (5..=3600).contains(&seconds)
}

/// So viele Sprung-Server hintereinander sind erlaubt.
pub const MAX_JUMPS: usize = 4;

/// Warum sich der Weg zu einem Server nicht bilden lässt.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ChainError {
    /// Das Profil selbst gibt es nicht.
    UnknownProfile,
    /// Ein Sprung-Server verweist auf ein Profil, das es nicht mehr gibt.
    JumpMissing,
    /// Die Sprung-Server verweisen im Kreis aufeinander.
    Loop,
    /// Mehr als [`MAX_JUMPS`] Sprung-Server.
    TooLong,
}

#[derive(Serialize, Deserialize)]
struct ProfileFile {
    version: u32,
    profiles: Vec<Profile>,
}

/// Profile im Speicher, mit jeder Änderung nach `profiles.json` geschrieben.
pub struct ProfileStore {
    path: PathBuf,
    profiles: Vec<Profile>,
}

impl ProfileStore {
    /// Lädt die Datei. Fehlt sie, ist die Liste leer.
    ///
    /// Eine unlesbare Datei wird nicht überschrieben, sondern beiseitegelegt; ihr neuer Pfad kommt als zweiter
    /// Wert zurück, damit die Oberfläche es melden kann.
    pub fn load(path: PathBuf) -> io::Result<(Self, Option<PathBuf>)> {
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                return Ok((
                    Self {
                        path,
                        profiles: Vec::new(),
                    },
                    None,
                ));
            }
            Err(error) => return Err(error),
        };

        match serde_json::from_str::<ProfileFile>(&text) {
            // Jedes Profil geht durch dieselbe Prüfung wie beim Speichern: Die Datei kann von Hand bearbeitet
            // sein, und was dort nicht bestünde (ein Host mit `-` am Anfang, ein unbekanntes Protokoll), soll
            // auch geladen nicht gelten. Ein Profil, das die Prüfung nicht besteht, fehlt in der Liste.
            Ok(file) => Ok((
                Self {
                    path,
                    profiles: file
                        .profiles
                        .into_iter()
                        .filter_map(|profile| profile.normalized().ok())
                        .collect(),
                },
                None,
            )),
            Err(_) => {
                let aside = path.with_extension(format!("json.unreadable-{}", unix_now()));
                fs::rename(&path, &aside)?;
                Ok((
                    Self {
                        path,
                        profiles: Vec::new(),
                    },
                    Some(aside),
                ))
            }
        }
    }

    /// Alle Profile in der gespeicherten Reihenfolge.
    pub fn list(&self) -> &[Profile] {
        &self.profiles
    }

    /// Profil mit dieser Kennung.
    pub fn get(&self, id: &str) -> Option<&Profile> {
        self.profiles.iter().find(|profile| profile.id == id)
    }

    /// Der Weg zu einem Server: erst die Sprung-Server in der Reihenfolge, in der verbunden wird, zuletzt das
    /// Profil selbst.
    pub fn chain(&self, id: &str) -> Result<Vec<Profile>, ChainError> {
        let mut chain = vec![self.get(id).cloned().ok_or(ChainError::UnknownProfile)?];
        while let Some(jump) = chain[0].jump.clone() {
            if chain.iter().any(|profile| profile.id == jump) {
                return Err(ChainError::Loop);
            }

            if chain.len() > MAX_JUMPS {
                return Err(ChainError::TooLong);
            }

            chain.insert(0, self.get(&jump).cloned().ok_or(ChainError::JumpMissing)?);
        }

        Ok(chain)
    }

    /// Profil mit genau diesem Ziel, falls es eines gibt.
    pub fn find(&self, host: &str, port: u16, user: &str) -> Option<&Profile> {
        self.profiles
            .iter()
            .find(|profile| profile.host.eq_ignore_ascii_case(host) && profile.port == port && profile.user == user)
    }

    /// Legt ein Profil an (leere Kennung) oder ersetzt das mit derselben Kennung.
    pub fn save(&mut self, profile: Profile) -> io::Result<Profile> {
        let mut profile = profile;
        if profile.id.is_empty() {
            profile.id = new_id();
        }

        match self.profiles.iter_mut().find(|existing| existing.id == profile.id) {
            Some(existing) => *existing = profile.clone(),
            None => self.profiles.push(profile.clone()),
        }

        self.write()?;
        Ok(profile)
    }

    /// Entfernt die vorläufigen Profile, die ein früherer Lauf hinterlassen hat, und nennt ihre Kennungen – damit
    /// der Kern auch ihre Geheimnisse vergessen kann. Scheitert das Schreiben, sind sie für diesen Lauf trotzdem weg.
    pub fn drop_transient(&mut self) -> Vec<String> {
        let gone: Vec<String> = self
            .profiles
            .iter()
            .filter(|profile| profile.transient)
            .map(|profile| profile.id.clone())
            .collect();
        if !gone.is_empty() {
            self.profiles.retain(|profile| !profile.transient);
            let _ = self.write();
        }

        gone
    }

    /// Entfernt das Profil; `false`, wenn es keines mit dieser Kennung gab.
    pub fn delete(&mut self, id: &str) -> io::Result<bool> {
        let before = self.profiles.len();
        self.profiles.retain(|profile| profile.id != id);
        if self.profiles.len() == before {
            return Ok(false);
        }

        self.write()?;
        Ok(true)
    }

    /// Vermerkt eine erfolgreiche Verbindung.
    pub fn touch(&mut self, id: &str) -> io::Result<()> {
        if let Some(profile) = self.profiles.iter_mut().find(|profile| profile.id == id) {
            profile.last_connected = Some(unix_now());
            self.write()?;
        }

        Ok(())
    }

    // Erst in eine Nachbardatei, dann tauschen: Ein Absturz mitten im Schreiben lässt die alte Datei heil.
    fn write(&self) -> io::Result<()> {
        write_atomically(
            &self.path,
            &serde_json::to_string_pretty(&ProfileFile {
                version: 1,
                profiles: self.profiles.clone(),
            })?,
        )
    }
}

/// Schreibt `text` so nach `path`, dass nie eine halbe Datei zurückbleibt.
pub(crate) fn write_atomically(path: &Path, text: &str) -> io::Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }

    let temporary = path.with_extension("tmp");
    fs::write(&temporary, text)?;
    fs::rename(&temporary, path)
}

pub(crate) fn unix_now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|elapsed| elapsed.as_secs())
        .unwrap_or_default()
}

// Die Kennung muss nur eindeutig sein, nicht unvorhersehbar: Zeit plus Zähler genügt.
fn new_id() -> String {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|elapsed| elapsed.as_nanos())
        .unwrap_or_default();
    format!("{nanos:x}-{:x}", COUNTER.fetch_add(1, Ordering::Relaxed))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_file(name: &str) -> PathBuf {
        let directory = std::env::temp_dir().join(format!("nexus-core-test-{}-{name}", new_id()));
        fs::create_dir_all(&directory).unwrap();
        directory.join("profiles.json")
    }

    fn sample() -> Profile {
        Profile {
            name: "web01".into(),
            host: "10.0.1.10".into(),
            user: "ubuntu".into(),
            ..Profile::default()
        }
    }

    #[test]
    fn a_missing_file_is_an_empty_list() {
        let (store, aside) = ProfileStore::load(temp_file("missing")).unwrap();
        assert!(store.list().is_empty());
        assert!(aside.is_none());
    }

    #[test]
    fn a_profile_remembers_the_system_as_a_short_identifier() {
        let with = |system: &str| Profile {
            system: Some(system.to_owned()),
            ..sample()
        };

        assert_eq!(with(" Ubuntu ").normalized().unwrap().system.as_deref(), Some("ubuntu"));
        assert_eq!(with("Debian GNU/Linux").normalized().unwrap().system, None);
        assert_eq!(with("").normalized().unwrap().system, None);
        assert_eq!(sample().normalized().unwrap().system, None);
    }

    #[test]
    fn a_profile_remembers_its_identity_if_that_is_an_identifier() {
        let with = |identity: &str| Profile {
            identity: Some(identity.to_owned()),
            ..sample()
        };

        assert_eq!(
            with(" work-1 ").normalized().unwrap().identity.as_deref(),
            Some("work-1")
        );
        assert_eq!(with("").normalized().unwrap().identity, None);
        assert_eq!(with("a b").normalized().unwrap().identity, None);
        assert_eq!(with(&"x".repeat(65)).normalized().unwrap().identity, None);

        let old: Profile = serde_json::from_str(r#"{"name":"a","host":"h","user":"u"}"#).unwrap();
        assert_eq!(old.identity, None);
    }

    #[test]
    fn a_profile_keeps_the_id_of_a_colour_scheme_and_nothing_else() {
        let themed = |theme: &str| Profile {
            theme: Some(theme.to_owned()),
            ..sample()
        };

        assert_eq!(
            themed(" Custom-Prod-Red ").normalized().unwrap().theme.as_deref(),
            Some("custom-prod-red")
        );
        assert_eq!(
            themed("dracula").normalized().unwrap().theme.as_deref(),
            Some("dracula")
        );
        for wrong in ["", "#ff0000", "a b", "a\"b", "x".repeat(65).as_str()] {
            assert_eq!(themed(wrong).normalized().unwrap().theme, None, "{wrong}");
        }

        // Ohne Schema steht das Feld gar nicht in der Datei.
        let saved = serde_json::to_string(&sample().normalized().unwrap()).unwrap();
        assert!(!saved.contains("theme"), "{saved}");
    }

    #[test]
    fn a_profile_keeps_a_known_color_and_drops_any_other() {
        let colored = |color: &str| Profile {
            color: Some(color.to_owned()),
            ..sample()
        };

        assert_eq!(colored(" Red ").normalized().unwrap().color.as_deref(), Some("red"));
        assert_eq!(colored("#ff0000").normalized().unwrap().color, None);
        assert_eq!(colored("").normalized().unwrap().color, None);
        assert_eq!(sample().normalized().unwrap().color, None);

        // Ein Profil aus einer älteren Datei hat das Feld nicht.
        let old: Profile = serde_json::from_str(r#"{"name":"a","host":"h","user":"u"}"#).unwrap();
        assert_eq!(old.color, None);
    }

    #[test]
    fn saved_profiles_survive_a_reload() {
        let path = temp_file("reload");
        let (mut store, _) = ProfileStore::load(path.clone()).unwrap();
        let saved = store.save(sample()).unwrap();
        assert!(!saved.id.is_empty());

        let (reloaded, _) = ProfileStore::load(path).unwrap();
        assert_eq!(reloaded.list(), [saved]);
    }

    #[test]
    fn saving_with_an_existing_id_replaces_the_profile() {
        let (mut store, _) = ProfileStore::load(temp_file("replace")).unwrap();
        let mut saved = store.save(sample()).unwrap();
        saved.port = 2222;
        store.save(saved.clone()).unwrap();

        assert_eq!(store.list(), [saved]);
    }

    #[test]
    fn delete_removes_only_the_named_profile() {
        let (mut store, _) = ProfileStore::load(temp_file("delete")).unwrap();
        let first = store.save(sample()).unwrap();
        let second = store.save(sample()).unwrap();
        assert_ne!(first.id, second.id);

        assert!(store.delete(&first.id).unwrap());
        assert!(!store.delete(&first.id).unwrap());
        assert_eq!(store.list(), [second]);
    }

    #[test]
    fn a_profile_edited_by_hand_is_checked_when_the_file_is_loaded() {
        // Wie beim Speichern: Ein Host mit `-` am Anfang wäre für ein Programm eine Option, ein unbekanntes
        // Protokoll gibt es nicht, und was FTP ohne Verschlüsselung verlangt, muss genau so dastehen.
        let path = temp_file("hand-edited");
        fs::write(
            &path,
            r##"{ "version": 1, "profiles": [
                { "id": "ok", "name": "web", "host": " Web.example ", "port": 22, "user": "deploy", "group": "", "tags": [], "favorite": false, "key_path": null, "use_agent": true },
                { "id": "bad", "name": "evil", "host": "-oProxyCommand=calc", "port": 22, "user": "x", "group": "", "tags": [], "favorite": false, "key_path": null, "use_agent": true },
                { "id": "odd", "name": "odd", "host": "h", "port": 22, "user": "u", "group": "", "tags": [], "favorite": false, "key_path": null, "use_agent": true, "protocol": "gopher", "color": "#ff0000", "ftp_tls": "none" }
            ] }"##,
        )
        .unwrap();

        let (store, aside) = ProfileStore::load(path).unwrap();
        assert!(aside.is_none());
        let loaded: Vec<(&str, &str)> = store
            .list()
            .iter()
            .map(|profile| (profile.id.as_str(), profile.host.as_str()))
            .collect();
        assert_eq!(loaded, [("ok", "Web.example"), ("odd", "h")]);
        let odd = &store.list()[1];
        assert_eq!((&odd.protocol, &odd.color, &odd.ftp_tls), (&None, &None, &None));
    }

    #[test]
    fn ftp_knows_two_wishes_about_tls_and_nothing_else() {
        let path = temp_file("ftp-tls");
        let profile = |id: &str, protocol: &str, tls: &str| {
            format!(
                r#"{{ "id": "{id}", "name": "{id}", "host": "h", "port": 21, "user": "u", "group": "", "tags": [], "favorite": false, "key_path": null, "use_agent": false, "protocol": "{protocol}", "ftp_tls": "{tls}" }}"#
            )
        };
        fs::write(
            &path,
            format!(
                r#"{{ "version": 1, "profiles": [{}, {}, {}, {}] }}"#,
                profile("plain", "ftp", "none"),
                profile("implicit", "ftp", " Implicit "),
                profile("other", "ftp", "sslv3"),
                profile("ssh", "ssh", "implicit"),
            ),
        )
        .unwrap();

        let (store, _) = ProfileStore::load(path).unwrap();
        let wishes: Vec<(bool, bool)> = store
            .list()
            .iter()
            .map(|profile| (profile.ftp_plain(), profile.ftp_implicit()))
            .collect();

        // Ohne TLS und TLS ab dem ersten Byte sind Wünsche; alles andere heißt: TLS, wie üblich begonnen.
        assert_eq!(wishes, [(true, false), (false, true), (false, false), (false, false)]);
    }

    #[test]
    fn an_unreadable_file_is_set_aside_not_overwritten() {
        let path = temp_file("broken");
        fs::write(&path, "{ this is not json").unwrap();

        let (mut store, aside) = ProfileStore::load(path.clone()).unwrap();
        let aside = aside.expect("die kaputte Datei bleibt erhalten");
        assert_eq!(fs::read_to_string(aside).unwrap(), "{ this is not json");

        store.save(sample()).unwrap();
        assert!(path.exists());
    }

    #[test]
    fn a_file_from_an_older_version_gets_defaults_for_new_fields() {
        let path = temp_file("older");
        fs::write(&path, r#"{"version":1,"profiles":[{"id":"a","host":"h","user":"u"}]}"#).unwrap();

        let (store, _) = ProfileStore::load(path).unwrap();
        let profile = store.get("a").unwrap();
        assert_eq!(profile.port, 22);
        assert!(profile.use_agent);
        assert_eq!(profile.jump, None);
        assert!(!profile.forward_agent, "Agent Forwarding ist nie stillschweigend an");
    }

    #[test]
    fn the_chain_lists_jump_hosts_in_connection_order() {
        let (mut store, _) = ProfileStore::load(temp_file("chain")).unwrap();
        let bastion = store
            .save(Profile {
                name: "bastion".into(),
                ..sample()
            })
            .unwrap();
        let inner = store
            .save(Profile {
                name: "inner".into(),
                jump: Some(bastion.id.clone()),
                ..sample()
            })
            .unwrap();
        let target = store
            .save(Profile {
                name: "target".into(),
                jump: Some(inner.id.clone()),
                ..sample()
            })
            .unwrap();

        let names = |id: &str| -> Vec<String> { store.chain(id).unwrap().into_iter().map(|p| p.name).collect() };
        assert_eq!(names(&bastion.id), ["bastion"]);
        assert_eq!(names(&target.id), ["bastion", "inner", "target"]);
        assert_eq!(store.chain("gibt-es-nicht"), Err(ChainError::UnknownProfile));
    }

    #[test]
    fn a_chain_with_a_missing_or_circular_jump_is_refused() {
        let (mut store, _) = ProfileStore::load(temp_file("chain-broken")).unwrap();
        let orphan = store
            .save(Profile {
                jump: Some("weg".into()),
                ..sample()
            })
            .unwrap();
        assert_eq!(store.chain(&orphan.id), Err(ChainError::JumpMissing));

        let first = store.save(sample()).unwrap();
        let second = store
            .save(Profile {
                jump: Some(first.id.clone()),
                ..sample()
            })
            .unwrap();
        store
            .save(Profile {
                jump: Some(second.id.clone()),
                ..first.clone()
            })
            .unwrap();
        assert_eq!(store.chain(&first.id), Err(ChainError::Loop));

        assert_eq!(
            Profile {
                id: "x".into(),
                jump: Some("x".into()),
                ..sample()
            }
            .normalized(),
            Err("jump")
        );
    }

    #[test]
    fn normalizing_trims_and_falls_back_to_the_host_as_name() {
        let profile = Profile {
            host: "  example.org ".into(),
            user: " root ".into(),
            tags: vec![" web ".into(), "".into()],
            key_path: Some("  ".into()),
            ..Profile::default()
        }
        .normalized()
        .unwrap();

        assert_eq!(profile.host, "example.org");
        assert_eq!(profile.name, "example.org");
        assert_eq!(profile.user, "root");
        assert_eq!(profile.tags, ["web"]);
        assert_eq!(profile.key_path, None);
    }

    #[test]
    fn normalizing_rejects_unusable_values() {
        let with = |change: fn(&mut Profile)| {
            let mut profile = sample();
            change(&mut profile);
            profile.normalized()
        };

        assert_eq!(with(|p| p.host = "".into()), Err("host"));
        assert_eq!(with(|p| p.host = "two words".into()), Err("host"));
        assert_eq!(with(|p| p.host = "-oProxyCommand=x".into()), Err("host"));
        assert_eq!(with(|p| p.user = "".into()), Err("user"));
        assert_eq!(with(|p| p.user = "a\nb".into()), Err("user"));
        assert_eq!(with(|p| p.port = 0), Err("port"));
    }

    #[test]
    fn a_local_profile_needs_no_user_and_sheds_everything_of_ssh() {
        let folder = std::env::temp_dir().display().to_string();
        let full = Profile {
            name: String::new(),
            protocol: Some(" Local ".into()),
            host: " cmd ".into(),
            port: 0,
            user: "someone".into(),
            key_path: Some("/k".into()),
            use_agent: true,
            identities_only: true,
            identity: Some("me".into()),
            jump: Some("j".into()),
            forward_agent: true,
            forward_x11: true,
            compression: true,
            legacy_algorithms: true,
            tunnels: vec![Tunnel {
                listen_port: 8080,
                target_host: "x".into(),
                target_port: 80,
                ..Tunnel::default()
            }],
            env: vec![EnvVar {
                name: "LANG".into(),
                value: "C".into(),
            }],
            charset: Some("latin1".into()),
            term: Some("vt100".into()),
            address_family: Some("ipv4".into()),
            connect_timeout: Some(30),
            keepalive_interval: Some(60),
            keepalive_max: Some(5),
            ssh_alias: Some("web".into()),
            baud: Some(9600),
            local_dir: Some(format!("  {folder}  ")),
            color: Some("red".into()),
            ..sample()
        };
        let profile = full.clone().normalized().unwrap();

        assert!(profile.is_local());
        assert_eq!((profile.host.as_str(), profile.name.as_str()), ("cmd", "cmd"));
        assert_eq!(profile.local_dir.as_deref(), Some(folder.as_str()));
        assert_eq!(profile.color.as_deref(), Some("red"));
        assert!(profile.user.is_empty());
        assert_eq!(
            (&profile.key_path, &profile.identity, &profile.jump),
            (&None, &None, &None)
        );
        assert!(!profile.use_agent && !profile.identities_only && !profile.forward_agent && !profile.forward_x11);
        assert!(!profile.compression && !profile.legacy_algorithms);
        assert!(profile.tunnels.is_empty() && profile.env.is_empty());
        assert_eq!(
            (&profile.charset, &profile.term, &profile.ssh_alias),
            (&None, &None, &None)
        );
        assert_eq!(
            (&profile.address_family, profile.connect_timeout, profile.baud),
            (&None, None, None)
        );
        assert_eq!((profile.keepalive_interval, profile.keepalive_max), (None, None));

        // `host` ist die Kennung einer Shell, nichts sonst – kein Pfad, keine Befehlszeile.
        for host in [
            "evil",
            "wsl:a b",
            "C:\\Windows\\System32\\cmd.exe",
            "pwsh -c calc",
            "wsl:../x",
            "",
        ] {
            let with = Profile {
                host: host.into(),
                ..full.clone()
            };
            assert_eq!(with.normalized(), Err("host"), "{host}");
        }

        // Der Startordner ist absolut und schlicht; ohne Angabe gilt der Ordner des Benutzers.
        let long = format!("{folder}{}", "a".repeat(MAX_LOCAL_DIR));
        for dir in [
            "src".to_owned(),
            "C:".to_owned(),
            format!("{folder}a\nb"),
            format!("{folder}\u{202e}fdp.exe"),
            long,
        ] {
            let with = Profile {
                local_dir: Some(dir.clone()),
                ..full.clone()
            };
            assert_eq!(with.normalized(), Err("local_dir"), "{dir:?}");
        }

        let unset = Profile {
            local_dir: Some("   ".into()),
            ..full.clone()
        };
        assert_eq!(unset.normalized().unwrap().local_dir, None);

        // Ein Startordner gehört nur zum lokalen Terminal.
        let ssh = Profile {
            local_dir: Some(folder),
            ..sample()
        };
        assert_eq!(ssh.normalized().unwrap().local_dir, None);
    }

    #[test]
    fn keepalive_wishes_outside_the_limits_fall_back_to_the_general_setting() {
        let keep = |interval, max| {
            let profile = Profile {
                keepalive_interval: interval,
                keepalive_max: max,
                ..sample()
            }
            .normalized()
            .unwrap();
            (profile.keepalive_interval, profile.keepalive_max)
        };
        assert_eq!(keep(Some(60), Some(5)), (Some(60), Some(5)));
        assert_eq!(keep(Some(0), Some(1)), (Some(0), Some(1)));
        assert_eq!(keep(Some(5), Some(20)), (Some(5), Some(20)));
        assert_eq!(keep(Some(3600), None), (Some(3600), None));
        assert_eq!(keep(Some(4), Some(0)), (None, None));
        assert_eq!(keep(Some(3601), Some(21)), (None, None));
    }

    #[test]
    fn terminal_environment_and_charset_are_tidied() {
        let variable = |name: &str, value: &str| EnvVar {
            name: name.into(),
            value: value.into(),
        };
        let profile = Profile {
            term: Some(" vt100 ".into()),
            env: vec![
                variable(" LANG ", "de_DE.ISO-8859-1"),
                variable("LC_ALL", ""),
                variable("LANG", "doppelt"),
                variable("1ST", "x"),
                variable("A B", "x"),
                variable("A=B", "x"),
                variable("NEWLINE", "a\nb"),
                variable("LONG", &"x".repeat(MAX_ENV_VALUE + 1)),
                variable("", "x"),
            ],
            charset: Some(" Latin1 ".into()),
            ..sample()
        }
        .normalized()
        .unwrap();
        assert_eq!(profile.term.as_deref(), Some("vt100"));
        assert_eq!(
            profile.env,
            [variable("LANG", "de_DE.ISO-8859-1"), variable("LC_ALL", "")]
        );
        assert_eq!(profile.charset.as_deref(), Some("latin1"));

        // Das Übliche steht nicht im Profil; was kein Name ist oder kein bekannter Zeichensatz, auch nicht.
        for (term, charset) in [
            (DEFAULT_TERM, "utf8"),
            ("", ""),
            ("xterm\u{ff}", "cp437"),
            ("-xterm", "latin2"),
            ("a b", "x"),
        ] {
            let profile = Profile {
                term: Some(term.into()),
                charset: Some(charset.into()),
                ..sample()
            }
            .normalized()
            .unwrap();
            assert_eq!((profile.term, profile.charset), (None, None), "{term:?} {charset:?}");
        }
        assert!(terminal_name("xterm-256color") && terminal_name("screen.xterm-256color") && terminal_name("vt220+x"));
        assert!(!terminal_name(&"x".repeat(65)));

        // Mehr als die Obergrenze an Variablen gibt es nicht.
        let many = Profile {
            env: (0..40).map(|index| variable(&format!("V{index}"), "x")).collect(),
            ..sample()
        }
        .normalized()
        .unwrap();
        assert_eq!(many.env.len(), MAX_ENV);

        // Telnet nennt sein Terminal, kennt aber keine Variablen; seriell keines von beiden, wohl aber den
        // Zeichensatz; FTP hat kein Terminal.
        let with = |protocol: &str| {
            Profile {
                protocol: Some(protocol.into()),
                host: if protocol == "serial" {
                    "COM3".into()
                } else {
                    "h".into()
                },
                term: Some("vt100".into()),
                env: vec![variable("LANG", "C")],
                charset: Some("windows1252".into()),
                ..sample()
            }
            .normalized()
            .unwrap()
        };
        let (telnet, serial, ftp) = (with("telnet"), with("serial"), with("ftp"));
        assert_eq!(
            (telnet.term.as_deref(), telnet.env.len(), telnet.charset.as_deref()),
            (Some("vt100"), 0, Some("windows1252"))
        );
        assert_eq!(
            (serial.term.as_deref(), serial.env.len(), serial.charset.as_deref()),
            (None, 0, Some("windows1252"))
        );
        assert_eq!((ftp.term, ftp.env.len(), ftp.charset), (None, 0, None));
    }
    #[test]
    fn notes_and_connection_wishes_are_tidied() {
        let profile = Profile {
            notes: "  Datenbank\r\nWartung: \u{7}Dienstag\t2 Uhr \n".into(),
            address_family: Some(" IPv6 ".into()),
            connect_timeout: Some(30),
            compression: true,
            legacy_algorithms: true,
            ..sample()
        }
        .normalized()
        .unwrap();
        assert_eq!(profile.notes, "Datenbank\nWartung: Dienstag2 Uhr");
        assert_eq!(profile.address_family.as_deref(), Some("ipv6"));
        assert_eq!(profile.connect_timeout, Some(30));
        assert!(profile.compression && profile.legacy_algorithms);

        // Was es nicht gibt, ist keine Einschränkung; eine Notiz hat ein Ende.
        let profile = Profile {
            notes: "x".repeat(MAX_NOTES + 50),
            address_family: Some("ipx".into()),
            connect_timeout: Some(100_000),
            ..sample()
        }
        .normalized()
        .unwrap();
        assert_eq!(profile.notes.chars().count(), MAX_NOTES);
        assert_eq!((profile.address_family, profile.connect_timeout), (None, None));

        // Telnet kennt weder Komprimierung noch Verfahren – wohl aber Adressart und Wartezeit.
        let telnet = Profile {
            protocol: Some("telnet".into()),
            compression: true,
            legacy_algorithms: true,
            address_family: Some("ipv4".into()),
            connect_timeout: Some(5),
            ..sample()
        }
        .normalized()
        .unwrap();
        assert!(!telnet.compression && !telnet.legacy_algorithms);
        assert_eq!(
            (telnet.address_family.as_deref(), telnet.connect_timeout),
            (Some("ipv4"), Some(5))
        );
    }

    #[test]
    fn mosh_belongs_to_ssh_without_a_jump_host() {
        let with = |protocol: Option<&str>, jump: Option<&str>, ports: Option<&str>| {
            Profile {
                protocol: protocol.map(str::to_owned),
                jump: jump.map(str::to_owned),
                mosh: true,
                mosh_ports: ports.map(str::to_owned),
                forward_agent: true,
                forward_x11: true,
                charset: Some("latin1".into()),
                ..sample()
            }
            .normalized()
        };

        // Über SSH: Mosh bleibt, die Ports werden aufgeräumt – und was Mosh nicht trägt, fällt weg.
        let ssh = with(None, None, Some(" 60001:60010 ")).unwrap();
        assert!(ssh.mosh);
        assert_eq!(ssh.mosh_ports.as_deref(), Some("60001:60010"));
        assert_eq!(ssh.mosh_port_range(), Some((60001, 60010)));
        assert!(!ssh.forward_agent && !ssh.forward_x11);
        assert_eq!(ssh.charset, None);
        assert_eq!(
            with(None, None, Some("60001")).unwrap().mosh_port_range(),
            Some((60001, 60001))
        );
        assert_eq!(with(None, None, Some(" ")).unwrap().mosh_ports, None);

        // Telnet, FTP, seriell und lokal kennen kein Mosh: Es fällt weg, samt den Ports.
        for protocol in ["telnet", "ftp", "serial", "local"] {
            let profile = Profile {
                protocol: Some(protocol.into()),
                host: if protocol == "local" { "pwsh" } else { "COM3" }.into(),
                mosh: true,
                mosh_ports: Some("60001".into()),
                jump: Some("bastion".into()),
                ..sample()
            }
            .normalized()
            .unwrap_or_else(|field| panic!("{protocol}: {field}"));
            assert!(!profile.mosh && profile.mosh_ports.is_none(), "{protocol}");
        }

        // UDP geht nicht durch einen Sprung-Server: Das Profil wird abgelehnt.
        assert_eq!(with(None, Some("bastion"), None), Err("mosh"));

        // Ports: eine Zahl oder „von:bis“, 1–65535, von nicht größer als bis – sonst ist es kein Bereich.
        for wrong in [
            "61000:60000",
            "0",
            "65536",
            "1:65536",
            "60001-60010",
            "60001:",
            ":60001",
            "6000a",
            "+1",
            "123456",
        ] {
            assert_eq!(with(None, None, Some(wrong)), Err("mosh_ports"), "{wrong}");
        }

        // Ohne Mosh bleiben Agent, X11 und Zeichensatz, wie sie sind.
        let plain = Profile {
            mosh: false,
            forward_agent: true,
            charset: Some("latin1".into()),
            ..sample()
        }
        .normalized()
        .unwrap();
        assert!(!plain.mosh && plain.forward_agent);
        assert_eq!(plain.charset.as_deref(), Some("latin1"));
    }

    #[test]
    fn mosh_is_left_out_of_a_saved_profile_without_it() {
        let saved = serde_json::to_string(&sample().normalized().unwrap()).unwrap();
        assert!(!saved.contains("mosh"), "{saved}");
        let with: Profile =
            serde_json::from_str(&serde_json::to_string(&Profile { mosh: true, ..sample() }).unwrap()).unwrap();
        assert!(with.mosh);
    }

    #[test]
    fn a_profile_file_from_before_reads_with_the_new_fields_at_their_defaults() {
        let profile: Profile = serde_json::from_str(
            r#"{"id":"a","name":"n","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,
                "key_path":null,"use_agent":true,"jump":null,"forward_agent":false,"color":null,"last_connected":null}"#,
        )
        .unwrap();
        assert_eq!(profile.notes, "");
        assert!(!profile.no_reconnect && !profile.compression && !profile.legacy_algorithms);
        assert_eq!((profile.address_family, profile.connect_timeout), (None, None));
        assert!(!profile.mosh && profile.mosh_ports.is_none());
    }
}
