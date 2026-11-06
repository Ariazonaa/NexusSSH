//! Portweiterleitung über eine stehende SSH-Verbindung – die drei Arten von OpenSSH:
//!
//! - **lokal** (`ssh -L`): Ein Port auf diesem Rechner führt zu einem Dienst, den der Server erreicht.
//! - **dynamisch** (`ssh -D`): Ein Port auf diesem Rechner ist ein SOCKS-Proxy (4, 4a und 5, ohne Anmeldung, nur
//!   `CONNECT`), der über den Server hinausgeht.
//! - **entfernt** (`ssh -R`): Ein Port am Server führt zu einem Dienst, den dieser Rechner erreicht.
//!
//! Regeln, nicht lockern:
//!
//! - Gelauscht wird von Haus aus **nur für diesen Rechner** (127.0.0.1 und ::1). Für das ganze Netz nur mit
//!   `open` am Tunnel – jeder im Netz käme sonst an den Dienst dahinter, ohne sich anzumelden.
//! - Der Server darf nur Verbindungen zu Zielen öffnen, um die er **gebeten wurde** ([`Remotes`]): Einen Kanal
//!   für einen Port, den niemand weitergeleitet hat, lehnt die Verbindung ab. Wohin es auf dieser Seite geht,
//!   steht im Tunnel und nie in dem, was der Server nennt.
//! - Ein Tunnel gehört zu einer Verbindung ([`crate::session::Link`]) und endet mit ihr. Stehen zwei
//!   Verbindungen zum selben Profil, trägt die erste die Tunnel ([`Shared::claim_tunnel`]).
//! - Was ein Programm dem SOCKS-Proxy schickt, ist fremde Eingabe ([`socks`]): begrenzt, mit Frist, und ein Name
//!   geht nur als Name an den Server.

use std::collections::HashMap;
use std::net::{Ipv4Addr, Ipv6Addr, SocketAddr};
use std::sync::{Arc, Mutex, MutexGuard, Weak};
use std::time::Duration;

use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::task::JoinHandle;
use tokio::time::timeout;

use crate::core::Shared;
use crate::profiles::{Tunnel, TunnelKind};
use crate::protocol::Event;
use crate::session::Link;

/// So lange darf der Server brauchen, um einen Kanal zu öffnen oder einen Port zu belegen.
const OPEN_TIMEOUT: Duration = Duration::from_secs(15);
/// So lange hat ein Programm, dem SOCKS-Proxy zu sagen, wohin es will.
const HELLO_TIMEOUT: Duration = Duration::from_secs(10);
/// So lange darf ein Dienst auf dieser Seite brauchen, eine Verbindung anzunehmen (`remote`).
const CONNECT_TIMEOUT: Duration = Duration::from_secs(10);

/// So viele Verbindungen laufen zugleich durch einen Lauscher; weitere werden abgewiesen.
const MAX_CONNECTIONS: usize = 256;

/// Die Ports, um deren Weiterleitung der Server gebeten wurde: Port am Server → Ziel auf dieser Seite.
pub(crate) type Remotes = Arc<Mutex<HashMap<u32, (String, u16)>>>;

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// Die laufenden Tunnel einer Verbindung. Fällt die Verbindung, fallen sie mit.
pub(crate) struct Tunnels {
    shared: Weak<Shared>,
    profile: String,
    remotes: Remotes,
    running: Mutex<HashMap<String, Running>>,
}

struct Running {
    tasks: Vec<JoinHandle<()>>,
    /// Bei `remote`: mit welcher Adresse und welchem Port der Server gebeten wurde – zum Zurücknehmen.
    remote: Option<(&'static str, u32)>,
}

impl Tunnels {
    pub(crate) fn new(shared: &Arc<Shared>, profile: &str, remotes: Remotes) -> Self {
        Self {
            shared: Arc::downgrade(shared),
            profile: profile.to_owned(),
            remotes,
            running: Mutex::new(HashMap::new()),
        }
    }

