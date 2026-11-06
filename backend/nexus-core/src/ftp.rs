//! FTP und FTPS als Dateiziel (`Profile.protocol: ftp`): eine Sitzung ohne Terminal, nur mit Dateien.
//!
//! Wie beim Dateizugriff ohne SFTP ([`crate::shellfs`]) spricht der Kern dafür SFTP mit sich selbst: Ein kleiner
//! SFTP-Server in diesem Prozess übersetzt jede Anfrage in FTP-Befehle. Dateibrowser, Transfers, Betrachter und
//! Editor arbeiten darüber unverändert.
//!
//! Was dabei gilt:
//!
//! - **FTPS ist die Vorgabe** (`AUTH TLS` auf der gewöhnlichen Verbindung). Unverschlüsseltes FTP gibt es nur,
//!   wenn das Profil es ausdrücklich verlangt (`ftp_tls: none`) – dann warnt der Kern bei jedem Verbinden:
//!   Passwort und Dateien gehen im Klartext über die Leitung.
//! - **Zertifikate** prüft der Zertifikatspeicher des Systems. Hält eines dort nicht (selbst signiert, interne
//!   Stelle), entscheidet der Benutzer wie bei einem Host-Schlüssel anhand des Fingerabdrucks; seine Zustimmung
//!   gilt genau diesem Zertifikat für genau diesen Server (`tls_trust.json`). Zeigt der Server später ein
//!   anderes, wird wieder gefragt – mit dem Hinweis, dass es sich geändert hat. Nie ein Zertifikat ohne Prüfung
//!   oder Zustimmung annehmen.
//! - **Die Datenverbindung geht immer zum Server der Steuerverbindung**, gleich welche Adresse er in seiner
//!   Antwort nennt: Ein Server soll den Kern nicht zu einem dritten Rechner schicken können.
//! - **Pfade mit Zeilenumbruch werden abgelehnt** – in der Steuerverbindung wäre der Rest ein eigener Befehl.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, UNIX_EPOCH};

use russh_sftp::client::SftpSession;
use russh_sftp::protocol::{Attrs, Data, File, FileAttributes, Handle, Name, OpenFlags, Status, StatusCode, Version};
use sha2::{Digest, Sha256};
use suppaftp::tokio::{AsyncRustlsConnector, AsyncRustlsFtpStream, AsyncRustlsStream};
use suppaftp::types::FileType;
use suppaftp::{FtpError, list};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::mpsc::UnboundedReceiver;
use tokio::sync::watch;
use tokio_rustls::rustls;
use tokio_rustls::rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use tokio_rustls::rustls::pki_types::{CertificateDer, ServerName, UnixTime};

use crate::core::Shared;
use crate::profiles::Profile;
use crate::protocol::{CloseReason, Event, SecretField, SecretKind, SessionState};
use crate::session::Command;

type Ftp = AsyncRustlsFtpStream;
type Transfer = suppaftp::tokio::TransferStream<AsyncRustlsStream>;

/// So oft wird nach dem Passwort gefragt.
const MAX_PASSWORD_ATTEMPTS: u32 = 3;

/// So lange gilt eine Liste, wenn nach den Angaben einer einzelnen Datei gefragt wird – ein Transfer fragt je
/// Datei, und FTP kennt dafür nur „das ganze Verzeichnis“.
const LISTING_AGE: Duration = Duration::from_secs(3);

/// Puffer zwischen dem SFTP-Client des Kerns und diesem Server.
const PIPE: usize = 512 * 1024;

type Ended = Result<(), (CloseReason, String)>;

/// Die Fingerabdrücke der Zertifikate, denen der Benutzer zugestimmt hat – je Server (`host:port`) einer.
#[derive(Default)]
pub(crate) struct TlsTrust {
    file: Option<PathBuf>,
    known: Mutex<HashMap<String, String>>,
}

impl TlsTrust {
    pub(crate) fn load(file: PathBuf) -> Self {
        let known = std::fs::read_to_string(&file)
            .ok()
            .and_then(|text| serde_json::from_str(&text).ok())
            .unwrap_or_default();
        Self {
            file: Some(file),
            known: Mutex::new(known),
        }
    }

    fn get(&self, server: &str) -> Option<String> {
        self.known
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .get(server)
            .cloned()
    }

    fn trust(&self, server: &str, fingerprint: &str) -> std::io::Result<()> {
        let mut known = self.known.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        known.insert(server.to_owned(), fingerprint.to_owned());
        match &self.file {
            Some(file) => std::fs::write(file, serde_json::to_string_pretty(&*known).unwrap_or_default()),
            None => Ok(()),
        }
    }
}

/// `SHA256:` und der Fingerabdruck des Zertifikats in Base64 ohne Füllzeichen – wie bei Host-Schlüsseln.
fn fingerprint(certificate: &[u8]) -> String {
    let text = crate::sftp::to_base64(&Sha256::digest(certificate));
    format!("SHA256:{}", text.trim_end_matches('='))
}

/// Prüft das Zertifikat des Servers: erst der Speicher des Systems; hält es dort nicht, gilt nur das eine, dem der
/// Benutzer für diesen Server zugestimmt hat. Was gezeigt wurde, bleibt in `seen` – danach wird gefragt.
/// Hat der Rechner gar keinen Speicher (`system` fehlt – ein Linux ohne Zertifikate der Stellen), kennt er eben
/// keines: Dann entscheidet bei jedem Server der Benutzer, statt dass keine Verbindung zustande kommt.
#[derive(Debug)]
struct Verifier {
    system: Option<rustls_platform_verifier::Verifier>,
    provider: Arc<rustls::crypto::CryptoProvider>,
    pinned: Option<String>,
    seen: Arc<Mutex<Option<String>>>,
}

impl ServerCertVerifier for Verifier {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        intermediates: &[CertificateDer<'_>],
        server_name: &ServerName<'_>,
        ocsp_response: &[u8],
        now: UnixTime,
    ) -> Result<ServerCertVerified, rustls::Error> {
        let shown = fingerprint(end_entity.as_ref());
        *self.seen.lock().unwrap_or_else(std::sync::PoisonError::into_inner) = Some(shown.clone());
        let checked = match &self.system {
            Some(system) => system.verify_server_cert(end_entity, intermediates, server_name, ocsp_response, now),
            None => Err(rustls::Error::InvalidCertificate(
                rustls::CertificateError::UnknownIssuer,
            )),
        };
        match checked {
            Ok(verified) => Ok(verified),
            Err(_) if self.pinned.as_deref() == Some(shown.as_str()) => Ok(ServerCertVerified::assertion()),
            Err(error) => Err(error),
        }
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &rustls::DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        rustls::crypto::verify_tls12_signature(message, cert, dss, &self.provider.signature_verification_algorithms)
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &rustls::DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        rustls::crypto::verify_tls13_signature(message, cert, dss, &self.provider.signature_verification_algorithms)
    }

    fn supported_verify_schemes(&self) -> Vec<rustls::SignatureScheme> {
        self.provider.signature_verification_algorithms.supported_schemes()
    }
}

fn connector(pinned: Option<String>, seen: Arc<Mutex<Option<String>>>) -> Result<tokio_rustls::TlsConnector, String> {
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let system = rustls_platform_verifier::Verifier::new(Arc::clone(&provider)).ok();
    let verifier = Verifier {
        system,
        provider: Arc::clone(&provider),
        pinned,
        seen,
    };
    let config = rustls::ClientConfig::builder_with_provider(provider)
        .with_safe_default_protocol_versions()
        .map_err(|error| error.to_string())?
        .dangerous()
        .with_custom_certificate_verifier(Arc::new(verifier))
        .with_no_client_auth();
    Ok(tokio_rustls::TlsConnector::from(Arc::new(config)))
}

fn failed(reason: CloseReason) -> impl Fn(FtpError) -> (CloseReason, String) {
    move |error| (reason, error.to_string())
}

/// Was es braucht, um eine weitere Verbindung zum selben Server aufzubauen – ohne noch einmal zu fragen: das
/// Passwort, das eben galt (nur im Speicher, für die Dauer der Sitzung), und das Zertifikat, dem zugestimmt ist.
///
/// FTP erlaubt je Verbindung nur eine Übertragung zur selben Zeit, und eine abgebrochene lässt sich nicht bei
/// jedem Server sauber beenden. Deshalb bekommt jede laufende Übertragung ihre eigene Verbindung; die erste
/// bleibt für Listen und alles Übrige frei.
struct Dialer {
    profile: Profile,
    password: String,
    pinned: Option<String>,
}

/// So lange wartet der aktive Modus darauf, dass der Server die Datenverbindung aufbaut.
const ACTIVE_LIMIT: Duration = Duration::from_secs(6);

/// Was eine Liste meldet, die ihre Frist überschritten hat.
const LIST_LATE: &str = "listing took too long";

/// Was eine Liste im aktiven Modus meldet, wenn der Server die Datenverbindung nicht rechtzeitig aufgebaut hat.
const ACTIVE_SILENT: &str = "the server did not open the data connection in time";

/// So lange darf der Aufbau einer Verbindung mit implizitem TLS dauern.
const IMPLICIT_LIMIT: Duration = Duration::from_secs(20);

