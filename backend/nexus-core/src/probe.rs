//! Verbindungstest: Was lässt sich über den Weg zu einem Server sagen, ohne sich anzumelden?
//!
//! Der Reihe nach: den Namen auflösen, den Port öffnen (und messen, wie lange das dauert), lesen, als was sich der
//! Dienst vorstellt, den Schlüsselaustausch führen – das zeigt den Host-Schlüssel und ob er bekannt ist – und
//! fragen, welche Anmeldeverfahren der Server anbietet. Dafür genügt die Anfrage „keines“ (`none`): Sie meldet
//! niemanden an, der Server nennt nur, was er annimmt. Es wird nichts gespeichert, nichts gefragt und kein
//! Geheimnis benutzt; einem unbekannten Schlüssel wird hier nicht vertraut, er wird nur angesehen.
//!
//! Geprüft wird der Server des Profils selbst. Führt der Weg über einen Sprung-Server, wäre dafür eine Anmeldung
//! dort nötig – dann sagt der Test das und prüft nichts.

use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use russh::client::{self, AuthResult};
use russh::keys::PublicKeyOrCertificate;
use russh::{Disconnect, SshId};
use tokio::io::AsyncReadExt;
use tokio::net::TcpStream;
use tokio::time::timeout;

use crate::dial::{self, Failed, Trouble};
use crate::hostkeys::{HostKeys, Verdict, fingerprint};
use crate::profiles::Profile;

/// So lange wartet der Test darauf, dass sich der Dienst vorstellt.
const GREETING_TIMEOUT: Duration = Duration::from_secs(4);

/// Mehr als das liest der Test von der Vorstellung nicht.
const MAX_GREETING: usize = 1024;

/// Texte des Servers werden für die Anzeige gekürzt.
const MAX_TEXT: usize = 120;

/// Was der Test herausgefunden hat. Felder bleiben leer, wenn er nicht so weit kam; `step` nennt den letzten
/// Schritt, der gelang.
#[derive(Debug, Default)]
pub(crate) struct Report {
    /// `resolve`, `connect`, `greeting`, `key`, `methods` – der letzte Schritt, der gelang; leer, wenn keiner.
    pub(crate) step: &'static str,
    /// Die Adressen, die der Name ergibt (nach der Auswahl des Profils).
    pub(crate) addresses: Vec<String>,
    /// Die Adresse, die geantwortet hat.
    pub(crate) address: Option<String>,
    /// Wie lange das Öffnen des Ports dauerte, in Millisekunden.
    pub(crate) connect_ms: Option<u64>,
    /// Als was sich der Dienst vorstellt, etwa `SSH-2.0-OpenSSH_9.6`.
    pub(crate) software: Option<String>,
    pub(crate) key_algorithm: Option<String>,
    pub(crate) fingerprint: Option<String>,
    /// `trusted`, `certified` (ein Zertifikat einer bekannten Stelle), `unknown` oder `changed`.
    pub(crate) key_state: Option<&'static str>,
    /// Die Anmeldeverfahren, die der Server für diesen Benutzer anbietet.
    pub(crate) methods: Vec<String>,
    /// Woran es scheiterte, wenn es scheiterte (Kennung wie in `session.state`).
    pub(crate) trouble: Option<&'static str>,
    /// Die Einzelheit dazu, englisch.
    pub(crate) message: Option<String>,
}

impl Report {
    fn failed(mut self, trouble: &'static str, message: impl Into<String>) -> Self {
        self.trouble = Some(trouble);
        self.message = Some(message.into());
        self
    }

    fn stopped(self, failed: Failed) -> Self {
        self.failed(failed.trouble.name(), failed.message)
    }
}

/// Text des Servers für die Anzeige: ohne Steuerzeichen, gekürzt.
fn display_text(value: &str) -> String {
    value
        .trim()
        .chars()
        .filter(|character| !character.is_control())
        .take(MAX_TEXT)
        .collect()
}

/// Die Zeile, mit der sich ein SSH-Dienst vorstellt. Davor darf er anderes schicken (RFC 4253 §4.2); die Zeile mit
/// `SSH-` am Anfang ist es. Ohne eine solche Zeile ist die erste Zeile immerhin eine Auskunft (ein anderer Dienst).
pub(crate) fn greeting(bytes: &[u8]) -> Option<String> {
    let text = String::from_utf8_lossy(bytes);
    let lines: Vec<&str> = text.lines().map(str::trim).filter(|line| !line.is_empty()).collect();
    lines
        .iter()
        .find(|line| line.starts_with("SSH-"))
        .or(lines.first())
        .map(|line| display_text(line))
        .filter(|line| !line.is_empty())
}