    fn emit(&self, tunnel: &str, state: &'static str, message: Option<String>) {
        if let Some(shared) = self.shared.upgrade() {
            shared.emit(Event::TunnelState {
                profile: self.profile.clone(),
                tunnel: tunnel.to_owned(),
                state,
                message,
            });
        }
    }

    /// Startet einen Tunnel auf dieser Verbindung und meldet, ob er steht. Läuft er schon – hier oder auf einer
    /// anderen Verbindung zum selben Profil –, geschieht nichts.
    pub(crate) async fn start(&self, link: &Arc<Link>, tunnel: &Tunnel) {
        let Some(shared) = self.shared.upgrade() else {
            return;
        };
        if !shared.claim_tunnel(&self.profile, &tunnel.id) {
            return;
        }

        // Wird dieser Aufruf mittendrin fallen gelassen (der Kern endet), gibt die Marke den Tunnel wieder frei –
        // sonst gälte er für immer als laufend und ließe sich nie wieder starten (Hinweis von Codex).
        let mut claim = Claim {
            shared: &shared,
            profile: &self.profile,
            tunnel: &tunnel.id,
            kept: false,
        };
        let outcome = match tunnel.kind {
            TunnelKind::Local | TunnelKind::Dynamic => listen(link, tunnel).await,
            TunnelKind::Remote => self.ask_server(link, tunnel).await,
        };
        match outcome {
            Ok(running) => {
                lock(&self.running).insert(tunnel.id.clone(), running);
                claim.kept = true;
                self.emit(&tunnel.id, "listening", None);
            }
            Err(message) => {
                drop(claim);
                self.emit(&tunnel.id, "failed", Some(message));
            }
        }
    }

    /// Hält einen Tunnel an. Verbindungen, die schon durch ihn laufen, bleiben bestehen, bis sie selbst enden.
    pub(crate) async fn stop(&self, link: &Link, id: &str) {
        let Some(running) = lock(&self.running).remove(id) else {
            return;
        };
        for task in &running.tasks {
            task.abort();
        }

        if let Some((address, port)) = running.remote {
            lock(&self.remotes).remove(&port);
            let _ = timeout(OPEN_TIMEOUT, link.cancel_forward(address, port)).await;
        }

        if let Some(shared) = self.shared.upgrade() {
            shared.release_tunnel(&self.profile, id);
        }

        self.emit(id, "stopped", None);
    }

    /// Ob dieser Tunnel über diese Verbindung läuft.
    pub(crate) fn runs(&self, id: &str) -> bool {
        lock(&self.running).contains_key(id)
    }

    // Bittet den Server, an einem Port zu lauschen. Nur für ihn selbst (`localhost`), außer der Tunnel ist offen –
    // und auch dann entscheidet der Server (`GatewayPorts`), ob er für andere lauscht.
    async fn ask_server(&self, link: &Arc<Link>, tunnel: &Tunnel) -> Result<Running, String> {
        let address = if tunnel.open { "" } else { "localhost" };
        let port = u32::from(tunnel.listen_port);

        // Erst eintragen, dann bitten: Der Server kann die erste Verbindung schicken, bevor seine Antwort da ist.
        lock(&self.remotes).insert(port, (tunnel.target_host.clone(), tunnel.target_port));
        let asked = timeout(OPEN_TIMEOUT, link.forward_port(address, port)).await;
        match asked {
            Ok(Ok(_)) => Ok(Running {
                tasks: Vec::new(),
                remote: Some((address, port)),
            }),
            Ok(Err(error)) => {
                lock(&self.remotes).remove(&port);
                Err(format!(
                    "the server does not listen on port {port} ({error}) – the port may be taken, below 1024, or forwarding is switched off there"
                ))
            }
            Err(_) => {
                lock(&self.remotes).remove(&port);
                Err(format!(
                    "the server did not answer the request to listen on port {port}"
                ))
            }
        }
    }
}

/// Hält einen Tunnel belegt, solange sein Start läuft; steht er danach nicht, gibt das Fallen ihn frei.
struct Claim<'a> {
    shared: &'a Shared,
    profile: &'a str,
    tunnel: &'a str,
    kept: bool,
}

