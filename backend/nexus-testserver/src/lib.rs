//! Kleiner SSH-Server mit nachgespielter Shell.
//!
//! Dient als Gegenstelle für die Tests von `nexus-core` und – über das Programm `nexus-testserver` – zum
//! Ausprobieren der App, ohne einen echten Server zu brauchen. Er führt nichts aus: Die „Shell“ kennt nur die
//! Befehle `echo`, `size`, `conn`, `flood`, `agent` und `exit`. Mit [`Options::sftp_root`] bietet er zusätzlich SFTP über ein
//! Verzeichnis an, mit [`Options::forwarding`] dient er als Sprung-Server, und mit [`Options::monitor`] spielt er
//! die Auslastung eines Linux-Servers vor.

mod ftp;
mod sftp;

pub use ftp::{FtpOptions, start_ftp};

use std::collections::{HashMap, HashSet};
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::Duration;

use russh::keys::ssh_key::private::Ed25519Keypair;
use russh::keys::{PrivateKey, PublicKey};
use russh::server::{
    Auth, ChannelOpenHandle, Handle, Handler, Msg, PublicKeyOffer, Server, Session, VerifiedPublicKey,
};
use russh::{Channel, ChannelId, ChannelOpenFailure, MethodKind, MethodSet};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};

/// Prompt der nachgespielten Shell.
pub const PROMPT: &str = "$ ";

/// Wer sich anmelden darf.
#[derive(Clone, Default)]
pub struct Options {
    /// Benutzer und Passwort für die Passwort-Anmeldung; `None` schaltet sie ab.
    pub password: Option<(String, String)>,
    /// Öffentliche Schlüssel, die für jeden Benutzer gelten.
    pub authorized_keys: Vec<PublicKey>,
    /// Verzeichnis, das per SFTP als `/` erscheint; `None` lehnt das SFTP-Subsystem ab.
    pub sftp_root: Option<PathBuf>,
    /// TCP-Verbindungen weiterreichen (`direct-tcpip`) – damit taugt der Server als Sprung-Server.
    pub forwarding: bool,
    /// Was der Server auf die Abfrage der Auslastung antwortet.
    pub monitor: Monitor,
    /// Agent Forwarding annehmen. Der Befehl `agent` der Shell fragt dann den Agenten des Clients – auch wenn
    /// der Client das Forwarding gar nicht verlangt hat: So lässt sich prüfen, dass er es dann ablehnt.
    pub agent_forwarding: bool,
    /// Text, den der Server vor der Anmeldung schickt (`Banner` in `sshd_config`).
    pub banner: Option<String>,
    /// Höchstzahl gleichzeitig offener Kanäle je Verbindung (wie `MaxSessions` bei OpenSSH); `None` = beliebig viele.
    pub max_sessions: Option<usize>,
    /// Was der Server tut, wenn der Client eine Shell will.
    pub shell: Shell,
    /// Ein altes Gerät: spricht nur, was heute als schwach gilt (`diffie-hellman-group14-sha1`, `aes128-cbc`,
    /// `hmac-sha1`). Ein Client ohne „ältere Verfahren“ findet mit ihm kein gemeinsames Verfahren.
    pub old_device: bool,
    /// Host-Zertifikate, die der Server vorzeigt, wenn der Client darum bittet (für seinen Host-Schlüssel
    /// ausgestellt). Ohne sie zeigt er nur den bloßen Schlüssel.
    pub host_certificates: Vec<russh::keys::Certificate>,
    /// `sudo` für das Konto: Damit lässt sich der SFTP-Server als root starten. `None`: Es gibt kein `sudo`.
    pub sudo: Option<Sudo>,
    /// Befehle wirklich ausführen, mit `sh` in diesem Verzeichnis – statt sie nachzuspielen. Für den Dateizugriff
    /// ohne SFTP, der aus lauter Befehlen besteht. Braucht ein `sh` auf diesem Rechner (unter Windows das von Git).
    pub real_shell: Option<PathBuf>,
    /// Mit `real_shell`: Jede ausgeführte Zeile wird an diese Datei angehängt – für Tests, die prüfen, was der
    /// Kern am Server fragt (und was nicht).
    pub command_log: Option<PathBuf>,
    /// Die Anmelde-Shell liest Befehle nicht wie eine POSIX-Shell (wie `cmd.exe` bei OpenSSH unter Windows): Auf
    /// die Probe des Kerns antwortet der Server dann so, wie eine solche Shell es täte.
    pub foreign_shell: bool,
    /// Was der Start von `mosh-server` ausgibt. Ohne Antworten gibt es kein `mosh-server`.
    pub mosh: MoshServer,
}

/// Die Antworten auf den Start von `mosh-server`, der Reihe nach – eine je Versuch des Kerns. Sind sie aufgebraucht
/// (oder gibt es keine), antwortet der Server wie einer ohne `mosh-server`: Die Anfangszeile des Kerns gibt dann
/// `@nexus-no-mosh` aus und endet mit 127. Jede Zeile, die ankam, steht danach in [`MoshServer::lines`].
#[derive(Clone, Default)]
pub struct MoshServer {
    replies: Arc<std::sync::Mutex<std::collections::VecDeque<MoshReply>>>,
    lines: Arc<std::sync::Mutex<Vec<String>>>,
}

/// Was ein Versuch ausgibt.
#[derive(Clone, Debug, Default)]
pub struct MoshReply {
    /// Standardausgabe, samt dem, was die Anfangszeile davor schreibt (`@nexus-mosh`).
    pub stdout: String,
    /// Fehlerausgabe.
    pub stderr: String,
    /// Rückgabewert.
    pub status: u32,
}

impl MoshServer {
    /// Ein Server, der der Reihe nach so antwortet.
    pub fn answering(replies: impl IntoIterator<Item = MoshReply>) -> Self {
        Self {
            replies: Arc::new(std::sync::Mutex::new(replies.into_iter().collect())),
            lines: Arc::default(),
        }
    }

    /// Die Befehlszeilen, mit denen `mosh-server` gestartet werden sollte, wie sie ankamen.
    pub fn lines(&self) -> Vec<String> {
        self.lines.lock().unwrap().clone()
    }

    fn reply(&self, line: &str) -> MoshReply {
        self.lines.lock().unwrap().push(line.to_owned());
        self.replies.lock().unwrap().pop_front().unwrap_or_else(|| MoshReply {
            stdout: "@nexus-no-mosh\n".to_owned(),
            stderr: String::new(),
            status: 127,
        })
    }
}

/// Ob es auf diesem Rechner ein `sh` gibt, das Befehle ausführt.
pub fn has_shell() -> bool {
    std::process::Command::new("sh")
        .args(["-c", "exit 0"])
        .status()
        .is_ok_and(|status| status.success())
}

/// Was `sudo` dem Konto erlaubt.
#[derive(Clone, Debug)]
pub struct Sudo {
    /// Das Passwort, das `sudo` verlangt; `None` = ohne Passwort (`NOPASSWD`).
    pub password: Option<String>,
    /// Verzeichnis, das dem SFTP-Server unter `sudo` als `/` erscheint – ein anderes als das des Benutzers, damit
    /// ein Test sieht, wessen Dateien er liest.
    pub root: PathBuf,
}

/// Antwort auf die Bitte um eine Shell.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Shell {
    /// Die nachgespielte Shell.
    #[default]
    Interactive,
    /// Wie ein Konto, das nur SFTP darf (`ForceCommand internal-sftp` bei OpenSSH) oder dessen Shell `nologin`
    /// ist: Die Anfrage wird angenommen, es kommt ein Satz, dann endet der Kanal mit Rückgabewert 1.
    SftpOnly,
    /// Die Anfrage wird abgelehnt – so halten es manche reinen SFTP-Server.
    Refused,
}

/// Der Satz, mit dem [`Shell::SftpOnly`] die Shell verweigert – derselbe wie bei OpenSSH.
pub const SFTP_ONLY_MESSAGE: &str = "This service allows sftp connections only.";