/// Eine Steuerverbindung bis vor die Anmeldung. Mit `secure` verschlüsselt: gewöhnlich im Klartext begonnen und
/// mit `AUTH TLS` gesichert, bei implizitem TLS vom ersten Byte an. Ohne `secure` bleibt sie unverschlüsselt –
/// das entscheidet der Aufrufer nach dem Profil, nie der Server.
async fn control(profile: &Profile, secure: Option<tokio_rustls::TlsConnector>) -> Result<Ftp, FtpError> {
    let dial = || async {
        crate::dial::tcp(profile)
            .await
            .map_err(|failed| FtpError::ConnectionError(std::io::Error::other(failed.message)))
    };
    // Über IPv6 kennt FTP nur die erweiterten Befehle für Datenverbindungen (RFC 2428): `PASV` nennt eine
    // IPv4-Adresse, und IIS lehnt es dort ab (gefunden 2026-10-15) – jede Liste scheiterte.
    let mut over_ipv6 = false;
    let mut ftp = match secure {
        Some(secure) if profile.ftp_implicit() => {
            // Die Bibliothek baut diese Verbindung selbst auf – an die Adressen, die auch der gewöhnliche Weg
            // nähme (Adressart aus dem Profil), der Reihe nach. Nicht erst auf dem gewöhnlichen Weg verbinden und
            // wieder schließen, um die Adresse zu erfahren: FileZilla Server lässt nach einer Verbindung, die
            // ohne Handschlag endet, jede weitere zehn Sekunden warten (gemessen 2026-10-14).
            let addresses = crate::dial::resolve(profile)
                .await
                .map_err(|failed| FtpError::ConnectionError(std::io::Error::other(failed.message)))?;
            let mut reached = None;
            let mut failed = FtpError::ConnectionError(std::io::Error::other("no address to connect to"));
            for address in addresses {
                let attempt = tokio::time::timeout(
                    IMPLICIT_LIMIT,
                    Ftp::connect_secure_implicit(address, AsyncRustlsConnector::from(secure.clone()), &profile.host),
                )
                .await;
                match attempt {
                    Ok(Ok(ftp)) => {
                        over_ipv6 = address.is_ipv6();
                        reached = Some(ftp);
                        break;
                    }
                    // Das Zertifikat passt nicht: Das läge an einer anderen Adresse nicht anders.
                    Ok(Err(error @ FtpError::SecureError(_))) => return Err(error),
                    Ok(Err(error)) => failed = error,
                    Err(_) => {
                        failed = FtpError::ConnectionError(std::io::Error::new(
                            std::io::ErrorKind::TimedOut,
                            "the server did not answer over TLS",
                        ));
                    }
                }
            }
            let Some(mut ftp) = reached else {
                return Err(failed);
            };

            // Auch die Datenverbindungen verschlüsselt – bei `AUTH TLS` verlangt das die Bibliothek selbst.
            ftp.custom_command("PBSZ 0", &[suppaftp::Status::CommandOk]).await?;
            ftp.custom_command("PROT P", &[suppaftp::Status::CommandOk]).await?;
            ftp
        }
        Some(secure) => {
            let stream = dial().await?;
            over_ipv6 = stream.peer_addr().is_ok_and(|peer| peer.is_ipv6());
            Ftp::connect_with_stream(stream)
                .await?
                .into_secure(AsyncRustlsConnector::from(secure), &profile.host)
                .await?
        }
        None => {
            let stream = dial().await?;
            over_ipv6 = stream.peer_addr().is_ok_and(|peer| peer.is_ipv6());
            Ftp::connect_with_stream(stream).await?
        }
    };
    // Vor allem anderen, auch vor der Anmeldung: Was ab hier über die Steuerverbindung geht, ist so kodiert.
    if let Some(names) = Names::of(profile) {
        ftp.set_encoding(names).await;
    }

    ftp.set_passive_nat_workaround(true);
    if over_ipv6 {
        // `EPSV` nennt nur den Port; die Bibliothek verbindet dann zum Server der Steuerverbindung – wie es der
        // Kern auch bei `PASV` hält.
        ftp.set_mode(suppaftp::Mode::ExtendedPassive);
    }
    if profile.ftp_active {
        // Der Server kommt zu uns: Für jede Datenverbindung lauscht die Bibliothek kurz auf einem freien Port,
        // nennt dem Server die Adresse, von der die Steuerverbindung ausgeht, und nimmt nur an, was vom Server
        // dieser Verbindung kommt. Gelauscht wird dabei auf allen Adressen des Rechners (so baut es die
        // Bibliothek, nachgesehen 2026-10-14) – Windows fragt deshalb beim ersten Mal, ob die Firewall die App
        // durchlassen soll. Die Wartezeit liegt unter der Frist, mit der eine Übertragung beginnen muss
        // (`BEGIN_LIMIT`). Über IPv6 trägt das nicht: Die Bibliothek nennt dem Server mit `EPRT` ihre IPv6-Adresse,
        // lauscht aber nur auf `0.0.0.0` – der Server kommt nie an (suppaftp #197; geprüft an IIS, 2026-10-15).
        ftp = ftp.active_mode(ACTIVE_LIMIT);
    }

    Ok(ftp)
}

/// Wie ein Server Dateinamen schreibt, der kein UTF-8 spricht (`ftp_encoding` im Profil). Ein Byte ist ein
/// Zeichen, in beiden Richtungen eindeutig: Was der Server nennt, lässt sich ihm genau so wieder nennen. Ein
/// Name mit einem Zeichen, das es in der Kodierung nicht gibt, wird nicht verschickt – die Bibliothek lässt den
/// Befehl scheitern, statt einen anderen Namen zu schicken.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Names {
    /// ISO-8859-1: Die ersten 256 Zeichen von Unicode, eins zu eins.
    Latin1,
    /// Windows-1252: wie ISO-8859-1, nur stehen an den Stellen 0x80–0x9F Zeichen wie „€“ und die typografischen
    /// Anführungszeichen. Fünf Stellen dort sind unbelegt; sie bleiben, was sie in ISO-8859-1 sind.
    Windows1252,
}

/// Die Stellen 0x80–0x9F von Windows-1252. 0 = unbelegt.
const WINDOWS_1252_HIGH: [u16; 32] = [
    0x20AC, 0, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0, 0x017D, 0, 0,
    0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0, 0x017E, 0x0178,
];

impl Names {
    pub(crate) fn of(profile: &Profile) -> Option<Self> {
        match profile.ftp_encoding.as_deref() {
            Some("latin1") => Some(Self::Latin1),
            Some("windows1252") => Some(Self::Windows1252),
            _ => None,
        }
    }

    fn character(self, byte: u8) -> char {
        let special = match (self, byte) {
            (Self::Windows1252, 0x80..=0x9F) => WINDOWS_1252_HIGH[usize::from(byte - 0x80)],
            _ => 0,
        };
        char::from_u32(u32::from(special))
            .filter(|_| special != 0)
            .unwrap_or(char::from(byte))
    }

    fn byte(self, character: char) -> Option<u8> {
        let code = u32::from(character);
        if self == Self::Windows1252 {
            if let Some(at) = WINDOWS_1252_HIGH
                .iter()
                .position(|special| *special != 0 && u32::from(*special) == code)
            {
                return u8::try_from(0x80 + at).ok();
            }

            // Eine belegte Stelle ist nicht zugleich das Steuerzeichen von ISO-8859-1: Sonst gäbe es zwei Bytes
            // für verschiedene Zeichen, die beim Zurücklesen dasselbe wären.
            if (0x80..=0x9F).contains(&code) && WINDOWS_1252_HIGH[(code - 0x80) as usize] != 0 {
                return None;
            }
        }

        u8::try_from(code).ok()
    }
}

impl suppaftp::Encoding for Names {
    fn decode(&self, bytes: &[u8]) -> String {
        bytes.iter().map(|byte| self.character(*byte)).collect()
    }

    fn encode(&self, text: &str) -> std::io::Result<Vec<u8>> {
        text.chars()
            .map(|character| {
                self.byte(character).ok_or_else(|| {
                    std::io::Error::new(
                        std::io::ErrorKind::InvalidInput,
                        "a character in this name does not exist in the file name encoding of this server",
                    )
                })
            })
            .collect()
    }
}

impl Dialer {
    async fn open(&self) -> Result<Ftp, FtpError> {
        let secure = if self.profile.ftp_plain() {
            None
        } else {
            Some(connector(self.pinned.clone(), Arc::new(Mutex::new(None))).map_err(FtpError::SecureError)?)
        };
        let mut ftp = control(&self.profile, secure).await?;
        ftp.login(self.profile.user.as_str(), self.password.as_str()).await?;
        ftp.transfer_type(FileType::Binary).await?;
        Ok(ftp)
    }
}