impl Drop for Claim<'_> {
    fn drop(&mut self) {
        if !self.kept {
            self.shared.release_tunnel(self.profile, self.tunnel);
        }
    }
}

impl Drop for Tunnels {
    fn drop(&mut self) {
        let running = std::mem::take(&mut *lock(&self.running));
        for (id, running) in running {
            for task in &running.tasks {
                task.abort();
            }

            if let Some(shared) = self.shared.upgrade() {
                shared.release_tunnel(&self.profile, &id);
            }

            self.emit(&id, "stopped", None);
        }
    }
}

// Lauscht auf diesem Rechner: für ihn selbst über IPv4 und, wo es geht, IPv6 – „localhost“ heißt in manchem
// Programm zuerst ::1. Scheitert IPv4, steht der Tunnel nicht.
async fn listen(link: &Arc<Link>, tunnel: &Tunnel) -> Result<Running, String> {
    let port = tunnel.listen_port;
    let (v4, v6) = if tunnel.open {
        (Ipv4Addr::UNSPECIFIED, Ipv6Addr::UNSPECIFIED)
    } else {
        (Ipv4Addr::LOCALHOST, Ipv6Addr::LOCALHOST)
    };
    let first = TcpListener::bind(SocketAddr::from((v4, port)))
        .await
        .map_err(|error| match error.kind() {
            std::io::ErrorKind::AddrInUse => format!("port {port} is already in use on this computer"),
            std::io::ErrorKind::PermissionDenied => format!("this computer does not allow listening on port {port}"),
            _ => format!("could not listen on port {port}: {error}"),
        })?;
    let mut listeners = vec![first];
    if let Ok(second) = TcpListener::bind(SocketAddr::from((v6, port))).await {
        listeners.push(second);
    }

    let tasks = listeners
        .into_iter()
        .map(|listener| tokio::spawn(accept(listener, Arc::downgrade(link), tunnel.clone())))
        .collect();
    Ok(Running { tasks, remote: None })
}

async fn accept(listener: TcpListener, link: Weak<Link>, tunnel: Tunnel) {
    // Eine Obergrenze je Lauscher: Ein Programm auf diesem Rechner (oder, bei einem offenen Tunnel, im Netz) soll
    // nicht beliebig viele Kanäle am Server öffnen können. Was darüber hinaus anklopft, wird gleich wieder getrennt.
    let room = Arc::new(tokio::sync::Semaphore::new(MAX_CONNECTIONS));
    loop {
        let (stream, peer) = match listener.accept().await {
            Ok(accepted) => accepted,
            // Ein einzelner gescheiterter Versuch (der andere hat schon wieder aufgelegt) beendet das Lauschen nicht.
            Err(_) => {
                tokio::time::sleep(Duration::from_millis(100)).await;
                continue;
            }
        };
        let Some(link) = link.upgrade() else {
            return;
        };
        let Ok(place) = Arc::clone(&room).try_acquire_owned() else {
            continue;
        };
        let tunnel = tunnel.clone();
        tokio::spawn(async move {
            serve(stream, peer, link, tunnel).await;
            drop(place);
        });
    }
}