async fn read_greeting(stream: &mut TcpStream) -> Option<String> {
    let mut buffer = Vec::with_capacity(256);
    let read = async {
        let mut chunk = [0_u8; 256];
        while buffer.len() < MAX_GREETING {
            match stream.read(&mut chunk).await {
                Ok(0) | Err(_) => break,
                Ok(count) => {
                    buffer.extend_from_slice(&chunk[..count]);
                    if buffer.windows(4).any(|window| window == b"SSH-") && buffer.ends_with(b"\n") {
                        break;
                    }
                }
            }
        }
    };
    let _ = timeout(GREETING_TIMEOUT, read).await;
    greeting(&buffer)
}

/// Sieht sich den Schlüssel des Servers an und lässt den Schlüsselaustausch weiterlaufen – ohne etwas zu merken.
/// Was der Server vorgelegt hat: sein Schlüssel und, wenn er eines zeigte, das Zertifikat dazu.
type Seen = (russh::keys::PublicKey, Option<russh::keys::Certificate>);

struct Looker {
    seen: Arc<Mutex<Option<Seen>>>,
}

impl client::Handler for Looker {
    type Error = russh::Error;

    async fn check_server_key(&mut self, server_key: &PublicKeyOrCertificate) -> Result<bool, Self::Error> {
        let certificate = match server_key {
            PublicKeyOrCertificate::Certificate(certificate) => Some(certificate.clone()),
            PublicKeyOrCertificate::PublicKey { .. } => None,
        };
        *self.seen.lock().unwrap_or_else(std::sync::PoisonError::into_inner) =
            Some((server_key.public_key().to_owned(), certificate));
        Ok(true)
    }
}