/// Verbindet, sichert die Verbindung (wenn das Profil es nicht ausdrücklich abwählt) und meldet an. Dazu, was
/// weitere Verbindungen brauchen.
async fn connect(
    shared: &Arc<Shared>,
    session: u64,
    profile: &Profile,
) -> Result<(Ftp, Dialer), (CloseReason, String)> {
    let server = format!("{}:{}", profile.host, profile.port);
    let mut pinned = None;
    let mut ftp = if profile.ftp_plain() {
        shared.emit(Event::Warning {
            code: "ftp_unencrypted",
            message: format!(
                "{}: plain FTP – the password and all files travel unencrypted",
                profile.name
            ),
        });
        control(profile, None)
            .await
            .map_err(failed(CloseReason::ConnectFailed))?
    } else {
        let seen = Arc::new(Mutex::new(None));
        pinned = shared.tls_trust.get(&server);
        let secure =
            connector(pinned.clone(), Arc::clone(&seen)).map_err(|error| (CloseReason::ConnectFailed, error))?;
        match control(profile, Some(secure)).await {
            Ok(ftp) => ftp,
            Err(error) => {
                // Kein Zertifikat gesehen: Der Server kann kein TLS, oder die Verbindung kam nicht zustande –
                // das ist kein Fall für eine Rückfrage.
                let shown = seen.lock().unwrap_or_else(std::sync::PoisonError::into_inner).take();
                let Some(shown) = shown else {
                    return Err(failed(CloseReason::ConnectFailed)(error));
                };

                // Das System kennt das Zertifikat nicht: Der Benutzer entscheidet, wie bei einem Host-Schlüssel.
                let changed = pinned.is_some();
                let (host, port, print) = (profile.host.clone(), profile.port, shown.clone());
                let answer = shared
                    .ask(session, |prompt| Event::HostKeyPrompt {
                        session,
                        prompt,
                        host,
                        port,
                        algorithm: "x509".to_owned(),
                        fingerprint: print,
                        changed,
                        known_hosts_file: None,
                        known_hosts_line: None,
                        previous_fingerprint: pinned.clone(),
                        known_hosts_foreign: None,
                    })
                    .await;
                if !answer.is_some_and(|answer| answer.accept) {
                    return Err((
                        CloseReason::HostKeyRejected,
                        "the certificate was not accepted".to_owned(),
                    ));
                }

                if let Err(error) = shared.tls_trust.trust(&server, &shown) {
                    shared.emit(Event::Warning {
                        code: "tls_trust_not_saved",
                        message: error.to_string(),
                    });
                }

                // Noch einmal von vorn – diesmal gilt genau dieses Zertifikat.
                pinned = Some(shown);
                let secure = connector(pinned.clone(), Arc::new(Mutex::new(None)))
                    .map_err(|error| (CloseReason::ConnectFailed, error))?;
                control(profile, Some(secure))
                    .await
                    .map_err(failed(CloseReason::ConnectFailed))?
            }
        }
    };

    shared.state(session, SessionState::Authenticating, None);
    let password = login(shared, session, profile, &mut ftp).await?;
    ftp.transfer_type(FileType::Binary)
        .await
        .map_err(failed(CloseReason::ConnectionLost))?;
    let dialer = Dialer {
        profile: profile.clone(),
        password,
        pinned,
    };
    Ok((ftp, dialer))
}

/// Meldet an und liefert das Passwort, das galt – für weitere Verbindungen derselben Sitzung.
async fn login(
    shared: &Arc<Shared>,
    session: u64,
    profile: &Profile,
    ftp: &mut Ftp,
) -> Result<String, (CloseReason, String)> {
    let refused = |error: &FtpError| !matches!(error, FtpError::ConnectionError(_));

    // „anonymous“ meldet sich ohne Rückfrage an – das Passwort ist dort nur eine Höflichkeit.
    if profile.user.eq_ignore_ascii_case("anonymous") || profile.user.eq_ignore_ascii_case("ftp") {
        let courtesy = "anonymous@";
        return ftp
            .login(profile.user.as_str(), courtesy)
            .await
            .map(|()| courtesy.to_owned())
            .map_err(failed(CloseReason::AuthFailed));
    }

    if let Some(saved) = crate::secrets::saved_password(&*shared.secrets, profile) {
        match ftp.login(profile.user.as_str(), saved.as_str()).await {
            Ok(()) => return Ok(saved),
            // Das gespeicherte Passwort stimmt nicht mehr: entfernen und fragen.
            Err(error) if refused(&error) => crate::secrets::forget_password(&*shared.secrets, &profile.id),
            Err(error) => return Err((CloseReason::ConnectionLost, error.to_string())),
        }
    }

    for attempt in 1..=MAX_PASSWORD_ATTEMPTS {
        let (user, host) = (profile.user.clone(), profile.host.clone());
        let answer = shared
            .ask(session, |prompt| Event::SecretPrompt {
                session,
                prompt,
                kind: SecretKind::Password,
                user,
                host,
                key_path: None,
                name: None,
                instructions: None,
                fields: vec![SecretField {
                    text: String::new(),
                    echo: false,
                }],
                can_save: true,
                attempt,
            })
            .await;
        let Some((password, save)) = answer.and_then(|answer| Some((answer.values?.into_iter().next()?, answer.save)))
        else {
            return Err((CloseReason::AuthFailed, "cancelled".to_owned()));
        };

        // Ein Zeilenumbruch im Passwort wäre in der Steuerverbindung ein zweiter Befehl.
        if password.contains(['\r', '\n']) {
            continue;
        }

        match ftp.login(profile.user.as_str(), password.as_str()).await {
            Ok(()) => {
                if save {
                    let label = format!("{}@{}", profile.user, profile.host);
                    if let Err(message) = crate::secrets::save_password(&*shared.secrets, profile, &label, &password) {
                        shared.emit(Event::Warning {
                            code: "secret_not_saved",
                            message,
                        });
                    }
                }

                return Ok(password);
            }
            Err(error) if refused(&error) => {}
            Err(error) => return Err((CloseReason::ConnectionLost, error.to_string())),
        }
    }

    Err((
        CloseReason::AuthFailed,
        "the server did not accept the password".to_owned(),
    ))
}

/// Eine FTP-Sitzung von Anfang bis Ende: verbinden, anmelden, die Dateien anbieten – und warten, bis die App sie
/// schließt oder die Verbindung verloren geht.
pub(crate) async fn drive(
    shared: &Arc<Shared>,
    session: u64,
    profile: &Profile,
    mut commands: UnboundedReceiver<Command>,
) -> Ended {
    shared.state(session, SessionState::Connecting, None);
    let (mut ftp, dialer) = connect(shared, session, profile).await?;
    let home = ftp.pwd().await.unwrap_or_else(|_| "/".to_owned());

    // Ob der Server die genaue Liste kennt, sagt er selbst (`FEAT`; `MLST` steht dort für beide Befehle). Es
    // auszuprobieren geht nicht überall gut: vsftpd nimmt nach einem abgelehnten Befehl die Datenverbindung nicht
    // an, und über TLS wartete die Bibliothek dann auf einen Handschlag, der nie kommt.
    let plain_list = ftp.feat().await.map_or(true, |features| {
        !features
            .keys()
            .any(|name| name.eq_ignore_ascii_case("MLSD") || name.eq_ignore_ascii_case("MLST"))
    });

    let (lost, mut is_lost) = watch::channel(false);
    let (client, server) = tokio::io::duplex(PIPE);
    tokio::spawn(russh_sftp::server::run(
        server,
        FtpFiles::new(ftp, dialer, home.clone(), lost, plain_list, {
            let (shared, name) = (Arc::clone(shared), profile.name.clone());
            Box::new(move |code, text| {
                shared.emit(Event::Warning {
                    code,
                    message: format!("{name}: {text}"),
                });
            })
        }),
    ));
    let (client, meter) = crate::metered::counted(client);
    let sftp = SftpSession::new(client)
        .await
        .map(Arc::new)
        .map_err(|error| (CloseReason::ConnectionLost, error.to_string()))?;
    crate::metered::attach(&sftp, &meter);
    meter.mark_few();
    shared.connected_files_only(session, &profile.id, sftp, home);

    // Ein Terminal gibt es nicht; was dafür ankommt, verfällt. Die Sitzung endet, wenn die App sie schließt
    // (der Empfänger fällt) oder der Dateizugriff meldet, dass die Verbindung weg ist.
    loop {
        tokio::select! {
            command = commands.recv() => match command {
                // Über FTP lässt sich nichts messen: Das sagt die Sitzung, statt zu schweigen.
                Some(Command::Monitor(true)) => shared.emit(Event::MonitorUnavailable {
                    session,
                    message: "FTP has no way to measure the server".to_owned(),
                    retrying: false,
                }),
                Some(_) => {}
                None => return Ok(()),
            },
            changed = is_lost.changed() => if changed.is_err() || *is_lost.borrow() {
                return Err((CloseReason::ConnectionLost, "the connection to the FTP server was lost".to_owned()));
            },
        }
    }
}

/// Eine laufende Übertragung: ihre eigene Verbindung, der Datenstrom darauf und wo er in der Datei steht.
struct Active {
    ftp: Ftp,
    stream: Transfer,
    position: u64,
    writing: bool,
}

/// So viele Einträge eines Verzeichnisses gehen in eine Antwort.
const READDIR_CHUNK: usize = 200;

enum Open {
    Directory(Option<Vec<File>>),
    File {
        path: String,
        /// Zum Schreiben geöffnet und noch nichts geschrieben: Die Datei muss trotzdem entstehen bzw. leer werden.
        truncate: bool,
        active: Option<Box<Active>>,
        /// Beim Lesen: Hier endete die Datei. Die Gegenseite fragt im Voraus nach den nächsten Stücken, auch
        /// weit hinter dem Ende – jede dieser Fragen bräuchte sonst eine eigene Verbindung, nur um „nichts“ zu hören.
        end: Option<u64>,
    },
}

/// Wie eine Übertragung beginnt ([`FtpFiles::start_at`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Start {
    /// Am Anfang der Datei.
    Whole,
    /// An einer Stelle, die der Server sich merkt (`REST`).
    Restart,
    /// Am Anfang gelesen, bis zur Stelle verworfen.
    Skip,
    /// Ans Ende der Datei geschrieben (`APPE`).
    Append,
}

/// So viele Bytes vom Anfang einer Datei genügen, um zu sehen, ob der Server eine Stelle hält.
const PROBE: usize = 64;

/// So lange darf es dauern, bis eine Übertragung beginnt (Befehl, Datenverbindung, Handschlag).
const BEGIN_LIMIT: Duration = Duration::from_secs(8);