// Eine Verbindung durch den Tunnel: Ziel bestimmen (fest oder per SOCKS erfragt), Kanal öffnen, durchreichen.
async fn serve(mut stream: TcpStream, peer: SocketAddr, link: Arc<Link>, tunnel: Tunnel) {
    let _ = stream.set_nodelay(true);
    let (host, port, asked) = if tunnel.kind == TunnelKind::Dynamic {
        match timeout(HELLO_TIMEOUT, socks::greet(&mut stream)).await {
            Ok(Ok(wanted)) => (wanted.host, wanted.port, Some(wanted.version)),
            _ => return,
        }
    } else {
        (tunnel.target_host, tunnel.target_port, None)
    };

    let opened = timeout(OPEN_TIMEOUT, link.open_direct(host, port, peer)).await;

    // Die Verbindung zum Server hält ab hier nur noch der Kanal – der Tunnel soll sie nicht am Leben halten.
    drop(link);
    let Ok(Ok(mut remote)) = opened else {
        if let Some(version) = asked {
            let _ = socks::answer(&mut stream, version, false).await;
        }

        return;
    };
    if let Some(version) = asked
        && socks::answer(&mut stream, version, true).await.is_err()
    {
        return;
    }

    let _ = tokio::io::copy_bidirectional(&mut stream, &mut remote).await;
}

/// Der Server hat eine Verbindung an einem weitergeleiteten Port angenommen: zum Ziel auf dieser Seite verbinden
/// und durchreichen. Klappt das nicht, endet der Kanal.
pub(crate) fn forward<S>(channel: S, host: String, port: u16)
where
    S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    tokio::spawn(async move {
        let mut channel = channel;
        if let Ok(Ok(mut local)) = timeout(CONNECT_TIMEOUT, TcpStream::connect((host.as_str(), port))).await {
            let _ = local.set_nodelay(true);
            let _ = tokio::io::copy_bidirectional(&mut channel, &mut local).await;
        }

        let _ = channel.shutdown().await;
    });
}

/// Das Ziel, zu dem der Server an diesem Port weiterleiten darf – `None`, wenn ihn niemand darum gebeten hat.
pub(crate) fn target(remotes: &Remotes, port: u32) -> Option<(String, u16)> {
    lock(remotes).get(&port).cloned()
}

/// Was ein Programm dem SOCKS-Proxy sagt. Fremde Eingabe: Die Leser hier sind reine Funktionen über Bytes und
/// haben Zufallstests ([`crate::fuzzing`]).
pub(crate) mod socks {
    use super::*;

    /// Länger wird keine Bitte: Die größte (SOCKS 4a mit Benutzer und Name) bleibt weit darunter.
    const MAX_REQUEST: usize = 600;

    #[derive(Clone, Copy, Debug, PartialEq, Eq)]
    pub(crate) enum Version {
        Four,
        Five,
    }

    /// Wohin ein Programm verbunden werden will.
    #[derive(Clone, Debug, PartialEq, Eq)]
    pub(crate) struct Wanted {
        pub(crate) host: String,
        pub(crate) port: u16,
        pub(crate) version: Version,
    }

    #[derive(Clone, Debug, PartialEq, Eq)]
    pub(crate) enum Step {
        /// Es fehlen noch Bytes.
        More,
        /// Das ist kein SOCKS, oder etwas, das dieser Proxy nicht tut (Anmeldung, `BIND`, UDP).
        Bad,
        /// SOCKS 5, erste Nachricht: die angebotenen Verfahren. `plain` – „ohne Anmeldung“ ist dabei.
        Methods { used: usize, plain: bool },
        /// Die Bitte um eine Verbindung.
        Connect(Wanted),
    }

    /// Die erste Nachricht: bei SOCKS 4 schon die ganze Bitte, bei SOCKS 5 die Verfahren.
    pub(crate) fn hello(bytes: &[u8]) -> Step {
        match bytes.first() {
            None => Step::More,
            Some(4) => four(bytes),
            Some(5) => {
                let Some(count) = bytes.get(1).map(|count| usize::from(*count)) else {
                    return Step::More;
                };
                match bytes.get(2..2 + count) {
                    Some(methods) => Step::Methods {
                        used: 2 + count,
                        plain: methods.contains(&0),
                    },
                    None => Step::More,
                }
            }
            Some(_) => Step::Bad,
        }
    }