/// Antwort auf Befehle ohne Terminal (`exec`) – der Kern fragt so die Auslastung ab.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Monitor {
    /// Befehle werden abgelehnt.
    #[default]
    Refused,
    /// Antwortet wie ein Linux-Server, mit schwankender Auslastung.
    Linux,
    /// Antwortet wie ein Server ohne `/proc`.
    NoProc,
}

/// Ein Host-Schlüssel, der sich aus einem Byte ergibt. So hat der Testserver bei jedem Start denselben
/// Fingerabdruck, und Tests können gezielt einen „geänderten“ Schlüssel erzeugen.
pub fn host_key(seed: u8) -> PrivateKey {
    PrivateKey::from(Ed25519Keypair::from_seed(&[seed; 32]))
}

/// Ein laufender Testserver. Er endet, wenn der Wert fallen gelassen wird.
pub struct Running {
    /// Adresse, auf der der Server lauscht.
    pub address: SocketAddr,
    task: tokio::task::JoinHandle<()>,
}

impl Drop for Running {
    fn drop(&mut self) {
        self.task.abort();
    }
}

/// So viel nimmt der Server je Verbindung an, auch wenn er gerade nicht liest ([`listen`]).
const RECEIVE_BUFFER: u32 = 16 * 1024 * 1024;

/// Lauscht auf `127.0.0.1:port` (0 = irgendein freier Port) – mit einem großen Empfangspuffer, den jede
/// angenommene Verbindung erbt.
///
/// Der Puffer ist nötig, weil hier `russh` mit `russh` spricht: Dessen Schleife schreibt auf die Leitung, ohne
/// währenddessen zu lesen. Schickt der Client viel (Uploads, Leseanfragen auf Vorrat) und der Server zugleich
/// viel zurück, warten mit den üblichen 64 KiB beide darauf, dass der andere liest – und die Verbindung steht,
/// bis eine Anfrage in ihr Zeitlimit läuft. Ein echter Server (OpenSSH) liest auch beim Schreiben weiter.
pub fn listen(port: u16) -> std::io::Result<TcpListener> {
    let socket = tokio::net::TcpSocket::new_v4()?;
    socket.set_recv_buffer_size(RECEIVE_BUFFER)?;
    socket.bind(SocketAddr::from(([127, 0, 0, 1], port)))?;
    socket.listen(1024)
}

/// Startet den Server auf `listener` im laufenden tokio-Runtime.
pub fn start(listener: TcpListener, key: PrivateKey, options: Options) -> std::io::Result<Running> {
    let address = listener.local_addr()?;
    let mut methods = MethodSet::empty();
    if options.password.is_some() {
        methods.push(MethodKind::Password);
    }
    if !options.authorized_keys.is_empty() {
        methods.push(MethodKind::PublicKey);
    }

    let config = Arc::new(russh::server::Config {
        methods: methods.clone(),
        // Ein Testserver braucht keine Bremse gegen Passwort-Raten; sie würde nur die Tests verlangsamen.
        auth_rejection_time: Duration::ZERO,
        auth_rejection_time_initial: Some(Duration::ZERO),
        keys: vec![key],
        certificates: options.host_certificates.clone(),
        // Wer mehrere Dateien zugleich lädt, stellt viele Leseanfragen auf Vorrat (russh-sftp: 16 je Datei, der
        // Kern lädt 8 Dateien nebeneinander). Mit den voreingestellten 100 Nachrichten je Kanal bliebe dieser
        // Server dann stehen: Der SFTP-Teil wartet mit einer Antwort auf ein größeres Fenster, die Schleife der
        // Verbindung auf Platz für die nächste Anfrage – und liest deshalb nie, dass der Client das Fenster
        // längst vergrößert hat. Ein echter Server (OpenSSH) kennt das nicht.
        channel_buffer_size: 4096,
        preferred: if options.old_device {
            russh::Preferred {
                kex: vec![russh::kex::DH_G14_SHA1].into(),
                cipher: vec![russh::cipher::AES_128_CBC].into(),
                mac: vec![russh::mac::HMAC_SHA1].into(),
                ..russh::Preferred::DEFAULT
            }
        } else {
            russh::Preferred::DEFAULT
        },
        ..Default::default()
    });

    let task = tokio::spawn(async move {
        let mut server = TestServer { options, methods };
        let _ = server.run_on_socket(config, &listener).await;
    });
    Ok(Running { address, task })
}

struct TestServer {
    options: Options,
    methods: MethodSet,
}

impl Server for TestServer {
    type Handler = Connection;

    fn new_client(&mut self, _peer: Option<SocketAddr>) -> Connection {
        static NEXT: AtomicU64 = AtomicU64::new(1);
        Connection {
            options: self.options.clone(),
            methods: self.methods.clone(),
            number: NEXT.fetch_add(1, Ordering::Relaxed),
            open: OpenChannels::default(),
            sizes: HashMap::new(),
            environment: HashMap::new(),
            pending: HashMap::new(),
            shells: HashMap::new(),
            closed: HashMap::new(),
            follows: HashSet::new(),
            hushed_until: Arc::new(AtomicU64::new(0)),
            x11: None,
            forwards: HashMap::new(),
        }
    }
}

/// Die offenen Kanäle einer Verbindung, für [`Options::max_sessions`]. Wie bei OpenSSH ist ein Kanal frei, sobald
/// er geschlossen ist – auch, wenn der Server ihn schließt: Dann meldet russh die Antwort des Clients nicht mehr
/// ([`Handler::channel_close`] kommt nicht), deshalb vergisst ihn der Server dort selbst. Geteilt, weil auch
/// Aufgaben nebenher Kanäle schließen. Vorher zählte jeder Befehl, den der Server beendet hatte, für immer mit.
#[derive(Clone, Default)]
struct OpenChannels(Arc<std::sync::Mutex<HashSet<ChannelId>>>);

impl OpenChannels {
    fn len(&self) -> usize {
        self.0.lock().map_or(0, |open| open.len())
    }

    fn insert(&self, channel: ChannelId) {
        if let Ok(mut open) = self.0.lock() {
            open.insert(channel);
        }
    }

    fn remove(&self, channel: ChannelId) {
        if let Ok(mut open) = self.0.lock() {
            open.remove(&channel);
        }
    }
}

struct Connection {
    options: Options,
    methods: MethodSet,
    /// Laufende Nummer der Verbindung; der Befehl `conn` nennt sie. So zeigt sich, ob zwei Shells dieselbe
    /// Verbindung benutzen.
    number: u64,
    /// Die offenen Kanäle dieser Verbindung, für [`Options::max_sessions`].
    open: OpenChannels,
    /// Größe des Terminals je Kanal.
    sizes: HashMap<ChannelId, (u32, u32)>,
    /// Was der Client je Kanal über seine Umgebung gesagt hat: `TERM` aus der Bitte um ein Terminal und die
    /// Variablen, die angenommen wurden. Der Befehl `env` nennt sie.
    environment: HashMap<ChannelId, Vec<String>>,
    /// Geöffnete Kanäle, die noch nicht wissen, ob sie Shell oder SFTP werden.
    pending: HashMap<ChannelId, Channel<Msg>>,
    /// Die Kanäle mit einer nachgespielten Shell und ihre angefangene Zeile. Nur deren Daten gehen durch
    /// [`Handler::data`] an die Shell – die eines SFTP-Kanals liest der SFTP-Teil selbst.
    shells: HashMap<ChannelId, Vec<u8>>,
    /// Laufende Befehle (`exec`): Die Marke wird gesetzt, wenn der Client den Kanal schließt.
    closed: HashMap<ChannelId, Arc<AtomicBool>>,
    /// Kanäle, auf denen eine Datei mitgelesen wird (`tail -f`): Das Ende ihrer Eingabe beendet sie.
    follows: HashSet<ChannelId>,
    /// Das Geheimnis, das der Client für X11-Forwarding genannt hat (hexadezimal); `None`, solange er keines
    /// verlangt hat. Der Befehl `x11` der Shell öffnet damit eine X11-Verbindung zum Client.
    x11: Option<String>,
    /// Die Ports, an denen dieser Server für den Client lauscht (`ssh -R`), samt der Aufgabe, die dort annimmt.
    forwards: HashMap<u32, tokio::task::JoinHandle<()>>,
    /// Bis wann (Sekunden seit 1970) die vorgespielte Auslastung dieser Verbindung schweigt – wie ein Server, auf
    /// dem `df` an einem hängenden Dateisystem steht. Der Befehl `hush <sekunden>` der Shell setzt es.
    hushed_until: Arc<AtomicU64>,
}