/// Führt den Test. Kommt immer mit einem Bericht zurück – auch wenn schon der erste Schritt scheitert.
pub(crate) async fn run(profile: &Profile, host_keys: &HostKeys) -> Report {
    let mut report = Report::default();
    if profile.is_serial() {
        return report.failed("other", "a serial port has no network path to test");
    }

    if profile.is_local() {
        return report.failed("other", "a local terminal has no network path to test");
    }

    if profile.jump.is_some() {
        return report.failed(
            "jump",
            "the way leads through a jump host; testing it would need a sign-in there",
        );
    }

    let patience = dial::patience(profile);
    let addresses: Vec<SocketAddr> = match timeout(patience, dial::resolve(profile)).await {
        Ok(Ok(addresses)) => addresses,
        Ok(Err(failed)) => return report.stopped(failed),
        Err(_) => return report.failed(Trouble::Name.name(), "resolving the name took too long"),
    };
    report.addresses = addresses.iter().map(|address| address.ip().to_string()).collect();
    report.step = "resolve";

    let started = Instant::now();
    let (mut stream, address) = match timeout(patience, dial::connect_any(&addresses)).await {
        Ok(Ok(connected)) => connected,
        Ok(Err(failed)) => return report.stopped(failed),
        Err(_) => return report.stopped(dial::timed_out()),
    };
    report.connect_ms = Some(u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX));
    report.address = Some(address.ip().to_string());
    report.step = "connect";

    // Ein Telnet-Gerät stellt sich nicht vor wie ein SSH-Dienst; was es als Erstes schickt, ist Aushandlung.
    // Auch bei FTP bleibt es beim offenen Port: Mehr zu fragen hieße, sich anzumelden.
    if profile.is_telnet() || profile.is_ftp() {
        return report;
    }

    report.software = read_greeting(&mut stream).await;
    drop(stream);
    match report.software.as_deref() {
        Some(line) if line.starts_with("SSH-") => report.step = "greeting",
        Some(_) => return report.failed("not_ssh", "the service on this port does not speak SSH"),
        None => return report.failed("not_ssh", "the service on this port did not introduce itself"),
    }

    // Für den Schlüsselaustausch eine zweite Verbindung: Die erste hat die Vorstellung schon gelesen.
    let stream = match timeout(patience, TcpStream::connect(address)).await {
        Ok(Ok(stream)) => stream,
        Ok(Err(error)) => return report.failed("other", error.to_string()),
        Err(_) => return report.stopped(dial::timed_out()),
    };
    // Wie beim Verbinden: um ein Zertifikat nur bitten, wenn es sich prüfen lässt (`HostKeys::authorities`).
    let mut preferred = crate::session::preferred(profile);
    if !host_keys.authorities(&profile.host, profile.port).is_empty() {
        preferred.host_key_certificates = preferred.key.clone();
    }

    let config = Arc::new(client::Config {
        client_id: SshId::Standard(concat!("SSH-2.0-NexusSSH_", env!("CARGO_PKG_VERSION")).into()),
        preferred,
        ..Default::default()
    });
    let seen = Arc::new(Mutex::new(None));
    let looker = Looker {
        seen: Arc::clone(&seen),
    };
    let mut handle = match timeout(patience, client::connect_stream(config, stream, looker)).await {
        Ok(Ok(handle)) => handle,
        Ok(Err(russh::Error::NoCommonAlgo { .. })) => {
            return report.failed("algorithms", "the server and NexusSSH share no algorithm");
        }
        Ok(Err(error)) => return report.failed("other", error.to_string()),
        Err(_) => return report.stopped(dial::timed_out()),
    };

    if let Some((key, certificate)) = seen.lock().unwrap_or_else(std::sync::PoisonError::into_inner).take() {
        report.key_algorithm = Some(key.algorithm().to_string());
        report.fingerprint = Some(fingerprint(&key));
        // Ein Zertifikat einer bekannten Stelle genügt – wie beim Verbinden; hält es nicht, gilt der Schlüssel darin.
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |since| since.as_secs());
        let certified = certificate.is_some_and(|certificate| {
            host_keys
                .check_certificate(&profile.host, profile.port, &certificate, now)
                .is_ok()
        });
        report.key_state = Some(match host_keys.check(&profile.host, profile.port, &key) {
            // Widerrufen schlägt alles andere, auch ein gültiges Zertifikat. Für die Anzeige ist ein widerrufener
            // Schlüssel ein geänderter: Vertraut wird ihm nicht.
            Verdict::Revoked => "changed",
            _ if certified => "certified",
            Verdict::Trusted => "trusted",
            Verdict::Unknown => "unknown",
            Verdict::Changed { .. } => "changed",
        });
        report.step = "key";
    }

    // „Keines“ meldet niemanden an; die Antwort nennt, was der Server für diesen Benutzer annimmt.
    let user = if profile.user.is_empty() {
        "nexusssh-probe"
    } else {
        profile.user.as_str()
    };
    match timeout(patience, handle.authenticate_none(user)).await {
        Ok(Ok(AuthResult::Failure { remaining_methods, .. })) => {
            report.methods = remaining_methods
                .iter()
                .map(|method| <&str>::from(method).to_owned())
                .collect();
            report.step = "methods";
        }
        // Ein Server, der jeden ohne Anmeldung hereinlässt: Das ist die Auskunft.
        Ok(Ok(AuthResult::Success)) => {
            report.methods = vec!["none".to_owned()];
            report.step = "methods";
        }
        Ok(Err(error)) => report = report.failed("other", error.to_string()),
        Err(_) => report = report.stopped(dial::timed_out()),
    }

    let _ = handle.disconnect(Disconnect::ByApplication, "", "").await;
    report
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_greeting_is_the_line_that_starts_with_ssh() {
        assert_eq!(
            greeting(b"SSH-2.0-OpenSSH_9.6p1 Debian-3\r\n").as_deref(),
            Some("SSH-2.0-OpenSSH_9.6p1 Debian-3")
        );
        assert_eq!(
            greeting(b"Welcome to the switch\r\nSSH-2.0-dropbear_2022.83\r\n").as_deref(),
            Some("SSH-2.0-dropbear_2022.83")
        );
    }

    #[test]
    fn another_service_is_named_by_its_first_line_and_silence_is_nothing() {
        assert_eq!(
            greeting(b"220 mail.example.org ESMTP\r\n").as_deref(),
            Some("220 mail.example.org ESMTP")
        );
        assert_eq!(greeting(b""), None);
        assert_eq!(greeting(b"\r\n\r\n"), None);
    }

    #[test]
    fn server_text_is_cleaned_and_cut() {
        let long = format!("SSH-2.0-{}\x1b[31m\r\n", "x".repeat(400));
        let line = greeting(long.as_bytes()).unwrap();
        assert_eq!(line.chars().count(), MAX_TEXT);
        assert!(!line.contains('\x1b'));
    }
}