    /// SOCKS 5, zweite Nachricht: Version, Befehl, reserviert, Art der Adresse, Adresse, Port.
    pub(crate) fn request(bytes: &[u8]) -> Step {
        let Some(head) = bytes.get(..4) else {
            return Step::More;
        };
        if head[0] != 5 || head[1] != 1 {
            return Step::Bad;
        }

        let (host, rest) = match head[3] {
            1 => match bytes.get(4..8) {
                Some(address) => (
                    Ipv4Addr::new(address[0], address[1], address[2], address[3]).to_string(),
                    8,
                ),
                None => return Step::More,
            },
            4 => match bytes.get(4..20) {
                Some(address) => {
                    let mut octets = [0u8; 16];
                    octets.copy_from_slice(address);
                    (Ipv6Addr::from(octets).to_string(), 20)
                }
                None => return Step::More,
            },
            3 => {
                let Some(length) = bytes.get(4).map(|length| usize::from(*length)) else {
                    return Step::More;
                };
                match bytes.get(5..5 + length) {
                    Some(name) => match host_name(name) {
                        Some(name) => (name, 5 + length),
                        None => return Step::Bad,
                    },
                    None => return Step::More,
                }
            }
            _ => return Step::Bad,
        };
        match bytes.get(rest..rest + 2) {
            Some(port) => connect(host, u16::from_be_bytes([port[0], port[1]]), Version::Five),
            None => Step::More,
        }
    }

    // SOCKS 4 und 4a: Version, Befehl, Port, Adresse, Benutzer bis zur Null – und bei 4a (Adresse 0.0.0.x) der Name.
    fn four(bytes: &[u8]) -> Step {
        let Some(head) = bytes.get(..8) else {
            return Step::More;
        };
        if head[1] != 1 {
            return Step::Bad;
        }

        let port = u16::from_be_bytes([head[2], head[3]]);
        // Der Benutzer endet mit einer Null, nach höchstens 255 Bytes – gleich, in welchen Stücken er ankommt.
        let Some(user_end) = bytes[8..].iter().take(256).position(|byte| *byte == 0).map(|at| 8 + at) else {
            return if bytes.len() > 8 + 255 { Step::Bad } else { Step::More };
        };
        if head[4..7] != [0, 0, 0] || head[7] == 0 {
            return connect(
                Ipv4Addr::new(head[4], head[5], head[6], head[7]).to_string(),
                port,
                Version::Four,
            );
        }

        let name = &bytes[user_end + 1..];
        match name.iter().position(|byte| *byte == 0) {
            Some(end) => match host_name(&name[..end]) {
                Some(name) => connect(name, port, Version::Four),
                None => Step::Bad,
            },
            None if name.len() > 255 => Step::Bad,
            None => Step::More,
        }
    }

    fn connect(host: String, port: u16, version: Version) -> Step {
        if port == 0 {
            Step::Bad
        } else {
            Step::Connect(Wanted { host, port, version })
        }
    }

    // Ein Name, wie er an den Server geht: druckbares ASCII ohne Leerraum, nicht leer, nicht länger als DNS erlaubt.
    fn host_name(bytes: &[u8]) -> Option<String> {
        let name = std::str::from_utf8(bytes).ok()?;
        (!name.is_empty() && name.len() <= 255 && name.bytes().all(|byte| byte.is_ascii_graphic()))
            .then(|| name.to_owned())
    }