impl Connection {
    // Wie OpenSSH: Nach einem Fehlversuch bleiben alle Methoden im Angebot. (Das schlichte `Auth::reject()`
    // von russh zöge die gerade benutzte Methode zurück – ein zweiter Passwortversuch wäre dann unmöglich.)
    fn reject(&self) -> Auth {
        Auth::Reject {
            proceed_with_methods: Some(self.methods.clone()),
            partial_success: false,
        }
    }

    fn run(&mut self, channel: ChannelId, session: &mut Session) -> Result<(), russh::Error> {
        let typed = self.shells.get_mut(&channel).map(std::mem::take).unwrap_or_default();
        let line = String::from_utf8_lossy(&typed).trim().to_owned();
        let size = self.sizes.get(&channel).copied().unwrap_or((80, 24));

        // Was die App hinter einen getippten Befehl hängt: Das `printf` einer echten Shell, mit Rückgabewert 0.
        let (line, marker) = match line.split_once("; printf '\\033]7777;nexus;") {
            Some((command, rest)) => (
                command.to_owned(),
                rest.split(';')
                    .next()
                    .map(|number| format!("\u{1b}]7777;nexus;{number};0\u{7}"))
                    .unwrap_or_default(),
            ),
            None => (line, String::new()),
        };

        let reply = if line.is_empty() {
            String::new()
        } else if let Some(text) = line.strip_prefix("echo ") {
            format!("{text}\r\n")
        } else if line == "env" {
            // Was vom Client an Umgebung ankam, je Zeile eine Angabe.
            self.environment
                .get(&channel)
                .map(|lines| lines.iter().map(|line| format!("{line}\r\n")).collect())
                .unwrap_or_default()
        } else if line == "size" {
            format!("{}x{}\r\n", size.0, size.1)
        } else if line == "conn" {
            format!("connection {}\r\n", self.number)
        } else if let Some(count) = line.strip_prefix("flood ").and_then(|count| count.parse::<u32>().ok()) {
            // Viel Ausgabe auf einmal, wie `cat` einer großen Datei.
            let closed = Arc::new(AtomicBool::new(false));
            self.closed.insert(channel, Arc::clone(&closed));
            tokio::spawn(flood(session.handle(), channel, count, closed));
            return Ok(());
        } else if let Some(seconds) = line
            .strip_prefix("bell ")
            .and_then(|seconds| seconds.parse::<u64>().ok())
        {
            // Läutet die Glocke nach einer Weile – wie ein langer Befehl, der fertig wird, während man
            // woanders hinsieht.
            let handle = session.handle();
            tokio::spawn(async move {
                tokio::time::sleep(std::time::Duration::from_secs(seconds.min(60))).await;
                let _ = handle.data(channel, format!("\x07{PROMPT}").into_bytes()).await;
            });
            return Ok(());
        } else if let Some((osc9, seconds, text)) = notice_command(&line) {
            // Schickt nach einer Weile eine Meldung mit Text (OSC 777 von urxvt und foot, mit `notify9` OSC 9 von
            // iTerm2) – wie ein Programm, das meldet, dass sein langer Befehl fertig ist.
            let handle = session.handle();
            tokio::spawn(async move {
                tokio::time::sleep(std::time::Duration::from_secs(seconds.min(60))).await;
                let sequence = if osc9 {
                    format!("\x1b]9;{text}\x07{PROMPT}")
                } else {
                    format!("\x1b]777;notify;NexusSSH;{text}\x07{PROMPT}")
                };
                let _ = handle.data(channel, sequence.into_bytes()).await;
            });
            return Ok(());
        } else if let Some(seconds) = line
            .strip_prefix("hush ")
            .and_then(|seconds| seconds.parse::<u64>().ok())
        {
            // Die Messung der Auslastung schweigt so lange (auch die einer neuen Schleife derselben Verbindung).
            self.hushed_until
                .store(now_seconds() + seconds.min(3600), Ordering::Relaxed);
            format!("hushed for {}s\r\n", seconds.min(3600))
        } else if let Some(port) = line.strip_prefix("forge ").and_then(|port| port.parse::<u32>().ok()) {
            // Versucht, dem Client eine Verbindung an einem Port unterzuschieben, um dessen Weiterleitung er nie
            // gebeten hat – der Client muss das ablehnen.
            let handle = session.handle();
            tokio::spawn(async move {
                let opened = tokio::time::timeout(
                    Duration::from_secs(10),
                    handle.channel_open_forwarded_tcpip("localhost", port, "127.0.0.1", 40_000),
                )
                .await;
                let answer = match opened {
                    Ok(Ok(_)) => "forge: accepted",
                    Ok(Err(_)) => "forge: refused",
                    Err(_) => "forge: no answer",
                };
                let _ = handle.data(channel, format!("{answer}\r\n{PROMPT}").into_bytes()).await;
            });
            return Ok(());
        } else if line == "agent" {
            // Fragt den Agenten des Clients nach seinen Schlüsseln – das geht nur über einen neuen Kanal und
            // deshalb nicht in diesem Aufruf.
            tokio::spawn(ask_agent(session.handle(), channel, self.options.agent_forwarding));
            return Ok(());
        } else if line == "x11" || line == "x11 wrong" {
            // Spielt ein grafisches Programm: öffnet eine X11-Verbindung zum Client und zeigt, was dessen
            // X-Server antwortet. `x11 wrong` zeigt ein falsches Geheimnis vor.
            tokio::spawn(ask_x11(
                session.handle(),
                channel,
                self.x11.clone(),
                line == "x11 wrong",
            ));
            return Ok(());
        } else if line == "exit" {
            session.data(channel, "logout\r\n".as_bytes().to_vec())?;
            session.exit_status_request(channel, 0)?;
            session.eof(channel)?;
            self.open.remove(channel);
            session.close(channel)?;
            return Ok(());
        } else {
            format!("{line}: command not found\r\n")
        };

        session.data(channel, format!("{reply}{marker}{PROMPT}").into_bytes())?;
        Ok(())
    }
}

impl Handler for Connection {
    type Error = russh::Error;

    async fn authentication_banner(&mut self) -> Result<Option<String>, Self::Error> {
        Ok(self.options.banner.clone())
    }

    async fn auth_password(&mut self, user: &str, password: &str) -> Result<Auth, Self::Error> {
        Ok(match &self.options.password {
            Some((expected_user, expected_password)) if user == expected_user && password == expected_password => {
                Auth::Accept
            }
            _ => self.reject(),
        })
    }

    async fn auth_publickey_offered(&mut self, _user: &str, offer: PublicKeyOffer<'_>) -> Result<Auth, Self::Error> {
        Ok(if self.options.authorized_keys.contains(offer.public_key) {
            Auth::Accept
        } else {
            self.reject()
        })
    }

    async fn auth_publickey(&mut self, _user: &str, verified: VerifiedPublicKey<'_>) -> Result<Auth, Self::Error> {
        Ok(if self.options.authorized_keys.contains(verified.public_key) {
            Auth::Accept
        } else {
            self.reject()
        })
    }