/// Ein Hinweis an den Benutzer; der Text des Servers steht darin als Einzelheit.
type Notice = Box<dyn Fn(&'static str, String) + Send + Sync>;

/// So oft höchstens steht derselbe Hinweis da.
const NOTICE_EVERY: Duration = Duration::from_secs(60);

/// So viele Verbindungen bleiben nach einer Übertragung für die nächste liegen.
const IDLE_CONNECTIONS: usize = 3;

/// Ein Eintrag aus der Liste des Servers: Name, Angaben und – bei einem Link, dessen Ziel die Zeile nennt
/// (`name -> ziel` bei `LIST`) – wohin er zeigt.
type Listed = (String, FileAttributes, Option<String>);

struct FtpFiles {
    /// Die erste Verbindung: Listen, Anlegen, Umbenennen, Löschen – nie eine Übertragung.
    ftp: Ftp,
    dialer: Dialer,
    /// Verbindungen, deren Übertragung ordentlich zu Ende ging – für die nächste.
    idle: Vec<Ftp>,
    home: String,
    lost: watch::Sender<bool>,
    handles: HashMap<String, Open>,
    next_handle: u64,
    /// Der Server kennt `MLSD` nicht.
    plain_list: bool,
    /// Ob die gewöhnliche Liste mit `-a` auch versteckte Dateien nennt; `None` = noch nicht bekannt.
    dash_a: Option<bool>,
    /// Ob der Server eine Stelle (`REST`) bis zur Übertragung behält; `None` = noch nicht bekannt.
    restarts: Option<bool>,
    /// Sagt dem Benutzer etwas, das sonst in „Failure“ unterginge.
    notice: Notice,
    noticed: Option<Instant>,
    /// Wann zuletzt der Hinweis zum aktiven Modus kam ([`FtpFiles::unreachable`]).
    noticed_active: Option<Instant>,
    /// Ob der Hinweis schon kam, dass der Server keine Stelle hält ([`FtpFiles::start_at`]) – einmal je Sitzung.
    noticed_restart: bool,
    /// Die zuletzt gelesenen Verzeichnisse – für Fragen nach einer einzelnen Datei und dem Ziel eines Links.
    listings: HashMap<String, (Instant, Vec<Listed>)>,
}

fn ok(id: u32) -> Status {
    Status {
        id,
        status_code: StatusCode::Ok,
        error_message: "Ok".to_owned(),
        language_tag: "en-US".to_owned(),
    }
}

/// Ein Pfad, der sich in einen FTP-Befehl setzen lässt: ohne Zeilenumbruch.
fn safe(path: &str) -> Result<&str, StatusCode> {
    if path.is_empty() || path.contains(['\r', '\n', '\0']) {
        Err(StatusCode::BadMessage)
    } else {
        Ok(path)
    }
}

fn parent_and_name(path: &str) -> (String, String) {
    let trimmed = path.trim_end_matches('/');
    match trimmed.rsplit_once('/') {
        Some(("", name)) => ("/".to_owned(), name.to_owned()),
        Some((parent, name)) => (parent.to_owned(), name.to_owned()),
        None => (".".to_owned(), trimmed.to_owned()),
    }
}

/// Was ein Eintrag der Liste des Servers über die Datei sagt, als Angaben, wie SFTP sie nennt.
pub(crate) fn attributes_of(file: &list::File) -> FileAttributes {
    use suppaftp::list::PosixPexQuery::{Group, Others, Owner};

    let kind = if file.is_directory() {
        0o040_000
    } else if file.is_symlink() {
        0o120_000
    } else {
        0o100_000
    };
    let mut bits = 0;
    for (who, shift) in [(Owner, 6), (Group, 3), (Others, 0)] {
        bits |= (u32::from(file.can_read(who)) << 2
            | u32::from(file.can_write(who)) << 1
            | u32::from(file.can_execute(who)))
            << shift;
    }

    // Ein Server, der keine Rechte nennt (MLSD ohne `unix.mode`, DOS-Liste): das Übliche.
    if bits == 0 {
        bits = if file.is_directory() { 0o755 } else { 0o644 };
    }

    let modified = file
        .modified()
        .duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|since| u32::try_from(since.as_secs()).ok());
    FileAttributes {
        size: Some(file.size() as u64),
        uid: file.uid(),
        user: None,
        gid: file.gid(),
        group: None,
        permissions: Some(kind | bits),
        atime: modified,
        mtime: modified,
    }
}

/// Ruft einen Leser der FTP-Bibliothek und macht aus einem Panic darin „nicht lesbar“. Die Zeilen nennt der
/// Server, und der Leser der Bibliothek stolpert über manche verbogene (gefunden von den Zufallstests: `unix.mode`
/// mit Zeichen aus mehreren Bytes) – eine solche Zeile soll fehlen, nicht den Dateibrowser beenden.
/// Gemeldet am 2026-10-10: <https://github.com/veeso/suppaftp/issues/186>. Der Schutz bleibt auch danach – er gilt
/// jedem Panic in diesem Leser, nicht nur dem einen.
fn guarded<T>(read: impl FnOnce() -> Option<T>) -> Option<T> {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(read))
        .ok()
        .flatten()
}

/// Eine Zeile von `MLSD`, so hergerichtet, dass die Bibliothek sie liest, und daneben der Name: Die Angaben
/// stehen vor dem ersten Leerzeichen, danach kommt der Name – mit allem, was darin steht (die Bibliothek nähme
/// von `a;b` nur `b`). Zeiten dürfen Bruchteile einer Sekunde nennen (`modify=20261013212530.123`, RFC 3659 –
/// so schreibt FileZilla Server); die Bibliothek liest nur ganze Sekunden und verwürfe die ganze Zeile, die
/// Liste bliebe leer (gefunden 2026-10-13).
fn mlsd_facts(line: &str) -> Option<(String, &str)> {
    let (facts, name) = line.split_once(' ')?;
    let mut read = String::with_capacity(facts.len() + 2);
    for fact in facts.split(';').filter(|fact| !fact.is_empty()) {
        let whole = match fact.split_once('=') {
            Some((key, value)) if key.eq_ignore_ascii_case("modify") || key.eq_ignore_ascii_case("create") => {
                &fact[..key.len() + 1 + value.find('.').unwrap_or(value.len())]
            }
            _ => fact,
        };
        read.push_str(whole);
        read.push(';');
    }
    read.push_str(" -");

    Some((read, name))
}

/// Liest die Zeilen einer Liste – `MLSD` oder `LIST` in der Schreibweise von Unix oder DOS; was sich nicht lesen
/// lässt, und `.`/`..`, bleibt aus. Das Ziel eines Links nennt nur `LIST` (`name -> ziel`); `MLSD` kennt es nicht.
pub(crate) fn parse_listing(lines: &[String], machine: bool) -> Vec<Listed> {
    lines
        .iter()
        .filter_map(|line| {
            // Eine Liste in der Schreibweise von DOS nennt keine Rechte – dort gilt das Übliche.
            let mut own_name = None;
            let (file, has_rights) = if machine {
                let facts = match mlsd_facts(line) {
                    Some((facts, name)) => {
                        own_name = Some(name);
                        facts
                    }
                    None => line.clone(),
                };
                (guarded(|| list::ListParser::parse_mlsd(&facts).ok())?, true)
            } else {
                match guarded(|| list::ListParser::parse_posix(line).ok()) {
                    Some(file) => (file, true),
                    None => (guarded(|| list::ListParser::parse_dos(line).ok())?, false),
                }
            };
            let name = own_name.map_or_else(|| file.name().to_owned(), str::to_owned);
            let mut attributes = attributes_of(&file);
            if !has_rights {
                attributes.permissions = Some(if file.is_directory() { 0o040_755 } else { 0o100_644 });
            }

            // Die Bibliothek legt bei einem Link ohne Ziel (`MLSD`) einen leeren Pfad an – das ist keines.
            let target = file
                .symlink()
                .and_then(|path| path.to_str())
                .filter(|target| !target.is_empty())
                .map(str::to_owned);

            (!name.is_empty() && name != "." && name != ".." && !name.contains('/'))
                .then_some((name, attributes, target))
        })
        .collect()
}

/// Der Eintrag `name` in einer Liste: seine Angaben und, wenn er ein Link ist, dessen Ziel.
fn find_entry(entries: &[Listed], name: &str) -> Result<(FileAttributes, Option<String>), StatusCode> {
    entries
        .iter()
        .find(|(entry, ..)| entry == name)
        .map(|(_, attributes, target)| (attributes.clone(), target.clone()))
        .ok_or(StatusCode::NoSuchFile)
}

impl FtpFiles {
    fn new(
        ftp: Ftp,
        dialer: Dialer,
        home: String,
        lost: watch::Sender<bool>,
        plain_list: bool,
        notice: Notice,
    ) -> Self {
        Self {
            ftp,
            dialer,
            idle: Vec::new(),
            home,
            lost,
            handles: HashMap::new(),
            next_handle: 0,
            plain_list,
            dash_a: None,
            restarts: None,
            notice,
            noticed: None,
            noticed_active: None,
            noticed_restart: false,
            listings: HashMap::new(),
        }
    }

    fn remember(&mut self, open: Open) -> String {
        self.next_handle += 1;
        let handle = self.next_handle.to_string();
        self.handles.insert(handle.clone(), open);
        handle
    }

    /// Was der Server geantwortet hat, als Grund für SFTP.
    fn reason(error: &FtpError) -> StatusCode {
        match error {
            FtpError::ConnectionError(_) => StatusCode::ConnectionLost,
            FtpError::UnexpectedResponse(response) => {
                let text = String::from_utf8_lossy(&response.body).to_lowercase();
                if text.contains("permission") || text.contains("denied") || text.contains("not allowed") {
                    StatusCode::PermissionDenied
                } else if text.contains("no such") || text.contains("not found") || text.contains("exist") {
                    StatusCode::NoSuchFile
                } else {
                    StatusCode::Failure
                }
            }
            _ => StatusCode::Failure,
        }
    }