    /// Liest, wohin ein Programm will, und stimmt bei SOCKS 5 dem Verfahren „ohne Anmeldung“ zu.
    pub(crate) async fn greet<S>(stream: &mut S) -> std::io::Result<Wanted>
    where
        S: AsyncRead + AsyncWrite + Unpin,
    {
        let bad = || std::io::Error::new(std::io::ErrorKind::InvalidData, "not a SOCKS request this proxy serves");
        let mut bytes = Vec::new();
        let used = loop {
            match hello(&bytes) {
                Step::Connect(wanted) => return Ok(wanted),
                Step::Methods { used, plain: true } => break used,
                Step::Methods { .. } => {
                    // Kein Verfahren, das dieser Proxy kennt.
                    let _ = stream.write_all(&[5, 0xFF]).await;
                    return Err(bad());
                }
                Step::Bad => return Err(bad()),
                Step::More => more(stream, &mut bytes).await?,
            }
        };
        stream.write_all(&[5, 0]).await?;
        bytes.drain(..used);
        loop {
            match request(&bytes) {
                Step::Connect(wanted) => return Ok(wanted),
                Step::More => more(stream, &mut bytes).await?,
                _ => {
                    // „Befehl nicht unterstützt“ – auch für alles andere, was hier nicht gelesen wird.
                    let _ = stream.write_all(&[5, 7, 0, 1, 0, 0, 0, 0, 0, 0]).await;
                    return Err(bad());
                }
            }
        }
    }

    async fn more<S: AsyncRead + Unpin>(stream: &mut S, bytes: &mut Vec<u8>) -> std::io::Result<()> {
        let mut buffer = [0u8; 256];
        let read = stream.read(&mut buffer).await?;
        if read == 0 || bytes.len() + read > MAX_REQUEST {
            return Err(std::io::Error::new(
                std::io::ErrorKind::UnexpectedEof,
                "the SOCKS request ended early or is too long",
            ));
        }

        bytes.extend_from_slice(&buffer[..read]);
        Ok(())
    }