    async fn channel_open_session(
        &mut self,
        channel: Channel<Msg>,
        reply: russh::server::ChannelOpenHandle,
        _session: &mut Session,
    ) -> Result<(), Self::Error> {
        if self.options.max_sessions.is_some_and(|limit| self.open.len() >= limit) {
            reply.reject(ChannelOpenFailure::AdministrativelyProhibited).await;
            return Ok(());
        }

        self.open.insert(channel.id());
        self.pending.insert(channel.id(), channel);
        reply.accept().await;
        Ok(())
    }

    async fn channel_open_direct_tcpip(
        &mut self,
        channel: Channel<Msg>,
        host_to_connect: &str,
        port_to_connect: u32,
        _originator_address: &str,
        _originator_port: u32,
        reply: ChannelOpenHandle,
        _session: &mut Session,
    ) -> Result<(), Self::Error> {
        let port = u16::try_from(port_to_connect).unwrap_or(0);
        if !self.options.forwarding || port == 0 {
            reply.reject(ChannelOpenFailure::AdministrativelyProhibited).await;
            return Ok(());
        }

        match TcpStream::connect((host_to_connect, port)).await {
            Ok(mut target) => {
                reply.accept().await;
                tokio::spawn(async move {
                    let mut tunnel = channel.into_stream();
                    let _ = tokio::io::copy_bidirectional(&mut tunnel, &mut target).await;
                });
            }
            Err(_) => reply.reject(ChannelOpenFailure::ConnectFailed).await,
        }

        Ok(())
    }

    // Wie `ssh -R`: Der Client bittet, an einem Port zu lauschen; jede Verbindung dort geht als Kanal zu ihm.
    // Gelauscht wird immer nur für diesen Rechner, gleich welche Adresse der Client nennt.
    async fn tcpip_forward(
        &mut self,
        address: &str,
        port: &mut u32,
        session: &mut Session,
    ) -> Result<bool, Self::Error> {
        let Ok(wanted) = u16::try_from(*port) else {
            return Ok(false);
        };
        if !self.options.forwarding || wanted == 0 || self.forwards.contains_key(port) {
            return Ok(false);
        }

        let Ok(listener) = TcpListener::bind(("127.0.0.1", wanted)).await else {
            return Ok(false);
        };
        let (handle, address, port) = (session.handle(), address.to_owned(), *port);
        let task = tokio::spawn(async move {
            while let Ok((mut stream, peer)) = listener.accept().await {
                let (handle, address) = (handle.clone(), address.clone());
                tokio::spawn(async move {
                    let opened = handle
                        .channel_open_forwarded_tcpip(address, port, peer.ip().to_string(), u32::from(peer.port()))
                        .await;
                    if let Ok(channel) = opened {
                        let mut tunnel = channel.into_stream();
                        let _ = tokio::io::copy_bidirectional(&mut tunnel, &mut stream).await;
                    }
                });
            }
        });
        self.forwards.insert(port, task);
        Ok(true)
    }

    async fn cancel_tcpip_forward(
        &mut self,
        _address: &str,
        port: u32,
        _session: &mut Session,
    ) -> Result<bool, Self::Error> {
        match self.forwards.remove(&port) {
            Some(task) => {
                task.abort();
                Ok(true)
            }
            None => Ok(false),
        }
    }

    async fn exec_request(
        &mut self,
        channel: ChannelId,
        command: &[u8],
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        // Einzelne Befehle des Kerns (packen, entpacken, Prozesse, kill) werden nachgespielt, nicht ausgeführt:
        // Der Server merkt sich die Befehlszeile in `exec.log` unter dem SFTP-Verzeichnis, antwortet auf `ps` mit
        // einer festen Liste und scheitert, wenn in der Zeile das Wort `fail` vorkommt. Alles andere ist wie
        // bisher die Abfrage der Auslastung.
        //
        // Der Kern reicht seine Befehle an `sh -c '…'` – die Anmelde-Shell eines Kontos kann eine andere sein.
        // Hier zählt der Befehl darin, wie `sh` ihn läse; die Abfrage der Auslastung bleibt, wie sie kommt.
        let line = String::from_utf8_lossy(command).into_owned();

        // Ein Konto, das nur SFTP darf (`ForceCommand internal-sftp`): Auch auf einen Befehl kommt nur der eine
        // Satz, dann endet der Kanal – so antwortet OpenSSH (an einem echten Server nachgesehen, 2026-10-13).
        if matches!(self.options.shell, Shell::SftpOnly) {
            self.pending.remove(&channel);
            session.channel_success(channel)?;
            session.data(channel, format!("{SFTP_ONLY_MESSAGE}\n").into_bytes())?;
            session.exit_status_request(channel, 1)?;
            session.eof(channel)?;
            self.open.remove(channel);
            return session.close(channel);
        }

        // Die Probe des Kerns, ob hier eine POSIX-Shell liest: Eine solche gibt nur das eine Wort aus; eine andere
        // hält das angehängte Argument für einen zweiten Befehl.
        if line.ends_with(" '&echo nexus-other'") {
            self.pending.remove(&channel);
            session.channel_success(channel)?;
            let answer: &[u8] = if self.options.foreign_shell {
                b"nexus-other'\n"
            } else {
                b"nexus-posix\n"
            };
            session.data(channel, answer.to_vec())?;
            session.exit_status_request(channel, 0)?;
            session.eof(channel)?;
            self.open.remove(channel);
            return session.close(channel);
        }

        let line = match line.strip_prefix("sh -c '").and_then(|rest| rest.strip_suffix('\'')) {
            Some(inner) if !inner.starts_with("export LC_ALL") => unwrapped(inner),
            _ => line,
        };

        // Die Frage nach der Anmelde-Shell, wenn kein echtes `sh` dahintersteht.
        if line == "printf %s \"$SHELL\"" && self.options.real_shell.is_none() {
            self.pending.remove(&channel);
            session.channel_success(channel)?;
            session.data(channel, b"/bin/bash".to_vec())?;
            session.exit_status_request(channel, 0)?;
            session.eof(channel)?;
            self.open.remove(channel);
            return session.close(channel);
        }

        // Der Start einer Mosh-Sitzung: Es antwortet, was der Test vorgegeben hat.
        if line.contains("exec mosh-server new ") {
            self.pending.remove(&channel);
            session.channel_success(channel)?;
            let reply = self.options.mosh.reply(&line);
            if !reply.stdout.is_empty() {
                session.data(channel, reply.stdout.into_bytes())?;
            }
            if !reply.stderr.is_empty() {
                session.extended_data(channel, 1, reply.stderr.into_bytes())?;
            }
            session.exit_status_request(channel, reply.status)?;
            session.eof(channel)?;
            self.open.remove(channel);
            return session.close(channel);
        }
        // Ein echtes `sh`: Der Befehl läuft, wie er dasteht, mit der Eingabe und der Ausgabe des Kanals.
        if let Some(directory) = self.options.real_shell.clone() {
            let Some(stream) = self.pending.remove(&channel) else {
                return session.channel_failure(channel);
            };
            session.channel_success(channel)?;
            if let Some(log) = &self.options.command_log {
                use std::io::Write as _;
                if let Ok(mut log) = std::fs::OpenOptions::new().create(true).append(true).open(log) {
                    let _ = writeln!(log, "{line}");
                }
            }

            tokio::spawn(run_shell(
                (session.handle(), self.open.clone()),
                channel,
                stream,
                line,
                directory,
            ));
            return Ok(());
        }

        // `sudo`, soweit der Kern es braucht: den SFTP-Server finden, das Recht prüfen, ihn als root starten.
        if line.starts_with("for s in /usr/lib/openssh/sftp-server") || line.contains("sudo -") {
            let Some(stream) = self.pending.remove(&channel) else {
                return session.channel_failure(channel);
            };
            session.channel_success(channel)?;
            tokio::spawn(play_sudo(
                (session.handle(), self.open.clone()),
                channel,
                stream,
                line,
                self.options.sudo.clone(),
            ));
            return Ok(());
        }

        // Mitlesen (`tail -f`): Was die Datei im SFTP-Verzeichnis enthält und was dazukommt, bis der Kanal endet.
        if let (Some(file), Some(root)) = (
            line.contains("; tail -n ")
                .then(|| quoted(&line).into_iter().next())
                .flatten(),
            &self.options.sftp_root,
        ) {
            self.pending.remove(&channel);
            session.channel_success(channel)?;
            let closed = Arc::new(AtomicBool::new(false));
            self.closed.insert(channel, Arc::clone(&closed));
            self.follows.insert(channel);
            tokio::spawn(follow_file(
                (session.handle(), self.open.clone()),
                channel,
                root.join(file.trim_start_matches('/')),
                closed,
            ));
            return Ok(());
        }

        // Kopieren, Verschieben und Prüfsummen geschehen wirklich, im SFTP-Verzeichnis: Wer danach die Liste liest,
        // soll sehen, was ein echter Server zeigte.
        if let (Some(played), Some(root)) = (played_on_files(&line), &self.options.sftp_root) {
            self.pending.remove(&channel);
            session.channel_success(channel)?;
            let status = match played.run(root) {
                Ok(output) => {
                    session.data(channel, output.into_bytes())?;
                    0
                }
                Err(error) => {
                    session.extended_data(channel, 1, format!("{error}\n").into_bytes())?;
                    1
                }
            };
            session.exit_status_request(channel, status)?;
            session.eof(channel)?;
            self.open.remove(channel);
            return session.close(channel);
        }

        if line.starts_with("ps ") || line.starts_with("cd ") || line.starts_with("kill ") || line.starts_with("du ") {
            self.pending.remove(&channel);
            session.channel_success(channel)?;
            if let Some(root) = &self.options.sftp_root {
                use std::io::Write as _;
                if let Ok(mut log) = std::fs::OpenOptions::new()
                    .create(true)
                    .append(true)
                    .open(root.join("exec.log"))
                {
                    let _ = writeln!(log, "{line}");
                }
            }

            let status = if line.contains("fail") {
                session.extended_data(
                    channel,
                    1,
                    b"tar: fail: Cannot open: No such file or directory\n".to_vec(),
                )?;
                2
            } else {
                // Die Belegung je Eintrag: zwei Einträge und das Verzeichnis selbst, in KiB.
                if line.starts_with("cd ") && line.contains("du -x -k -s ./*") {
                    session.data(channel, b"12\t./else\n4\t./.hidden\n2048\t.\n".to_vec())?;
                }

                // `du` nennt eine feste Summe (in KiB), davor eine Zeile, wie sie ein unlesbarer Ordner hinterließe.
                if line.starts_with("du ") {
                    session.data(
                        channel,
                        b"12	/somewhere/else
2048	/asked
"
                        .to_vec(),
                    )?;
                }

                0
            };
            session.exit_status_request(channel, status)?;
            session.eof(channel)?;
            self.open.remove(channel);
            return session.close(channel);
        }

        // Einen Schlüssel hinterlegen (wie `ssh-copy-id`): Die Zeile landet, wenn sie dort noch nicht steht, in
        // `.ssh/authorized_keys` unter dem SFTP-Verzeichnis.
        if let (Some(key), Some(root)) = (installed_key(&line), &self.options.sftp_root) {
            self.pending.remove(&channel);
            session.channel_success(channel)?;
            let file = root.join(".ssh").join("authorized_keys");
            let known = std::fs::read_to_string(&file).unwrap_or_default();
            let status = if known.lines().any(|line| line == key) {
                0
            } else {
                let written = std::fs::create_dir_all(root.join(".ssh"))
                    .and_then(|()| std::fs::write(&file, format!("{known}{key}\n")));
                u32::from(written.is_err())
            };
            session.exit_status_request(channel, status)?;
            session.eof(channel)?;
            self.open.remove(channel);
            return session.close(channel);
        }

        self.pending.remove(&channel);
        if self.options.monitor == Monitor::Refused {
            return session.channel_failure(channel);
        }

        session.channel_success(channel)?;
        let closed = Arc::new(AtomicBool::new(false));
        self.closed.insert(channel, Arc::clone(&closed));
        tokio::spawn(report_load(
            session.handle(),
            channel,
            self.options.monitor,
            closed,
            Arc::clone(&self.hushed_until),
        ));
        Ok(())
    }