    /// Ein Fehler auf der ersten Verbindung. Ein Fehler der Verbindung selbst heißt noch nicht, dass der Server
    /// weg ist: Viele trennen nach ein paar Minuten ohne Befehl, und auch eine Datenverbindung, die der Server
    /// nicht mehr annahm (weil er den Befehl schon abgelehnt hat), meldet sich so – danach ist zudem unklar,
    /// welche Antwort noch aussteht. Eine neue Verbindung klärt beides; erst wenn auch die scheitert, ist die
    /// Sitzung zu Ende.
    async fn main_failed(&mut self, error: &FtpError) -> StatusCode {
        if !matches!(error, FtpError::ConnectionError(_)) {
            return Self::reason(error);
        }

        match self.dialer.open().await {
            Ok(ftp) => {
                self.ftp = ftp;
                StatusCode::Failure
            }
            Err(_) => {
                let _ = self.lost.send(true);
                StatusCode::ConnectionLost
            }
        }
    }

    /// Beginnt eine Übertragung ab `offset` – auf einer Verbindung, die bereitliegt, sonst auf einer neuen. Eine,
    /// die bereitlag, kann der Server inzwischen getrennt haben, oder er nimmt auf ihr keine Datenverbindung mehr
    /// an (TLS-Sitzung verbraucht): Dann fällt sie weg, und es geht mit der nächsten weiter.
    async fn begin(&mut self, path: &str, offset: u64, writing: bool) -> Result<Active, StatusCode> {
        let start = usize::try_from(offset).map_err(|_| StatusCode::Failure)?;
        let how = if offset == 0 {
            Start::Whole
        } else {
            self.start_at(path, offset, writing).await?
        };
        loop {
            let (mut ftp, kept) = match self.idle.pop() {
                Some(ftp) => (ftp, true),
                None => match self.dialer.open().await {
                    Ok(ftp) => (ftp, false),
                    Err(error) => {
                        self.refused(&error);
                        return Err(Self::reason(&error));
                    }
                },
            };
            // Mit einer Frist: Lehnt der Server den Befehl ab, nimmt mancher (vsftpd) die Datenverbindung gar
            // nicht erst an, und über TLS wartet die Bibliothek dann endlos auf den Handschlag. Die Frist liegt
            // unter der, nach der die Gegenseite hier die Anfrage aufgibt – sonst stünde solange alles.
            let begun = tokio::time::timeout(BEGIN_LIMIT, async {
                if how == Start::Restart {
                    // Der Server setzt an der Stelle an (`REST`) und liest oder schreibt von dort.
                    ftp.resume_transfer(start).await?;
                }

                match (writing, how) {
                    (true, Start::Append) => ftp.append_with_stream(path).await,
                    (true, _) => ftp.put_with_stream(path).await,
                    (false, _) => ftp.retr_as_stream(path).await,
                }
            })
            .await;
            match begun {
                // Die Verbindung fällt weg: Auf ihr steht noch eine Antwort aus, die niemand mehr liest.
                Err(_) if kept => {}
                Err(_) => {
                    self.unreachable(&FtpError::ConnectionError(std::io::Error::new(
                        std::io::ErrorKind::TimedOut,
                        "the transfer did not begin in time",
                    )));
                    return Err(StatusCode::Failure);
                }
                Ok(Ok(mut stream)) => {
                    if how == Start::Skip {
                        // Vom Anfang gelesen: Was vor der Stelle liegt, fällt weg. Endet die Datei vorher,
                        // steht der Strom am Ende – wie nach einem `REST` hinter das Ende.
                        let mut ahead = offset;
                        let mut dropped = vec![0u8; 64 * 1024];
                        while ahead > 0 {
                            let most = usize::try_from(ahead).map_or(dropped.len(), |ahead| ahead.min(dropped.len()));
                            match stream.read(&mut dropped[..most]).await {
                                Ok(count) if count > 0 => ahead -= count as u64,
                                _ => break,
                            }
                        }
                    }

                    return Ok(Active {
                        ftp,
                        stream,
                        position: offset,
                        writing,
                    });
                }
                Ok(Err(error)) if kept && Self::worn(&error) && !self.unanswered(&error) => {}
                Ok(Err(error)) => {
                    self.unreachable(&error);
                    return Err(Self::reason(&error));
                }
            }
        }
    }

    /// Wie eine Übertragung an einer Stelle hinter dem Anfang beginnt. `REST` gilt nach RFC 3659 nur für den
    /// Befehl gleich danach; die Bibliothek schickt dazwischen aber `PASV` (oder `PORT`). Die meisten Server
    /// (vsftpd, ProFTPD, Pure-FTPd) behalten die Stelle trotzdem – FileZilla Server vergisst sie, und ohne Meldung:
    /// Ein Lesen ab der Mitte lieferte den Anfang der Datei, ein fortgesetzter Upload hätte die Datei durch ihr
    /// Ende ersetzt (gefunden 2026-10-13 an FileZilla Server 1.12.6). Deshalb wird einmal je Sitzung nachgesehen,
    /// ob der Server die Stelle hält ([`FtpFiles::probe`]). Hält er sie nicht (oder ist es noch nicht bekannt):
    /// Lesen beginnt am Anfang und überspringt; Schreiben hängt an (`APPE`), wenn die Datei genau an der Stelle
    /// endet – sonst scheitert es, statt etwas Falsches zu schreiben.
    async fn start_at(&mut self, path: &str, offset: u64, writing: bool) -> Result<Start, StatusCode> {
        if self.restarts.is_none() {
            self.restarts = self.probe(path).await;
        }

        if self.restarts == Some(true) {
            return Ok(Start::Restart);
        }

        // Steht fest, dass der Server die Stelle nicht hält, erfährt der Benutzer es einmal je Sitzung – sonst
        // dauert ein fortgesetzter Download still so lange wie ein neuer, und ein Upload scheitert mit „Failure“.
        if self.restarts == Some(false) && !self.noticed_restart {
            self.noticed_restart = true;
            (self.notice)(
                "ftp_no_restart",
                "the server does not keep the position to continue from (REST)".to_owned(),
            );
        }

        if !writing {
            return Ok(Start::Skip);
        }

        match self.ftp.size(path).await {
            Ok(size) if size as u64 == offset => Ok(Start::Append),
            _ => Err(StatusCode::Failure),
        }
    }

    /// Ob der Server eine Stelle (`REST`) über den Befehl hinweg behält, den die Bibliothek danach schickt:
    /// der Anfang von `path` einmal ganz und einmal ab dem zweiten Byte. `None`, wenn es sich daran nicht
    /// entscheiden lässt (Datei zu kurz oder überall dasselbe Byte, Server nicht erreicht) – dann gilt für
    /// dieses Mal der sichere Weg, und die nächste Datei entscheidet.
    async fn probe(&mut self, path: &str) -> Option<bool> {
        let whole = self.sample(path, 0, PROBE + 1).await?;
        if whole.len() < 2 {
            return None;
        }

        let part = self.sample(path, 1, PROBE).await?;
        let length = part.len().min(whole.len() - 1);
        if length == 0 {
            return None;
        }

        match (part[..length] == whole[1..=length], part[..length] == whole[..length]) {
            (true, false) => Some(true),
            (false, true) => Some(false),
            _ => None,
        }
    }

    /// Die ersten Bytes von `path` ab `rest`, höchstens `most`. Die Verbindung dafür fällt danach weg – die
    /// Übertragung wird nicht zu Ende gelesen.
    async fn sample(&mut self, path: &str, rest: usize, most: usize) -> Option<Vec<u8>> {
        let mut ftp = match self.idle.pop() {
            Some(ftp) => ftp,
            None => self.dialer.open().await.ok()?,
        };
        let read = tokio::time::timeout(BEGIN_LIMIT, async {
            if rest > 0 {
                ftp.resume_transfer(rest).await?;
            }

            let mut stream = ftp.retr_as_stream(path).await?;
            let mut data = vec![0u8; most];
            let mut filled = 0;
            while filled < data.len() {
                match stream.read(&mut data[filled..]).await {
                    Ok(count) if count > 0 => filled += count,
                    _ => break,
                }
            }

            data.truncate(filled);
            Ok::<_, FtpError>(data)
        })
        .await;
        Self::leave(ftp).await;
        read.ok()?.ok()
    }

    /// Verabschiedet eine Verbindung, die nicht mehr gebraucht wird (`QUIT`), statt sie fallen zu lassen: vsftpd
    /// nahm nach einer einfach abgerissenen Verbindung auf den anderen derselben Adresse die nächste
    /// Datenverbindung nicht an (gesehen an 3.0.5 mit TLS).
    async fn leave(mut ftp: Ftp) {
        let _ = tokio::time::timeout(Duration::from_secs(2), ftp.quit()).await;
    }

    /// Der Server hat eine weitere Verbindung abgelehnt (meist: zu viele je Benutzer oder Adresse). Das erfährt
    /// der Benutzer mit dem Text des Servers – die Antwort an die Gegenseite hier kennt nur „gescheitert“.
    fn refused(&mut self, error: &FtpError) {
        let FtpError::UnexpectedResponse(response) = error else {
            return;
        };
        if self.noticed.is_some_and(|last| last.elapsed() < NOTICE_EVERY) {
            return;
        }

        self.noticed = Some(Instant::now());
        let said: String = String::from_utf8_lossy(&response.body)
            .chars()
            .filter(|character| !character.is_control())
            .take(200)
            .collect();
        (self.notice)(
            "ftp_refused",
            format!("the server refused another connection – {}", said.trim()),
        );
    }