    /// Sagt dem Programm, ob die Verbindung steht.
    pub(crate) async fn answer<S: AsyncWrite + Unpin>(
        stream: &mut S,
        version: Version,
        connected: bool,
    ) -> std::io::Result<()> {
        match (version, connected) {
            (Version::Five, true) => stream.write_all(&[5, 0, 0, 1, 0, 0, 0, 0, 0, 0]).await,
            // 5 = „Verbindung abgelehnt“: genauer weiß es der Kern nicht.
            (Version::Five, false) => stream.write_all(&[5, 5, 0, 1, 0, 0, 0, 0, 0, 0]).await,
            (Version::Four, true) => stream.write_all(&[0, 0x5A, 0, 0, 0, 0, 0, 0]).await,
            (Version::Four, false) => stream.write_all(&[0, 0x5B, 0, 0, 0, 0, 0, 0]).await,
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        fn wanted(host: &str, port: u16, version: Version) -> Step {
            Step::Connect(Wanted {
                host: host.to_owned(),
                port,
                version,
            })
        }

        #[test]
        fn socks_5_offers_its_methods_and_then_asks_for_a_name_or_an_address() {
            assert_eq!(hello(&[]), Step::More);
            assert_eq!(hello(&[5]), Step::More);
            assert_eq!(hello(&[5, 2, 0]), Step::More);
            assert_eq!(hello(&[5, 2, 0, 2]), Step::Methods { used: 4, plain: true });
            assert_eq!(hello(&[5, 1, 2, 9, 9]), Step::Methods { used: 3, plain: false });
            assert_eq!(hello(&[9, 1, 2]), Step::Bad);

            let mut name = vec![5, 1, 0, 3, 11];
            name.extend_from_slice(b"example.org");
            assert_eq!(request(&name), Step::More);
            name.extend_from_slice(&[0x01, 0xBB]);
            assert_eq!(request(&name), wanted("example.org", 443, Version::Five));

            assert_eq!(
                request(&[5, 1, 0, 1, 10, 0, 0, 7, 0x1F, 0x90]),
                wanted("10.0.0.7", 8080, Version::Five)
            );
            let mut six = vec![5, 1, 0, 4];
            six.extend_from_slice(&[0; 15]);
            six.extend_from_slice(&[1, 0, 22]);
            assert_eq!(request(&six), wanted("::1", 22, Version::Five));

            // Kein CONNECT (BIND, UDP), eine unbekannte Art von Adresse, Port 0, ein Name mit Leerraum.
            assert_eq!(request(&[5, 2, 0, 1, 10, 0, 0, 7, 0, 80]), Step::Bad);
            assert_eq!(request(&[5, 1, 0, 9, 10, 0, 0, 7, 0, 80]), Step::Bad);
            assert_eq!(request(&[5, 1, 0, 1, 10, 0, 0, 7, 0, 0]), Step::Bad);
            assert_eq!(request(&[5, 1, 0, 3, 3, b'a', b' ', b'b', 0, 80]), Step::Bad);
            assert_eq!(request(&[5, 1, 0, 3, 0, 0, 80]), Step::Bad);
        }

        #[test]
        fn socks_4_names_an_address_and_4a_a_host_name() {
            assert_eq!(hello(&[4, 1, 0, 80, 192, 0, 2, 1]), Step::More);
            assert_eq!(
                hello(&[4, 1, 0, 80, 192, 0, 2, 1, 0]),
                wanted("192.0.2.1", 80, Version::Four)
            );
            assert_eq!(
                hello(&[4, 1, 0, 80, 192, 0, 2, 1, b'm', b'e', 0]),
                wanted("192.0.2.1", 80, Version::Four)
            );

            let mut named = vec![4, 1, 0x01, 0xBB, 0, 0, 0, 1, b'u', 0];
            named.extend_from_slice(b"example.org");
            assert_eq!(hello(&named), Step::More);
            named.push(0);
            assert_eq!(hello(&named), wanted("example.org", 443, Version::Four));

            // BIND, und ein Benutzer ohne Ende.
            assert_eq!(hello(&[4, 2, 0, 80, 192, 0, 2, 1, 0]), Step::Bad);
            let mut endless = vec![4, 1, 0, 80, 192, 0, 2, 1];
            endless.extend_from_slice(&[b'x'; 300]);
            assert_eq!(hello(&endless), Step::Bad);
        }

        #[tokio::test]
        async fn a_program_is_greeted_and_told_how_it_went() {
            let (mut program, mut proxy) = tokio::io::duplex(1024);
            let asking = tokio::spawn(async move {
                let wanted = greet(&mut proxy).await;
                if wanted.is_ok() {
                    answer(&mut proxy, Version::Five, true).await.unwrap();
                }

                wanted
            });
            program.write_all(&[5, 1, 0]).await.unwrap();
            let mut chosen = [0u8; 2];
            program.read_exact(&mut chosen).await.unwrap();
            assert_eq!(chosen, [5, 0]);
            program
                .write_all(&[5, 1, 0, 3, 4, b'h', b'o', b's', b't', 0, 22])
                .await
                .unwrap();
            let mut reply = [0u8; 10];
            program.read_exact(&mut reply).await.unwrap();
            assert_eq!(reply[..2], [5, 0]);
            assert_eq!(asking.await.unwrap().unwrap().host, "host");

            // Wer eine Anmeldung verlangt, bekommt „kein Verfahren“; wer zu viel schickt, nichts.
            let (mut program, mut proxy) = tokio::io::duplex(4096);
            let asking = tokio::spawn(async move { greet(&mut proxy).await });
            program.write_all(&[5, 1, 2]).await.unwrap();
            program.read_exact(&mut chosen).await.unwrap();
            assert_eq!(chosen, [5, 0xFF]);
            assert!(asking.await.unwrap().is_err());

            let (mut program, mut proxy) = tokio::io::duplex(4096);
            let asking = tokio::spawn(async move { greet(&mut proxy).await });
            let mut flood = vec![4, 1, 0, 80, 0, 0, 0, 1];
            flood.extend_from_slice(&[b'x'; 200]);
            flood.push(0);
            flood.extend_from_slice(&[b'y'; 1000]);
            program.write_all(&flood).await.unwrap();
            assert!(asking.await.unwrap().is_err());
        }
    }
}