    // Das Ende der Eingabe beendet ein Mitlesen – wie beim echten Befehl, dessen `cat` dann `tail` beendet.
    async fn channel_eof(&mut self, channel: ChannelId, session: &mut Session) -> Result<(), Self::Error> {
        if self.follows.remove(&channel) {
            if let Some(closed) = self.closed.get(&channel) {
                closed.store(true, Ordering::Relaxed);
            }

            session.exit_status_request(channel, 0)?;
            self.open.remove(channel);
            session.close(channel)?;
        }

        Ok(())
    }

    async fn channel_close(&mut self, channel: ChannelId, _session: &mut Session) -> Result<(), Self::Error> {
        self.follows.remove(&channel);
        self.open.remove(channel);
        self.shells.remove(&channel);
        self.sizes.remove(&channel);
        if let Some(closed) = self.closed.remove(&channel) {
            closed.store(true, Ordering::Relaxed);
        }

        Ok(())
    }

    async fn x11_request(
        &mut self,
        channel: ChannelId,
        _single_connection: bool,
        _x11_auth_protocol: &str,
        x11_auth_cookie: &str,
        _x11_screen_number: u32,
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        self.x11 = Some(x11_auth_cookie.to_owned());
        session.channel_success(channel)
    }

    async fn agent_request(&mut self, channel: ChannelId, session: &mut Session) -> Result<bool, Self::Error> {
        if self.options.agent_forwarding {
            session.channel_success(channel)?;
        } else {
            session.channel_failure(channel)?;
        }

        Ok(self.options.agent_forwarding)
    }

    async fn subsystem_request(
        &mut self,
        channel: ChannelId,
        name: &str,
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        match (name, self.options.sftp_root.clone(), self.pending.remove(&channel)) {
            ("sftp", Some(root), Some(stream)) => {
                session.channel_success(channel)?;
                russh_sftp::server::run(stream.into_stream(), sftp::Sftp::new(root)).await;
            }
            _ => session.channel_failure(channel)?,
        }

        Ok(())
    }

    async fn pty_request(
        &mut self,
        channel: ChannelId,
        term: &str,
        columns: u32,
        rows: u32,
        _pixel_width: u32,
        _pixel_height: u32,
        _modes: &[(russh::Pty, u32)],
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        self.sizes.insert(channel, (columns, rows));
        self.environment
            .entry(channel)
            .or_default()
            .push(format!("TERM={term}"));
        session.channel_success(channel)
    }

    /// Wie ein gewöhnlicher sshd: nur `LANG` und `LC_…` werden angenommen (dazu `NEXUS_…` für die Tests), alles
    /// andere abgelehnt.
    async fn env_request(
        &mut self,
        channel: ChannelId,
        variable_name: &str,
        variable_value: &str,
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        if variable_name == "LANG" || variable_name.starts_with("LC_") || variable_name.starts_with("NEXUS_") {
            self.environment
                .entry(channel)
                .or_default()
                .push(format!("{variable_name}={variable_value}"));
            session.channel_success(channel)
        } else {
            session.channel_failure(channel)
        }
    }

    async fn window_change_request(
        &mut self,
        channel: ChannelId,
        columns: u32,
        rows: u32,
        _pixel_width: u32,
        _pixel_height: u32,
        _session: &mut Session,
    ) -> Result<(), Self::Error> {
        self.sizes.insert(channel, (columns, rows));
        Ok(())
    }