    /// Im aktiven Modus kam die Datenverbindung nicht zustande: Der Server hat die genannte Adresse abgelehnt
    /// oder diesen Rechner nicht erreicht (VPN, Router, Firewall). Auch das ginge sonst in „Failure“ unter –
    /// dabei hilft dem Benutzer genau ein Schalter im Profil. Andere Fehler (Datei fehlt, kein Recht) gehören
    /// nicht dazu.
    fn unreachable(&mut self, error: &FtpError) {
        if !self.dialer.profile.ftp_active {
            return;
        }

        let said: String = match error {
            FtpError::UnexpectedResponse(response)
                if matches!(response.status.code(), 425 | 500 | 501 | 502 | 504 | 522) =>
            {
                String::from_utf8_lossy(&response.body).into_owned()
            }
            FtpError::ConnectionError(error) => error.to_string(),
            _ => return,
        };
        if self.noticed_active.is_some_and(|last| last.elapsed() < NOTICE_EVERY) {
            return;
        }

        self.noticed_active = Some(Instant::now());
        let said: String = said
            .chars()
            .filter(|character| !character.is_control())
            .take(200)
            .collect();
        (self.notice)(
            "ftp_active",
            format!("no data connection from the server in active mode – {}", said.trim()),
        );
    }

    /// Im aktiven Modus kam die Datenverbindung nicht binnen [`ACTIVE_LIMIT`]: Die Bibliothek meldet das als
    /// `TimedOut`. Eine eingeschlafene Steuerverbindung endet anders (abgebrochen, geschlossen), und die eigene
    /// Frist der Liste ([`LIST_LATE`]) ist etwas anderes – dort kam die Verbindung, nur dauerte sie.
    fn unanswered(&self, error: &FtpError) -> bool {
        self.dialer.profile.ftp_active
            && matches!(error, FtpError::ConnectionError(error)
                if error.kind() == std::io::ErrorKind::TimedOut && error.to_string() != LIST_LATE)
    }

    /// Ein Eintrag ist weg: Er fällt aus der gemerkten Liste seines Verzeichnisses, statt dass alle Listen
    /// verfallen – beim Löschen eines Ordners läse der Kern sonst nach jeder Datei das Verzeichnis neu.
    fn gone(&mut self, path: &str) {
        let path = path.trim_end_matches('/');
        let (parent, name) = parent_and_name(path);
        if let Some((_, entries)) = self.listings.get_mut(&parent) {
            entries.retain(|(entry, ..)| *entry != name);
        }

        self.listings.remove(path);
    }

    /// Ein Fehler, der an der Verbindung liegen kann statt an der Datei.
    fn worn(error: &FtpError) -> bool {
        use suppaftp::Status;
        match error {
            FtpError::ConnectionError(_) | FtpError::SecureError(_) | FtpError::BadResponse => true,
            FtpError::UnexpectedResponse(response) => matches!(
                response.status,
                Status::NotAvailable | Status::CannotOpenDataConnection | Status::TransferAborted
            ),
            _ => false,
        }
    }

    /// Schließt eine Übertragung ordentlich ab; ihre Verbindung bleibt für die nächste liegen. Eine, die nicht zu
    /// Ende gelesen ist, fällt samt ihrer Verbindung einfach weg – das versteht jeder Server.
    async fn finish(&mut self, active: Active, complete: bool) -> Result<(), StatusCode> {
        let Active {
            ftp,
            mut stream,
            writing,
            ..
        } = active;
        if !writing && !complete {
            return Ok(());
        }

        if writing {
            let _ = stream.shutdown().await;

            // Lesen, bis auch der Server seine Seite schließt. Über TLS 1.3 schickt er nach dem Handschlag noch
            // etwas (Scheine für die Wiederaufnahme), das beim Schreiben niemand liest; wird die Verbindung mit
            // Ungelesenem geschlossen, bricht Windows sie ab, statt sie zu beenden – der Server verliert dann das
            // Ende der Datei und meldet den Upload als gescheitert (vsftpd: „426 Failure reading network stream“).
            let _ = tokio::time::timeout(Duration::from_secs(5), async {
                let mut rest = [0u8; 256];
                while matches!(stream.read(&mut rest).await, Ok(count) if count > 0) {}
            })
            .await;
        }

        stream.finish().await.map_err(|error| Self::reason(&error))?;

        // Über TLS bleibt eine Verbindung nach einem Upload nicht liegen. Server wie vsftpd und ProFTPD nehmen
        // eine Datenverbindung nur an, wenn sie die TLS-Sitzung der Steuerverbindung wieder aufnimmt; die Scheine
        // dafür gibt der Server beim Aufbau mit und danach nur, wenn von ihm gelesen wird – ein Upload verbraucht
        // also einen und bringt keinen. Wie viele es sind, weiß niemand (meist zwei), und fehlt einer, scheitert
        // die nächste Übertragung mitten im Schreiben (gesehen an ProFTPD 1.3.8).
        let spent = writing && !self.dialer.profile.ftp_plain();
        if !spent && self.idle.len() < IDLE_CONNECTIONS {
            self.idle.push(ftp);
        } else {
            Self::leave(ftp).await;
        }

        Ok(())
    }

    fn take_active(&mut self, handle: &str) -> Option<Active> {
        match self.handles.get_mut(handle) {
            Some(Open::File { active, .. }) => active.take().map(|boxed| *boxed),
            _ => None,
        }
    }

    fn put_active(&mut self, handle: &str, value: Active) {
        if let Some(Open::File { active, .. }) = self.handles.get_mut(handle) {
            *active = Some(Box::new(value));
        }
    }

    async fn listing(&mut self, directory: &str) -> Result<Vec<Listed>, StatusCode> {
        safe(directory)?;

        let entries = match self.list_once(directory).await {
            Ok(entries) => entries,
            // Der Server kam im aktiven Modus nicht durch (Firewall vor diesem Rechner): Ein zweiter Versuch
            // dauerte noch einmal so lange und überschritte die Frist der Anfrage – der Hinweis käme nie an.
            Err(error) if self.unanswered(&error) => {
                self.unreachable(&error);
                let _ = self.main_failed(&error).await;
                return Err(StatusCode::Failure);
            }
            Err(error @ FtpError::ConnectionError(_)) => {
                // Die Verbindung war weg (oft: zu lange nichts gefragt). Steht eine neue, noch ein Versuch –
                // scheitert auch der an der Datenverbindung, hat der Server das Verzeichnis abgelehnt.
                if self.main_failed(&error).await == StatusCode::ConnectionLost {
                    return Err(StatusCode::ConnectionLost);
                }

                match self.list_once(directory).await {
                    Ok(entries) => entries,
                    Err(error @ FtpError::ConnectionError(_)) => {
                        self.unreachable(&error);
                        return Err(match self.main_failed(&error).await {
                            StatusCode::ConnectionLost => StatusCode::ConnectionLost,
                            _ => StatusCode::NoSuchFile,
                        });
                    }
                    Err(error) => {
                        self.unreachable(&error);
                        return Err(Self::reason(&error));
                    }
                }
            }
            Err(error) => {
                self.unreachable(&error);
                return Err(Self::reason(&error));
            }
        };
        self.listings
            .insert(directory.to_owned(), (Instant::now(), entries.clone()));
        Ok(entries)
    }

    /// Erst die genaue Liste (`MLSD`); ein Server, der den Befehl nicht kennt, bekommt von da an die gewöhnliche.
    /// Lehnt er dagegen das Verzeichnis ab, bleibt es bei dieser Antwort.
    async fn list_once(&mut self, directory: &str) -> Result<Vec<Listed>, FtpError> {
        // Mit einer Frist für die ganze Liste: Die Bibliothek sammelt alle Zeilen, bis der Server die
        // Datenverbindung schließt. Im aktiven Modus kürzer: Echte Server (vsftpd, ProFTPD) antworten auf den
        // Befehl erst, wenn ihre Datenverbindung steht – verwirft eine Firewall vor diesem Rechner sie, wartet die
        // Bibliothek auf diese Antwort, bis der Server aufgibt (Minuten), und die Gegenseite hätte die Anfrage nach
        // zehn Sekunden längst aufgegeben. Nach [`BEGIN_LIMIT`] gilt das als „Server kam nicht durch“.
        let active = self.dialer.profile.ftp_active;
        let limit = if active { BEGIN_LIMIT } else { crate::sftp::LIST_LIMIT };
        let late = || {
            let what = if active { ACTIVE_SILENT } else { LIST_LATE };
            FtpError::ConnectionError(std::io::Error::new(std::io::ErrorKind::TimedOut, what))
        };
        if !self.plain_list {
            match tokio::time::timeout(limit, self.ftp.mlsd(Some(directory)))
                .await
                .unwrap_or_else(|_| Err(late()))
            {
                Ok(lines) => return Ok(parse_listing(&lines, true)),
                Err(FtpError::UnexpectedResponse(response)) if (500..=504).contains(&response.status.code()) => {
                    self.plain_list = true;
                }
                Err(error) => return Err(error),
            }
        }

        // Die gewöhnliche Liste lässt bei vielen Servern aus, was mit einem Punkt beginnt (vsftpd); mit `-a`
        // nennen sie es. Nicht jeder Server kennt das: Lehnt er es ab oder kommt nichts, wo die Liste ohne etwas
        // nennt, bleibt es von da an bei der ohne.
        if self.dash_a != Some(false) {
            let all = format!("-a {directory}");
            match tokio::time::timeout(limit, self.ftp.list(Some(&all)))
                .await
                .unwrap_or_else(|_| Err(late()))
            {
                Ok(lines) => {
                    let entries = parse_listing(&lines, false);
                    if !entries.is_empty() || self.dash_a == Some(true) {
                        self.dash_a = Some(true);
                        return Ok(entries);
                    }
                }
                Err(FtpError::UnexpectedResponse(_)) if self.dash_a.is_none() => {}
                Err(error) => {
                    // Auch ein Fehler der Verbindung kann die Ablehnung sein: Mancher Server schließt die
                    // Datenverbindung, bevor sie steht. Der nächste Versuch (nach dem Neuverbinden) fragt ohne.
                    // Kam der Server im aktiven Modus gar nicht durch, sagt das nichts über `-a`.
                    if self.dash_a.is_none() && !self.unanswered(&error) {
                        self.dash_a = Some(false);
                    }

                    return Err(error);
                }
            }
        }

        let lines = tokio::time::timeout(limit, self.ftp.list(Some(directory)))
            .await
            .unwrap_or_else(|_| Err(late()))?;
        let entries = parse_listing(&lines, false);
        if self.dash_a.is_none() && !entries.is_empty() {
            self.dash_a = Some(false);
        }

        Ok(entries)
    }