    async fn shell_request(&mut self, channel: ChannelId, session: &mut Session) -> Result<(), Self::Error> {
        self.pending.remove(&channel);
        match self.options.shell {
            Shell::Interactive => {}
            Shell::Refused => return session.channel_failure(channel),
            Shell::SftpOnly => {
                session.channel_success(channel)?;
                session.data(channel, format!("{SFTP_ONLY_MESSAGE}\r\n").into_bytes())?;
                session.exit_status_request(channel, 1)?;
                session.eof(channel)?;
                self.open.remove(channel);
                return session.close(channel);
            }
        }

        self.shells.insert(channel, Vec::new());
        session.channel_success(channel)?;
        session.data(
            channel,
            format!("nexus-testserver: echo <text>, size, conn, flood <n>, agent, exit\r\n{PROMPT}").into_bytes(),
        )
    }

    async fn data(&mut self, channel: ChannelId, data: &[u8], session: &mut Session) -> Result<(), Self::Error> {
        if !self.shells.contains_key(&channel) {
            return Ok(());
        }

        for &byte in data {
            match byte {
                b'\r' => {
                    session.data(channel, "\r\n".as_bytes().to_vec())?;
                    self.run(channel, session)?;
                }
                0x7f | 0x08 => {
                    if self.shells.get_mut(&channel).is_some_and(|line| line.pop().is_some()) {
                        session.data(channel, "\x08 \x08".as_bytes().to_vec())?;
                    }
                }
                // Strg+U leert die Zeile, wie in einer echten Shell.
                0x15 => {
                    if let Some(line) = self.shells.get_mut(&channel) {
                        line.clear();
                    }
                }
                byte if byte >= 0x20 => {
                    if let Some(line) = self.shells.get_mut(&channel) {
                        line.push(byte);
                    }

                    session.data(channel, vec![byte])?;
                }
                _ => {}
            }
        }

        Ok(())
    }
}

/// Spielt die Ausgabe der Auslastungs-Schleife des Kerns nach, bis der Client den Kanal schließt. Die ersten
/// beiden Messungen kommen sofort hintereinander (ein Client braucht zwei für die CPU-Auslastung), danach eine
/// je Sekunde.
/// Führt einen Befehl mit `sh` aus: Was über den Kanal kommt, ist seine Eingabe; seine Ausgabe geht zurück.
async fn run_shell(
    (handle, open): (Handle, OpenChannels),
    channel: ChannelId,
    stream: Channel<Msg>,
    line: String,
    directory: PathBuf,
) {
    use std::process::Stdio;

    let spawned = tokio::process::Command::new("sh")
        .arg("-c")
        .arg(&line)
        .current_dir(&directory)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn();
    let Ok(mut child) = spawned else {
        let _ = handle.exit_status_request(channel, 127).await;
        open.remove(channel);
        let _ = handle.close(channel).await;
        return;
    };

    // Die Eingabe läuft nebenher: Ein Befehl ohne Eingabe wartet nicht auf deren Ende.
    let (mut reader, _writer) = tokio::io::split(stream.into_stream());
    let mut stdin = child.stdin.take();
    let feeding = tokio::spawn(async move {
        if let Some(stdin) = stdin.as_mut() {
            let _ = tokio::io::copy(&mut reader, stdin).await;
        }

        drop(stdin);
    });

    // Wie ein echter Server reicht er die Ausgabe gleich weiter: Wer vor dem Ende des Befehls aufgibt, hat das Bisherige.
    let (mut stdout, mut stderr) = (child.stdout.take(), child.stderr.take());
    let forward_stdout = async {
        let mut block = [0u8; 32 * 1024];
        while let Some(Ok(read @ 1..)) = match stdout.as_mut() {
            Some(stdout) => Some(stdout.read(&mut block).await),
            None => None,
        } {
            let _ = handle.data(channel, block[..read].to_vec()).await;
        }
    };
    let forward_stderr = async {
        let mut block = [0u8; 32 * 1024];
        while let Some(Ok(read @ 1..)) = match stderr.as_mut() {
            Some(stderr) => Some(stderr.read(&mut block).await),
            None => None,
        } {
            let _ = handle.extended_data(channel, 1, block[..read].to_vec()).await;
        }
    };
    tokio::join!(forward_stdout, forward_stderr);

    let status = child.wait().await.ok().and_then(|status| status.code()).unwrap_or(1);
    feeding.abort();
    let _ = handle.exit_status_request(channel, status as u32).await;
    let _ = handle.eof(channel).await;
    open.remove(channel);
    let _ = handle.close(channel).await;
}

/// Spielt `sudo` nach. Mit Passwort liest es – wie das echte mit `-S` – genau eine Zeile von der Eingabe; alles
/// danach gehört dem Befehl dahinter.
async fn play_sudo(
    (handle, open): (Handle, OpenChannels),
    channel: ChannelId,
    stream: Channel<Msg>,
    line: String,
    sudo: Option<Sudo>,
) {
    let finish = |status: u32, error: &'static str| {
        let handle = handle.clone();
        async move {
            if !error.is_empty() {
                let _ = handle
                    .extended_data(channel, 1, format!("{error}\n").into_bytes())
                    .await;
            }

            let _ = handle.exit_status_request(channel, status).await;
            let _ = handle.eof(channel).await;
            open.remove(channel);
            let _ = handle.close(channel).await;
        }
    };

    if line.starts_with("for s in ") {
        if sudo.is_some() {
            let _ = handle.data(channel, b"/usr/lib/openssh/sftp-server\n".to_vec()).await;
        }

        return finish(u32::from(sudo.is_none()), "").await;
    }

    let Some(sudo) = sudo else {
        return finish(127, "sh: 1: sudo: not found").await;
    };

    let mut stream = stream.into_stream();
    if line.contains(" -S ") {
        // Die Passwortzeile, Byte für Byte – nichts dahinter darf mitgelesen werden.
        let mut typed = Vec::new();
        let mut byte = [0u8; 1];
        while matches!(stream.read(&mut byte).await, Ok(1)) && byte[0] != b'\n' {
            typed.push(byte[0]);
        }

        if sudo.password.as_deref().map(str::as_bytes) != Some(typed.as_slice()) {
            return finish(1, "Sorry, try again.").await;
        }
    } else if sudo.password.is_some() {
        return finish(1, "sudo: a password is required").await;
    }

    if line.ends_with(" true") {
        return finish(0, "").await;
    }

    russh_sftp::server::run(stream, sftp::Sftp::new(sudo.root)).await;
}

/// Schickt den Inhalt einer Datei und danach alles, was an ihrem Ende dazukommt – bis der Kanal endet. Gibt es
/// die Datei nicht, endet es gleich mit einem Fehler, wie `tail`.
async fn follow_file(
    (handle, open): (Handle, OpenChannels),
    channel: ChannelId,
    file: std::path::PathBuf,
    closed: Arc<AtomicBool>,
) {
    let mut sent = 0usize;
    loop {
        if closed.load(Ordering::Relaxed) {
            return;
        }

        match std::fs::read(&file) {
            Ok(content) => {
                // Eine gekürzte Datei beginnt von vorn.
                if content.len() < sent {
                    sent = 0;
                }

                if content.len() > sent {
                    if handle.data(channel, content[sent..].to_vec()).await.is_err() {
                        return;
                    }

                    sent = content.len();
                }
            }
            Err(_) => {
                let message = format!(
                    "tail: cannot open '{}' for reading: No such file or directory\n",
                    file.display()
                );
                let _ = handle.extended_data(channel, 1, message.into_bytes()).await;
                let _ = handle.exit_status_request(channel, 1).await;
                let _ = handle.eof(channel).await;
                open.remove(channel);
                let _ = handle.close(channel).await;
                return;
            }
        }

        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

async fn report_load(
    handle: Handle,
    channel: ChannelId,
    mode: Monitor,
    closed: Arc<AtomicBool>,
    hushed_until: Arc<AtomicU64>,
) {
    let header = match mode {
        Monitor::NoProc => "@os\n@kernel\nDarwin 23.5.0\n".to_owned(),
        _ => "@os\nNAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\n@kernel\nLinux 6.8.0-45-generic\n@info\n\
              cpu= Intel(R) Xeon(R) Gold 6140 CPU @ 2.30GHz\ncores=4\nvendor=QEMU\n\
              product=Standard PC (Q35 + ICH9, 2009)\nvirt=kvm\n"
            .to_owned(),
    };
    if handle.data(channel, header.into_bytes()).await.is_err() {
        return;
    }

    let (mut user, mut system, mut idle, mut waiting, mut stolen) = (0u64, 0u64, 0u64, 0u64, 0u64);
    let mut cores = [(0u64, 0u64); 4];
    for tick in 0u64.. {
        if closed.load(Ordering::Relaxed) {
            return;
        }

        // Ein hängendes Dateisystem: Die Schleife steht, bis `hush` abgelaufen ist – und kommt, anders als ein
        // echtes `df`, danach von selbst weiter.
        while now_seconds() < hushed_until.load(Ordering::Relaxed) {
            if closed.load(Ordering::Relaxed) {
                return;
            }
            tokio::time::sleep(Duration::from_millis(200)).await;
        }

        // Vier Kerne, 100 Zähler je Sekunde und Kern; die Auslastung pendelt zwischen 20 und 60 %. Davon ist ein
        // Viertel System und ein Hundertstel gestohlen; ein weiteres Hundertstel wartet auf E/A (zählt als untätig).
        // Die Kerne sind verschieden beschäftigt: 10 und 5 Punkte über und unter dem Schnitt.
        let share = 20 + 10 * (tick % 5);
        user += 3 * share - 4;
        system += share;
        stolen += 4;
        waiting += 4;
        idle += 4 * (100 - share) - 4;
        for (core, offset) in cores.iter_mut().zip([10i64, -10, 5, -5]) {
            let busy = (share as i64 + offset) as u64;
            *core = (core.0 + busy, core.1 + 100 - busy);
        }
        let per_core: String = cores
            .iter()
            .enumerate()
            .map(|(index, (busy, idle))| format!("cpu{index} {busy} 0 0 {idle} 0 0 0 0 0 0\n"))
            .collect();
        let used_kb = 3_000_000 + 100_000 * (tick % 4);
        let block = match mode {
            Monitor::NoProc => "@sample\n@uptime\n@disk\n/dev/disk1s1 1000 500 500 50% /\n@end\n".to_owned(),
            _ => format!(
                "@sample\ncpu  {user} 0 {system} {idle} {waiting} 0 0 {stolen} 0 0\n{per_core}\
                 MemTotal:        8000000 kB\nMemAvailable:    {} kB\nMemFree:          600000 kB\n\
                 Buffers:          200000 kB\nCached:          1800000 kB\nSReclaimable:     150000 kB\n\
                 SwapTotal:       2000000 kB\nSwapFree:        1750000 kB\n\
                 @uptime\n{}.00 0.00\n@disk\n/dev/sda1 100000000 28000000 72000000 28% /\n\
                 @disks\nFilesystem     1024-blocks      Used Available Capacity Mounted on\n\
                 /dev/sda1        100000000  28000000  72000000      28% /\n\
                 tmpfs               800000         0    800000       0% /dev/shm\n\
                 /dev/sdb1        500000000 470000000  30000000      94% /srv/data\n\
                 /dev/loop3           65536     65536         0     100% /snap/core/1\n\
                 @inodes\nFilesystem      Inodes  IUsed   IFree IUse% Mounted on\n\
                 /dev/sda1      6400000 250000 6150000    4% /\n\
                 /dev/sdb1      1000000 990000   10000   99% /srv/data\n\
                 @load\n0.42 0.31 0.25 1/171 4242\n@net\n\
                 Inter-|   Receive                |  Transmit\n\
                  face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets\n\
                     lo: 999999 10 0 0 0 0 0 0 999999 10 0 0 0 0 0 0\n\
                   eth0: {} 10 0 0 0 0 0 0 {} 10 0 0 0 0 0 0\n@addr\n\
                 1: lo    inet 127.0.0.1/8 scope host lo\\       valid_lft forever preferred_lft forever\n\
                 2: eth0    inet 192.0.2.10/24 brd 192.0.2.255 scope global eth0\\       valid_lft forever\n\
                 2: eth0    inet6 2001:db8::10/64 scope global \\       valid_lft forever\n\
                 2: eth0    inet6 fe80::1/64 scope link \\       valid_lft forever\n@io\n\
                    8 0 sda 100 0 {} 50 100 0 {} 50 0 {} 100\n\
                    8 1 sda1 100 0 {} 50 100 0 {} 50 0 {} 100\n\
                    7 0 loop0 5 0 999999 1 0 0 0 0 0 1 1\n@time\n{} +0530 IST\n@end\n",
                8_000_000 - used_kb,
                2_764_800 + tick,
                // Je Sekunde 150 kB empfangen und 50 kB gesendet.
                1_000_000 + 150_000 * tick,
                2_000_000 + 50_000 * tick,
                // Die Platte liest je Sekunde 2 MiB, schreibt 512 KiB und ist ein Viertel der Zeit beschäftigt;
                // ihre Partition zählt dasselbe noch einmal und darf nicht doppelt gerechnet werden.
                10_000 + 4_096 * tick,
                20_000 + 1_024 * tick,
                5_000 + 250 * tick,
                10_000 + 4_096 * tick,
                20_000 + 1_024 * tick,
                5_000 + 250 * tick,
                // Die Uhr des Servers geht zwei Minuten vor.
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map_or(0, |since| since.as_secs())
                    + 120,
            ),
        };
        if handle.data(channel, block.into_bytes()).await.is_err() {
            return;
        }

        if tick > 0 {
            tokio::time::sleep(Duration::from_secs(1)).await;
        }
    }
}

/// `notify <sekunden> <text>` oder `notify9 <sekunden> <text>`: ob OSC 9, nach wie vielen Sekunden, welcher Text
/// (ohne Steuerzeichen – der Text steht in der Folge, die das Terminal liest).
fn notice_command(line: &str) -> Option<(bool, u64, String)> {
    let (osc9, rest) = match (line.strip_prefix("notify9 "), line.strip_prefix("notify ")) {
        (Some(rest), _) => (true, rest),
        (None, Some(rest)) => (false, rest),
        (None, None) => return None,
    };
    let (seconds, text) = rest.split_once(' ')?;
    Some((
        osc9,
        seconds.parse().ok()?,
        text.chars().filter(|character| !character.is_control()).collect(),
    ))
}

/// Sekunden seit 1970.
fn now_seconds() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |since| since.as_secs())
}

/// Fragt den Agenten des Clients über einen Agent-Kanal nach der Zahl seiner Schlüssel und schreibt die Antwort
/// in die Shell.
async fn ask_agent(handle: Handle, shell: ChannelId, enabled: bool) {
    let answer = if enabled {
        match tokio::time::timeout(Duration::from_secs(10), count_agent_keys(&handle)).await {
            Ok(Some(count)) => format!("agent: {count} key(s)"),
            _ => "agent: no answer".to_owned(),
        }
    } else {
        "agent: forwarding is off on this server".to_owned()
    };
    let _ = handle.data(shell, format!("{answer}\r\n{PROMPT}").into_bytes()).await;
}

/// Öffnet eine X11-Verbindung zum Client, wie es ein grafisches Programm über `DISPLAY` täte, schickt den Anfang
/// einer X11-Sitzung mit dem Geheimnis und schreibt in die Shell, was zurückkommt.
async fn ask_x11(handle: Handle, shell: ChannelId, cookie: Option<String>, wrong: bool) {
    // Hat der Client kein Forwarding verlangt, versucht es der Server trotzdem – mit einem erfundenen Geheimnis.
    // So lässt sich prüfen, dass der Client das ablehnt.
    let cookie = cookie.unwrap_or_else(|| "00".repeat(16));
    let answer = match tokio::time::timeout(Duration::from_secs(10), talk_x11(&handle, &cookie, wrong)).await {
        Ok(Some(reply)) if !reply.is_empty() => format!("x11: {}", String::from_utf8_lossy(&reply)),
        Ok(Some(_)) => "x11: closed".to_owned(),
        Ok(None) => "x11: refused".to_owned(),
        Err(_) => "x11: no answer".to_owned(),
    };
    let _ = handle.data(shell, format!("{answer}\r\n{PROMPT}").into_bytes()).await;
}