    async fn attributes(&mut self, path: &str) -> Result<FileAttributes, StatusCode> {
        let path = path.trim_end_matches('/');
        if path.is_empty() || path == self.home.trim_end_matches('/') {
            return Ok(FileAttributes {
                size: Some(0),
                uid: None,
                user: None,
                gid: None,
                group: None,
                permissions: Some(0o040_755),
                atime: None,
                mtime: None,
            });
        }

        Ok(self.listed(path).await?.0)
    }

    /// Der Eintrag zu einem Pfad, aus der Liste seines Verzeichnisses: seine Angaben und, bei einem Link, wohin er
    /// zeigt. Die gemerkte Liste gilt, solange sie jünger ist als [`LISTING_AGE`]; sonst wird neu gelesen.
    async fn listed(&mut self, path: &str) -> Result<(FileAttributes, Option<String>), StatusCode> {
        let (parent, name) = parent_and_name(path);
        if let Some((read, entries)) = self.listings.get(&parent)
            && read.elapsed() < LISTING_AGE
        {
            return find_entry(entries, &name);
        }

        find_entry(&self.listing(&parent).await?, &name)
    }

    fn path_of(&self, handle: &str) -> Result<String, StatusCode> {
        match self.handles.get(handle) {
            Some(Open::File { path, .. }) => Ok(path.clone()),
            _ => Err(StatusCode::Failure),
        }
    }

    fn changed(&mut self) {
        self.listings.clear();
    }
}

impl russh_sftp::server::Handler for FtpFiles {
    type Error = StatusCode;

    fn unimplemented(&self) -> Self::Error {
        StatusCode::OpUnsupported
    }

    async fn init(&mut self, _version: u32, _extensions: HashMap<String, String>) -> Result<Version, Self::Error> {
        Ok(Version::new())
    }

    async fn realpath(&mut self, id: u32, path: String) -> Result<Name, Self::Error> {
        let resolved = if path.is_empty() || path == "." {
            self.home.clone()
        } else {
            let mut parts: Vec<&str> = Vec::new();
            for part in path.split('/') {
                match part {
                    "" | "." => {}
                    ".." => {
                        parts.pop();
                    }
                    part => parts.push(part),
                }
            }

            format!("/{}", parts.join("/"))
        };
        Ok(Name {
            id,
            files: vec![File::dummy(resolved)],
        })
    }

    async fn opendir(&mut self, id: u32, path: String) -> Result<Handle, Self::Error> {
        let files = self
            .listing(&path)
            .await?
            .into_iter()
            .map(|(name, attributes, _)| File::new(name, attributes))
            .collect();
        Ok(Handle {
            id,
            handle: self.remember(Open::Directory(Some(files))),
        })
    }

    async fn readdir(&mut self, id: u32, handle: String) -> Result<Name, Self::Error> {
        match self.handles.get_mut(&handle) {
            // In Stücken, wie ein echter Server: Ein ganzes Verzeichnis in einem einzigen Paket sprengt bei sehr
            // vielen Einträgen die Größe, die die Gegenseite für ein Paket annimmt.
            Some(Open::Directory(files)) => match files.take() {
                // Vom Ende her: Das kostet je Stück nur das Stück, nicht den ganzen Rest. Die Reihenfolge einer
                // Verzeichnisliste bedeutet nichts – wer sie braucht, sortiert.
                Some(mut left) if left.len() > READDIR_CHUNK => {
                    let chunk = left.split_off(left.len() - READDIR_CHUNK);
                    *files = Some(left);
                    Ok(Name { id, files: chunk })
                }
                Some(last) => Ok(Name { id, files: last }),
                None => Err(StatusCode::Eof),
            },
            _ => Err(StatusCode::Failure),
        }
    }

    async fn open(
        &mut self,
        id: u32,
        filename: String,
        flags: OpenFlags,
        _attributes: FileAttributes,
    ) -> Result<Handle, Self::Error> {
        safe(&filename)?;
        let writing = flags.contains(OpenFlags::WRITE);
        if !writing {
            // Lesen: Es muss die Datei geben, und sie darf kein Verzeichnis sein.
            let attributes = self.attributes(&filename).await?;
            if attributes.permissions.map(|mode| mode & 0o170_000) == Some(0o040_000) {
                return Err(StatusCode::Failure);
            }
        }

        Ok(Handle {
            id,
            handle: self.remember(Open::File {
                path: filename,
                truncate: writing && flags.contains(OpenFlags::TRUNCATE),
                active: None,
                end: None,
            }),
        })
    }

    async fn read(&mut self, id: u32, handle: String, offset: u64, length: u32) -> Result<Data, Self::Error> {
        let path = self.path_of(&handle)?;
        if let Some(Open::File { end: Some(end), .. }) = self.handles.get(&handle)
            && offset >= *end
        {
            return Err(StatusCode::Eof);
        }

        let mut active = match self.take_active(&handle) {
            Some(active) if !active.writing && active.position == offset => active,
            other => {
                // An anderer Stelle weiter: Die alte Übertragung fällt weg, eine neue beginnt bei `offset`.
                drop(other);
                self.begin(&path, offset, false).await?
            }
        };

        let mut data = vec![0u8; length as usize];
        let mut filled = 0;
        let mut ended = false;
        while filled < data.len() {
            match active.stream.read(&mut data[filled..]).await {
                Ok(0) => {
                    ended = true;
                    break;
                }
                Ok(count) => filled += count,
                Err(_) => return Err(StatusCode::ConnectionLost),
            }
        }

        active.position += filled as u64;
        if ended {
            // Zu Ende gelesen: ordentlich abschließen (der Server bestätigt die Übertragung).
            if let Some(Open::File { end, .. }) = self.handles.get_mut(&handle) {
                *end = Some(active.position);
            }

            self.finish(active, true).await?;
        } else {
            self.put_active(&handle, active);
        }

        if filled == 0 {
            return Err(StatusCode::Eof);
        }

        data.truncate(filled);
        Ok(Data { id, data })
    }

    async fn write(&mut self, id: u32, handle: String, offset: u64, data: Vec<u8>) -> Result<Status, Self::Error> {
        let path = self.path_of(&handle)?;
        let mut active = match self.take_active(&handle) {
            Some(active) if active.writing && active.position == offset => active,
            other => {
                // Was bisher geschrieben wurde, abschließen – dann an der neuen Stelle ansetzen.
                if let Some(previous) = other {
                    self.finish(previous, true).await?;
                }

                self.begin(&path, offset, true).await?
            }
        };

        if active.stream.write_all(&data).await.is_err() {
            return Err(StatusCode::ConnectionLost);
        }

        active.position += data.len() as u64;
        self.put_active(&handle, active);
        if let Some(Open::File { truncate, end, .. }) = self.handles.get_mut(&handle) {
            *truncate = false;
            *end = None;
        }

        self.changed();
        Ok(ok(id))
    }

    async fn close(&mut self, id: u32, handle: String) -> Result<Status, Self::Error> {
        match self.handles.remove(&handle) {
            Some(Open::File {
                active: Some(active), ..
            }) => {
                // Ein Download, der nicht zu Ende gelesen wurde, fällt weg; alles Geschriebene wird abgeschlossen.
                self.finish(*active, false).await?;
                self.changed();
            }
            // Zum Überschreiben geöffnet und nichts geschrieben: Die Datei soll trotzdem leer dastehen.
            Some(Open::File {
                path,
                truncate: true,
                active: None,
                ..
            }) => {
                let empty = self.begin(&path, 0, true).await?;
                self.finish(empty, true).await?;
                self.changed();
            }
            _ => {}
        }

        Ok(ok(id))
    }