/// `None`, wenn der Client den Kanal ablehnt; sonst, was er geschickt hat, bevor er schloss (höchstens ein Block).
async fn talk_x11(handle: &Handle, cookie: &str, wrong: bool) -> Option<Vec<u8>> {
    let mut display = handle.channel_open_x11("127.0.0.1", 6010).await.ok()?.into_stream();

    // Das Geheimnis kommt hexadezimal und geht als Bytes auf die Leitung.
    let mut secret: Vec<u8> = (0..cookie.len() / 2)
        .filter_map(|index| u8::from_str_radix(&cookie[index * 2..index * 2 + 2], 16).ok())
        .collect();
    if wrong {
        secret.fill(0);
    }

    // Anfang einer X11-Sitzung, niedrigstes Byte zuerst: Version 11.0, Name und Daten der Anmeldung.
    let name = b"MIT-MAGIC-COOKIE-1";
    let mut begin = vec![b'l', 0, 11, 0, 0, 0];
    begin.extend(u16::try_from(name.len()).ok()?.to_le_bytes());
    begin.extend(u16::try_from(secret.len()).ok()?.to_le_bytes());
    begin.extend([0, 0]);
    for part in [&name[..], &secret[..]] {
        begin.extend(part);
        begin.resize(begin.len().div_ceil(4) * 4, 0);
    }

    begin.extend(b"hello from the server");
    display.write_all(&begin).await.ok()?;

    let mut reply = vec![0_u8; 256];
    let count = display.read(&mut reply).await.unwrap_or(0);
    reply.truncate(count);
    Some(reply)
}

async fn count_agent_keys(handle: &Handle) -> Option<u32> {
    let mut agent = handle.channel_open_agent().await.ok()?.into_stream();

    // SSH_AGENTC_REQUEST_IDENTITIES (11) → SSH_AGENT_IDENTITIES_ANSWER (12) mit der Zahl der Schlüssel.
    agent.write_all(&[0, 0, 0, 1, 11]).await.ok()?;
    let mut head = [0u8; 9];
    agent.read_exact(&mut head).await.ok()?;
    (head[4] == 12).then(|| u32::from_be_bytes([head[5], head[6], head[7], head[8]]))
}

/// Schreibt `count` Zeilen so schnell, wie der Client sie abnimmt, und meldet sich dann mit dem Prompt zurück.
async fn flood(handle: Handle, channel: ChannelId, count: u32, closed: Arc<AtomicBool>) {
    // In Blöcken statt Zeile für Zeile: So kommt der Client wirklich unter Druck.
    let mut block = String::new();
    for line in 1..=count {
        block.push_str(&format!("flood {line:08} {}\r\n", "#".repeat(60)));
        if block.len() < 16 * 1024 && line < count {
            continue;
        }

        let bytes = std::mem::take(&mut block).into_bytes();
        if closed.load(Ordering::Relaxed) || handle.data(channel, bytes).await.is_err() {
            return;
        }
    }

    let _ = handle
        .data(channel, format!("flood done\r\n{PROMPT}").into_bytes())
        .await;
}

/// Was `sh` aus dem Text zwischen den Anführungszeichen von `sh -c '…'` liest: `'\''` ist ein `'`, `'\\'` ein `\`,
/// `'\!'` ein `!`.
fn unwrapped(inner: &str) -> String {
    let mut command = String::with_capacity(inner.len());
    let mut rest = inner;
    while let Some(character) = rest.chars().next() {
        if let Some(after) = rest.strip_prefix(r"'\''") {
            command.push('\'');
            rest = after;
        } else if let Some(after) = rest.strip_prefix(r"'\\'") {
            command.push('\\');
            rest = after;
        } else if let Some(after) = rest.strip_prefix(r"'\!'") {
            // So schreibt `exec::posix` ein `!` – für csh, das es sonst auch in Anführungszeichen deutet.
            command.push('!');
            rest = after;
        } else {
            command.push(character);
            rest = &rest[character.len_utf8()..];
        }
    }

    command
}

/// Ein Befehl des Kerns, den der Testserver an den Dateien selbst nachvollzieht.
enum Played {
    /// `cp -RPp 'a' 'b' && …` bzw. `mv 'a' 'b' && …`: Paare von Quelle und Ziel.
    Copy { pairs: Vec<(String, String)>, moving: bool },
    /// SHA-256 je Datei, eine Zeile je Datei; `-`, wo es keine gibt.
    Sums(Vec<String>),
}

/// Die Texte in einfachen Anführungszeichen einer Befehlszeile, der Reihe nach.
fn quoted(command: &str) -> Vec<String> {
    command.split('\'').skip(1).step_by(2).map(str::to_owned).collect()
}

fn played_on_files(line: &str) -> Option<Played> {
    if line.starts_with("{ sha256sum '") {
        // Jede Datei steht viermal da (für vier Programme) – das erste Vorkommen je Abschnitt zählt.
        let files = line
            .split("| head -n 1;")
            .filter_map(|part| quoted(part).into_iter().next())
            .collect();
        return Some(Played::Sums(files));
    }

    let moving = line.starts_with("mv '");
    if !moving && !line.starts_with("cp -RPp '") {
        return None;
    }

    let pairs = line
        .split(" && ")
        .filter_map(|step| match quoted(step).as_slice() {
            [from, to] => Some((from.clone(), to.clone())),
            _ => None,
        })
        .collect();
    Some(Played::Copy { pairs, moving })
}

impl Played {
    fn run(&self, root: &std::path::Path) -> std::io::Result<String> {
        let local = |path: &str| root.join(path.trim_start_matches('/'));
        match self {
            Self::Copy { pairs, moving } => {
                for (from, to) in pairs {
                    if *moving {
                        std::fs::rename(local(from), local(to))?;
                    } else {
                        copy_tree(&local(from), &local(to))?;
                    }
                }

                Ok(String::new())
            }
            Self::Sums(files) => Ok(files
                .iter()
                .map(|file| match std::fs::read(local(file)) {
                    Ok(content) => {
                        use sha2::Digest as _;
                        let sum: String = sha2::Sha256::digest(&content)
                            .iter()
                            .map(|byte| format!("{byte:02x}"))
                            .collect();
                        format!("{sum}  {file}\n")
                    }
                    Err(_) => "-\n".to_owned(),
                })
                .collect()),
        }
    }
}

fn copy_tree(from: &std::path::Path, to: &std::path::Path) -> std::io::Result<()> {
    if !from.is_dir() {
        return std::fs::copy(from, to).map(|_| ());
    }

    std::fs::create_dir_all(to)?;
    for entry in std::fs::read_dir(from)? {
        let entry = entry?;
        copy_tree(&entry.path(), &to.join(entry.file_name()))?;
    }

    Ok(())
}

/// Die Zeile des Schlüssels aus dem Befehl, mit dem der Kern ihn hinterlegt: zwischen `grep -qxF -- '` und
/// `' "$f"`, in einfachen Anführungszeichen (`'\''` steht für eines darin).
fn installed_key(command: &str) -> Option<String> {
    let rest = command
        .strip_prefix("umask 077 && f=~/.ssh/authorized_keys && ")?
        .split_once("{ grep -qxF -- '")?
        .1;
    let end = rest.find("' \"$f\"")?;
    Some(rest[..end].replace(r"'\''", "'"))
}

impl Drop for Connection {
    fn drop(&mut self) {
        // Mit der Verbindung enden auch die Ports, an denen für sie gelauscht wurde.
        for (_, task) in self.forwards.drain() {
            task.abort();
        }
    }
}