    async fn stat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        Ok(Attrs {
            id,
            attrs: self.attributes(&path).await?,
        })
    }

    async fn lstat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        Ok(Attrs {
            id,
            attrs: self.attributes(&path).await?,
        })
    }

    async fn fstat(&mut self, id: u32, handle: String) -> Result<Attrs, Self::Error> {
        let path = self.path_of(&handle)?;
        Ok(Attrs {
            id,
            attrs: self.attributes(&path).await?,
        })
    }

    /// Wohin ein Link zeigt, weiß nur die Liste des Verzeichnisses: `LIST` nennt es hinter dem Namen. Wo sie es nicht
    /// nennt (`MLSD`, oder es ist kein Link), kann FTP es nicht sagen.
    async fn readlink(&mut self, id: u32, path: String) -> Result<Name, Self::Error> {
        // Die Wurzel hat keinen Namen in einer Liste: Danach zu fragen kostete nur ein `LIST` des Arbeitsordners.
        let path = path.trim_end_matches('/');
        if path.is_empty() {
            return Err(StatusCode::NoSuchFile);
        }

        let (_, target) = self.listed(path).await?;
        Ok(Name {
            id,
            files: vec![File::dummy(target.ok_or(StatusCode::OpUnsupported)?)],
        })
    }

    async fn setstat(&mut self, id: u32, path: String, attributes: FileAttributes) -> Result<Status, Self::Error> {
        // Rechte gehen bei vielen Servern über `SITE CHMOD`; Zeiten und Besitzer kennt FTP nicht – die bleiben.
        if let Some(mode) = attributes.permissions {
            safe(&path)?;
            if let Err(error) = self.ftp.site(format!("CHMOD {:o} {path}", mode & 0o7777)).await {
                return Err(self.main_failed(&error).await);
            }

            self.changed();
        }

        Ok(ok(id))
    }

    async fn fsetstat(&mut self, id: u32, _handle: String, _attributes: FileAttributes) -> Result<Status, Self::Error> {
        Ok(ok(id))
    }

    async fn mkdir(&mut self, id: u32, path: String, _attributes: FileAttributes) -> Result<Status, Self::Error> {
        safe(&path)?;
        if let Err(error) = self.ftp.mkdir(&path).await {
            return Err(self.main_failed(&error).await);
        }

        self.changed();
        Ok(ok(id))
    }

    async fn rmdir(&mut self, id: u32, path: String) -> Result<Status, Self::Error> {
        safe(&path)?;
        if let Err(error) = self.ftp.rmdir(&path).await {
            return Err(self.main_failed(&error).await);
        }

        self.gone(&path);
        Ok(ok(id))
    }

    async fn remove(&mut self, id: u32, filename: String) -> Result<Status, Self::Error> {
        safe(&filename)?;
        if let Err(error) = self.ftp.rm(&filename).await {
            return Err(self.main_failed(&error).await);
        }

        self.gone(&filename);
        Ok(ok(id))
    }

    async fn rename(&mut self, id: u32, old_path: String, new_path: String) -> Result<Status, Self::Error> {
        safe(&old_path)?;
        safe(&new_path)?;

        // Wie SFTP: Liegt am Ziel schon etwas, scheitert es – FTP überschriebe.
        if self.attributes(&new_path).await.is_ok() {
            return Err(StatusCode::Failure);
        }

        if let Err(error) = self.ftp.rename(&old_path, &new_path).await {
            return Err(self.main_failed(&error).await);
        }

        self.changed();
        Ok(ok(id))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_byte_of_a_file_name_encoding_comes_back_as_the_same_byte() {
        use suppaftp::Encoding;

        let all: Vec<u8> = (0..=255).collect();
        for names in [Names::Latin1, Names::Windows1252] {
            let text = names.decode(&all);
            assert_eq!(text.chars().count(), 256, "{names:?}");
            assert_eq!(names.encode(&text).unwrap(), all, "{names:?}");
        }

        assert_eq!(Names::Latin1.decode(b"Gr\xF6\xDFe.txt"), "Größe.txt");
        assert_eq!(Names::Latin1.encode("Größe.txt").unwrap(), b"Gr\xF6\xDFe.txt");
        assert_eq!(Names::Windows1252.decode(b"\x80 \x93neu\x94 \x85"), "€ “neu” …");
        assert_eq!(
            Names::Windows1252.encode("€ “neu” …").unwrap(),
            b"\x80 \x93neu\x94 \x85"
        );

        // Was es in der Kodierung nicht gibt, wird nicht zu etwas anderem.
        assert!(Names::Latin1.encode("€uro").is_err());
        assert!(Names::Latin1.encode("日本").is_err());
        assert!(Names::Windows1252.encode("日本").is_err());
        // In Windows-1252 ist 0x80 das Euro-Zeichen – das Steuerzeichen U+0080 hat dort keinen Platz.
        assert!(Names::Windows1252.encode("\u{80}").is_err());
        assert_eq!(Names::Windows1252.encode("\u{81}").unwrap(), [0x81]);
    }

    #[test]
    fn listings_of_every_kind_become_names_and_attributes() {
        let machine = parse_listing(
            &[
                "type=cdir;modify=20261009101500; .".to_owned(),
                "type=dir;modify=20261009101500;unix.mode=0755; www".to_owned(),
                "type=file;size=1834;modify=20261009101500;unix.mode=0640; nginx conf.txt".to_owned(),
            ],
            true,
        );
        assert_eq!(
            machine.iter().map(|(name, ..)| name.as_str()).collect::<Vec<_>>(),
            ["www", "nginx conf.txt"]
        );
        assert_eq!(machine[0].1.permissions.map(|mode| mode & 0o170_000), Some(0o040_000));
        assert_eq!(machine[1].1.size, Some(1834));
        assert_eq!(machine[1].1.mtime, Some(1_791_540_900));

        // FileZilla Server nennt Bruchteile einer Sekunde und Rechte auf seine Art; ein Name darf ein Semikolon
        // und mehrere Leerzeichen haben.
        let fractions = parse_listing(
            &[
                "type=dir;modify=20261009101500.250;perms=cple; pub".to_owned(),
                "type=file;size=11;modify=20261009101500.123;perms=awr; note.txt".to_owned(),
                "type=file;size=3;modify=20261009101500; a;b  c.txt".to_owned(),
            ],
            true,
        );
        assert_eq!(
            fractions.iter().map(|(name, ..)| name.as_str()).collect::<Vec<_>>(),
            ["pub", "note.txt", "a;b  c.txt"]
        );
        assert_eq!(fractions[0].1.permissions.map(|mode| mode & 0o170_000), Some(0o040_000));
        assert_eq!(
            (fractions[1].1.size, fractions[1].1.mtime),
            (Some(11), Some(1_791_540_900))
        );

        let unix = parse_listing(
            &[
                "total 8".to_owned(),
                "drwxr-xr-x   2 1000 1000 4096 Jan  5  2021 html".to_owned(),
                "-rw-r-----   1 1000 1000  120 Jan  5  2021 a b.txt".to_owned(),
            ],
            false,
        );
        assert_eq!(unix.len(), 2);
        assert_eq!(unix[1].0, "a b.txt");
        assert_eq!(unix[1].1.permissions, Some(0o100_640));

        let dos = parse_listing(
            &[
                "10-19-20  03:19PM       <DIR>          pub".to_owned(),
                "04-08-14  03:09PM                  403 readme.txt".to_owned(),
            ],
            false,
        );
        assert_eq!(dos.len(), 2);
        assert_eq!(dos[0].1.permissions, Some(0o040_755));
        assert_eq!(
            (dos[1].0.as_str(), dos[1].1.size, dos[1].1.permissions),
            ("readme.txt", Some(403), Some(0o100_644))
        );
    }

    #[test]
    fn a_link_in_a_listing_keeps_its_target_for_readlink() {
        let listing = parse_listing(
            &[
                "lrwxrwxrwx 1 a b 4 Jan 1 2026 neu -> alt".to_owned(),
                "lrwxrwxrwx 1 a b 14 Jan 1 2026 mit leer -> ../a b/c d".to_owned(),
                "-rw-r--r-- 1 a b 120 Jan 1 2026 alt".to_owned(),
                "drwxr-xr-x 1 a b 4096 Jan 1 2026 ordner".to_owned(),
            ],
            false,
        );
        assert_eq!(
            listing.iter().map(|(name, ..)| name.as_str()).collect::<Vec<_>>(),
            ["neu", "mit leer", "alt", "ordner"]
        );

        // Der Name steht ohne Pfeil da, das Ziel daneben, und der Eintrag bleibt ein Link.
        let (attributes, target) = find_entry(&listing, "neu").unwrap();
        assert_eq!(target.as_deref(), Some("alt"));
        assert_eq!(attributes.permissions.map(|mode| mode & 0o170_000), Some(0o120_000));
        assert_eq!(
            find_entry(&listing, "mit leer").unwrap().1.as_deref(),
            Some("../a b/c d")
        );

        // Was kein Link ist, nennt kein Ziel – ein Link aus `MLSD`, das keines kennt, ebenso.
        assert_eq!(find_entry(&listing, "alt").unwrap().1, None);
        assert_eq!(find_entry(&listing, "ordner").unwrap().1, None);
        let machine = parse_listing(&["type=link;size=3;modify=20261009101500; neu".to_owned()], true);
        assert_eq!(machine.len(), 1);
        assert_eq!(find_entry(&machine, "neu").unwrap().1, None);

        // Was die Liste nicht nennt, gibt es nicht.
        assert_eq!(find_entry(&listing, "fehlt").unwrap_err(), StatusCode::NoSuchFile);
    }

    #[test]
    fn a_path_with_a_line_break_never_reaches_the_control_connection() {
        assert_eq!(safe("/srv/a\r\nDELE /etc/passwd"), Err(StatusCode::BadMessage));
        assert_eq!(safe("/srv/a\nb"), Err(StatusCode::BadMessage));
        assert_eq!(safe(""), Err(StatusCode::BadMessage));
        assert_eq!(safe("/srv/a b"), Ok("/srv/a b"));
        assert_eq!(
            parent_and_name("/srv/www/index.html"),
            ("/srv/www".to_owned(), "index.html".to_owned())
        );
        assert_eq!(parent_and_name("/top"), ("/".to_owned(), "top".to_owned()));
    }

    #[test]
    fn a_certificate_the_user_agreed_to_is_remembered_per_server() {
        let file = std::env::temp_dir().join(format!("nexus-tls-trust-{}.json", std::process::id()));
        let _ = std::fs::remove_file(&file);
        let trust = TlsTrust::load(file.clone());
        assert_eq!(trust.get("nas.local:21"), None);

        trust.trust("nas.local:21", "SHA256:abc").unwrap();
        assert_eq!(
            TlsTrust::load(file.clone()).get("nas.local:21").as_deref(),
            Some("SHA256:abc")
        );
        // Ein anderer Port ist ein anderer Server.
        assert_eq!(TlsTrust::load(file.clone()).get("nas.local:990"), None);
        let _ = std::fs::remove_file(&file);

        assert!(fingerprint(b"certificate").starts_with("SHA256:"));
        assert!(!fingerprint(b"certificate").ends_with('='));
    }
}
